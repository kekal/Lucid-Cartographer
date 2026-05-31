using LucidCartographer.Configuration;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services.Mcp;

/// <summary>A validated, in-memory image ready to be stored. Url is the source URL to record on the POI.</summary>
public sealed record DownloadedImage(byte[] Bytes, string ContentType, string Url);

/// <summary>
/// Downloads a manually-supplied image URL and stores the bytes as a POI's
/// photo (the <see cref="PoiImage"/> row that <c>/api/poi-image/{id}</c> serves).
/// Used by the <c>create_poi</c> / <c>edit_poi</c> MCP tools.
///
/// The UI renders a photo only when BOTH <see cref="Poi.ImageUrl"/> is set AND
/// bytes exist in PoiImages, so a manual photo must store the bytes — a bare URL
/// string would render as a broken image. <see cref="ApplyAsync"/> therefore
/// writes the bytes AND <see cref="Poi.ImageUrl"/> on the SAME context in a single
/// SaveChanges, so the two can never drift apart.
///
/// The flow is split into <see cref="DownloadAsync"/> (network + validation, no DB)
/// and <see cref="ApplyAsync"/> (DB write) so callers can order side effects safely:
/// download/validate first (a bad URL throws before anything is persisted), then
/// validate+persist the rest of the entity, then apply the image last.
///
/// SECURITY (residual risk): this fetches an arbitrary caller-supplied URL
/// server-side, which is SSRF-shaped (a caller could aim it at a cloud metadata
/// endpoint or an internal host). We intentionally do NOT block private/loopback
/// ranges by default because self-hosting images on the LAN is a legitimate use
/// case for this personal tool. Mitigations in place: http/https only on the first
/// hop, redirects capped at 3 (see PoiServicesExtensions) though NOT re-validated
/// per hop (a redirect can still reach an internal host — accepted, same posture as
/// a direct fetch), a response size cap, a request timeout, and content-type AND
/// magic-byte validation. A future opt-in <c>Images:BlockPrivateNetworks</c> flag
/// could resolve the host and reject private IPs if stricter isolation is wanted.
/// </summary>
public static class ImageDownloadHelper
{
    /// <summary>Maximum image size accepted (bytes).</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>Matches the ImageUrl column cap (AppDbContext / ValidatePoi).</summary>
    private const int MaxUrlLength = 2048;

    /// <summary>
    /// Downloads and validates <paramref name="imageUrl"/> WITHOUT touching the DB.
    /// Returns <c>null</c> when the URL is blank (caller intent: clear the photo);
    /// otherwise returns the validated bytes. Throws <see cref="ArgumentException"/>
    /// on an invalid URL, an unreachable host/timeout, or a payload that is not a
    /// real image, so the calling MCP tool surfaces a clear error before any state
    /// is changed.
    /// </summary>
    public static async Task<DownloadedImage?> DownloadAsync(
        IHttpClientFactory httpFactory,
        string? imageUrl,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null; // clear intent
        }

        var url = imageUrl.Trim();

        if (url.Length > MaxUrlLength)
        {
            throw new ArgumentException($"Image URL exceeds {MaxUrlLength} characters.", nameof(imageUrl));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "Image URL must be an absolute http or https URL.", nameof(imageUrl));
        }

        var client = httpFactory.CreateClient(PoiServicesExtensions.ImageDownloadClient);

        HttpResponseMessage resp;
        try
        {
            resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // Client timeout (vs. genuine caller cancellation, which propagates).
            throw new ArgumentException("Image URL could not be downloaded (timed out).", nameof(imageUrl));
        }
        catch (HttpRequestException ex)
        {
            throw new ArgumentException($"Image URL could not be downloaded ({ex.Message}).", nameof(imageUrl));
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                throw new ArgumentException(
                    $"Image URL returned HTTP {(int)resp.StatusCode}.", nameof(imageUrl));
            }

            var contentType = resp.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrEmpty(contentType)
                || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"URL did not return an image (content-type: {contentType ?? "none"}).", nameof(imageUrl));
            }

            // Reject early on an honest oversized Content-Length…
            if (resp.Content.Headers.ContentLength is > MaxBytes)
            {
                throw new ArgumentException(
                    $"Image exceeds the {MaxBytes / (1024 * 1024)} MB limit.", nameof(imageUrl));
            }

            // …then cap while reading (don't trust Content-Length alone).
            var bytes = await ReadCappedAsync(resp, ct);

            if (!LooksLikeImage(bytes))
            {
                throw new ArgumentException(
                    "Downloaded data is not a recognised image (jpeg/png/gif/webp).", nameof(imageUrl));
            }

            return new DownloadedImage(bytes, contentType, url);
        }
    }

    /// <summary>
    /// Atomically applies an image to <paramref name="poiId"/> on a single context:
    /// <paramref name="image"/> null clears the photo (removes the PoiImage row and
    /// nulls <see cref="Poi.ImageUrl"/>); non-null upserts the bytes and sets
    /// <see cref="Poi.ImageUrl"/>. Both the PoiImage row and Poi.ImageUrl are saved
    /// in ONE SaveChanges so they cannot diverge. Returns the resolved ImageUrl
    /// (or null when cleared) for the caller's in-memory DTO. Throws if the POI is gone.
    /// </summary>
    public static async Task<string?> ApplyAsync(
        IDbContextFactory<AppDbContext> dbFactory,
        int poiId,
        DownloadedImage? image,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var poi = await db.Pois.FirstOrDefaultAsync(p => p.Id == poiId, ct);
        if (poi is null)
        {
            throw new InvalidOperationException($"POI {poiId} not found.");
        }

        var existing = await db.PoiImages.FindAsync([poiId], ct);

        if (image is null)
        {
            // Clear.
            if (existing is not null)
            {
                db.PoiImages.Remove(existing);
            }
            poi.ImageUrl = null;
            await db.SaveChangesAsync(ct);
            return null;
        }

        if (existing is null)
        {
            db.PoiImages.Add(new PoiImage { PoiId = poiId, Data = image.Bytes, ContentType = image.ContentType });
        }
        else
        {
            existing.Data = image.Bytes;
            existing.ContentType = image.ContentType;
        }
        poi.ImageUrl = image.Url;
        await db.SaveChangesAsync(ct);
        return image.Url;
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                throw new ArgumentException(
                    $"Image exceeds the {MaxBytes / (1024 * 1024)} MB limit.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Magic-byte sniff for the common web image formats. Cheap defence against a
    /// server that lies about content-type or an HTML error page served as an image.
    /// </summary>
    private static bool LooksLikeImage(byte[] b)
    {
        if (b.Length < 12)
        {
            return false;
        }

        // JPEG: FF D8 FF
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        {
            return true;
        }

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
            && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
        {
            return true;
        }

        // GIF: "GIF8"
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38)
        {
            return true;
        }

        // WEBP: "RIFF" .... "WEBP"
        if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
            && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50)
        {
            return true;
        }

        return false;
    }
}
