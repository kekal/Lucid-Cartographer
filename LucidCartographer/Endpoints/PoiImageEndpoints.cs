using System.Security.Cryptography;
using LucidCartographer.Data;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace LucidCartographer.Endpoints;

public static class PoiImageEndpoints
{
    /// <summary>
    /// Serves persisted image bytes for POI detail pane; avoids cross-origin
    /// blocking and expiry of signed Google URLs. Uses ETag-based revalidation
    /// to skip downloads when the image is unchanged (304 on re-enrichment).
    /// </summary>
    public static IEndpointRouteBuilder MapPoiImageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/poi-image/{id:int}", async (int id, HttpContext http, IDbContextFactory<AppDbContext> dbFactory) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var image = await db.PoiImages
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.PoiId == id);
            if (image is null || image.Data.Length == 0)
            {
                return Results.NotFound();
            }

            var tag = '"' + Convert.ToHexString(SHA256.HashData(image.Data)) + '"';
            var etag = new EntityTagHeaderValue(tag);

            // Prevent MIME-sniff to executable and force inline rendering.
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";

            // No-cache forces revalidation on every render; swapped photos show
            // immediately instead of lingering as stale cache hits.
            var responseHeaders = new ResponseHeaders(http.Response.Headers)
            {
                CacheControl = new CacheControlHeaderValue { NoCache = true },
                ContentDisposition = new ContentDispositionHeaderValue("inline")
            };

            var requestEtags = http.Request.GetTypedHeaders().IfNoneMatch;
            if (requestEtags.Any(e => e.Tag.Equals("*") || e.Tag.Equals(tag)))
            {
                responseHeaders.ETag = etag;
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.File(image.Data, image.ContentType ?? "image/jpeg", entityTag: etag);
        });

        return endpoints;
    }
}
