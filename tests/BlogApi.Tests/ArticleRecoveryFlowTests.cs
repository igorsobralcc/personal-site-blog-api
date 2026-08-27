using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace BlogApi.Tests;

public sealed class ArticleRecoveryFlowTests
{
    [Fact]
    public async Task Missing_and_stale_article_preconditions_are_rejected()
    {
        await using var app = new TestApplication();
        var article = await app.CreateArticle("preconditions");

        var missingPatch = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/articles/{article.Id}", new { title = "x" },
            mediaType: "application/merge-patch+json"));
        var stalePatch = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/articles/{article.Id}", new { title = "x" }, "\"999\"",
            "application/merge-patch+json"));
        var multiplePatch = app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/articles/{article.Id}", new { title = "x" },
            mediaType: "application/merge-patch+json");
        multiplePatch.Headers.TryAddWithoutValidation("If-Match", new[] { article.ETag, "\"2\"" });
        var multiple = await app.Client.SendAsync(multiplePatch);

        Assert.Equal((HttpStatusCode)428, missingPatch.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stalePatch.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, multiple.StatusCode);
        Assert.Single(app.Store.Articles[article.Id].Revisions);
    }

    [Fact]
    public async Task Delete_is_soft_and_repeated_delete_is_idempotent()
    {
        await using var app = new TestApplication();
        var article = await app.CreateArticle("delete-idempotent");
        using var firstRequest = app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/articles/{article.Id}", etag: article.ETag);
        var first = await app.Client.SendAsync(firstRequest);
        var deleted = app.Store.Articles[article.Id];
        var version = deleted.Version;
        var revisions = deleted.Revisions.Count;
        using var repeatRequest = app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/articles/{article.Id}", etag: "\"stale-is-ignored\"");
        var repeat = await app.Client.SendAsync(repeatRequest);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, repeat.StatusCode);
        Assert.NotNull(deleted.DeletedAt);
        Assert.Equal(version, deleted.Version);
        Assert.Equal(revisions, deleted.Revisions.Count);
    }

    [Fact]
    public async Task Delete_missing_and_deleted_patch_restore_lookup_paths_return_not_found()
    {
        await using var app = new TestApplication();
        var missing = Guid.NewGuid();
        var deleteMissing = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/articles/{missing}", etag: "\"1\""));
        var article = await app.CreateArticle("deleted-targets");
        await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/articles/{article.Id}", etag: article.ETag));
        var patchDeleted = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/articles/{article.Id}", new { title = "x" }, "\"2\"",
            "application/merge-patch+json"));
        var restoreMissing = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/articles/{missing}/restore", etag: "\"1\""));

        Assert.Equal(HttpStatusCode.NotFound, deleteMissing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, patchDeleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, restoreMissing.StatusCode);
    }

    [Fact]
    public async Task Restore_forces_draft_and_appends_revision()
    {
        await using var app = new TestApplication();
        var article = await app.CreateArticle("restore-draft");
        var published = await app.PublishArticle(article.Id, article.ETag);
        await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/articles/{article.Id}",
            etag: published.Headers.ETag!.ToString()));
        var deletedVersion = app.Store.Articles[article.Id].Version;

        var restored = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/articles/{article.Id}/restore",
            etag: $"\"{deletedVersion}\""));

        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        Assert.Equal(PublicationStatus.Draft, app.Store.Articles[article.Id].Status);
        Assert.Null(app.Store.Articles[article.Id].DeletedAt);
        Assert.Equal("Restored", app.Store.Articles[article.Id].Revisions[^1].Operation);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/v1/articles/restore-draft")).StatusCode);
    }

    [Fact]
    public async Task Restore_conflicting_slug_preserves_deleted_state()
    {
        await using var app = new TestApplication();
        var original = await app.CreateArticle("restore-conflict");
        await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/articles/{original.Id}", etag: original.ETag));
        await app.CreateArticle("restore-conflict");
        var deleted = app.Store.Articles[original.Id];

        var response = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/articles/{original.Id}/restore",
            etag: $"\"{deleted.Version}\""));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotNull(deleted.DeletedAt);
    }

    [Fact]
    public async Task Restore_requires_current_etag_and_active_restore_is_not_found()
    {
        await using var app = new TestApplication();
        var article = await app.CreateArticle("restore-precondition");
        var activeRestore = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/articles/{article.Id}/restore", etag: article.ETag));
        await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/articles/{article.Id}", etag: article.ETag));
        var missing = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/articles/{article.Id}/restore"));
        var stale = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/articles/{article.Id}/restore", etag: "\"1\""));

        Assert.Equal(HttpStatusCode.NotFound, activeRestore.StatusCode);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task Revision_list_and_detail_cover_existing_and_missing_paths()
    {
        await using var app = new TestApplication();
        var article = await app.CreateArticle("revisions");
        var updated = await app.PatchArticle(article.Id, article.ETag, new { title = "Changed" });
        updated.Response.EnsureSuccessStatusCode();

        var list = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Get, $"/api/v1/admin/articles/{article.Id}/revisions"));
        var revisions = await list.Content.ReadFromJsonAsync<JsonArray>();
        var detail = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Get, $"/api/v1/admin/articles/{article.Id}/revisions/2"));
        var missingRevision = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Get, $"/api/v1/admin/articles/{article.Id}/revisions/99"));
        var missingParent = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Get, $"/api/v1/admin/articles/{Guid.NewGuid()}/revisions"));

        Assert.Equal(2, revisions!.Count);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingRevision.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingParent.StatusCode);
    }
}
