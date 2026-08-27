using System.Collections.Concurrent;
namespace BlogApi.Infrastructure;

public sealed class BlogStore
{
    public object Gate { get; } = new();
    public ConcurrentDictionary<Guid, MediaAsset> Media { get; } = new();
    public ConcurrentDictionary<Guid, Article> Articles { get; } = new();
    public ConcurrentDictionary<Guid, ArticleSeries> Series { get; } = new();
}
public abstract class Entity
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt
    {
        get; init;
    }
    public DateTimeOffset UpdatedAt
    {
        get; set;
    }
    public DateTimeOffset? DeletedAt
    {
        get; set;
    }
    public long Version { get; set; } = 1;
}
