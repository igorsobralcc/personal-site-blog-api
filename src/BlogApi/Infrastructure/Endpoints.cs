namespace BlogApi.Infrastructure;
public static class Endpoints
{
    public static IEndpointRouteBuilder MapBlogApi(this IEndpointRouteBuilder app)
    { app.MapMediaEndpoints(); app.MapArticleEndpoints(); app.MapSeriesEndpoints(); return app; }
}
