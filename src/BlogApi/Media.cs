using System.Security.Cryptography;
using BlogApi.Infrastructure;

public sealed class MediaAsset : Entity
{
    public required string OriginalFileName
    {
        get; init;
    }
    public required string InputType
    {
        get; init;
    }
    public required string OutputType
    {
        get; init;
    }
    public required string Digest
    {
        get; init;
    }
    public required string Url
    {
        get; init;
    }
    public required string Alt
    {
        get; set;
    }
    public string? Caption
    {
        get; set;
    }
    public long ByteSize
    {
        get; init;
    }
    public int Width
    {
        get; init;
    }
    public int Height
    {
        get; init;
    }
}

public interface IMediaStorage
{
    Task<string> Upload(Guid id, string digest, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken);
    Task<bool> Exists(string url, string digest, CancellationToken cancellationToken);
}
public sealed class InMemoryMediaStorage : IMediaStorage
{
    private readonly Dictionary<string, byte[]> _objects = [];
    public Task<string> Upload(Guid id, string digest, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        var url = $"https://assets.invalid/blog/media/{id}/{digest}";
        _objects[url] = content.ToArray();
        return Task.FromResult(url);
    }
    public Task<bool> Exists(string url, string digest, CancellationToken cancellationToken)
    {
        return Task.FromResult(_objects.ContainsKey(url));
    }
}

public sealed record MediaPatch(string? Alt, string? Caption, bool ClearCaption = false);

public static class MediaEndpoints
{
    private const long MaxBytes = 10 * 1024 * 1024;
    public static void MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin/media");
        admin.MapGet("/", List);
        admin.MapPost("/", Upload).DisableAntiforgery();
        admin.MapGet("/{id:guid}", Get);
        admin.MapPatch("/{id:guid}", Patch);
        admin.MapDelete("/{id:guid}", Delete);
        admin.MapPost("/{id:guid}/restore", Restore);
    }

    private static IResult List(HttpContext c, BlogStore store)
    {
        var (page, size, includeDeleted, error) = Paging.Read(c);
        if (error is not null)
        {
            return error;
        }

        var query = store.Media.Values.Where(x => includeDeleted || x.DeletedAt is null).OrderByDescending(x => x.CreatedAt).ToArray();
        return Results.Ok(new Page<MediaAsset>(query.Skip((page - 1) * size).Take(size).ToArray(), page, size, query.Length, (int)Math.Ceiling(query.Length / (double)size)));
    }
    private static IResult Get(Guid id, HttpContext c, BlogStore store)
    {
        if (!store.Media.TryGetValue(id, out var item))
        {
            return Problems.Result(c, 404, "Not Found");
        }

        c.Response.Headers.ETag = HttpConcurrency.ETag(item.Version);
        return Results.Ok(item);
    }

    private static async Task<IResult> Upload(HttpContext c, BlogStore store, IMediaStorage storage, TimeProvider clock)
    {
        if (!c.Request.HasFormContentType)
        {
            return Problems.Result(c, 400, "Validation failed", "multipart/form-data is required.");
        }

        var form = await c.Request.ReadFormAsync(c.RequestAborted);
        var file = form.Files.GetFile("file");
        var alt = form["alt"].ToString().Trim();
        var decorative = bool.TryParse(form["decorative"], out var d) && d;
        var caption = form["caption"].ToString().Trim();
        if (file is null || file.Length == 0 || file.Length > MaxBytes || alt.Length > 500 || (!decorative && alt.Length == 0) || caption.Length > 1000)
        {
            return Problems.Result(c, 400, "Validation failed", "File, alt text, or caption is invalid.");
        }

        if (file.ContentType is not ("image/jpeg" or "image/png" or "image/webp"))
        {
            return Problems.Result(c, 400, "Validation failed", "Unsupported image type.");
        }

        await using var input = file.OpenReadStream();
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, c.RequestAborted);
        var bytes = memory.ToArray();
        if (!ImageProbe.Matches(bytes, file.ContentType))
        {
            return Problems.Result(c, 400, "Validation failed", "The file signature does not match its media type.");
        }

        var id = Guid.CreateVersion7();
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var now = clock.GetUtcNow();
        var url = await storage.Upload(id, digest, bytes, file.ContentType, c.RequestAborted);
        var asset = new MediaAsset
        {
            Id = id,
            OriginalFileName = Path.GetFileName(file.FileName),
            InputType = file.ContentType,
            OutputType = file.ContentType,
            Digest = digest,
            Url = url,
            Alt = alt,
            Caption = string.IsNullOrEmpty(caption) ? null : caption,
            ByteSize = bytes.Length,
            Width = 1,
            Height = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        store.Media[id] = asset;
        c.Response.Headers.Location = $"/api/v1/admin/media/{id}";
        c.Response.Headers.ETag = HttpConcurrency.ETag(1);
        return Results.Created(c.Response.Headers.Location!, asset);
    }
    private static IResult Patch(Guid id, MediaPatch patch, HttpContext c, BlogStore store, TimeProvider clock)
    {
        if (!store.Media.TryGetValue(id, out var item) || item.DeletedAt is not null)
        {
            return Problems.Result(c, 404, "Not Found");
        }

        var pre = HttpConcurrency.Require(c, item.Version);
        if (pre is not null)
        {
            return pre;
        }

        var alt = patch.Alt?.Trim();
        if (alt is { Length: > 500 })
        {
            return Problems.Result(c, 400, "Validation failed");
        }

        var caption = patch.Caption?.Trim();
        if (caption is { Length: > 1000 })
        {
            return Problems.Result(c, 400, "Validation failed");
        }

        if (alt == item.Alt && (!patch.ClearCaption ? caption ?? item.Caption : null) == item.Caption)
        {
            c.Response.Headers.ETag = HttpConcurrency.ETag(item.Version);
            return Results.Ok(item);
        }
        if (alt is not null)
        {
            item.Alt = alt;
        }

        if (patch.ClearCaption)
        {
            item.Caption = null;
        }
        else if (patch.Caption is not null)
        {
            item.Caption = caption;
        }

        item.Version++;
        item.UpdatedAt = clock.GetUtcNow();
        c.Response.Headers.ETag = HttpConcurrency.ETag(item.Version);
        return Results.Ok(item);
    }
    private static IResult Delete(Guid id, HttpContext c, BlogStore store, TimeProvider clock)
    {
        if (!store.Media.TryGetValue(id, out var item))
        {
            return Problems.Result(c, 404, "Not Found");
        }

        var pre = HttpConcurrency.Require(c, item.Version, item.DeletedAt is not null);
        if (pre is not null)
        {
            return pre;
        }

        if (item.DeletedAt is not null)
        {
            return Results.NoContent();
        }

        if (store.Articles.Values.Any(a => a.DeletedAt is null && a.MediaIds.Contains(id)))
        {
            return Problems.Result(c, 409, "Conflict", "The media asset is referenced by an active article.");
        }

        item.DeletedAt = item.UpdatedAt = clock.GetUtcNow();
        item.Version++;
        return Results.NoContent();
    }
    private static async Task<IResult> Restore(Guid id, HttpContext c, BlogStore store, IMediaStorage storage, TimeProvider clock)
    {
        if (!store.Media.TryGetValue(id, out var item) || item.DeletedAt is null)
        {
            return Problems.Result(c, 404, "Not Found");
        }

        var pre = HttpConcurrency.Require(c, item.Version);
        if (pre is not null)
        {
            return pre;
        }

        if (!await storage.Exists(item.Url, item.Digest, c.RequestAborted))
        {
            return Problems.Result(c, 409, "Conflict", "The stored object is unavailable.");
        }

        item.DeletedAt = null;
        item.UpdatedAt = clock.GetUtcNow();
        item.Version++;
        return Results.NoContent();
    }
}

internal static class ImageProbe
{
    public static bool Matches(byte[] b, string type)
    {
        return type switch
        {
            "image/jpeg" => b.Length > 3 && b[0] == 0xff && b[1] == 0xd8 && b[^2] == 0xff && b[^1] == 0xd9,
            "image/png" => b.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/webp" => b.Length > 12 && b.AsSpan(0, 4).SequenceEqual("RIFF"u8) && b.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
    }
}
