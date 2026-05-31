using System.Security.Cryptography;
using LucidCartographer.Data;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace LucidCartographer.Endpoints;

public static class PoiImageEndpoints
{
    /// <summary>
    /// Serves image bytes stored in the PoiImage table. Used by PoiDetailPane
    /// to render scraped Google Maps thumbnails — we persist the bytes rather
    /// than hotlinking the signed googleusercontent URLs (which Google blocks
    /// cross-origin and expires in ~minutes). Auth middleware gates this
    /// endpoint behind the same cookie as the rest of the app.
    ///
    /// The URL (/api/poi-image/{poiId}) is stable across re-enrichments, so we
    /// use an ETag + Cache-Control:no-cache to keep the browser honest: it
    /// revalidates on every render and only re-downloads when the bytes have
    /// actually changed (e.g. after enrichment swaps the photo). A POI edit
    /// that leaves the image untouched yields the same ETag → 304, no refetch.
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

            // Strong ETag derived from the bytes — changes iff the photo does.
            var tag = '"' + Convert.ToHexString(SHA256.HashData(image.Data)) + '"';
            var etag = new EntityTagHeaderValue(tag);

            // Defence-in-depth: the bytes are already validated as a raster image
            // (content-type allowlist + magic-byte sniff on ingest), but tell the
            // browser never to MIME-sniff this response into something executable,
            // and render it inline as a file rather than as a document.
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";

            // Force revalidation rather than heuristic caching, so a swapped
            // photo shows immediately instead of lingering as a stale cache hit.
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
