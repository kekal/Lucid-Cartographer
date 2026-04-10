using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace LucidCartographer.Services.Import;

public class CsvImporter : IFileImporter
{
    public string FormatName => "CSV";
    public string[] SupportedExtensions => new[] { ".csv" };

    public async Task<List<ImportedPoi>> ParseAsync(Stream fileStream, string fileName)
    {
        using var reader = new StreamReader(fileStream);
        var content = await reader.ReadToEndAsync();

        using var stringReader = new StringReader(content);
        using var csv = new CsvReader(stringReader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null
        });

        csv.Read();
        csv.ReadHeader();
        var headers = csv.HeaderRecord?.Select(h => h.Trim().ToLowerInvariant()).ToArray() ?? Array.Empty<string>();

        // Auto-detect column indices
        var latCol = FindColumn(headers, "lat", "latitude", "y");
        var lonCol = FindColumn(headers, "lon", "lng", "longitude", "long", "x");
        var nameCol = FindColumn(headers, "name", "title", "place", "location");
        var urlCol = FindColumn(headers, "url", "link", "google_maps_url", "maps_url");
        var addressCol = FindColumn(headers, "address", "addr");
        var categoryCol = FindColumn(headers, "category", "type", "kind");
        var descCol = FindColumn(headers, "description", "desc", "notes", "comment");

        if (latCol < 0 || lonCol < 0)
            throw new ArgumentException("CSV must contain latitude and longitude columns (lat/latitude/y and lon/lng/longitude/x)");

        var results = new List<ImportedPoi>();

        while (csv.Read())
        {
            var latStr = csv.GetField(latCol);
            var lonStr = csv.GetField(lonCol);
            if (string.IsNullOrWhiteSpace(latStr) || string.IsNullOrWhiteSpace(lonStr)) continue;
            if (!double.TryParse(latStr, CultureInfo.InvariantCulture, out var lat)) continue;
            if (!double.TryParse(lonStr, CultureInfo.InvariantCulture, out var lon)) continue;

            var name = nameCol >= 0 ? csv.GetField(nameCol) : null;
            if (string.IsNullOrWhiteSpace(name)) name = $"Point ({lat:F4}, {lon:F4})";

            results.Add(new ImportedPoi(
                Name: name!.Trim(),
                Latitude: lat,
                Longitude: lon,
                GoogleMapsUrl: urlCol >= 0 ? csv.GetField(urlCol) : null,
                Address: addressCol >= 0 ? csv.GetField(addressCol) : null,
                Category: categoryCol >= 0 ? csv.GetField(categoryCol) : null,
                Description: descCol >= 0 ? csv.GetField(descCol) : null
            ));
        }

        return results;
    }

    private static int FindColumn(string[] headers, params string[] candidates)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            if (candidates.Any(c => headers[i].Contains(c)))
                return i;
        }
        return -1;
    }
}
