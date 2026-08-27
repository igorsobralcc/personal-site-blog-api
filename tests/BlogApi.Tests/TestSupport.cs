using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using BlogApi.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BlogApi.Tests;

internal sealed class TestApplication : IAsyncDisposable
{
    public const string AdminKey = "test-key";
    private readonly TestFactory _factory;

    public TestApplication(
        IMediaStorage? mediaStorage = null,
        IReadOnlyDictionary<string, string?>? configuration = null,
        TimeProvider? timeProvider = null)
    {
        _factory = new TestFactory(mediaStorage, configuration, timeProvider);
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    public HttpClient Client { get; }

    public BlogStore Store => _factory.Services.GetRequiredService<BlogStore>();

    public HttpRequestMessage AdminRequest(
        HttpMethod method,
        string path,
        object? body = null,
        string? etag = null,
        string mediaType = "application/json")
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Admin-Key", AdminKey);
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(
                body,
                mediaType: MediaTypeHeaderValue.Parse(mediaType));
        }

        return request;
    }

    public async Task<(HttpResponseMessage Response, JsonObject Json)> SendJson(
        HttpRequestMessage request)
    {
        var response = await Client.SendAsync(request);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        return (response, json!);
    }

    public async Task<(Guid Id, string ETag, JsonObject Json)> CreateArticle(
        string slug,
        object? body = null,
        object? extra = null)
    {
        var payload = extra ?? new
        {
            slug,
            title = $"Title {slug}",
            summary = $"Summary {slug}",
            body = body ?? new[] { new { type = "paragraph", text = "Body" } }
        };
        var (response, json) = await SendJson(AdminRequest(
            HttpMethod.Post, "/api/v1/admin/articles", payload));
        response.EnsureSuccessStatusCode();
        return (json["id"]!.GetValue<Guid>(), response.Headers.ETag!.ToString(), json);
    }

    public async Task<(HttpResponseMessage Response, JsonObject Json)> PatchArticle(
        Guid id,
        string etag,
        object patch)
    {
        return await SendJson(AdminRequest(
            HttpMethod.Patch,
            $"/api/v1/admin/articles/{id}",
            patch,
            etag,
            "application/merge-patch+json"));
    }

    public async Task<(Guid Id, string ETag, JsonObject Json)> CreateSeries(
        string slug,
        IReadOnlyCollection<Guid>? articleIds = null)
    {
        var (response, json) = await SendJson(AdminRequest(
            HttpMethod.Post,
            "/api/v1/admin/series",
            new { slug, title = $"Title {slug}", summary = "Summary", articleIds }));
        response.EnsureSuccessStatusCode();
        return (json["id"]!.GetValue<Guid>(), response.Headers.ETag!.ToString(), json);
    }

    public async Task<(HttpResponseMessage Response, JsonObject Json)> PatchSeries(
        Guid id,
        string etag,
        object patch)
    {
        return await SendJson(AdminRequest(
            HttpMethod.Patch,
            $"/api/v1/admin/series/{id}",
            patch,
            etag,
            "application/merge-patch+json"));
    }

    public async Task<(Guid Id, string ETag, JsonObject Json)> UploadMedia(
        string contentType = "image/png",
        byte[]? bytes = null,
        string alt = "Alt",
        string? caption = null,
        bool decorative = false,
        string fileName = "image.png")
    {
        bytes ??= TestImages.Png;
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
        form.Add(new StringContent(alt), "alt");
        form.Add(new StringContent(decorative.ToString()), "decorative");
        if (caption is not null)
        {
            form.Add(new StringContent(caption), "caption");
        }

        using var request = AdminRequest(HttpMethod.Post, "/api/v1/admin/media");
        request.Content = form;
        var (response, json) = await SendJson(request);
        response.EnsureSuccessStatusCode();
        return (json["id"]!.GetValue<Guid>(), response.Headers.ETag!.ToString(), json);
    }

    public async Task<HttpResponseMessage> PublishArticle(Guid id, string etag)
    {
        var (drafted, _) = await PatchArticle(id, etag, new { status = "Draft" });
        drafted.EnsureSuccessStatusCode();
        var (published, _) = await PatchArticle(
            id, drafted.Headers.ETag!.ToString(), new { status = "Published" });
        return published;
    }

    public async Task<HttpResponseMessage> PublishSeries(Guid id, string etag)
    {
        var (drafted, _) = await PatchSeries(id, etag, new { status = "Draft" });
        drafted.EnsureSuccessStatusCode();
        var (published, _) = await PatchSeries(
            id, drafted.Headers.ETag!.ToString(), new { status = "Published" });
        return published;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
    }
}

internal sealed class TestFactory(
    IMediaStorage? mediaStorage,
    IReadOnlyDictionary<string, string?>? configuration,
    TimeProvider? timeProvider) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["AdminKey"] = TestApplication.AdminKey,
                ["CursorKey"] = "cursor-test-key",
                ["PublicSiteOrigin"] = "https://igor.example/",
                ["Cors:Origins:0"] = "https://front.example"
            };
            if (configuration is not null)
            {
                foreach (var pair in configuration)
                {
                    values[pair.Key] = pair.Value;
                }
            }
            config.AddInMemoryCollection(values);
        });
        builder.ConfigureServices(services =>
        {
            if (mediaStorage is not null)
            {
                services.RemoveAll<IMediaStorage>();
                services.AddSingleton(mediaStorage);
            }
            if (timeProvider is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(timeProvider);
            }
        });
    }
}

internal static class TestImages
{
    public static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10];
    public static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xd9];
    public static readonly byte[] WebP = Encoding.ASCII.GetBytes("RIFF0000WEBP0");
}

internal sealed class ControllableMediaStorage : IMediaStorage
{
    private readonly Dictionary<string, byte[]> _objects = [];

    public bool UploadFails { get; set; }
    public bool ExistsResult { get; set; } = true;
    public int UploadCalls { get; private set; }
    public int ExistsCalls { get; private set; }

    public Task<string> Upload(
        Guid id,
        string digest,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken)
    {
        UploadCalls++;
        if (UploadFails)
        {
            throw new InvalidOperationException("storage unavailable");
        }
        var url = $"https://test.assets/{id}/{digest}";
        _objects[url] = content.ToArray();
        return Task.FromResult(url);
    }

    public Task<bool> Exists(string url, string digest, CancellationToken cancellationToken)
    {
        ExistsCalls++;
        return Task.FromResult(ExistsResult && _objects.ContainsKey(url));
    }
}
