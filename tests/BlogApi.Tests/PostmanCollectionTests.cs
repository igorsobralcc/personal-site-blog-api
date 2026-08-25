using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlogApi.Tests;

public sealed partial class PostmanCollectionTests
{
    private static readonly string CollectionPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs",
        "PersonalSite.Blog.Api.postman_collection.json"));

    [Fact]
    public void Collection_is_valid_and_covers_every_route_family()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CollectionPath));
        var root = document.RootElement;
        Assert.Equal(
            "https://schema.getpostman.com/json/collection/v2.1.0/collection.json",
            root.GetProperty("info").GetProperty("schema").GetString());

        var variables = root.GetProperty("variable").EnumerateArray()
            .Select(value => value.GetProperty("key").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var json = root.GetRawText();
        var references = VariableRegex().Matches(json).Select(match => match.Groups[1].Value).ToHashSet();
        Assert.Empty(references.Except(variables));

        foreach (var route in new[]
        {
            "/health/live", "/health/ready", "/api/v1/articles",
            "/api/v1/admin/articles", "/api/v1/admin/media",
            "/api/v1/admin/series", "/api/v1/series/"
        })
            Assert.Contains(route, json, StringComparison.Ordinal);

        var methods = EnumerateRequests(root.GetProperty("item"))
            .Select(request => request.GetProperty("method").GetString())
            .ToHashSet();
        foreach (var method in new[] { "GET", "POST", "PATCH", "DELETE" })
            Assert.Contains(method, methods);
    }

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9]*)\}\}")]
    private static partial Regex VariableRegex();

    private static IEnumerable<JsonElement> EnumerateRequests(JsonElement items)
    {
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("request", out var request)) yield return request;
            if (item.TryGetProperty("item", out var children))
                foreach (var child in EnumerateRequests(children)) yield return child;
        }
    }
}
