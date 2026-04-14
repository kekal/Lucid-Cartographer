using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Operations
{
    /// <summary>
    /// Stateless POI matching service. All public methods are thread-safe.
    /// Matching uses a two-tier strategy:
    /// Tier 1 - Normalized Google Maps URL comparison (O(1) with pre-built index).
    /// Tier 2 - Proximity (Haversine) + name similarity (Levenshtein).
    /// If both POIs have URLs that differ, they are considered different places (no fallthrough to Tier 2).
    /// </summary>
    public partial class PoiMatcher : IPoiMatcher
    {
        /// <summary>Default spatial tolerance in meters for proximity matching.</summary>
        public const double DefaultToleranceMeters = IPoiMatcher.DefaultToleranceMeters;

        /// <summary>Default threshold for name similarity (0.0 - 1.0). Names with similarity below this are not considered matches.</summary>
        public const double DefaultNameSimilarityThreshold = IPoiMatcher.DefaultNameSimilarityThreshold;

        /// <summary>
        /// Approximate degrees of latitude per meter, used for fast latitude pre-filter.
        /// 1 degree latitude ~ 111,320 meters.
        /// </summary>
        private const double DegreesPerMeter = 1.0 / 111_320.0;

        /// <summary>
        /// Determines if two POIs represent the same place.
        /// Tier 1: Google Maps URL match (normalized).
        /// Tier 2: Within spatial tolerance + name similarity.
        /// If both POIs have URLs that differ, returns false immediately (OPS-H05).
        /// </summary>
        /// <param name="a">First POI.</param>
        /// <param name="b">Second POI.</param>
        /// <param name="toleranceMeters">Maximum distance in meters for proximity matching.</param>
        /// <param name="nameSimilarityThreshold">Minimum name similarity score (0.0 - 1.0) for proximity matching.</param>
        /// <returns>True if the two POIs are considered a match.</returns>
        public bool IsMatch(Poi a, Poi b, double toleranceMeters = DefaultToleranceMeters, double nameSimilarityThreshold = DefaultNameSimilarityThreshold)
        {
            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(b);

            bool aHasUrl = !string.IsNullOrEmpty(a.GoogleMapsUrl);
            bool bHasUrl = !string.IsNullOrEmpty(b.GoogleMapsUrl);

            // Tier 1: URL comparison
            if (aHasUrl && bHasUrl)
            {
                // Both have URLs: if they match, same place; if they differ, different places (OPS-H05)
                return NormalizeUrl(a.GoogleMapsUrl!) == NormalizeUrl(b.GoogleMapsUrl!);
            }

            // Tier 2: Proximity + name similarity (only when at least one POI lacks a URL)
            return IsProximityMatch(a, b, toleranceMeters, nameSimilarityThreshold, out _);
        }

        /// <summary>
        /// Internal method that checks proximity + name similarity and returns the computed distance.
        /// Avoids double Haversine computation (OPS-R01).
        /// </summary>
        private static bool IsProximityMatch(Poi a, Poi b, double toleranceMeters, double nameSimilarityThreshold, out double distance)
        {
            distance = GeoUtils.HaversineDistance(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
            if (distance > toleranceMeters)
                return false;

            return NameSimilarity(a.Name, b.Name) >= nameSimilarityThreshold;
        }

        /// <summary>
        /// Finds the best match for a POI in a collection of candidates.
        /// Scans all candidates and returns the closest match (by distance) that passes matching criteria.
        /// Computes Haversine distance only once per candidate (OPS-R01).
        /// </summary>
        public Poi? FindMatch(Poi poi, IEnumerable<Poi> candidates, double toleranceMeters = DefaultToleranceMeters, double nameSimilarityThreshold = DefaultNameSimilarityThreshold)
        {
            ArgumentNullException.ThrowIfNull(poi);
            ArgumentNullException.ThrowIfNull(candidates);

            Poi? bestMatch = null;
            double bestDistance = double.MaxValue;
            bool poiHasUrl = !string.IsNullOrEmpty(poi.GoogleMapsUrl);

            foreach (var c in candidates)
            {
                bool cHasUrl = !string.IsNullOrEmpty(c.GoogleMapsUrl);

                if (poiHasUrl && cHasUrl)
                {
                    // Tier 1: URL comparison
                    if (NormalizeUrl(poi.GoogleMapsUrl!) == NormalizeUrl(c.GoogleMapsUrl!))
                    {
                        // URL match; compute distance for ranking
                        var dist = GeoUtils.HaversineDistance(poi.Latitude, poi.Longitude, c.Latitude, c.Longitude);
                        if (dist < bestDistance)
                        {
                            bestDistance = dist;
                            bestMatch = c;
                        }
                    }
                    // Both have URLs that differ -> skip (OPS-H05)
                    continue;
                }

                // Tier 2: Proximity + name similarity
                if (IsProximityMatch(poi, c, toleranceMeters, nameSimilarityThreshold, out var distance) && distance < bestDistance)
                {
                    bestDistance = distance;
                    bestMatch = c;
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// Finds the best match using a pre-built URL index for O(1) URL lookup,
        /// falling back to proximity matching for POIs without URLs (OPS-C02).
        /// </summary>
        public Poi? FindMatch(Poi poi, Dictionary<string, Poi> urlIndex, IEnumerable<Poi> candidates, double toleranceMeters = DefaultToleranceMeters, double nameSimilarityThreshold = DefaultNameSimilarityThreshold)
        {
            ArgumentNullException.ThrowIfNull(poi);
            ArgumentNullException.ThrowIfNull(urlIndex);
            ArgumentNullException.ThrowIfNull(candidates);

            // Tier 1: O(1) URL lookup
            if (!string.IsNullOrEmpty(poi.GoogleMapsUrl))
            {
                var normalizedUrl = NormalizeUrl(poi.GoogleMapsUrl);
                if (urlIndex.TryGetValue(normalizedUrl, out var urlMatch))
                    return urlMatch;

                // POI has a URL but no match found in index.
                // We still fall through to proximity for candidates without URLs.
            }

            // Tier 2: Proximity matching (only for candidates without a definitive URL mismatch)
            Poi? bestMatch = null;
            double bestDistance = double.MaxValue;
            bool poiHasUrl = !string.IsNullOrEmpty(poi.GoogleMapsUrl);

            foreach (var c in candidates)
            {
                bool cHasUrl = !string.IsNullOrEmpty(c.GoogleMapsUrl);

                // If both have URLs and we got here, URLs didn't match -> skip (OPS-H05)
                if (poiHasUrl && cHasUrl)
                    continue;

                var dist = GeoUtils.HaversineDistance(poi.Latitude, poi.Longitude, c.Latitude, c.Longitude);
                if (dist > toleranceMeters)
                    continue;

                if (NameSimilarity(poi.Name, c.Name) < nameSimilarityThreshold)
                    continue;

                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    bestMatch = c;
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// Finds all duplicate groups using union-find for transitive grouping (OPS-C03).
        /// If A~B and B~C but not A~C, all three end up in the same group.
        /// Uses latitude pre-filter for O(1) rejection of distant pairs (OPS-R07).
        /// Accepts CancellationToken for cancellation of long-running operations (OPS-R20).
        /// </summary>
        public List<List<Poi>> FindDuplicateGroups(List<Poi> pois, double toleranceMeters = DefaultToleranceMeters, double nameSimilarityThreshold = DefaultNameSimilarityThreshold, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(pois);

            int n = pois.Count;
            var parent = new int[n];
            var rank = new int[n];
            for (int i = 0; i < n; i++)
                parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]]; // path compression
                    x = parent[x];
                }
                return x;
            }

            void Union(int x, int y)
            {
                int rx = Find(x), ry = Find(y);
                if (rx == ry) return;
                if (rank[rx] < rank[ry]) (rx, ry) = (ry, rx);
                parent[ry] = rx;
                if (rank[rx] == rank[ry]) rank[rx]++;
            }

            // Pre-compute latitude threshold in degrees for fast rejection (OPS-R07)
            double latThresholdDegrees = toleranceMeters * DegreesPerMeter;

            for (int i = 0; i < n; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int j = i + 1; j < n; j++)
                {
                    // Fast latitude pre-filter: reject pairs that are obviously too far apart (OPS-R07)
                    if (Math.Abs(pois[i].Latitude - pois[j].Latitude) > latThresholdDegrees)
                        continue;

                    if (IsMatch(pois[i], pois[j], toleranceMeters, nameSimilarityThreshold))
                    {
                        Union(i, j);
                    }
                }
            }

            // Collect groups
            var groupMap = new Dictionary<int, List<Poi>>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(i);
                if (!groupMap.TryGetValue(root, out var list))
                {
                    list = new List<Poi>();
                    groupMap[root] = list;
                }
                list.Add(pois[i]);
            }

            return groupMap.Values.Where(g => g.Count > 1).ToList();
        }

        /// <summary>
        /// Builds a dictionary mapping normalized URLs to POIs for O(1) URL-based lookup (OPS-C02).
        /// If multiple POIs share the same normalized URL, the first one wins and subsequent
        /// collisions are logged via trace diagnostics (OPS-R11).
        /// </summary>
        public Dictionary<string, Poi> BuildUrlIndex(IEnumerable<Poi> pois)
        {
            ArgumentNullException.ThrowIfNull(pois);

            var index = new Dictionary<string, Poi>(StringComparer.Ordinal);
            foreach (var poi in pois)
            {
                if (string.IsNullOrEmpty(poi.GoogleMapsUrl))
                    continue;

                var normalized = NormalizeUrl(poi.GoogleMapsUrl);
                if (!index.TryAdd(normalized, poi))
                {
                    System.Diagnostics.Trace.TraceWarning(
                        $"BuildUrlIndex: duplicate normalized URL '{normalized}' for POI Id={poi.Id} Name='{poi.Name}'. " +
                        $"Existing entry: Id={index[normalized].Id} Name='{index[normalized].Name}'.");
                }
            }
            return index;
        }

        /// <summary>
        /// Computes name similarity between two strings using Levenshtein distance.
        /// Applies Unicode NFC normalization before comparison (OPS-H06).
        /// Substring match returns 0.9 only when the shorter string is at least half the length
        /// of the longer string (OPS-R10).
        /// </summary>
        /// <param name="a">First name.</param>
        /// <param name="b">Second name.</param>
        /// <returns>Similarity score between 0.0 and 1.0.</returns>
        internal static double NameSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return 0;

            // Unicode NFC normalization (OPS-H06)
            a = a.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormC);
            b = b.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormC);

            if (a == b) return 1.0;

            // Substring match with length-ratio guard (OPS-R10)
            int shorterLen = Math.Min(a.Length, b.Length);
            int longerLen = Math.Max(a.Length, b.Length);
            if ((a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
                && (double)shorterLen / longerLen > 0.5)
            {
                return 0.9;
            }

            // Levenshtein-based similarity
            var distance = LevenshteinDistance(a, b);
            var maxLen = Math.Max(a.Length, b.Length);
            return 1.0 - (double)distance / maxLen;
        }

        /// <summary>
        /// Computes Levenshtein edit distance using two-row optimization (OPS-C04).
        /// Space: O(min(n,m)) instead of O(n*m). Uses ArrayPool to avoid GC pressure (OPS-R15).
        /// </summary>
        internal static int LevenshteinDistance(string s, string t)
        {
            // Ensure s is the shorter string to minimize buffer size
            if (s.Length > t.Length)
                (s, t) = (t, s);

            int sLen = s.Length;
            int tLen = t.Length;

            if (sLen == 0) return tLen;

            var pool = ArrayPool<int>.Shared;
            var prev = pool.Rent(sLen + 1);
            var curr = pool.Rent(sLen + 1);

            try
            {
                for (int i = 0; i <= sLen; i++)
                    prev[i] = i;

                for (int j = 1; j <= tLen; j++)
                {
                    curr[0] = j;
                    for (int i = 1; i <= sLen; i++)
                    {
                        int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                        curr[i] = Math.Min(
                            Math.Min(curr[i - 1] + 1, prev[i] + 1),
                            prev[i - 1] + cost);
                    }
                    (prev, curr) = (curr, prev);
                }

                return prev[sLen];
            }
            finally
            {
                pool.Return(prev);
                pool.Return(curr);
            }
        }

        /// <summary>
        /// Normalizes a Google Maps URL for comparison (OPS-H03).
        /// Handles: http vs https, www vs non-www, trailing slashes, fragment removal,
        /// tracking parameter removal, percent-encoding normalization, CID/ftid extraction.
        /// This is a static pure function (OPS-R03).
        /// </summary>
        public static string NormalizeUrl(string url)
        {
            url = url.Trim();

            // Decode percent-encoded characters before normalization (OPS-R09)
            try
            {
                url = Uri.UnescapeDataString(url);
            }
            catch (FormatException)
            {
                // Malformed percent-encoding; proceed with the raw URL
            }

            // Remove fragment
            int fragIdx = url.IndexOf('#');
            if (fragIdx >= 0)
                url = url[..fragIdx];

            // Normalize trailing slashes
            url = url.TrimEnd('/');

            // Normalize scheme to https
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url[7..];

            // Remove www. prefix from host (OPS-R19: regex now only matches "www." after scheme)
            if (url.StartsWith("https://www.", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url[12..];

            // Try to extract CID parameter for Google Maps URLs (OPS-H03)
            var cidMatch = CidParamRegex().Match(url);
            if (cidMatch.Success)
            {
                // CID is the authoritative identifier; use it as the canonical form
                return "cid:" + cidMatch.Groups[1].Value;
            }

            // Try to extract ftid parameter for Google Maps URLs (OPS-R08)
            var ftidMatch = FtidParamRegex().Match(url);
            if (ftidMatch.Success)
            {
                return "ftid:" + ftidMatch.Groups[1].Value;
            }

            // Remove common tracking parameters
            url = RemoveTrackingParams(url);

            // Lowercase the scheme and host, but preserve path case (place IDs are case-sensitive)
            int pathStart = url.IndexOf('/', 8); // skip "https://"
            if (pathStart > 0)
            {
                string hostPart = url[..pathStart].ToLowerInvariant();
                string rest = url[pathStart..];
                url = hostPart + rest;
            }
            else
            {
                url = url.ToLowerInvariant();
            }

            return url;
        }

        /// <summary>
        /// Removes common tracking query parameters (utm_*, hl, authuser, entry) from a URL.
        /// </summary>
        private static string RemoveTrackingParams(string url)
        {
            int qIdx = url.IndexOf('?');
            if (qIdx < 0) return url;

            string basePart = url[..qIdx];
            string query = url[(qIdx + 1)..];

            var keepParams = new List<string>();
            foreach (var param in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var key = param.Split('=')[0].ToLowerInvariant();
                if (key.StartsWith("utm_") || key == "hl" || key == "authuser" || key == "entry")
                    continue;
                keepParams.Add(param);
            }

            if (keepParams.Count == 0)
                return basePart;

            // Sort parameters for consistent ordering
            keepParams.Sort(StringComparer.Ordinal);
            return basePart + "?" + string.Join("&", keepParams);
        }

        [GeneratedRegex(@"[?&]cid=(\d+)", RegexOptions.IgnoreCase)]
        private static partial Regex CidParamRegex();

        [GeneratedRegex(@"[?&]ftid=(0x[0-9a-fA-F]+:0x[0-9a-fA-F]+)", RegexOptions.IgnoreCase)]
        private static partial Regex FtidParamRegex();
    }
}
