using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;

namespace LucidCartographer.Services.Import;

public class CsvImporter(ILogger<CsvImporter> logger) : IFileImporter
{
    public string FormatName => "CSV";

    private static readonly string[] Extensions = [".csv"];
    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public async Task<List<ImportedPoi>> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Parsing CSV file: {FileName}", fileName);

        // CsvHelper reads synchronously — we yield once to avoid blocking the caller's
        // synchronization context, then proceed with sync I/O on the thread-pool.
        await Task.Yield();

        using var reader = new StreamReader(fileStream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null
        });

        await csv.ReadAsync();
        csv.ReadHeader();
        var headers = csv.HeaderRecord?.Select(h => h.Trim().ToLowerInvariant()).ToArray() ?? [];

        var latCol = FindColumn(headers, "lat", "latitude", "y");
        var lonCol = FindColumn(headers, "lon", "lng", "longitude", "long", "x");
        var nameCol = FindColumn(headers, "name", "title", "place", "location");
        var urlCol = FindColumn(headers, "url", "link", "google_maps_url", "maps_url");
        var addressCol = FindColumn(headers, "address", "addr");
        var categoryCol = FindColumn(headers, "category", "type", "kind");
        var descCol = FindColumn(headers, "description", "desc", "notes", "comment");

        if (latCol < 0 || lonCol < 0)
        {
            logger.LogError("CSV file {FileName} missing required lat/lon columns", fileName);
            throw new ArgumentException("CSV must contain latitude and longitude columns (lat/latitude/y and lon/lng/longitude/x)");
        }

        var skipped = 0;
        var results = new List<ImportedPoi>();

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var latStr = csv.GetField(latCol);
            var lonStr = csv.GetField(lonCol);
            if (string.IsNullOrWhiteSpace(latStr) || string.IsNullOrWhiteSpace(lonStr)) { skipped++; continue; }
            if (!double.TryParse(latStr, CultureInfo.InvariantCulture, out var lat)) { skipped++; continue; }
            if (!double.TryParse(lonStr, CultureInfo.InvariantCulture, out var lon)) { skipped++; continue; }

            var name = nameCol >= 0 ? csv.GetField(nameCol) : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"Point ({lat:F4}, {lon:F4})";
            }

            results.Add(new ImportedPoi(
                Name: name.Trim(),
                Latitude: lat,
                Longitude: lon,
                GoogleMapsUrl: urlCol >= 0 ? csv.GetField(urlCol) : null,
                Address: addressCol >= 0 ? csv.GetField(addressCol) : null,
                Category: categoryCol >= 0 ? csv.GetField(categoryCol) : null,
                Description: descCol >= 0 ? csv.GetField(descCol) : null
            ));
        }

        logger.LogInformation("CSV parse complete: {FileName} — {Count} POIs parsed, {Skipped} skipped",
            fileName, results.Count, skipped);
        return results;
    }

    /// <summary>
    /// Finds a column index by matching header names against candidates.
    /// IE-23: Tightened fallback from Contains to StartsWith/EndsWith with word boundary
    /// to prevent greedy matches like "foxylongitude" matching "lon".
    /// </summary>
    private static int FindColumn(string[] headers, params string[] candidates)
    {
        // Exact match first
        for (var i = 0; i < headers.Length; i++)
        {
            if (candidates.Any(c => headers[i] == c))
            {
                return i;
            }
        }
        // Fallback: StartsWith or EndsWith (word-boundary-ish match)
        for (var i = 0; i < headers.Length; i++)
        {
            if (candidates.Any(c => headers[i].StartsWith(c) || headers[i].EndsWith(c)))
            {
                return i;
            }
        }
        return -1;
    }
}
