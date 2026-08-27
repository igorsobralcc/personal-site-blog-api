using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace BlogApi.Tests;

public sealed class ArticleLifecycleFlowTests
{
    [Fact]
    public async Task Allowed_article_transition_paths_and_forbidden_transition_are_enforced()
    {
        await using var app = new TestApplication();
        var a = await app.CreateArticle("transitions-a");
        var etag = a.ETag;
        foreach (var status in new[] { "Draft", "Writing", "Draft", "NotListed", "Published", "NotListed", "Archived", "Draft" })
        {
            var (response, _) = await app.PatchArticle(a.Id, etag, new { status });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            etag = response.Headers.ETag!.ToString();
        }

        var b = await app.CreateArticle("transitions-b");
        var published = await app.PublishArticle(b.Id, b.ETag);
        var (archived, _) = await app.PatchArticle(
            b.Id, published.Headers.ETag!.ToString(), new { status = "Archived" });
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        var (forbidden, _) = await app.PatchArticle(a.Id, etag, new { status = "Archived" });
        Assert.Equal(HttpStatusCode.BadRequest, forbidden.StatusCode);
    }

    [Fact]
    public async Task Publication_requires_complete_article_and_sets_time_once()
    {
        await using var app = new TestApplication();
        var incomplete = await app.CreateArticle("incomplete", extra: new { slug = "incomplete" });
        var (drafted, _) = await app.PatchArticle(incomplete.Id, incomplete.ETag, new { status = "Draft" });
        var (rejected, _) = await app.PatchArticle(
            incomplete.Id, drafted.Headers.ETag!.ToString(), new { status = "Published" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        var complete = await app.CreateArticle("published-once");
        var first = await app.PublishArticle(complete.Id, complete.ETag);
        var publishedAt = app.Store.Articles[complete.Id].PublishedAt;
        var (hidden, _) = await app.PatchArticle(
            complete.Id, first.Headers.ETag!.ToString(), new { status = "NotListed" });
        var (republished, republishedJson) = await app.PatchArticle(
            complete.Id, hidden.Headers.ETag!.ToString(), new { status = "Published" });

        Assert.Equal(HttpStatusCode.OK, republished.StatusCode);
        Assert.Equal(publishedAt, republishedJson["publishedAt"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task Published_slug_is_immutable()
    {
        await using var app = new TestApplication();
        var article = await app.CreateArticle("fixed-slug");
        var published = await app.PublishArticle(article.Id, article.ETag);

        var (response, _) = await app.PatchArticle(
            article.Id, published.Headers.ETag!.ToString(), new { slug = "new-slug" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("fixed-slug", app.Store.Articles[article.Id].Slug);
    }

    [Fact]
    public async Task Public_detail_resolves_media_seo_canonical_and_body_images()
    {
        await using var app = new TestApplication();
        var media = await app.UploadMedia(alt: "Default alt", caption: "Default caption");
        var article = await app.CreateArticle("public-media", extra: new
        {
            slug = "public-media",
            title = "Public title",
            summary = "Public summary",
            topic = "Testing",
            tags = new[] { "one" },
            editorialMediaId = media.Id,
            socialMediaId = media.Id,
            body = new object[]
            {
                new { type = "image", mediaId = media.Id, alt = "Context alt", caption = "Context caption" },
                new { type = "paragraph", text = "Text" }
            }
        });
        await app.PublishArticle(article.Id, article.ETag);

        var response = await app.Client.GetAsync("/api/v1/articles/public-media");
        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        var imageBlock = json!["body"]![0]!.AsObject();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://igor.example/articles/public-media", json["canonicalUrl"]!.GetValue<string>());
        Assert.Equal("Public title", json["seoTitle"]!.GetValue<string>());
        Assert.Equal("Public summary", json["seoDescription"]!.GetValue<string>());
        Assert.Equal("Context alt", imageBlock["alt"]!.GetValue<string>());
        Assert.Null(imageBlock["mediaId"]);
        Assert.NotNull(json["image"]);
        Assert.NotNull(json["socialImage"]);
        Assert.Contains("public", response.Headers.CacheControl!.ToString());
    }

    [Theory]
    [InlineData("Writing")]
    [InlineData("Draft")]
    [InlineData("NotListed")]
    [InlineData("Archived")]
    public async Task Every_private_article_state_returns_public_not_found(string target)
    {
        await using var app = new TestApplication();
        var slug = $"private-{target.ToLowerInvariant()}";
        var article = await app.CreateArticle(slug);
        var etag = article.ETag;
        if (target != "Writing")
        {
            var (draft, _) = await app.PatchArticle(article.Id, etag, new { status = "Draft" });
            etag = draft.Headers.ETag!.ToString();
            if (target == "NotListed")
            {
                var changed = await app.PatchArticle(article.Id, etag, new { status = "NotListed" });
                etag = changed.Response.Headers.ETag!.ToString();
            }
            else if (target == "Archived")
            {
                var pub = await app.PatchArticle(article.Id, etag, new { status = "Published" });
                var archived = await app.PatchArticle(
                    article.Id, pub.Response.Headers.ETag!.ToString(), new { status = "Archived" });
                etag = archived.Response.Headers.ETag!.ToString();
            }
        }

        var response = await app.Client.GetAsync($"/api/v1/articles/{slug}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Feed_orders_paginates_and_supports_conditional_get()
    {
        await using var app = new TestApplication();
        foreach (var slug in new[] { "feed-one", "feed-two", "feed-three" })
        {
            var article = await app.CreateArticle(slug);
            (await app.PublishArticle(article.Id, article.ETag)).EnsureSuccessStatusCode();
        }
        await app.CreateArticle("feed-private");

        var first = await app.Client.GetAsync("/api/v1/articles?limit=2");
        var firstJson = await first.Content.ReadFromJsonAsync<JsonObject>();
        var cursor = firstJson!["nextCursor"]!.GetValue<string>();
        var second = await app.Client.GetAsync($"/api/v1/articles?limit=2&cursor={Uri.EscapeDataString(cursor)}");
        var secondJson = await second.Content.ReadFromJsonAsync<JsonObject>();
        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/articles?limit=2");
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag!.ToString());
        var conditional = await app.Client.SendAsync(conditionalRequest);

        Assert.Collection(firstJson["items"]!.AsArray(), _ => { }, _ => { });
        Assert.Single(secondJson!["items"]!.AsArray());
        Assert.Null(secondJson["nextCursor"]);
        Assert.Equal(HttpStatusCode.NotModified, conditional.StatusCode);
        Assert.Empty(await conditional.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("/api/v1/articles?limit=0")]
    [InlineData("/api/v1/articles?limit=51")]
    [InlineData("/api/v1/articles?cursor=garbage")]
    [InlineData("/api/v1/articles?cursor=MXxiYWR8YmFkfGJhZA")]
    public async Task Invalid_feed_inputs_return_bad_request(string path)
    {
        await using var app = new TestApplication();
        var response = await app.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Detail_nonmatching_etag_returns_body_and_matching_returns_304()
    {
        await using var app = new TestApplication();
        var article = await app.CreateArticle("conditional-detail");
        await app.PublishArticle(article.Id, article.ETag);
        var detail = await app.Client.GetAsync("/api/v1/articles/conditional-detail");

        using var mismatch = new HttpRequestMessage(HttpMethod.Get, "/api/v1/articles/conditional-detail");
        mismatch.Headers.TryAddWithoutValidation("If-None-Match", "\"999\"");
        var mismatchResponse = await app.Client.SendAsync(mismatch);
        using var match = new HttpRequestMessage(HttpMethod.Get, "/api/v1/articles/conditional-detail");
        match.Headers.TryAddWithoutValidation("If-None-Match", detail.Headers.ETag!.ToString());
        var matchResponse = await app.Client.SendAsync(match);

        Assert.Equal(HttpStatusCode.OK, mismatchResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotModified, matchResponse.StatusCode);
    }
}
