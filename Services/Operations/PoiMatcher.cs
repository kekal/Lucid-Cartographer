using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Operations
{
    public class PoiMatcher
    {
        /// <summary>
        /// Determines if two POIs represent the same place.
        /// Tier 1: Google Maps URL match (normalized)
        /// Tier 2: Within spatial tolerance + name similarity
        /// </summary>
        public bool IsMatch(Poi a, Poi b, double toleranceMeters = 100)
        {
            // Tier 1: URL match
            if (!string.IsNullOrEmpty(a.GoogleMapsUrl) && !string.IsNullOrEmpty(b.GoogleMapsUrl))
            {
                if (NormalizeUrl(a.GoogleMapsUrl) == NormalizeUrl(b.GoogleMapsUrl))
                    return true;
            }

            // Tier 2: Proximity + name similarity
            var distance = GeoUtils.HaversineDistance(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
            if (distance > toleranceMeters)
                return false;

            return NameSimilarity(a.Name, b.Name) >= 0.6;
        }

        /// <summary>
        /// Finds matches for a POI in a collection of candidates.
        /// </summary>
        public Poi? FindMatch(Poi poi, IEnumerable<Poi> candidates, double toleranceMeters = 100)
        {
            return candidates.FirstOrDefault(c => IsMatch(poi, c, toleranceMeters));
        }

        /// <summary>
        /// Finds all duplicate groups within a single list.
        /// Returns groups of 2+ POIs that match each other.
        /// </summary>
        public List<List<Poi>> FindDuplicateGroups(List<Poi> pois, double toleranceMeters = 100)
        {
            var used = new HashSet<int>();
            var groups = new List<List<Poi>>();

            for (int i = 0; i < pois.Count; i++)
            {
                if (used.Contains(pois[i].Id)) continue;

                var group = new List<Poi> { pois[i] };

                for (int j = i + 1; j < pois.Count; j++)
                {
                    if (used.Contains(pois[j].Id)) continue;
                    if (IsMatch(pois[i], pois[j], toleranceMeters))
                    {
                        group.Add(pois[j]);
                        used.Add(pois[j].Id);
                    }
                }

                if (group.Count > 1)
                {
                    used.Add(pois[i].Id);
                    groups.Add(group);
                }
            }

            return groups;
        }

        private static double NameSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return 0;

            a = a.Trim().ToLowerInvariant();
            b = b.Trim().ToLowerInvariant();

            if (a == b) return 1.0;
            if (a.Contains(b) || b.Contains(a)) return 0.9;

            // Simple Levenshtein-based similarity
            var distance = LevenshteinDistance(a, b);
            var maxLen = Math.Max(a.Length, b.Length);
            if (maxLen == 0) return 1.0;
            return 1.0 - (double)distance / maxLen;
        }

        private static int LevenshteinDistance(string s, string t)
        {
            var n = s.Length;
            var m = t.Length;
            var d = new int[n + 1, m + 1];

            for (int i = 0; i <= n; i++) d[i, 0] = i;
            for (int j = 0; j <= m; j++) d[0, j] = j;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m];
        }

        private static string NormalizeUrl(string url)
        {
            url = url.Trim().TrimEnd('/');
            if (url.StartsWith("http://"))
                url = "https://" + url[7..];
            return url.ToLowerInvariant();
        }
    }
}
