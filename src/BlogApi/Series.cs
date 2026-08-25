using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlogApi.Infrastructure;

public sealed class ArticleSeries : Entity
{
    public string? Slug { get; set; }
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public PublicationStatus Status { get; set; } = PublicationStatus.Writing;
    public DateTimeOffset? PublishedAt { get; set; }
    public List<Guid> ArticleIds { get; set; } = [];
    public List<SeriesRevision> Revisions { get; } = [];
}
public sealed record SeriesRevision(int RevisionNumber, string Operation, DateTimeOffset ChangedAt, string Actor, string CorrelationId, JsonObject Snapshot);
public sealed record CreateSeries(string? Slug, string? Title, string? Summary, List<Guid>? ArticleIds);

public static partial class SeriesEndpoints
{
    public static void MapSeriesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/series/{slug}", PublicGet);
        var admin = app.MapGroup("/api/v1/admin/series"); admin.MapGet("/", List); admin.MapPost("/", Create); admin.MapGet("/{id:guid}", Get);
        admin.MapPatch("/{id:guid}", Patch).Accepts<JsonObject>("application/merge-patch+json"); admin.MapDelete("/{id:guid}", Delete); admin.MapPost("/{id:guid}/restore", Restore);
        admin.MapGet("/{id:guid}/revisions", Revisions); admin.MapGet("/{id:guid}/revisions/{number:int}", Revision);
    }
    private static IResult List(HttpContext c, BlogStore store) { var (p, s, d, e) = Paging.Read(c); if (e is not null) return e; var all = store.Series.Values.Where(x => d || x.DeletedAt is null).OrderByDescending(x => x.CreatedAt).ToArray(); return Results.Ok(new Page<object>(all.Skip((p - 1) * s).Take(s).Select(View).ToArray(), p, s, all.Length, (int)Math.Ceiling(all.Length / (double)s))); }
    private static IResult Get(Guid id, HttpContext c, BlogStore store) { if (!store.Series.TryGetValue(id, out var x)) return Problems.Result(c, 404, "Not Found"); c.Response.Headers.ETag = HttpConcurrency.ETag(x.Version); return Results.Ok(View(x)); }
    private static IResult Create(CreateSeries input, HttpContext c, BlogStore store, TimeProvider clock)
    {
        var now = clock.GetUtcNow(); var x = new ArticleSeries { CreatedAt = now, UpdatedAt = now, Slug = Clean(input.Slug), Title = Clean(input.Title), Summary = Clean(input.Summary), ArticleIds = input.ArticleIds ?? [] };
        var error = Validate(x, store, null); if (error is not null) return Problems.Result(c, 400, "Validation failed", errors: error); AddRevision(x, "Created", c, now); store.Series[x.Id] = x;
        c.Response.Headers.Location = $"/api/v1/admin/series/{x.Id}"; c.Response.Headers.ETag = HttpConcurrency.ETag(1); return Results.Created(c.Response.Headers.Location!, View(x));
    }
    private static IResult Patch(Guid id, JsonObject patch, HttpContext c, BlogStore store, TimeProvider clock)
    {
        if (!store.Series.TryGetValue(id, out var x) || x.DeletedAt is not null) return Problems.Result(c, 404, "Not Found"); var pre = HttpConcurrency.Require(c, x.Version); if (pre is not null) return pre;
        var clone = Clone(x); try { Apply(clone, patch); } catch (Exception e) { return Problems.Result(c, 400, "Validation failed", e.Message); }
        var error = Validate(clone, store, id); if (error is not null) return Problems.Result(c, 400, "Validation failed", errors: error);
        if (x.PublishedAt is not null && clone.Slug != x.Slug) return Problems.Result(c, 409, "Conflict", "A published slug is immutable."); if (!Allowed(x.Status, clone.Status)) return Problems.Result(c, 400, "Validation failed", "Lifecycle transition is invalid.");
        if (clone.Status == PublicationStatus.Published && (clone.Slug is null || clone.Title is null)) return Problems.Result(c, 400, "Validation failed", "Published series require slug and title.");
        if (JsonSerializer.Serialize(View(x)) == JsonSerializer.Serialize(View(clone))) { c.Response.Headers.ETag = HttpConcurrency.ETag(x.Version); return Results.Ok(View(x)); }
        if (clone.Status == PublicationStatus.Published && x.PublishedAt is null) clone.PublishedAt = clock.GetUtcNow(); Copy(clone, x); x.Version++; x.UpdatedAt = clock.GetUtcNow(); AddRevision(x, "Updated", c, x.UpdatedAt); c.Response.Headers.ETag = HttpConcurrency.ETag(x.Version); return Results.Ok(View(x));
    }
    private static IResult Delete(Guid id, HttpContext c, BlogStore store, TimeProvider clock) { if (!store.Series.TryGetValue(id, out var x)) return Problems.Result(c, 404, "Not Found"); var pre = HttpConcurrency.Require(c, x.Version, x.DeletedAt is not null); if (pre is not null) return pre; if (x.DeletedAt is null) { x.DeletedAt = x.UpdatedAt = clock.GetUtcNow(); x.Version++; AddRevision(x, "Deleted", c, x.UpdatedAt); } return Results.NoContent(); }
    private static IResult Restore(Guid id, HttpContext c, BlogStore store, TimeProvider clock) { if (!store.Series.TryGetValue(id, out var x) || x.DeletedAt is null) return Problems.Result(c, 404, "Not Found"); var pre = HttpConcurrency.Require(c, x.Version); if (pre is not null) return pre; if (store.Series.Values.Any(y => y.Id != id && y.DeletedAt is null && string.Equals(y.Slug, x.Slug, StringComparison.OrdinalIgnoreCase))) return Problems.Result(c, 409, "Conflict"); x.DeletedAt = null; x.Status = PublicationStatus.Draft; x.Version++; x.UpdatedAt = clock.GetUtcNow(); AddRevision(x, "Restored", c, x.UpdatedAt); return Results.NoContent(); }
    private static IResult Revisions(Guid id, HttpContext c, BlogStore store) => store.Series.TryGetValue(id, out var x) ? Results.Ok(x.Revisions) : Problems.Result(c, 404, "Not Found");
    private static IResult Revision(Guid id, int number, HttpContext c, BlogStore store) => store.Series.TryGetValue(id, out var x) && x.Revisions.FirstOrDefault(r => r.RevisionNumber == number) is { } r ? Results.Ok(r) : Problems.Result(c, 404, "Not Found");
    private static IResult PublicGet(string slug, HttpContext c, BlogStore store)
    {
        var x = store.Series.Values.SingleOrDefault(y => y.DeletedAt is null && y.Status == PublicationStatus.Published && y.Slug == slug); if (x is null) return Problems.Result(c, 404, "Not Found");
        var articles = x.ArticleIds.Select(id => store.Articles.GetValueOrDefault(id)).Where(a => a is { DeletedAt: null, Status: PublicationStatus.Published }).OrderBy(a => a!.CreatedAt).ThenBy(a => a!.Id).Select(a => ArticleEndpoints.Summary(a!, store)).ToArray();
        var result = new { x.Id, x.Slug, x.Title, x.Summary, x.PublishedAt, articles }; var etag = HttpConcurrency.ETag(x.Version + articles.Sum(a => (long)a.id.GetHashCode())); c.Response.Headers.CacheControl = "public, max-age=60, stale-while-revalidate=300"; if (HttpConcurrency.NotModified(c, etag)) return Results.StatusCode(304); return Results.Ok(result);
    }
    private static object View(ArticleSeries x) => new { x.Id, x.Slug, x.Title, x.Summary, status = x.Status.ToString(), x.ArticleIds, x.PublishedAt, x.CreatedAt, x.UpdatedAt, x.DeletedAt };
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static Dictionary<string, string[]>? Validate(ArticleSeries x, BlogStore store, Guid? self) { var e = new Dictionary<string, string[]>(); if (x.Slug is { } s && (s.Length > 160 || !Slug().IsMatch(s))) e["slug"] = ["Slug is invalid."]; if (x.Title is { Length: > 200 }) e["title"] = ["Title is too long."]; if (x.Summary is { Length: > 500 }) e["summary"] = ["Summary is too long."]; if (x.ArticleIds.Distinct().Count() != x.ArticleIds.Count || x.ArticleIds.Any(id => !store.Articles.TryGetValue(id, out var a) || a.DeletedAt is not null)) e["articleIds"] = ["Article IDs must be unique and active."]; if (x.Slug is not null && store.Series.Values.Any(y => y.Id != self && y.DeletedAt is null && string.Equals(y.Slug, x.Slug, StringComparison.OrdinalIgnoreCase))) e["slug"] = ["Slug is already active."]; return e.Count == 0 ? null : e; }
    private static void Apply(ArticleSeries x, JsonObject p) { if (p.ContainsKey("slug")) x.Slug = Clean(p["slug"]?.GetValue<string>()); if (p.ContainsKey("title")) x.Title = Clean(p["title"]?.GetValue<string>()); if (p.ContainsKey("summary")) x.Summary = Clean(p["summary"]?.GetValue<string>()); if (p["status"] is JsonValue status) x.Status = Enum.Parse<PublicationStatus>(status.GetValue<string>(), true); if (p.ContainsKey("articleIds")) x.ArticleIds = p["articleIds"]?.AsArray().Select(v => v!.GetValue<Guid>()).ToList() ?? []; }
    private static bool Allowed(PublicationStatus from, PublicationStatus to) => from == to || (from, to) is (PublicationStatus.Writing, PublicationStatus.Draft) or (PublicationStatus.Draft, PublicationStatus.Writing or PublicationStatus.Published or PublicationStatus.NotListed) or (PublicationStatus.Published, PublicationStatus.NotListed or PublicationStatus.Archived) or (PublicationStatus.NotListed, PublicationStatus.Draft or PublicationStatus.Published or PublicationStatus.Archived) or (PublicationStatus.Archived, PublicationStatus.Draft);
    private static ArticleSeries Clone(ArticleSeries x) => new() { Id = x.Id, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt, DeletedAt = x.DeletedAt, Version = x.Version, Slug = x.Slug, Title = x.Title, Summary = x.Summary, Status = x.Status, PublishedAt = x.PublishedAt, ArticleIds = [.. x.ArticleIds] };
    private static void Copy(ArticleSeries f, ArticleSeries t) { t.Slug = f.Slug; t.Title = f.Title; t.Summary = f.Summary; t.Status = f.Status; t.PublishedAt = f.PublishedAt; t.ArticleIds = f.ArticleIds; }
    private static void AddRevision(ArticleSeries x, string operation, HttpContext c, DateTimeOffset at) => x.Revisions.Add(new(x.Revisions.Count + 1, operation, at, "site-owner", c.TraceIdentifier, JsonSerializer.SerializeToNode(View(x))!.AsObject()));
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")] private static partial Regex Slug();
}
