using System.Globalization;
using System.Text.RegularExpressions;
using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services;

/// <summary>
/// Shared Google Maps URL helper and coordinate extraction utilities.
/// </summary>
public static class PoiUrlHelper
{
    public static string GetGoogleMapsUrl(Poi poi)
    {
        if (!string.IsNullOrEmpty(poi.GoogleMapsUrl))
        {
            return poi.GoogleMapsUrl;
        }

        // Name-based search resolves to the real place far more reliably than bare coordinates.
        if (!string.IsNullOrWhiteSpace(poi.Name))
        {
            var query = string.IsNullOrWhiteSpace(poi.Category) ? poi.Name : $"{poi.Name} {poi.Category}";
            return "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(query);
        }

        return "#";
    }

    // Language-independent place identifiers: !1s (feature id: 0x…:0x…) and !16s (Knowledge Graph mid: /g/… or /m/…).
    private static readonly Regex FeatureIdRegex =
        new(@"!1s(0x[0-9a-fA-F]+:0x[0-9a-fA-F]+)", RegexOptions.Compiled);
    private static readonly Regex EntityIdTokenRegex =
        new(@"!16s([^!?&]+)", RegexOptions.Compiled);
    private static readonly Regex PlaceMidRegex =
        new(@"^/[gm]/[0-9a-zA-Z_]+$", RegexOptions.Compiled);

    /// <summary>
    /// Extracts the Google Maps <b>feature id</b> (<c>0x…:0x…</c>) from the
    /// <c>!1s</c> segment of a place URL. This is the most reliable
    /// language-independent identity for a place. Returned lower-cased so two
    /// URLs differing only in hex casing compare equal. Null if absent.
    /// </summary>
    public static string? ExtractFeatureId(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var m = FeatureIdRegex.Match(url);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Extracts the Google Knowledge Graph place id (<c>/g/…</c> or <c>/m/…</c>)
    /// from the <c>!16s</c> segment of a place URL (it is percent-encoded in
    /// the URL, e.g. <c>%2Fg%2F1226snj_</c>). Also language-independent, but
    /// not always present. Null if absent or malformed.
    /// </summary>
    public static string? ExtractPlaceEntityId(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var m = EntityIdTokenRegex.Match(url);
        if (!m.Success)
        {
            return null;
        }

        string token;
        try
        {
            token = Uri.UnescapeDataString(m.Groups[1].Value);
        }
        catch (UriFormatException)
        {
            return null;
        }

        return PlaceMidRegex.IsMatch(token) ? token : null;
    }

    /// <summary>
    /// Extracts the human-readable place name embedded in a canonical Google
    /// Maps place URL — the segment between <c>/maps/place/</c> and the next
    /// <c>/</c> (e.g. <c>.../maps/place/Wawel+Royal+Castle/@50.05,...</c> →
    /// "Wawel Royal Castle"). Google encodes spaces as '+' and the rest with
    /// percent-encoding, so we reverse both. Returns null when the URL is not a
    /// <c>/maps/place/</c> link or the decoded segment is empty.
    /// </summary>
    public static string? ExtractPlaceNameFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        const string marker = "/maps/place/";
        var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var start = idx + marker.Length;
        var end = url.IndexOf('/', start);
        var segment = end < 0 ? url[start..] : url[start..end];
        if (string.IsNullOrWhiteSpace(segment))
        {
            return null;
        }

        // Google uses '+' for spaces in the /place/ segment; the rest is
        // percent-encoded. Replace '+' first, then unescape the remainder.
        segment = segment.Replace('+', ' ');
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(segment).Trim();
        }
        catch (UriFormatException)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(decoded) ? null : decoded;
    }

    /// <summary>
    /// Extracts coordinates from a Google Maps URL.
    /// Checks !3d/!4d parameters first, then @/ viewport-center coordinates.
    /// </summary>
    public static (double lat, double lon)? ExtractCoordinatesFromUrl(string url)
    {
        var lat3d = ExtractBangParam(url, "!3d");
        var lon4d = ExtractBangParam(url, "!4d");
        if (lat3d.HasValue && lon4d.HasValue)
        {
            return (lat3d.Value, lon4d.Value);
        }

        // Fallback: @lat,lon viewport-center coordinates.
        var atIdx = url.IndexOf("/@", StringComparison.Ordinal);
        if (atIdx >= 0)
        {
            var afterAt = url[(atIdx + 2)..];
            var parts = afterAt.Split(',');
            if (parts.Length >= 2
                && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var lat)
                && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var lon))
            {
                return (lat, lon);
            }
        }

        return null;
    }

    /// <summary>
    /// Strict variant of <see cref="ExtractCoordinatesFromUrl"/> that only
    /// accepts the place marker (!3d/!4d) and never falls back to the
    /// `/@lat,lon` viewport-center form. Use this when the caller needs
    /// the actual place coordinates, not the current map view — e.g.
    /// the Add-POI flow on the Data Sources page, where a `/@` pan
    /// from some other place would bind the wrong coords to the new row.
    /// </summary>
    public static (double lat, double lon)? ExtractPlaceCoordinatesFromUrl(string url)
    {
        var lat3d = ExtractBangParam(url, "!3d");
        var lon4d = ExtractBangParam(url, "!4d");
        if (lat3d.HasValue && lon4d.HasValue)
        {
            return (lat3d.Value, lon4d.Value);
        }

        return null;
    }

    /// <summary>
    /// True if <paramref name="url"/> looks like a Google Maps link
    /// (any of google.com/maps, maps.google.com, maps.app.goo.gl, or
    /// the legacy goo.gl/maps form). Used by importers and the enrichment
    /// background service to decide whether navigating the URL will reach
    /// the place selectors the scraper expects.
    /// </summary>
    public static bool IsGoogleMapsUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }
        return url.Contains("google.com/maps", StringComparison.OrdinalIgnoreCase)
               || url.Contains("maps.google.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("maps.app.goo.gl", StringComparison.OrdinalIgnoreCase)
               || url.Contains("goo.gl/maps", StringComparison.OrdinalIgnoreCase);
    }

    private static double? ExtractBangParam(string url, string prefix)
    {
        var idx = url.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        var start = idx + prefix.Length;
        var end = start;
        while (end < url.Length && (char.IsDigit(url[end]) || url[end] == '.' || url[end] == '-'))
            end++;
        if (end > start && double.TryParse(url[start..end], CultureInfo.InvariantCulture, out var val))
        {
            return val;
        }

        return null;
    }
}
