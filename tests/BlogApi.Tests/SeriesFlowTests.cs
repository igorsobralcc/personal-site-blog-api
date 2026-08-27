using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace BlogApi.Tests;

public sealed class SeriesFlowTests
{
    [Fact]
    public async Task Create_series_sets_defaults_membership_revision_location_and_etag()
    {
        await using var app = new TestApplication();
        var article = await app.CreateArticle("member");
        var series = await app.CreateSeries("series-create", [article.Id]);

        Assert.Equal("Writing", series.Json["status"]!.GetValue<string>());
        Assert.Single(series.Json["articleIds"]!.AsArray());
        Assert.Equal("\"1\"", series.ETag);
        Assert.Single(app.Store.Series[series.Id].Revisions);
    }

    public static TheoryData<object> InvalidSeries => new()
    {
        new { slug = "Bad Slug" },
        new { slug = new string('a', 161) },
        new { title = new string('t', 201) },
        new { summary = new string('s', 501) },
        new { articleIds = new[] { Guid.NewGuid() } }
    };

    [Theory]
    [MemberData(nameof(InvalidSeries))]
    public async Task Invalid_series_create_is_atomic(object payload)
    {
        await using var app = new TestApplication();
        var response = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, "/api/v1/admin/series", payload));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(app.Store.Series);
    }

    [Fact]
    public async Task Duplicate_and_deleted_members_are_rejected()
    {
        await using var app = new TestApplication();
        var article = await app.CreateArticle("series-member-invalid");
        var duplicate = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, "/api/v1/admin/series", new
            {
                slug = "duplicate-members",
                articleIds = new[] { article.Id, article.Id }
            }));
        await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/articles/{article.Id}", etag: article.ETag));
        var deleted = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, "/api/v1/admin/series", new
            {
                slug = "deleted-member",
                articleIds = new[] { article.Id }
            }));

        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, deleted.StatusCode);
    }

    [Fact]
    public async Task Active_slug_conflict_is_case_insensitive_and_deleted_slug_can_be_reused()
    {
        await using var app = new TestApplication();
        var first = await app.CreateSeries("series-slug");
        var duplicate = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, "/api/v1/admin/series", new { slug = "series-slug" }));
        await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/series/{first.Id}", etag: first.ETag));
        var reused = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, "/api/v1/admin/series", new { slug = "series-slug" }));

        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.Created, reused.StatusCode);
    }

    [Fact]
    public async Task Patch_replaces_membership_and_null_clears_nullable_fields()
    {
        await using var app = new TestApplication();
        var a = await app.CreateArticle("member-a");
        var b = await app.CreateArticle("member-b");
        var series = await app.CreateSeries("replace-members", [a.Id]);

        var (response, json) = await app.PatchSeries(series.Id, series.ETag, new
        {
            summary = (string?)null,
            articleIds = new[] { b.Id }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(json["summary"]);
        Assert.Equal(b.Id, json["articleIds"]![0]!.GetValue<Guid>());
        Assert.Equal(2, app.Store.Series[series.Id].Revisions.Count);
    }

    [Fact]
    public async Task Reordered_equivalent_membership_is_a_noop()
    {
        await using var app = new TestApplication();
        var a = await app.CreateArticle("set-a");
        var b = await app.CreateArticle("set-b");
        var series = await app.CreateSeries("set-equivalent", [a.Id, b.Id]);

        var (response, _) = await app.PatchSeries(
            series.Id, series.ETag, new { articleIds = new[] { b.Id, a.Id } });

        Assert.Equal(series.ETag, response.Headers.ETag!.ToString());
        Assert.Single(app.Store.Series[series.Id].Revisions);
    }

    [Fact]
    public async Task Series_patch_invalid_shapes_validation_and_preconditions_are_safe()
    {
        await using var app = new TestApplication();
        var series = await app.CreateSeries("series-bad-patch");
        var missingHeader = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/series/{series.Id}", new { title = "x" },
            mediaType: "application/merge-patch+json"));
        var stale = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/series/{series.Id}", new { title = "x" }, "\"9\"",
            "application/merge-patch+json"));
        var invalidMember = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/series/{series.Id}",
            new { articleIds = new[] { Guid.NewGuid() } }, series.ETag,
            "application/merge-patch+json"));
        using var malformed = app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/series/{series.Id}", etag: series.ETag,
            mediaType: "application/merge-patch+json");
        malformed.Content = new StringContent(
            "{\"status\":\"invalid\"}", System.Text.Encoding.UTF8, "application/merge-patch+json");
        var invalidStatus = await app.Client.SendAsync(malformed);

        Assert.Equal((HttpStatusCode)428, missingHeader.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidMember.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidStatus.StatusCode);
    }

    [Fact]
    public async Task Allowed_series_transition_paths_and_forbidden_transition_are_enforced()
    {
        await using var app = new TestApplication();
        var series = await app.CreateSeries("series-transitions");
        var etag = series.ETag;
        foreach (var status in new[] { "Draft", "Writing", "Draft", "NotListed", "Published", "NotListed", "Archived", "Draft" })
        {
            var (response, _) = await app.PatchSeries(series.Id, etag, new { status });
            response.EnsureSuccessStatusCode();
            etag = response.Headers.ETag!.ToString();
        }
        var other = await app.CreateSeries("series-archive");
        var published = await app.PublishSeries(other.Id, other.ETag);
        var (archived, _) = await app.PatchSeries(
            other.Id, published.Headers.ETag!.ToString(), new { status = "Archived" });
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        var (forbidden, _) = await app.PatchSeries(series.Id, etag, new { status = "Archived" });
        Assert.Equal(HttpStatusCode.BadRequest, forbidden.StatusCode);
    }

    [Fact]
    public async Task Series_publication_requires_slug_and_title_and_freezes_slug()
    {
        await using var app = new TestApplication();
        var incompleteResponse = await app.SendJson(app.AdminRequest(
            HttpMethod.Post, "/api/v1/admin/series", new { slug = "incomplete-series" }));
        var drafted = await app.PatchSeries(
            incompleteResponse.Json["id"]!.GetValue<Guid>(),
            incompleteResponse.Response.Headers.ETag!.ToString(),
            new { status = "Draft" });
        var rejected = await app.PatchSeries(
            incompleteResponse.Json["id"]!.GetValue<Guid>(),
            drafted.Response.Headers.ETag!.ToString(),
            new { status = "Published" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.Response.StatusCode);

        var complete = await app.CreateSeries("fixed-series");
        var published = await app.PublishSeries(complete.Id, complete.ETag);
        var changed = await app.PatchSeries(
            complete.Id, published.Headers.ETag!.ToString(), new { slug = "changed-series" });
        Assert.Equal(HttpStatusCode.Conflict, changed.Response.StatusCode);
    }

    [Fact]
    public async Task Public_series_filters_private_members_orders_and_conditionally_caches()
    {
        await using var app = new TestApplication();
        var first = await app.CreateArticle("oldest-public");
        await app.PublishArticle(first.Id, first.ETag);
        var second = await app.CreateArticle("newest-public");
        await app.PublishArticle(second.Id, second.ETag);
        var draft = await app.CreateArticle("private-member");
        var series = await app.CreateSeries("public-series", [second.Id, draft.Id, first.Id]);
        await app.PublishSeries(series.Id, series.ETag);

        var response = await app.Client.GetAsync("/api/v1/series/public-series");
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        var members = json!["articles"]!.AsArray();
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/v1/series/public-series");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", response.Headers.ETag!.ToString());
        var notModified = await app.Client.SendAsync(conditional);

        Assert.Collection(members,
            item => Assert.Equal(first.Id, item!["id"]!.GetValue<Guid>()),
            item => Assert.Equal(second.Id, item!["id"]!.GetValue<Guid>()));
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Contains("public", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Public_series_etag_changes_when_visible_member_changes()
    {
        await using var app = new TestApplication();
        var article = await app.CreateArticle("etag-member");
        var publishedArticle = await app.PublishArticle(article.Id, article.ETag);
        var series = await app.CreateSeries("etag-series", [article.Id]);
        await app.PublishSeries(series.Id, series.ETag);
        var before = await app.Client.GetAsync("/api/v1/series/etag-series");
        await app.PatchArticle(
            article.Id, publishedArticle.Headers.ETag!.ToString(), new { title = "Changed title" });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/series/etag-series");
        request.Headers.TryAddWithoutValidation("If-None-Match", before.Headers.ETag!.ToString());
        var after = await app.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.NotEqual(before.Headers.ETag!.ToString(), after.Headers.ETag!.ToString());
    }

    [Theory]
    [InlineData("Writing")]
    [InlineData("Draft")]
    [InlineData("NotListed")]
    [InlineData("Archived")]
    public async Task Private_series_states_are_public_not_found(string target)
    {
        await using var app = new TestApplication();
        var slug = $"private-series-{target.ToLowerInvariant()}";
        var series = await app.CreateSeries(slug);
        var etag = series.ETag;
        if (target != "Writing")
        {
            var draft = await app.PatchSeries(series.Id, etag, new { status = "Draft" });
            etag = draft.Response.Headers.ETag!.ToString();
            if (target == "NotListed")
            {
                var hidden = await app.PatchSeries(series.Id, etag, new { status = "NotListed" });
                etag = hidden.Response.Headers.ETag!.ToString();
            }
            else if (target == "Archived")
            {
                var pub = await app.PatchSeries(series.Id, etag, new { status = "Published" });
                var archived = await app.PatchSeries(
                    series.Id, pub.Response.Headers.ETag!.ToString(), new { status = "Archived" });
                etag = archived.Response.Headers.ETag!.ToString();
            }
        }
        var response = await app.Client.GetAsync($"/api/v1/series/{slug}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Series_delete_restore_and_revisions_are_consistent()
    {
        await using var app = new TestApplication();
        var series = await app.CreateSeries("series-recover");
        var deleted = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/series/{series.Id}", etag: series.ETag));
        var entity = app.Store.Series[series.Id];
        var repeat = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/series/{series.Id}", etag: "\"ignored\""));
        var restored = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/series/{series.Id}/restore", etag: $"\"{entity.Version}\""));
        var list = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Get, $"/api/v1/admin/series/{series.Id}/revisions"));
        var revisions = await list.Content.ReadFromJsonAsync<JsonArray>();
        var detail = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Get, $"/api/v1/admin/series/{series.Id}/revisions/3"));
        var missingRevision = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Get, $"/api/v1/admin/series/{series.Id}/revisions/99"));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, repeat.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        Assert.Equal(PublicationStatus.Draft, entity.Status);
        Assert.Collection(revisions!, _ => { }, _ => { }, _ => { });
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingRevision.StatusCode);
    }

    [Fact]
    public async Task Series_restore_conflict_and_lookup_precondition_paths()
    {
        await using var app = new TestApplication();
        var original = await app.CreateSeries("series-conflict");
        var missingDelete = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/series/{Guid.NewGuid()}", etag: "\"1\""));
        var noDeleteHeader = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/series/{original.Id}"));
        await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/series/{original.Id}", etag: original.ETag));
        await app.CreateSeries("series-conflict");
        var entity = app.Store.Series[original.Id];
        var conflict = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, $"/api/v1/admin/series/{original.Id}/restore", etag: $"\"{entity.Version}\""));
        var patchDeleted = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Patch, $"/api/v1/admin/series/{original.Id}", new { title = "x" },
            $"\"{entity.Version}\"", "application/merge-patch+json"));
        var missingRevisionParent = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Get, $"/api/v1/admin/series/{Guid.NewGuid()}/revisions"));

        Assert.Equal(HttpStatusCode.NotFound, missingDelete.StatusCode);
        Assert.Equal((HttpStatusCode)428, noDeleteHeader.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, patchDeleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingRevisionParent.StatusCode);
    }

    [Fact]
    public async Task Admin_series_get_returns_deleted_and_missing_is_not_found()
    {
        await using var app = new TestApplication();
        var series = await app.CreateSeries("series-admin-get");
        await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/series/{series.Id}", etag: series.ETag));
        var deleted = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Get, $"/api/v1/admin/series/{series.Id}"));
        var missing = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Get, $"/api/v1/admin/series/{Guid.NewGuid()}"));

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.NotNull(deleted.Headers.ETag);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
