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
    /// Behavioural tests for the great-circle distance primitive.
    /// These pin real-world distances against independently-known
    /// values with tolerances large enough to survive any reasonable
    /// spherical-math library (Haversine with R=6,371,000, WGS84
    /// ellipsoidal Vincenty, or the Geolocation NuGet's variant), so
    /// the suite doesn't block future swaps — only implementations
    /// that are demonstrably wrong about the world's geometry.
    /// </summary>
    public class GeoUtilsPinningTests
    {
        [Theory]
        [InlineData(52.2297, 21.0122, 50.0647, 19.9450)]   // Warsaw ↔ Krakow
        [InlineData(50.0619, 19.9370, 50.0625, 19.9375)]   // Short urban pair
        [InlineData(90.0, 0.0, -90.0, 0.0)]                // Antipodes (poles)
        [InlineData(0.0, 0.0, 0.0, 1.0)]                   // 1 degree at equator
        [InlineData(48.8584, 2.2945, 40.7128, -74.0060)]   // Paris ↔ New York
        public void HaversineDistance_IsSymmetric(double lat1, double lon1, double lat2, double lon2)
        {
            var fwd = GeoUtils.HaversineDistance(lat1, lon1, lat2, lon2);
            var bwd = GeoUtils.HaversineDistance(lat2, lon2, lat1, lon1);
            // Symmetry is a mathematical property of great-circle distance
            // regardless of which library computes it; 1mm tolerance allows
            // for floating-point rounding inside the library.
            fwd.Should().BeApproximately(bwd, 0.001);
        }

        [Fact]
        public void HaversineDistance_Warsaw_Krakow_MatchesGroundTruth()
        {
            // Reference: great-circle distance ≈ 251.98 km per
            // movable-type.co.uk/scripts/latlong.html. ±100m tolerance
            // lets any reasonable spherical variant pass.
            var d = GeoUtils.HaversineDistance(52.2297, 21.0122, 50.0647, 19.9450);
            d.Should().BeApproximately(251_980, 100);
        }

        [Fact]
        public void HaversineDistance_ShortUrbanPair_IsTens_Of_Meters()
        {
            // Two points ~75m apart in Kraków — behavioural check that the
            // library returns sensible short-range distances, not whole
            // kilometers (unit-conversion bug) or near-zero (formula bug).
            var d = GeoUtils.HaversineDistance(50.0619, 19.9370, 50.0625, 19.9375);
            d.Should().BeInRange(60, 90);
        }

        [Fact]
        public void HaversineDistance_NorthPoleToSouthPole_IsHalfEarthCircumference()
        {
            // Pole-to-pole great-circle distance is π × R. For any
            // spherical-Earth library with R in [6,356km, 6,378km], this
            // lands between 19,960km and 20,037km. Widening to ±100km
            // keeps the check tolerant of ellipsoidal libraries too.
            var d = GeoUtils.HaversineDistance(90.0, 0.0, -90.0, 0.0);
            d.Should().BeApproximately(20_015_000, 100_000);
        }

        [Fact]
        public void HaversineDistance_OneDegreeAtEquator_IsAboutOneEleventh_Of_CircumferenceOver360()
        {
            // 1° longitude at the equator is exactly the equatorial
            // circumference / 360 ≈ 111.32 km (WGS84) or 111.19 km
            // (spherical R=6,371,000). ±200m tolerance.
            var d = GeoUtils.HaversineDistance(0.0, 0.0, 0.0, 1.0);
            d.Should().BeApproximately(111_200, 200);
        }

        [Fact]
        public void HaversineDistance_ParisToNewYork_IsAbout5833km()
        {
            // Reference: ~5,833 km per standard geodetic calculators.
            // ±5km tolerance absorbs spherical-vs-ellipsoidal drift for
            // a trans-Atlantic pair.
            var d = GeoUtils.HaversineDistance(48.8584, 2.2945, 40.7128, -74.0060);
            d.Should().BeApproximately(5_833_000, 5_000);
        }

        [Fact]
        public void HaversineDistance_SamePoint_ReturnsZero()
        {
            // Any sane distance function returns 0 for identical
            // coordinates — no tolerance needed.
            GeoUtils.HaversineDistance(52.2297, 21.0122, 52.2297, 21.0122).Should().Be(0.0);
        }
    }
}
