using System.Text;
using System.Text.RegularExpressions;
using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Operations
{
    /// <summary>
    /// Stateless POI matching service. All public methods delegate to
    /// <see cref="PoiIdentity.AreSamePlace(Poi?, Poi?, double, double)"/>
    /// — the single "same real place" rule used across import, enrichment
    /// and set operations. Name similarity (Fastenshtein) plus geographic
    /// proximity (Haversine). URL is deliberately NOT part of the rule:
    /// distinct franchise branches that share a corporate URL stay
    /// distinct. Thread-safe because nothing is mutated per call.
    /// </summary>
    public partial class PoiMatcher : IPoiMatcher
    {
        /// <summary>Default spatial tolerance in meters for proximity matching.</summary>
        public const double DefaultToleranceMeters = IPoiMatcher.DefaultToleranceMeters;

        /// <summary>Default threshold for name similarity (0.0 - 1.0).</summary>
        public const double DefaultNameSimilarityThreshold = IPoiMatcher.DefaultNameSimilarityThreshold;

        /// <summary>
        /// Approximate degrees of latitude per meter, used for the fast
        /// latitude pre-filter in <see cref="FindDuplicateGroups"/>.
        /// 1° latitude ~ 111,320 m.
        /// </summary>
        private const double DegreesPerMeter = 1.0 / 111_320.0;

        /// <inheritdoc />
        public bool IsMatch(Poi a, Poi b, double toleranceMeters = DefaultToleranceMeters, double nameSimilarityThreshold = DefaultNameSimilarityThreshold)
        {
            ArgumentNullException.ThrowIfNull(a);
            ArgumentNullException.ThrowIfNull(b);
            return PoiIdentity.AreSamePlace(a, b, toleranceMeters, nameSimilarityThreshold);
        }

        /// <inheritdoc />
        public Poi? FindMatch(Poi poi, IEnumerable<Poi> candidates, double toleranceMeters = DefaultToleranceMeters, double nameSimilarityThreshold = DefaultNameSimilarityThreshold)
        {
            ArgumentNullException.ThrowIfNull(poi);
            ArgumentNullException.ThrowIfNull(candidates);

            Poi? bestMatch = null;
            double bestDistance = double.MaxValue;

            // Scan all candidates. Ties break toward the closest candidate
            // so the result is stable when several rows pass the threshold.
            foreach (var c in candidates)
            {
                if (!PoiIdentity.AreSamePlace(poi, c, toleranceMeters, nameSimilarityThreshold))
                    continue;

                // AreSamePlace already requires non-null coords on both sides,
                // so the .Value dereferences here are safe.
                var dist = GeoUtils.HaversineDistance(
                    poi.Latitude!.Value, poi.Longitude!.Value,
                    c.Latitude!.Value, c.Longitude!.Value);
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    bestMatch = c;
                }
            }

            return bestMatch;
        }

        /// <inheritdoc />
        public List<List<Poi>> FindDuplicateGroups(List<Poi> pois, double toleranceMeters = DefaultToleranceMeters, double nameSimilarityThreshold = DefaultNameSimilarityThreshold, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(pois);

            // Filter out unlocated POIs up front — PoiIdentity excludes them
            // anyway, and the latitude pre-filter below needs .Value.
            pois = pois.Where(p => p.Latitude.HasValue && p.Longitude.HasValue).ToList();

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

            // Pre-compute latitude threshold in degrees so we can reject
            // obviously-distant pairs without paying for Haversine.
            double latThresholdDegrees = toleranceMeters * DegreesPerMeter;

            for (int i = 0; i < n; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int j = i + 1; j < n; j++)
                {
                    // Fast latitude pre-filter. Coords are guaranteed non-null
                    // by the Where() pass at the top of the method.
                    if (Math.Abs(pois[i].Latitude!.Value - pois[j].Latitude!.Value) > latThresholdDegrees)
                        continue;

                    if (PoiIdentity.AreSamePlace(pois[i], pois[j], toleranceMeters, nameSimilarityThreshold))
                        Union(i, j);
                }
            }

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

        // ---- Name similarity & URL normalization -------------------------------
        //
        // NameSimilarity stays here (not in PoiIdentity) because it is a
        // general-purpose string helper also used by the UI for fuzzy
        // match displays. NormalizeUrl is kept for display / outbound
        // "view on Google Maps" links; no import or dedup code calls it
        // any more.

        /// <summary>
        /// Computes name similarity between two strings using Fastenshtein
        /// edit distance. Applies Unicode NFC normalization before
        /// comparison. Substring match returns 0.9 only when the shorter
        /// string is at least half the length of the longer string.
        /// </summary>
        internal static double NameSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return 0;

            a = a.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormC);
            b = b.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormC);

            if (a == b) return 1.0;

            int shorterLen = Math.Min(a.Length, b.Length);
            int longerLen = Math.Max(a.Length, b.Length);
            if ((a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
                && (double)shorterLen / longerLen > 0.5)
            {
                return 0.9;
            }

            var distance = LevenshteinDistance(a, b);
            var maxLen = Math.Max(a.Length, b.Length);
            return 1.0 - (double)distance / maxLen;
        }

        /// <summary>
        /// Delegates to Fastenshtein. Preserves historical shorter-first
        /// ordering and empty-string short-circuit so behaviour is
        /// argument-order-independent.
        /// </summary>
        internal static int LevenshteinDistance(string s, string t)
        {
            if (s.Length > t.Length)
                (s, t) = (t, s);

            if (s.Length == 0) return t.Length;

            return Fastenshtein.Levenshtein.Distance(s, t);
        }

        /// <summary>
        /// Normalizes a Google Maps URL for display / outbound-link purposes.
        /// Handles: http vs https, www vs non-www, trailing slashes, fragment
        /// removal, tracking parameter removal, percent-encoding normalization,
        /// CID/ftid extraction. Pure function.
        /// </summary>
        public static string NormalizeUrl(string url)
        {
            url = url.Trim();

            try
            {
                url = Uri.UnescapeDataString(url);
            }
            catch (FormatException)
            {
                // Malformed percent-encoding; proceed with the raw URL.
            }

            int fragIdx = url.IndexOf('#');
            if (fragIdx >= 0)
                url = url[..fragIdx];

            url = url.TrimEnd('/');

            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url[7..];

            if (url.StartsWith("https://www.", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url[12..];

            var cidMatch = CidParamRegex().Match(url);
            if (cidMatch.Success)
                return "cid:" + cidMatch.Groups[1].Value;

            var ftidMatch = FtidParamRegex().Match(url);
            if (ftidMatch.Success)
                return "ftid:" + ftidMatch.Groups[1].Value;

            url = RemoveTrackingParams(url);

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
        /// Removes common tracking query parameters (utm_*, hl, authuser,
        /// entry) from a URL.
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

            keepParams.Sort(StringComparer.Ordinal);
            return basePart + "?" + string.Join("&", keepParams);
        }

        [GeneratedRegex(@"[?&]cid=(\d+)", RegexOptions.IgnoreCase)]
        private static partial Regex CidParamRegex();

        [GeneratedRegex(@"[?&]ftid=(0x[0-9a-fA-F]+:0x[0-9a-fA-F]+)", RegexOptions.IgnoreCase)]
        private static partial Regex FtidParamRegex();
    }
}
