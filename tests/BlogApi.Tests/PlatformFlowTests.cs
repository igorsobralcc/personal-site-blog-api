using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace BlogApi.Tests;

public sealed class PlatformFlowTests
{
    [Fact]
    public async Task Health_routes_are_anonymous_and_healthy()
    {
        await using var app = new TestApplication();

        var live = await app.Client.GetAsync("/health/live");
        var ready = await app.Client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("Healthy", (await live.Content.ReadFromJsonAsync<JsonObject>())!["status"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    public async Task Admin_key_rejections_share_problem_contract(string? key)
    {
        await using var app = new TestApplication();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/media");
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Admin-Key", key);
        }

        var response = await app.Client.SendAsync(request);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(401, problem!["status"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(problem["traceId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Unconfigured_admin_key_is_unauthorized()
    {
        await using var app = new TestApplication(configuration: new Dictionary<string, string?>
        {
            ["AdminKey"] = null
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/articles");
        request.Headers.Add("X-Admin-Key", TestApplication.AdminKey);
        var response = await app.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Cors_allows_only_configured_origin()
    {
        await using var app = new TestApplication();
        using var allowed = new HttpRequestMessage(HttpMethod.Options, "/api/v1/articles");
        allowed.Headers.Add("Origin", "https://front.example");
        allowed.Headers.Add("Access-Control-Request-Method", "GET");
        var allowedResponse = await app.Client.SendAsync(allowed);

        using var denied = new HttpRequestMessage(HttpMethod.Options, "/api/v1/articles");
        denied.Headers.Add("Origin", "https://evil.example");
        denied.Headers.Add("Access-Control-Request-Method", "GET");
        var deniedResponse = await app.Client.SendAsync(denied);

        Assert.Equal("https://front.example", allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(deniedResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("X-Page", "nope")]
    [InlineData("X-Page", "0")]
    [InlineData("X-Page-Size", "0")]
    [InlineData("X-Page-Size", "101")]
    [InlineData("X-Page-Size", "nope")]
    public async Task Invalid_paging_is_rejected(string header, string value)
    {
        await using var app = new TestApplication();
        using var request = app.AdminRequest(HttpMethod.Get, "/api/v1/admin/articles");
        request.Headers.TryAddWithoutValidation(header, value);

        var response = await app.Client.SendAsync(request);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(problem!["errors"]!["headers"]);
    }

    [Fact]
    public async Task Collections_page_and_include_deleted_consistently()
    {
        await using var app = new TestApplication();
        var first = await app.CreateArticle("page-one");
        await app.CreateArticle("page-two");
        using (var delete = app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/articles/{first.Id}", etag: first.ETag))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await app.Client.SendAsync(delete)).StatusCode);
        }

        using var activeRequest = app.AdminRequest(HttpMethod.Get, "/api/v1/admin/articles");
        activeRequest.Headers.Add("X-Page", "1");
        activeRequest.Headers.Add("X-Page-Size", "1");
        var active = await app.Client.SendAsync(activeRequest);
        var activeJson = await active.Content.ReadFromJsonAsync<JsonObject>();

        using var allRequest = app.AdminRequest(HttpMethod.Get, "/api/v1/admin/articles");
        allRequest.Headers.Add("X-Include-Deleted", "true");
        var all = await app.Client.SendAsync(allRequest);
        var allJson = await all.Content.ReadFromJsonAsync<JsonObject>();

        Assert.Single(activeJson!["items"]!.AsArray());
        Assert.Equal(1, activeJson["page"]!.GetValue<int>());
        Assert.Equal(1, activeJson["pageSize"]!.GetValue<int>());
        Assert.Equal(1, activeJson["totalItems"]!.GetValue<int>());
        Assert.Equal(2, allJson!["totalItems"]!.GetValue<int>());
    }

    [Fact]
    public async Task Empty_overrun_page_is_successful()
    {
        await using var app = new TestApplication();
        using var request = app.AdminRequest(HttpMethod.Get, "/api/v1/admin/series");
        request.Headers.Add("X-Page", "5");

        var response = await app.Client.SendAsync(request);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(json!["items"]!.AsArray());
        Assert.Equal(0, json["totalPages"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("/api/v1/admin/articles/not-a-guid")]
    [InlineData("/api/v1/not-a-route")]
    public async Task Unknown_or_constraint_miss_returns_not_found(string path)
    {
        await using var app = new TestApplication();
        using var request = app.AdminRequest(HttpMethod.Get, path);
        var response = await app.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unsupported_method_returns_method_not_allowed()
    {
        await using var app = new TestApplication();
        var response = await app.Client.PutAsync("/api/v1/articles", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
