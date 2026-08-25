using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlogApi.Infrastructure;

public enum PublicationStatus { Writing, Draft, Published, NotListed, Archived }
public sealed class Article : Entity
{
    public string? Slug { get; set; }
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public string? Topic { get; set; }
    public string? SeoTitle { get; set; }
    public string? SeoDescription { get; set; }
    public PublicationStatus Status { get; set; } = PublicationStatus.Writing;
    public DateTimeOffset? PublishedAt { get; set; }
    public JsonArray Body { get; set; } = [];
    public int BodyVersion { get; init; } = 1;
    public List<string> Tags { get; set; } = [];
    public Guid? EditorialMediaId { get; set; }
    public Guid? SocialMediaId { get; set; }
    public HashSet<Guid> MediaIds { get; set; } = [];
    public int ReadingTimeMinutes { get; set; } = 1;
    public List<ArticleRevision> Revisions { get; } = [];
}
public sealed record ArticleRevision(int RevisionNumber, string Operation, DateTimeOffset ChangedAt, string Actor, string CorrelationId, JsonObject Snapshot);
public sealed record CreateArticle(string? Slug, string? Title, string? Summary, string? Topic, string? SeoTitle, string? SeoDescription,
    JsonArray? Body, List<string>? Tags, Guid? EditorialMediaId, Guid? SocialMediaId);
public sealed record PublicImage(string Url, string Alt, int Width, int Height, string? Caption);

public static partial class ArticleEndpoints
{
    public static void MapArticleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/articles", PublicFeed);
        app.MapGet("/api/v1/articles/{slug}", PublicDetail);
        var admin = app.MapGroup("/api/v1/admin/articles");
        admin.MapGet("/", AdminList); admin.MapPost("/", Create); admin.MapGet("/{id:guid}", AdminGet);
        admin.MapPatch("/{id:guid}", Patch).Accepts<JsonObject>("application/merge-patch+json"); admin.MapDelete("/{id:guid}", Delete);
        admin.MapPost("/{id:guid}/restore", Restore); admin.MapGet("/{id:guid}/revisions", Revisions);
        admin.MapGet("/{id:guid}/revisions/{number:int}", Revision);
    }

    private static IResult AdminList(HttpContext c, BlogStore store)
    {
        var (page, size, includeDeleted, error) = Paging.Read(c); if (error is not null) return error;
        var all = store.Articles.Values.Where(a => includeDeleted || a.DeletedAt is null).OrderByDescending(a => a.CreatedAt).ToArray();
        return Results.Ok(new Page<object>(all.Skip((page - 1) * size).Take(size).Select(AdminView).ToArray(), page, size, all.Length, (int)Math.Ceiling(all.Length / (double)size)));
    }
    private static IResult AdminGet(Guid id, HttpContext c, BlogStore store)
    { if (!store.Articles.TryGetValue(id, out var a)) return Problems.Result(c, 404, "Not Found"); c.Response.Headers.ETag = HttpConcurrency.ETag(a.Version); return Results.Ok(AdminView(a)); }
    private static IResult Create(CreateArticle input, HttpContext c, BlogStore store, TimeProvider clock)
    {
        var now = clock.GetUtcNow(); var a = new Article { CreatedAt = now, UpdatedAt = now, Slug = Clean(input.Slug), Title = Clean(input.Title),
            Summary = Clean(input.Summary), Topic = Clean(input.Topic), SeoTitle = Clean(input.SeoTitle), SeoDescription = Clean(input.SeoDescription),
            Body = input.Body?.DeepClone().AsArray() ?? [], Tags = input.Tags ?? [], EditorialMediaId = input.EditorialMediaId, SocialMediaId = input.SocialMediaId };
        var error = Validate(a, store, null); if (error is not null) return Problems.Result(c, 400, "Validation failed", errors: error);
        a.ReadingTimeMinutes = ReadingTime(a.Body); a.MediaIds = CollectMedia(a); AddRevision(a, "Created", c, now); store.Articles[a.Id] = a;
        c.Response.Headers.Location = $"/api/v1/admin/articles/{a.Id}"; c.Response.Headers.ETag = HttpConcurrency.ETag(1); return Results.Created(c.Response.Headers.Location!, AdminView(a));
    }
    private static IResult Patch(Guid id, JsonObject patch, HttpContext c, BlogStore store, TimeProvider clock)
    {
        if (!store.Articles.TryGetValue(id, out var a) || a.DeletedAt is not null) return Problems.Result(c, 404, "Not Found");
        var pre = HttpConcurrency.Require(c, a.Version); if (pre is not null) return pre; var clone = Clone(a);
        try { Apply(clone, patch); } catch (Exception e) { return Problems.Result(c, 400, "Validation failed", e.Message); }
        var errors = Validate(clone, store, a.Id); if (errors is not null) return Problems.Result(c, 400, "Validation failed", errors: errors);
        if (a.PublishedAt is not null && clone.Slug != a.Slug) return Problems.Result(c, 409, "Conflict", "A published slug is immutable.");
        if (!Allowed(a.Status, clone.Status)) return Problems.Result(c, 400, "Validation failed", "The lifecycle transition is not allowed.");
        if (clone.Status == PublicationStatus.Published && (string.IsNullOrEmpty(clone.Slug) || string.IsNullOrEmpty(clone.Title) || string.IsNullOrEmpty(clone.Summary) || clone.Body.Count == 0))
            return Problems.Result(c, 400, "Validation failed", "Published articles require slug, title, summary, and body.");
        if (Equivalent(a, clone)) { c.Response.Headers.ETag = HttpConcurrency.ETag(a.Version); return Results.Ok(AdminView(a)); }
        if (clone.Status == PublicationStatus.Published && a.PublishedAt is null) clone.PublishedAt = clock.GetUtcNow();
        Copy(clone, a); a.Version++; a.UpdatedAt = clock.GetUtcNow(); a.ReadingTimeMinutes = ReadingTime(a.Body); a.MediaIds = CollectMedia(a); AddRevision(a, "Updated", c, a.UpdatedAt);
        c.Response.Headers.ETag = HttpConcurrency.ETag(a.Version); return Results.Ok(AdminView(a));
    }
    private static IResult Delete(Guid id, HttpContext c, BlogStore store, TimeProvider clock)
    {
        if (!store.Articles.TryGetValue(id, out var a)) return Problems.Result(c, 404, "Not Found"); var pre = HttpConcurrency.Require(c, a.Version, a.DeletedAt is not null); if (pre is not null) return pre;
        if (a.DeletedAt is null) { a.DeletedAt = a.UpdatedAt = clock.GetUtcNow(); a.Version++; AddRevision(a, "Deleted", c, a.UpdatedAt); } return Results.NoContent();
    }
    private static IResult Restore(Guid id, HttpContext c, BlogStore store, TimeProvider clock)
    {
        if (!store.Articles.TryGetValue(id, out var a) || a.DeletedAt is null) return Problems.Result(c, 404, "Not Found"); var pre = HttpConcurrency.Require(c, a.Version); if (pre is not null) return pre;
        if (store.Articles.Values.Any(x => x.Id != id && x.DeletedAt is null && string.Equals(x.Slug, a.Slug, StringComparison.OrdinalIgnoreCase))) return Problems.Result(c, 409, "Conflict");
        a.DeletedAt = null; a.Status = PublicationStatus.Draft; a.Version++; a.UpdatedAt = clock.GetUtcNow(); AddRevision(a, "Restored", c, a.UpdatedAt); return Results.NoContent();
    }
    private static IResult Revisions(Guid id, HttpContext c, BlogStore store) => store.Articles.TryGetValue(id, out var a) ? Results.Ok(a.Revisions) : Problems.Result(c, 404, "Not Found");
    private static IResult Revision(Guid id, int number, HttpContext c, BlogStore store) => store.Articles.TryGetValue(id, out var a) && a.Revisions.FirstOrDefault(r => r.RevisionNumber == number) is { } r ? Results.Ok(r) : Problems.Result(c, 404, "Not Found");

    private static IResult PublicFeed(HttpContext c, BlogStore store, IConfiguration config, int limit = 8, string? cursor = null)
    {
        if (limit is < 1 or > 50) return Problems.Result(c, 400, "Validation failed"); DateTimeOffset? beforeTime = null; Guid? beforeId = null;
        if (cursor is not null && !Cursor.TryRead(cursor, config["CursorKey"] ?? config["AdminKey"] ?? "development-only", out beforeTime, out beforeId)) return Problems.Result(c, 400, "Validation failed", "The cursor is invalid.");
        var query = store.Articles.Values.Where(a => a.DeletedAt is null && a.Status == PublicationStatus.Published)
            .OrderByDescending(a => a.PublishedAt).ThenByDescending(a => a.Id).AsEnumerable();
        if (beforeTime is not null) query = query.Where(a => a.PublishedAt < beforeTime || a.PublishedAt == beforeTime && a.Id.CompareTo(beforeId!.Value) < 0);
        var rows = query.Take(limit + 1).ToArray(); var items = rows.Take(limit).Select(a => Summary(a, store)).ToArray(); string? next = null;
        if (rows.Length > limit) { var last = rows[limit - 1]; next = Cursor.Write(last.PublishedAt!.Value, last.Id, config["CursorKey"] ?? config["AdminKey"] ?? "development-only"); }
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { items, next })))).ToLowerInvariant()}\"";
        c.Response.Headers.CacheControl = "public, max-age=60, stale-while-revalidate=300"; if (HttpConcurrency.NotModified(c, etag)) return Results.StatusCode(304); return Results.Ok(new { items, nextCursor = next });
    }
    private static IResult PublicDetail(string slug, HttpContext c, BlogStore store, IConfiguration config)
    {
        var a = store.Articles.Values.SingleOrDefault(x => x.DeletedAt is null && x.Status == PublicationStatus.Published && x.Slug == slug);
        if (a is null) return Problems.Result(c, 404, "Not Found"); var summary = Summary(a, store);
        var result = new { summary.id, summary.slug, summary.title, summary.summary, summary.publishedAt, summary.readingTimeMinutes, summary.topic, summary.image,
            a.UpdatedAt, tags = a.Tags, a.BodyVersion, body = PublicBody(a.Body, store), canonicalUrl = $"{(config["PublicSiteOrigin"] ?? "https://example.invalid").TrimEnd('/')}/articles/{a.Slug}",
            seoTitle = a.SeoTitle ?? a.Title, seoDescription = a.SeoDescription ?? a.Summary, socialImage = Image(a.SocialMediaId, null, null, store) };
        var etag = HttpConcurrency.ETag(a.Version); c.Response.Headers.CacheControl = "public, max-age=60, stale-while-revalidate=300"; if (HttpConcurrency.NotModified(c, etag)) return Results.StatusCode(304); return Results.Ok(result);
    }

    internal static dynamic Summary(Article a, BlogStore store) => new { id = a.Id, slug = a.Slug, title = a.Title, summary = a.Summary, publishedAt = a.PublishedAt,
        readingTimeMinutes = a.ReadingTimeMinutes, topic = a.Topic, image = Image(a.EditorialMediaId, null, null, store) };
    private static PublicImage? Image(Guid? id, string? alt, string? caption, BlogStore store) => id is { } value && store.Media.TryGetValue(value, out var m)
        ? new(m.Url, alt ?? m.Alt, m.Width, m.Height, caption ?? m.Caption) : null;
    private static JsonArray PublicBody(JsonArray body, BlogStore store) { var clone = body.DeepClone().AsArray(); foreach (var node in clone.OfType<JsonObject>().Where(x => x["type"]?.GetValue<string>() == "image"))
        { var id = node["mediaId"]!.GetValue<Guid>(); var image = Image(id, node["alt"]?.GetValue<string>(), node["caption"]?.GetValue<string>(), store)!; node.Remove("mediaId"); node["url"] = image.Url; node["alt"] = image.Alt; node["width"] = image.Width; node["height"] = image.Height; node["caption"] = image.Caption; } return clone; }

    private static object AdminView(Article a) => new { a.Id, a.Slug, a.Title, a.Summary, a.Topic, a.SeoTitle, a.SeoDescription, status = a.Status.ToString(),
        a.PublishedAt, a.BodyVersion, a.Body, a.Tags, a.EditorialMediaId, a.SocialMediaId, a.ReadingTimeMinutes, a.CreatedAt, a.UpdatedAt, a.DeletedAt };
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static Dictionary<string, string[]>? Validate(Article a, BlogStore store, Guid? self)
    {
        var e = new Dictionary<string, string[]>();
        if (a.Slug is { } slug && (slug.Length > 160 || !SlugRegex().IsMatch(slug))) e["slug"] = ["Slug format is invalid."];
        Limits(a.Title, 200, "title"); Limits(a.Summary, 500, "summary"); Limits(a.Topic, 80, "topic"); Limits(a.SeoTitle, 70, "seoTitle"); Limits(a.SeoDescription, 180, "seoDescription");
        if (a.Tags.Count > 20 || a.Tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Trim().Length > 40) || a.Tags.Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != a.Tags.Count) e["tags"] = ["Tags are invalid or duplicated."];
        string? bodyError = null;
        if (a.Body.Count > 500 || Encoding.UTF8.GetByteCount(a.Body.ToJsonString()) > 1024 * 1024 || !ValidBody(a.Body, store, out bodyError)) e["body"] = [bodyError ?? "Body is invalid."];
        if (a.Slug is not null && store.Articles.Values.Any(x => x.Id != self && x.DeletedAt is null && string.Equals(x.Slug, a.Slug, StringComparison.OrdinalIgnoreCase))) e["slug"] = ["Slug is already active."];
        foreach (var id in new[] { a.EditorialMediaId, a.SocialMediaId }.Where(x => x is not null).Cast<Guid>()) if (!store.Media.TryGetValue(id, out var m) || m.DeletedAt is not null) e["mediaId"] = ["Media does not exist or is deleted."];
        return e.Count == 0 ? null : e;
        void Limits(string? value, int max, string key) { if (value is { } v && (v.Length < 1 || v.Length > max)) e[key] = [$"Must contain 1 through {max} characters."]; }
    }
    private static bool ValidBody(JsonArray body, BlogStore store, out string? error)
    {
        error = null;
        foreach (var node in body)
        {
            if (node is not JsonObject b || b["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type)) { error = "Every block requires a type."; return false; }
            bool Text(string name) => b[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s);
            switch (type)
            {
                case "paragraph": case "heading": case "quote": if (!Text("text")) { error = $"{type} text is required."; return false; } break;
                case "code": if (!Text("code")) { error = "Code is required."; return false; } break;
                case "list": if (b["items"] is not JsonArray items || items.Count == 0 || items.Any(x => x is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s))) { error = "List items are required."; return false; } break;
                case "image": if (b["mediaId"] is not JsonValue idv || !idv.TryGetValue<Guid>(out var id) || !store.Media.TryGetValue(id, out var media) || media.DeletedAt is not null) { error = "Image media is missing or deleted."; return false; } break;
                case "table": if (!Text("caption") || b["headers"] is not JsonArray headers || headers.Count == 0 || b["rows"] is not JsonArray rows || rows.Any(r => r is not JsonArray cells || cells.Count != headers.Count)) { error = "Table shape is invalid."; return false; } break;
                default: error = "Unknown block type."; return false;
            }
        }
        return true;
    }
    private static int ReadingTime(JsonArray body)
    {
        var prose = new StringBuilder(); var codeLines = 0;
        foreach (var b in body.OfType<JsonObject>())
        {
            var type = b["type"]?.GetValue<string>(); if (type == "code") { codeLines += b["code"]!.GetValue<string>().Split('\n').Count(x => !string.IsNullOrWhiteSpace(x)); continue; }
            foreach (var key in new[] { "text", "caption" }) if (b[key] is JsonValue v && v.TryGetValue<string>(out var s)) prose.Append(' ').Append(s);
            foreach (var key in new[] { "items", "headers" }) if (b[key] is JsonArray values) foreach (var v in values) prose.Append(' ').Append(v?.GetValue<string>());
            if (b["rows"] is JsonArray rows) foreach (var row in rows.OfType<JsonArray>()) foreach (var cell in row) prose.Append(' ').Append(cell?.GetValue<string>());
        }
        var words = WordRegex().Matches(prose.ToString()).Count; return Math.Max(1, (int)Math.Ceiling(words / 200d + codeLines / 12d));
    }
    private static HashSet<Guid> CollectMedia(Article a)
    { var result = new HashSet<Guid>(); if (a.EditorialMediaId is { } e) result.Add(e); if (a.SocialMediaId is { } s) result.Add(s); foreach (var b in a.Body.OfType<JsonObject>().Where(x => x["type"]?.GetValue<string>() == "image")) result.Add(b["mediaId"]!.GetValue<Guid>()); return result; }
    private static void Apply(Article a, JsonObject p)
    {
        string? S(string key, string? old) => !p.ContainsKey(key) ? old : p[key]?.GetValue<string>() is { } s ? Clean(s) : null;
        a.Slug = S("slug", a.Slug); a.Title = S("title", a.Title); a.Summary = S("summary", a.Summary); a.Topic = S("topic", a.Topic); a.SeoTitle = S("seoTitle", a.SeoTitle); a.SeoDescription = S("seoDescription", a.SeoDescription);
        if (p["status"] is JsonValue sv) a.Status = Enum.Parse<PublicationStatus>(sv.GetValue<string>(), true);
        if (p.ContainsKey("body")) a.Body = p["body"]?.DeepClone().AsArray() ?? [];
        if (p.ContainsKey("tags")) a.Tags = p["tags"]?.AsArray().Select(x => x!.GetValue<string>().Trim()).ToList() ?? [];
        if (p.ContainsKey("editorialMediaId")) a.EditorialMediaId = p["editorialMediaId"]?.GetValue<Guid>(); if (p.ContainsKey("socialMediaId")) a.SocialMediaId = p["socialMediaId"]?.GetValue<Guid>();
    }
    private static bool Allowed(PublicationStatus from, PublicationStatus to) => from == to || (from, to) is
        (PublicationStatus.Writing, PublicationStatus.Draft) or (PublicationStatus.Draft, PublicationStatus.Writing or PublicationStatus.Published or PublicationStatus.NotListed) or
        (PublicationStatus.Published, PublicationStatus.NotListed or PublicationStatus.Archived) or (PublicationStatus.NotListed, PublicationStatus.Draft or PublicationStatus.Published or PublicationStatus.Archived) or
        (PublicationStatus.Archived, PublicationStatus.Draft);
    private static Article Clone(Article a) => new() { Id = a.Id, CreatedAt = a.CreatedAt, UpdatedAt = a.UpdatedAt, DeletedAt = a.DeletedAt, Version = a.Version,
        Slug = a.Slug, Title = a.Title, Summary = a.Summary, Topic = a.Topic, SeoTitle = a.SeoTitle, SeoDescription = a.SeoDescription, Status = a.Status, PublishedAt = a.PublishedAt,
        Body = a.Body.DeepClone().AsArray(), Tags = [.. a.Tags], EditorialMediaId = a.EditorialMediaId, SocialMediaId = a.SocialMediaId };
    private static void Copy(Article from, Article to) { to.Slug = from.Slug; to.Title = from.Title; to.Summary = from.Summary; to.Topic = from.Topic; to.SeoTitle = from.SeoTitle; to.SeoDescription = from.SeoDescription; to.Status = from.Status; to.PublishedAt = from.PublishedAt; to.Body = from.Body; to.Tags = from.Tags; to.EditorialMediaId = from.EditorialMediaId; to.SocialMediaId = from.SocialMediaId; }
    private static bool Equivalent(Article a, Article b) => JsonSerializer.Serialize(AdminView(a)) == JsonSerializer.Serialize(AdminView(b));
    private static void AddRevision(Article a, string operation, HttpContext c, DateTimeOffset at) => a.Revisions.Add(new(a.Revisions.Count + 1, operation, at, "site-owner", c.TraceIdentifier, JsonSerializer.SerializeToNode(AdminView(a))!.AsObject()));
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")] private static partial Regex SlugRegex();
    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)*")] private static partial Regex WordRegex();
}

internal static class Cursor
{
    public static string Write(DateTimeOffset time, Guid id, string key) { var data = $"1|{time:O}|{id}"; var sig = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data))).ToLowerInvariant(); return Base64Url(Encoding.UTF8.GetBytes($"{data}|{sig}")); }
    public static bool TryRead(string cursor, string key, out DateTimeOffset? time, out Guid? id)
    {
        time = null; id = null; try { var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/').PadRight((cursor.Length + 3) / 4 * 4, '='))); var p = value.Split('|');
            if (p.Length != 4 || p[0] != "1" || !DateTimeOffset.TryParseExact(p[1], "O", null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) || !Guid.TryParse(p[2], out var g)) return false;
            var data = string.Join('|', p[..3]); var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data)); if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(p[3]))) return false; time = t; id = g; return true; } catch { return false; }
    }
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
