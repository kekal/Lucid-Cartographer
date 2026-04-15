using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Operations;

namespace LucidCartographer.Tests
{
    /// <summary>
    /// Regression pinning tests for PoiMatcher + GeoUtils domain math.
    /// Locks in the exact dedup behavior and numeric outputs of the
    /// hand-rolled Levenshtein + Haversine implementations so that any
    /// third-party library swap (Fastenshtein / NetTopologySuite / etc.)
    /// can be validated to be behavior-preserving.
    ///
    /// These tests MUST pass both before and after the library swap.
    /// </summary>
    public class PoiMatcherPinningTests
    {
        private readonly PoiMatcher _matcher = new();

        private static Poi P(int id, string name, double lat, double lon, string? url = null) => new()
        {
            Id = id,
            Name = name,
            Latitude = lat,
            Longitude = lon,
            GoogleMapsUrl = url
        };

        /// <summary>
        /// Representative 10-POI fixture exercising:
        /// - Exact name + coord duplicates
        /// - Near-duplicates within tolerance with typos (Levenshtein path)
        /// - Substring-match name pairs (length-ratio guard)
        /// - URL-matched pair at different coords
        /// - URL-mismatched pair at same coords (must NOT match)
        /// - POIs far apart that must not merge
        /// </summary>
        private static List<Poi> BuildFixture() => new()
        {
            // Group A: same CID URL, different coords -> URL tier match
            P(1, "Rynek Glowny", 50.0619, 19.9370, "https://maps.google.com/?cid=12345"),
            P(2, "Main Square",  50.0625, 19.9375, "https://maps.google.com/?cid=12345"),

            // Group B: no URLs, near-duplicate names within 50m, Levenshtein path
            P(3, "Cafe Camelot",     50.0647, 19.9450),
            P(4, "Cafe Camellot",    50.0647, 19.9451), // typo, 1 edit
            P(5, "Cafe Camelot Bar", 50.0648, 19.9449), // substring-match

            // Isolated: same coord, different URLs -> must NOT match (Tier 1 rejection)
            P(6, "Place X", 52.2297, 21.0122, "https://maps.google.com/?cid=111"),
            P(7, "Place Y", 52.2297, 21.0122, "https://maps.google.com/?cid=222"),

            // Isolated: far from everything else
            P(8, "Eiffel Tower", 48.8584, 2.2945),

            // Group C: no URLs, exact same name + coord
            P(9,  "Wawel Castle", 50.0544, 19.9355),
            P(10, "Wawel Castle", 50.0544, 19.9355),
        };

        [Fact]
        public void FindDuplicateGroups_Fixture_ProducesExactGroups()
        {
            var pois = BuildFixture();

            var groups = _matcher.FindDuplicateGroups(pois);

            // Project to sorted id-sets for stable comparison
            var projected = groups
                .Select(g => g.Select(p => p.Id).OrderBy(i => i).ToArray())
                .OrderBy(a => a[0])
                .ToArray();

            projected.Should().HaveCount(3);
            projected[0].Should().Equal(new[] { 1, 2 });       // URL tier
            projected[1].Should().Equal(new[] { 3, 4, 5 });    // name-sim tier (transitive)
            projected[2].Should().Equal(new[] { 9, 10 });      // exact duplicate

            // POIs 6 and 7 must NOT be grouped (differing URLs at same coords)
            projected.SelectMany(x => x).Should().NotContain(6);
            projected.SelectMany(x => x).Should().NotContain(7);
            projected.SelectMany(x => x).Should().NotContain(8);
        }

        [Fact]
        public void IsMatch_UrlTierMismatch_AtSameCoord_ReturnsFalse()
        {
            var pois = BuildFixture();
            _matcher.IsMatch(pois[5], pois[6]).Should().BeFalse();
        }

        [Fact]
        public void IsMatch_UrlTierMatch_AtFarCoord_ReturnsTrue()
        {
            // Same CID but we still care that URL tier wins
            var a = P(1, "A", 50.0619, 19.9370, "https://maps.google.com/?cid=12345");
            var b = P(2, "B", 50.0625, 19.9375, "https://maps.google.com/?cid=12345");
            _matcher.IsMatch(a, b).Should().BeTrue();
        }

        [Fact]
        public void FindMatch_PicksClosestWithinTolerance()
        {
            var needle = P(100, "Cafe Camelot", 50.0647, 19.9450);
            var candidates = new[]
            {
                P(3, "Cafe Camelot",     50.0647, 19.9450),
                P(4, "Cafe Camellot",    50.0647, 19.9451),
                P(8, "Eiffel Tower",     48.8584, 2.2945),
            };

            var m = _matcher.FindMatch(needle, candidates);
            m!.Id.Should().Be(3);
        }

        // ----- Numeric pinning for the distance primitive -----
        // These values are captured from the current hand-rolled Levenshtein
        // implementation. A swap to Fastenshtein must preserve them exactly
        // because Fastenshtein returns the same integer edit distance.

        [Theory]
        [InlineData("cafe camelot", "cafe camellot", 1)]
        [InlineData("kitten",       "sitting",       3)]
        [InlineData("",             "abc",           3)]
        [InlineData("abc",          "",              3)]
        [InlineData("same",         "same",          0)]
        [InlineData("abcdef",       "azcdef",        1)]
        [InlineData("flaw",         "lawn",          2)]
        [InlineData("gumbo",        "gambol",        2)]
        public void LevenshteinDistance_Pinned(string a, string b, int expected)
        {
            PoiMatcher.LevenshteinDistance(a, b).Should().Be(expected);
        }

        [Fact]
        public void NameSimilarity_ExactMatch_IsOne()
        {
            PoiMatcher.NameSimilarity("Cafe Camelot", "cafe camelot").Should().Be(1.0);
        }

        [Fact]
        public void NameSimilarity_SingleTypo_LevenshteinPath()
        {
            // "cafe camelot" (12) vs "cafe camellot" (13), distance = 1, max = 13
            // similarity = 1 - 1/13 = 0.9230769230769231
            var sim = PoiMatcher.NameSimilarity("Cafe Camelot", "Cafe Camellot");
            sim.Should().BeApproximately(1.0 - 1.0 / 13.0, 1e-12);
        }

        [Fact]
        public void NameSimilarity_SubstringWithinHalfRatio_Returns0_9()
        {
            // "cafe camelot" contained in "cafe camelot bar", ratio 12/16 = 0.75 > 0.5
            PoiMatcher.NameSimilarity("Cafe Camelot", "Cafe Camelot Bar").Should().Be(0.9);
        }

        [Fact]
        public void NameSimilarity_SubstringBelowHalfRatio_FallsToLevenshtein()
        {
            // "ab" in "abcdefghij", ratio 2/10 = 0.2 <= 0.5 -> levenshtein path
            // distance = 8, maxLen = 10, sim = 0.2
            var sim = PoiMatcher.NameSimilarity("ab", "abcdefghij");
            sim.Should().BeApproximately(1.0 - 8.0 / 10.0, 1e-12);
        }
    }

    /// <summary>
    /// Numeric pinning for Haversine / geographic math.
    /// Values locked to 1e-6 meters against the current hand-rolled
    /// implementation so any library swap (NetTopologySuite, etc.)
    /// can be validated to be equivalent.
    /// </summary>
    public class GeoUtilsPinningTests
    {
        // These expected values are the exact double outputs produced by
        // the current GeoUtils.HaversineDistance implementation using
        // R = 6,371,000 m and the standard haversine formula in .NET 8.
        // They are captured by running the current implementation and
        // pasted back as the pinned oracle values.

        [Theory]
        // Warsaw -> Krakow
        [InlineData(52.2297, 21.0122, 50.0647, 19.9450)]
        // Small urban pair
        [InlineData(50.0619, 19.9370, 50.0625, 19.9375)]
        // Antipodes
        [InlineData(90.0, 0.0, -90.0, 0.0)]
        // Equator 1 degree longitude
        [InlineData(0.0, 0.0, 0.0, 1.0)]
        // Symmetric round-trip sanity
        [InlineData(48.8584, 2.2945, 40.7128, -74.0060)]
        public void HaversineDistance_IsSymmetric_Pinned(double lat1, double lon1, double lat2, double lon2)
        {
            var fwd = GeoUtils.HaversineDistance(lat1, lon1, lat2, lon2);
            var bwd = GeoUtils.HaversineDistance(lat2, lon2, lat1, lon1);
            fwd.Should().BeApproximately(bwd, 1e-9);
        }

        // Pinned absolute values below were captured from the current
        // hand-rolled GeoUtils.HaversineDistance implementation and are
        // the oracle values any library swap must match within 1e-6 m.

        [Fact]
        public void HaversineDistance_Warsaw_Krakow_Pinned()
        {
            var d = GeoUtils.HaversineDistance(52.2297, 21.0122, 50.0647, 19.9450);
            // Captured from current implementation
            d.Should().BeApproximately(251976.57791521866, 1e-6);
        }

        [Fact]
        public void HaversineDistance_Urban_Short_Pinned()
        {
            var d = GeoUtils.HaversineDistance(50.0619, 19.9370, 50.0625, 19.9375);
            d.Should().BeApproximately(75.66377675237395, 1e-6);
        }

        [Fact]
        public void HaversineDistance_Antipodes_Pinned()
        {
            var d = GeoUtils.HaversineDistance(90.0, 0.0, -90.0, 0.0);
            d.Should().BeApproximately(20015086.796020508, 1e-6);
        }

        [Fact]
        public void HaversineDistance_EquatorOneDegree_Pinned()
        {
            var d = GeoUtils.HaversineDistance(0.0, 0.0, 0.0, 1.0);
            d.Should().BeApproximately(111194.92664455873, 1e-6);
        }

        [Fact]
        public void HaversineDistance_Paris_NewYork_Pinned()
        {
            var d = GeoUtils.HaversineDistance(48.8584, 2.2945, 40.7128, -74.0060);
            d.Should().BeApproximately(5833246.628169387, 1e-6);
        }

        [Fact]
        public void HaversineDistance_SamePoint_ReturnsExactZero()
        {
            GeoUtils.HaversineDistance(52.2297, 21.0122, 52.2297, 21.0122).Should().Be(0.0);
        }
    }
}
