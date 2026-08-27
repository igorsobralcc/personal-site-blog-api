using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace BlogApi.Tests;

public sealed class ArticleAuthoringFlowTests
{
    [Fact]
    public async Task Create_minimum_article_sets_defaults_location_and_etag()
    {
        await using var app = new TestApplication();
        var (response, json) = await app.SendJson(app.AdminRequest(
            HttpMethod.Post, "/api/v1/admin/articles", new { }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Writing", json["status"]!.GetValue<string>());
        Assert.Equal(1, json["bodyVersion"]!.GetValue<int>());
        Assert.Equal(1, json["readingTimeMinutes"]!.GetValue<int>());
        Assert.Empty(json["body"]!.AsArray());
        Assert.Empty(json["tags"]!.AsArray());
        Assert.Equal("\"1\"", response.Headers.ETag!.ToString());
        Assert.EndsWith(json["id"]!.GetValue<Guid>().ToString(), response.Headers.Location!.ToString());
        Assert.Equal(7, json["id"]!.GetValue<Guid>().Version);
    }

    [Fact]
    public async Task Create_supports_every_block_and_resolves_active_media()
    {
        await using var app = new TestApplication();
        var media = await app.UploadMedia(caption: "Default caption");
        object[] body =
        [
            new { type = "paragraph", text = "Paragraph" },
            new { type = "heading", text = "Heading" },
            new { type = "quote", text = "Quote" },
            new { type = "list", items = new[] { "One", "Two" }, ordered = true },
            new { type = "code", code = "one\n\ntwo", language = "csharp" },
            new { type = "image", mediaId = media.Id, alt = "Context", caption = "Caption" },
            new { type = "table", caption = "Table", headers = new[] { "A", "B" }, rows = new[] { new[] { "1", "2" } } }
        ];

        var article = await app.CreateArticle("all-blocks", extra: new
        {
            slug = "all-blocks",
            title = " All blocks ",
            summary = " Summary ",
            topic = " Topic ",
            seoTitle = " SEO ",
            seoDescription = " Description ",
            body,
            tags = new[] { "dotnet", "api" },
            editorialMediaId = media.Id,
            socialMediaId = media.Id
        });

        Assert.Equal("All blocks", article.Json["title"]!.GetValue<string>());
        Assert.Equal("Topic", article.Json["topic"]!.GetValue<string>());
        Assert.Equal(7, article.Json["body"]!.AsArray().Count);
        Assert.Equal(2, article.Json["tags"]!.AsArray().Count);
        Assert.Single(app.Store.Articles[article.Id].MediaIds);
        Assert.Single(app.Store.Articles[article.Id].Revisions);
    }

    public static TheoryData<object> InvalidMetadata => new()
    {
        new { slug = "Bad Slug" },
        new { slug = new string('a', 161) },
        new { title = new string('t', 201) },
        new { summary = new string('s', 501) },
        new { topic = new string('t', 81) },
        new { seoTitle = new string('s', 71) },
        new { seoDescription = new string('d', 181) },
        new { tags = Enumerable.Range(0, 21).Select(i => $"tag{i}").ToArray() },
        new { tags = new[] { "same", " SAME " } },
        new { tags = new[] { " " } },
        new { tags = new[] { new string('x', 41) } }
    };

    [Theory]
    [MemberData(nameof(InvalidMetadata))]
    public async Task Invalid_metadata_never_creates_article(object payload)
    {
        await using var app = new TestApplication();
        var response = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, "/api/v1/admin/articles", payload));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(app.Store.Articles);
    }

    public static TheoryData<object> InvalidBodies => new()
    {
        new { body = new object[] { new { text = "missing type" } } },
        new { body = new object[] { new { type = "paragraph", text = " " } } },
        new { body = new object[] { new { type = "heading" } } },
        new { body = new object[] { new { type = "quote", text = "" } } },
        new { body = new object[] { new { type = "code", code = " " } } },
        new { body = new object[] { new { type = "list", items = Array.Empty<string>() } } },
        new { body = new object[] { new { type = "list", items = new[] { "ok", " " } } } },
        new { body = new object[] { new { type = "image", mediaId = Guid.NewGuid() } } },
        new { body = new object[] { new { type = "table", caption = " ", headers = new[] { "A" }, rows = Array.Empty<string[]>() } } },
        new { body = new object[] { new { type = "table", caption = "C", headers = Array.Empty<string>(), rows = Array.Empty<string[]>() } } },
        new
        {
            body = new object[]
            {
                new
                {
                    type = "table",
                    caption = "C",
                    headers = new[] { "A" },
                    rows = new[] { new[] { "1", "2" } }
                }
            }
        },
        new { body = new object[] { new { type = "html", html = "<script/>" } } }
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Invalid_blocks_are_rejected_atomically(object payload)
    {
        await using var app = new TestApplication();
        var response = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, "/api/v1/admin/articles", payload));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(app.Store.Articles);
    }

    [Fact]
    public async Task Body_count_and_serialized_size_limits_are_enforced()
    {
        await using var app = new TestApplication();
        var tooMany = Enumerable.Range(0, 501).Select(_ => new { type = "paragraph", text = "x" }).ToArray();
        var countResponse = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, "/api/v1/admin/articles", new { body = tooMany }));
        var sizeResponse = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, "/api/v1/admin/articles", new
            {
                body = new[] { new { type = "paragraph", text = new string('x', 1024 * 1024) } }
            }));

        Assert.Equal(HttpStatusCode.BadRequest, countResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, sizeResponse.StatusCode);
        Assert.Empty(app.Store.Articles);
    }

    [Fact]
    public async Task Duplicate_active_slug_is_case_insensitive_but_deleted_slug_can_be_reused()
    {
        await using var app = new TestApplication();
        var first = await app.CreateArticle("unique-slug");
        var duplicate = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, "/api/v1/admin/articles", new { slug = "unique-slug" }));
        using (var delete = app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/articles/{first.Id}", etag: first.ETag))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await app.Client.SendAsync(delete)).StatusCode);
        }
        var reused = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Post, "/api/v1/admin/articles", new { slug = "unique-slug" }));

        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.Created, reused.StatusCode);
    }

    [Fact]
    public async Task Patch_applies_replacement_and_null_semantics_and_trims_tags()
    {
        await using var app = new TestApplication();
        var article = await app.CreateArticle("patchable", extra: new
        {
            slug = "patchable",
            title = "Title",
            summary = "Summary",
            topic = "Topic",
            body = new[] { new { type = "paragraph", text = "old" } },
            tags = new[] { "old" }
        });

        var (response, json) = await app.PatchArticle(article.Id, article.ETag, new
        {
            topic = (string?)null,
            body = new[] { new { type = "quote", text = "new" } },
            tags = new[] { " one ", "two" },
            editorialMediaId = (Guid?)null,
            socialMediaId = (Guid?)null
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(json["topic"]);
        Assert.Equal("quote", json["body"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("one", json["tags"]![0]!.GetValue<string>());
        Assert.Equal("\"2\"", response.Headers.ETag!.ToString());
        Assert.Equal(2, app.Store.Articles[article.Id].Revisions.Count);
    }

    [Fact]
    public async Task Patch_wrong_shapes_and_invalid_status_return_bad_request()
    {
        await using var app = new TestApplication();
        var article = await app.CreateArticle("bad-patch");
        foreach (var raw in new[]
        {
            "{\"status\":\"NoSuchState\"}",
            "{\"tags\":\"not-an-array\"}",
            "{\"body\":{}}",
            "{\"editorialMediaId\":\"not-a-guid\"}"
        })
        {
            using var request = app.AdminRequest(
                HttpMethod.Patch, $"/api/v1/admin/articles/{article.Id}", etag: article.ETag,
                mediaType: "application/merge-patch+json");
            request.Content = new StringContent(raw, System.Text.Encoding.UTF8, "application/merge-patch+json");
            var response = await app.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Single(app.Store.Articles[article.Id].Revisions);
    }

    [Fact]
    public async Task Noop_patch_with_short_article_keeps_etag_and_revision()
    {
        await using var app = new TestApplication();
        var article = await app.CreateArticle("noop");
        var (response, _) = await app.PatchArticle(article.Id, article.ETag, new { });

        Assert.Equal(article.ETag, response.Headers.ETag!.ToString());
        Assert.Single(app.Store.Articles[article.Id].Revisions);
    }

    [Fact]
    public async Task Reading_time_combines_prose_and_nonblank_code_lines()
    {
        await using var app = new TestApplication();
        var words = string.Join(' ', Enumerable.Repeat("word", 201));
        var code = string.Join('\n', Enumerable.Range(1, 13).Select(i => i == 7 ? " " : $"line{i}")) + "\nline14";
        var article = await app.CreateArticle("reading", body: new object[]
        {
            new { type = "paragraph", text = words },
            new { type = "list", items = new[] { "one", "two" } },
            new { type = "table", caption = "caption", headers = new[] { "head" }, rows = new[] { new[] { "cell" } } },
            new { type = "code", code }
        });

        Assert.Equal(3, article.Json["readingTimeMinutes"]!.GetValue<int>());
    }

    [Fact]
    public async Task Admin_get_returns_deleted_article_and_missing_returns_not_found()
    {
        await using var app = new TestApplication();
        var article = await app.CreateArticle("admin-get");
        using (var delete = app.AdminRequest(
            HttpMethod.Delete, $"/api/v1/admin/articles/{article.Id}", etag: article.ETag))
        {
            await app.Client.SendAsync(delete);
        }

        var deleted = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Get, $"/api/v1/admin/articles/{article.Id}"));
        var missing = await app.Client.SendAsync(app.AdminRequest(
            HttpMethod.Get, $"/api/v1/admin/articles/{Guid.NewGuid()}"));

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.NotNull(deleted.Headers.ETag);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
