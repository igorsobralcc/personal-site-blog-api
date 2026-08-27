using System.Net;
using System.Reflection;
using System.Text;

namespace BlogApi.Tests;

public sealed class CoverageEdgeFlowTests
{
    [Fact]
    public async Task Article_patch_validation_and_delete_stale_paths_are_covered()
    {
        await using var app = new TestApplication();
        var article = await app.CreateArticle("edge-article");
        var invalid = await app.PatchArticle(
            article.Id, article.ETag, new { title = new string('x', 201) });
        var staleDelete = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/articles/{article.Id}", etag: "\"9\""));
        var badMediaCreate = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, "/api/v1/admin/articles", new
            {
                slug = "bad-top-media",
                editorialMediaId = Guid.NewGuid()
            }));

        Assert.Equal(HttpStatusCode.BadRequest, invalid.Response.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleDelete.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badMediaCreate.StatusCode);
    }

    [Fact]
    public async Task Article_restore_rejects_deleted_media_reference()
    {
        await using var app = new TestApplication();
        var media = await app.UploadMedia();
        var article = await app.CreateArticle("restore-media-edge", extra: new
        {
            slug = "restore-media-edge",
            editorialMediaId = media.Id
        });
        await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/articles/{article.Id}", etag: article.ETag));
        await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/media/{media.Id}", etag: media.ETag));
        var entity = app.Store.Articles[article.Id];

        var restore = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/articles/{article.Id}/restore",
            etag: $"\"{entity.Version}\""));

        Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
        Assert.NotNull(entity.DeletedAt);
    }

    [Fact]
    public async Task Cursor_wrong_signature_and_invalid_hex_are_rejected()
    {
        await using var app = new TestApplication();
        var time = DateTimeOffset.UtcNow.ToString("O");
        var id = Guid.NewGuid();
        var wrongSignature = Base64Url($"1|{time}|{id}|{new string('0', 64)}");
        var invalidHex = Base64Url($"1|{time}|{id}|zz");

        var wrong = await app.Client.GetAsync($"/api/v1/articles?cursor={wrongSignature}");
        var malformed = await app.Client.GetAsync($"/api/v1/articles?cursor={invalidHex}");

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [Fact]
    public async Task Default_media_storage_restore_and_invalid_media_paging_are_covered()
    {
        await using var app = new TestApplication();
        var media = await app.UploadMedia();
        await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/media/{media.Id}", etag: media.ETag));
        var entity = app.Store.Media[media.Id];
        var restored = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/media/{media.Id}/restore",
            etag: $"\"{entity.Version}\""));
        using var invalidPage = app.AdminRequest(HttpMethod.Get, "/api/v1/admin/media");
        invalidPage.Headers.Add("X-Page", "0");
        var invalid = await app.Client.SendAsync(invalidPage);

        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public void Image_probe_unknown_type_returns_false()
    {
        var type = typeof(Program).Assembly.GetType("ImageProbe", throwOnError: true)!;
        var method = type.GetMethod("Matches", BindingFlags.Public | BindingFlags.Static)!;
        var result = (bool)method.Invoke(null, new object[] { TestImages.Png, "image/unknown" })!;
        Assert.False(result);
    }

    [Fact]
    public async Task Series_list_restore_and_title_patch_edges_are_covered()
    {
        await using var app = new TestApplication();
        var series = await app.CreateSeries("series-edge");
        var activeRestore = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/series/{series.Id}/restore", etag: series.ETag));
        var titlePatch = await app.PatchSeries(series.Id, series.ETag, new { title = "Changed" });
        var etag = titlePatch.Response.Headers.ETag!.ToString();
        await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/series/{series.Id}", etag: etag));
        var deleted = app.Store.Series[series.Id];
        var noHeaderRestore = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/series/{series.Id}/restore"));
        var missingRestore = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/series/{Guid.NewGuid()}/restore", etag: "\"1\""));
        using var invalidPage = app.AdminRequest(HttpMethod.Get, "/api/v1/admin/series");
        invalidPage.Headers.Add("X-Page-Size", "0");
        var invalidList = await app.Client.SendAsync(invalidPage);

        Assert.Equal(HttpStatusCode.NotFound, activeRestore.StatusCode);
        Assert.Equal("Changed", app.Store.Series[series.Id].Title);
        Assert.Equal((HttpStatusCode)428, noHeaderRestore.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingRestore.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidList.StatusCode);
        Assert.NotNull(deleted.DeletedAt);
    }

    private static string Base64Url(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
