using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace BlogApi.Infrastructure;

public sealed class AdminKeyMiddleware(RequestDelegate next, IConfiguration configuration)
{
    public async Task Invoke(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1/admin"))
        {
            await next(context);
            return;
        }
        var expected = configuration["AdminKey"];
        var supplied = context.Request.Headers["X-Admin-Key"].ToString();
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(supplied) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied)))
        {
            await Problems.Write(context, 401, "Unauthorized", "An administrative credential is required.");
            return;
        }
        await next(context);
    }
}

public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        await next(context);
        logger.LogInformation("HTTP {Method} {Path} returned {StatusCode} in {ElapsedMs}ms trace {TraceId}",
            context.Request.Method, context.Request.Path, context.Response.StatusCode,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, context.TraceIdentifier);
    }
}

public static class Problems
{
    public static IResult Result(HttpContext context, int status, string title, string? detail = null,
        IDictionary<string, string[]>? errors = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = $"https://httpstatuses.com/{status}"
        };
        problem.Extensions["traceId"] = context.TraceIdentifier;
        if (errors is not null)
        {
            problem.Extensions["errors"] = errors;
        }

        return Results.Problem(problem);
    }
    public static Task Write(HttpContext context, int status, string title, string? detail = null)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsync(
            JsonSerializer.Serialize(new
            {
                type = $"https://httpstatuses.com/{status}",
                title,
                status,
                detail,
                traceId = context.TraceIdentifier
            }),
            context.RequestAborted);
    }
}

public static class HttpConcurrency
{
    public static string ETag(long version)
    {
        return $"\"{version}\"";
    }

    public static IResult? Require(HttpContext context, long version, bool ignoreValue = false)
    {
        if (!context.Request.Headers.TryGetValue("If-Match", out var values))
        {
            return Problems.Result(context, 428, "Precondition Required", "A strong If-Match header is required.");
        }

        if (values.Count != 1 || (!ignoreValue && values.ToString() != ETag(version)))
        {
            return Problems.Result(context, 412, "Precondition Failed", "The supplied entity tag is invalid or stale.");
        }

        return null;
    }
    public static bool NotModified(HttpContext context, string etag)
    {
        context.Response.Headers.ETag = etag;
        return context.Request.Headers.IfNoneMatch.ToString() == etag;
    }
}

public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int TotalItems, int TotalPages);
public static class Paging
{
    public static (int Page, int Size, bool IncludeDeleted, IResult? Error) Read(HttpContext context)
    {
        var pt = context.Request.Headers["X-Page"].ToString();
        var st = context.Request.Headers["X-Page-Size"].ToString();
        var page = 1;
        if (!string.IsNullOrEmpty(pt) && !int.TryParse(pt, out page))
        {
            page = 0;
        }

        var size = 20;
        if (!string.IsNullOrEmpty(st) && !int.TryParse(st, out size))
        {
            size = 0;
        }

        var deleted = bool.TryParse(context.Request.Headers["X-Include-Deleted"], out var d) && d;
        if (page < 1 || size is < 1 or > 100)
        {
            return (page, size, deleted,
            Problems.Result(context, 400, "Validation failed", errors: new Dictionary<string, string[]> { ["headers"] = ["Page must be positive and page size must be between 1 and 100."] }));
        }

        return (page, size, deleted, null);
    }
}
