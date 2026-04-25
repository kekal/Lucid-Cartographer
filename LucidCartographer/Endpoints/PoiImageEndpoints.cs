using LucidCartographer.Data;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Endpoints;

public static class PoiImageEndpoints
{
    /// <summary>
    /// Serves image bytes stored in the Poi.ImageData column. Used by
    /// PoiDetailPane to render scraped Google Maps thumbnails — we persist the
    /// bytes rather than hotlinking the signed googleusercontent URLs (which
    /// Google blocks cross-origin and expires in ~minutes). Auth middleware
    /// gates this endpoint behind the same cookie as the rest of the app.
    /// </summary>
    public static IEndpointRouteBuilder MapPoiImageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/poi-image/{id:int}", async (int id, IDbContextFactory<AppDbContext> dbFactory) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var image = await db.PoiImages
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.PoiId == id);
            if (image is null || image.Data.Length == 0)
                return Results.NotFound();
            return Results.File(image.Data, image.ContentType ?? "image/jpeg");
        });

        return endpoints;
    }
}
