using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;

namespace BlogApi.Tests;

public sealed class ApiTests : IClassFixture<BlogFactory>
{
    private readonly HttpClient _client;
    public ApiTests(BlogFactory factory) => _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Management_requires_blog_key()
    {
        var response = await _client.GetAsync("/api/v1/admin/articles");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Publishing_exposes_article_and_supports_conditional_get()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/articles");
        request.Headers.Add("X-Admin-Key", "test-key");
        request.Content = JsonContent.Create(new
        {
            slug = "stable-post",
            title = "Stable post",
            summary = "Summary",
            body = new[] { new { type = "paragraph", text = "Hello world" } },
            tags = new[] { "dotnet" }
        });
        var created = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await created.Content.ReadFromJsonAsync<JsonObject>();
        var id = json!["id"]!.GetValue<Guid>();
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/admin/articles/{id}");
        patch.Headers.Add("X-Admin-Key", "test-key");
        patch.Headers.TryAddWithoutValidation("If-Match", created.Headers.ETag!.ToString());
        patch.Content = JsonContent.Create(new
        {
            status = "Draft"
        }, mediaType: System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/merge-patch+json"));
        var drafted = await _client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, drafted.StatusCode);
        patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/admin/articles/{id}");
        patch.Headers.Add("X-Admin-Key", "test-key");
        patch.Headers.TryAddWithoutValidation("If-Match", drafted.Headers.ETag!.ToString());
        patch.Content = JsonContent.Create(new
        {
            status = "Published"
        }, mediaType: System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/merge-patch+json"));
        var published = await _client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var detail = await _client.GetAsync("/api/v1/articles/stable-post");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Contains("public", detail.Headers.CacheControl!.ToString());
        var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/v1/articles/stable-post");
        conditional.Headers.IfNoneMatch.Add(detail.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await _client.SendAsync(conditional)).StatusCode);
    }

    [Fact]
    public async Task Missing_if_match_is_rejected()
    {
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/series");
        create.Headers.Add("X-Admin-Key", "test-key");
        create.Content = JsonContent.Create(new
        {
            slug = "series",
            title = "Series"
        });
        var response = await _client.SendAsync(create);
        var value = await response.Content.ReadFromJsonAsync<JsonObject>();
        var id = value!["id"]!.GetValue<Guid>();
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/admin/series/{id}");
        patch.Headers.Add("X-Admin-Key", "test-key");
        patch.Content = JsonContent.Create(new
        {
            title = "Changed"
        }, mediaType: System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/merge-patch+json"));
        Assert.Equal((HttpStatusCode)428, (await _client.SendAsync(patch)).StatusCode);
    }
}

public sealed class BlogFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminKey"] = "test-key",
            ["CursorKey"] = "cursor-test-key",
            ["PublicSiteOrigin"] = "https://igor.example"
        }));
    }
}
