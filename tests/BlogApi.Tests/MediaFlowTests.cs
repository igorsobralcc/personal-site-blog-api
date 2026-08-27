using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace BlogApi.Tests;

public sealed class MediaFlowTests
{
    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    public async Task Upload_accepts_supported_signatures_and_sanitizes_filename(string contentType)
    {
        await using var app = new TestApplication();
        var bytes = contentType switch
        {
            "image/jpeg" => TestImages.Jpeg,
            "image/webp" => TestImages.WebP,
            _ => TestImages.Png
        };

        var media = await app.UploadMedia(
            contentType, bytes, " Alt ", " Caption ", fileName: "../unsafe-name.bin");

        Assert.Equal("unsafe-name.bin", media.Json["originalFileName"]!.GetValue<string>());
        Assert.Equal("Alt", media.Json["alt"]!.GetValue<string>());
        Assert.Equal("Caption", media.Json["caption"]!.GetValue<string>());
        Assert.Equal(contentType, media.Json["inputType"]!.GetValue<string>());
        Assert.Equal(64, media.Json["digest"]!.GetValue<string>().Length);
        Assert.Equal(1, media.Json["width"]!.GetValue<int>());
        Assert.Equal("\"1\"", media.ETag);
    }

    [Fact]
    public async Task Decorative_upload_allows_empty_alt()
    {
        await using var app = new TestApplication();
        var media = await app.UploadMedia(alt: "", decorative: true);
        Assert.Equal("", media.Json["alt"]!.GetValue<string>());
        Assert.Null(media.Json["caption"]);
    }

    [Fact]
    public async Task Upload_requires_multipart()
    {
        await using var app = new TestApplication();
        var response = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, "/api/v1/admin/media", new { file = "no" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_rejects_missing_empty_oversized_and_invalid_metadata()
    {
        await using var app = new TestApplication();

        var missing = await SendForm(app, null, "image/png", "Alt");
        var empty = await SendForm(app, [], "image/png", "Alt");
        var oversized = await SendForm(app, new byte[10 * 1024 * 1024 + 1], "image/png", "Alt");
        var blankAlt = await SendForm(app, TestImages.Png, "image/png", " ");
        var longAlt = await SendForm(app, TestImages.Png, "image/png", new string('a', 501));
        var longCaption = await SendForm(app, TestImages.Png, "image/png", "Alt", new string('c', 1001));

        Assert.All(new[] { missing, empty, oversized, blankAlt, longAlt, longCaption },
            response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
        Assert.Empty(app.Store.Media);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("image/gif")]
    public async Task Upload_rejects_unsupported_types(string contentType)
    {
        await using var app = new TestApplication();
        var response = await SendForm(app, TestImages.Png, contentType, "Alt");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    public async Task Upload_rejects_signature_mismatch(string contentType)
    {
        await using var app = new TestApplication();
        var response = await SendForm(app, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13], contentType, "Alt");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Storage_upload_exception_is_contained_as_server_error()
    {
        var storage = new ControllableMediaStorage { UploadFails = true };
        await using var app = new TestApplication(storage);

        var response = await SendForm(app, TestImages.Png, "image/png", "Alt");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(app.Store.Media);
        Assert.Equal(1, storage.UploadCalls);
    }

    [Fact]
    public async Task Media_list_get_and_deleted_filter_work()
    {
        await using var app = new TestApplication();
        var first = await app.UploadMedia(alt: "One");
        await app.UploadMedia(alt: "Two");
        await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/media/{first.Id}", etag: first.ETag));

        var active = await app.Client.SendAsync(app.AdminRequest(HttpMethod.Get, "/api/v1/admin/media"));
        var activeJson = await active.Content.ReadFromJsonAsync<JsonObject>();
        using var allRequest = app.AdminRequest(HttpMethod.Get, "/api/v1/admin/media");
        allRequest.Headers.Add("X-Include-Deleted", "true");
        var all = await app.Client.SendAsync(allRequest);
        var allJson = await all.Content.ReadFromJsonAsync<JsonObject>();
        var getDeleted = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Get, $"/api/v1/admin/media/{first.Id}"));
        var missing = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Get, $"/api/v1/admin/media/{Guid.NewGuid()}"));

        Assert.Single(activeJson!["items"]!.AsArray());
        Assert.Equal(2, allJson!["totalItems"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.OK, getDeleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Media_patch_changes_alt_caption_and_can_clear_caption()
    {
        await using var app = new TestApplication();
        var media = await app.UploadMedia(caption: "Old");
        var changed = await app.SendJson(app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/media/{media.Id}",
            new { alt = " New alt ", caption = " New caption " }, media.ETag));
        var cleared = await app.SendJson(app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/media/{media.Id}",
            new { clearCaption = true }, changed.Response.Headers.ETag!.ToString()));

        Assert.Equal("New alt", changed.Json["alt"]!.GetValue<string>());
        Assert.Equal("New caption", changed.Json["caption"]!.GetValue<string>());
        Assert.Null(cleared.Json["caption"]);
        Assert.Equal("\"3\"", cleared.Response.Headers.ETag!.ToString());
    }

    [Fact]
    public async Task Media_patch_noop_and_validation_paths_preserve_version()
    {
        await using var app = new TestApplication();
        var media = await app.UploadMedia(alt: "Alt", caption: "Caption");
        var noop = await app.SendJson(app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/media/{media.Id}",
            new { alt = "Alt" }, media.ETag));
        var longAlt = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/media/{media.Id}",
            new { alt = new string('a', 501) }, media.ETag));
        var longCaption = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/media/{media.Id}",
            new { caption = new string('c', 1001) }, media.ETag));

        Assert.Equal(media.ETag, noop.Response.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, longAlt.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longCaption.StatusCode);
        Assert.Equal(1, app.Store.Media[media.Id].Version);
    }

    [Fact]
    public async Task Media_patch_lookup_and_precondition_paths()
    {
        await using var app = new TestApplication();
        var media = await app.UploadMedia();
        var noHeader = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/media/{media.Id}", new { alt = "x" }));
        var stale = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/media/{media.Id}", new { alt = "x" }, "\"9\""));
        var missing = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/media/{Guid.NewGuid()}", new { alt = "x" }, "\"1\""));
        await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/media/{media.Id}", etag: media.ETag));
        var deleted = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/media/{media.Id}", new { alt = "x" }, "\"2\""));

        Assert.Equal((HttpStatusCode)428, noHeader.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
    }

    [Fact]
    public async Task Referenced_media_cannot_be_deleted_until_article_is_deleted()
    {
        await using var app = new TestApplication();
        var media = await app.UploadMedia();
        var article = await app.CreateArticle("media-reference", extra: new
        {
            slug = "media-reference",
            editorialMediaId = media.Id,
            body = new[] { new { type = "image", mediaId = media.Id } }
        });
        var conflict = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/media/{media.Id}", etag: media.ETag));
        await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/articles/{article.Id}", etag: article.ETag));
        var deleted = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/media/{media.Id}", etag: media.ETag));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task Media_delete_missing_precondition_and_idempotent_paths()
    {
        await using var app = new TestApplication();
        var missing = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/media/{Guid.NewGuid()}", etag: "\"1\""));
        var media = await app.UploadMedia();
        var noHeader = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/media/{media.Id}"));
        await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/media/{media.Id}", etag: media.ETag));
        var repeat = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/media/{media.Id}", etag: "\"anything\""));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal((HttpStatusCode)428, noHeader.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, repeat.StatusCode);
    }

    [Fact]
    public async Task Restore_checks_storage_and_preconditions()
    {
        var storage = new ControllableMediaStorage();
        await using var app = new TestApplication(storage);
        var media = await app.UploadMedia();
        var activeRestore = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/media/{media.Id}/restore", etag: media.ETag));
        await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/media/{media.Id}", etag: media.ETag));
        var deleted = app.Store.Media[media.Id];
        var noHeader = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/media/{media.Id}/restore"));
        var stale = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/media/{media.Id}/restore", etag: "\"1\""));
        storage.ExistsResult = false;
        var conflict = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/media/{media.Id}/restore", etag: $"\"{deleted.Version}\""));
        storage.ExistsResult = true;
        var restored = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/media/{media.Id}/restore", etag: $"\"{deleted.Version}\""));

        Assert.Equal(HttpStatusCode.NotFound, activeRestore.StatusCode);
        Assert.Equal((HttpStatusCode)428, noHeader.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        Assert.Null(deleted.DeletedAt);
        Assert.Equal(2, storage.ExistsCalls);
    }

    [Fact]
    public async Task Restore_missing_media_returns_not_found()
    {
        await using var app = new TestApplication();
        var response = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/media/{Guid.NewGuid()}/restore", etag: "\"1\""));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendForm(
        TestApplication app,
        byte[]? bytes,
        string contentType,
        string alt,
        string? caption = null)
    {
        using var form = new MultipartFormDataContent();
        if (bytes is not null)
        {
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            form.Add(file, "file", "image.bin");
        }
        form.Add(new StringContent(alt), "alt");
        if (caption is not null)
        {
            form.Add(new StringContent(caption), "caption");
        }
        using var request = app.AdminRequest(HttpMethod.Post, "/api/v1/admin/media");
        request.Content = form;
        return await app.Client.SendAsync(request);
    }
}
