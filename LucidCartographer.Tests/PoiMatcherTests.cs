using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Operations;

namespace LucidCartographer.Tests
{
    public class PoiMatcherTests
    {
        private readonly PoiMatcher _matcher = new();

        private static Poi CreatePoi(int id, string name, double lat, double lon, string? url = null) => new()
        {
            Id = id,
            Name = name,
            Latitude = lat,
            Longitude = lon,
            GoogleMapsUrl = url
        };

        [Fact]
        public void IsMatch_SameGoogleMapsUrl_DoesNotOverrideNameAndCoords()
        {
            // URL is no longer part of the identity rule. Two rows with the
            // same corporate URL (e.g. franchise branches) but different
            // names at different coords must stay distinct — that is the
            // whole reason the URL tier was removed.
            var a = CreatePoi(1, "Place A", 52.0, 21.0, "https://maps.google.com/place/abc");
            var b = CreatePoi(2, "Different Name", 10.0, 10.0, "https://maps.google.com/place/abc");

            _matcher.IsMatch(a, b).Should().BeFalse();
        }

        [Fact]
        public void IsMatch_SameNameWithinTolerance_ReturnsTrue()
        {
            // Two points ~10m apart
            var a = CreatePoi(1, "Coffee Shop", 52.22970, 21.01220);
            var b = CreatePoi(2, "Coffee Shop", 52.22975, 21.01225);

            _matcher.IsMatch(a, b).Should().BeTrue();
        }

        [Fact]
        public void IsMatch_SameNameFarApart_ReturnsFalse()
        {
            var a = CreatePoi(1, "Coffee Shop", 52.2297, 21.0122);
            var b = CreatePoi(2, "Coffee Shop", 50.0647, 19.9450);

            _matcher.IsMatch(a, b).Should().BeFalse();
        }

        [Fact]
        public void IsMatch_DifferentNameCloseTogether_ReturnsFalse()
        {
            var a = CreatePoi(1, "Coffee Shop", 52.22970, 21.01220);
            var b = CreatePoi(2, "Bakery XYZ", 52.22975, 21.01225);

            _matcher.IsMatch(a, b).Should().BeFalse();
        }

        [Fact]
        public void IsMatch_PlaceholderZeroCoords_NeverMatches()
        {
            // (0,0) is the placeholder that Google-list scrapes use until
            // enrichment fills real coordinates. PoiIdentity explicitly
            // excludes it so three "Plac zabaw" playgrounds can't collapse
            // into one by sharing the Gulf of Guinea.
            var a = CreatePoi(1, "Playground", 0, 0);
            var b = CreatePoi(2, "Playground", 0, 0);

            _matcher.IsMatch(a, b).Should().BeFalse();
        }

        [Fact]
        public void FindMatch_ReturnsMatchingPoi()
        {
            var poi = CreatePoi(1, "Coffee Shop", 52.22970, 21.01220);
            var candidates = new[]
            {
                CreatePoi(2, "Bakery", 52.22970, 21.01220),
                CreatePoi(3, "Coffee Shop", 52.22975, 21.01225),
                CreatePoi(4, "Restaurant", 52.22980, 21.01230)
            };

            var result = _matcher.FindMatch(poi, candidates);

            result.Should().NotBeNull();
            result!.Id.Should().Be(3);
        }

        [Fact]
        public void FindMatch_NoMatch_ReturnsNull()
        {
            var poi = CreatePoi(1, "Coffee Shop", 52.2297, 21.0122);
            var candidates = new[]
            {
                CreatePoi(2, "Bakery", 52.2297, 21.0122),
                CreatePoi(3, "Restaurant", 50.0, 19.0)
            };

            var result = _matcher.FindMatch(poi, candidates);

            result.Should().BeNull();
        }

        [Fact]
        public void FindDuplicateGroups_FindsGroupsOfNearbySameNamePois()
        {
            var pois = new List<Poi>
            {
                CreatePoi(1, "Coffee Shop", 52.22970, 21.01220),
                CreatePoi(2, "Coffee Shop", 52.22975, 21.01225),
                CreatePoi(3, "Restaurant", 50.0, 19.0)
            };

            var groups = _matcher.FindDuplicateGroups(pois);

            groups.Should().HaveCount(1);
            groups[0].Should().HaveCount(2);
            groups[0].Select(p => p.Id).Should().Contain(new[] { 1, 2 });
        }

        [Fact]
        public void FindDuplicateGroups_UniquePois_ReturnsEmpty()
        {
            var pois = new List<Poi>
            {
                CreatePoi(1, "Coffee Shop", 52.2297, 21.0122),
                CreatePoi(2, "Bakery", 50.0647, 19.9450),
                CreatePoi(3, "Restaurant", 48.8566, 2.3522)
            };

            var groups = _matcher.FindDuplicateGroups(pois);

            groups.Should().BeEmpty();
        }

        [Fact]
        public void NameSimilarity_ContainsMatch_IsMatch()
        {
            // "Coffee Shop" contains within "The Coffee Shop Downtown" triggers contains check
            var a = CreatePoi(1, "Coffee Shop", 52.22970, 21.01220);
            var b = CreatePoi(2, "Coffee Shop Downtown", 52.22975, 21.01225);

            _matcher.IsMatch(a, b).Should().BeTrue();
        }

        [Fact]
        public void NameSimilarity_SimilarNames_IsMatch()
        {
            // Minor typo: "Coffe Shop" vs "Coffee Shop" -> Levenshtein = 1, similarity high
            var a = CreatePoi(1, "Coffe Shop", 52.22970, 21.01220);
            var b = CreatePoi(2, "Coffee Shop", 52.22975, 21.01225);

            _matcher.IsMatch(a, b).Should().BeTrue();
        }

        [Fact]
        public void IsMatch_OneHasUrlOtherDoesNot_StillMatchesOnNameAndProximity()
        {
            // The presence or absence of a URL on either side is irrelevant
            // — the rule is always name similarity + coord proximity.
            var a = CreatePoi(1, "Coffee Shop", 52.22970, 21.01220, "https://maps.google.com/place/abc");
            var b = CreatePoi(2, "Coffee Shop", 52.22975, 21.01225); // No URL

            _matcher.IsMatch(a, b).Should().BeTrue();
        }

        [Fact]
        public void IsMatch_SameNameAndCoords_DifferentUrls_StillMatches()
        {
            // URL is not part of the rule. Two rows at the same coords with
            // the same name match regardless of URL — they almost certainly
            // represent the same real place (e.g. one from a KML import
            // and one from a Google Maps scrape with different canonical
            // URL shapes).
            var a = CreatePoi(1, "Coffee Shop", 52.22970, 21.01220, "https://maps.google.com/place/abc");
            var b = CreatePoi(2, "Coffee Shop", 52.22970, 21.01220, "https://maps.google.com/place/xyz");

            _matcher.IsMatch(a, b).Should().BeTrue();
        }

        [Fact]
        public void FindMatch_MultipleMatches_ReturnsBestByDistance()
        {
            var poi = CreatePoi(1, "Coffee Shop", 52.22970, 21.01220);
            var candidates = new[]
            {
                CreatePoi(2, "Coffee Shop", 52.22980, 21.01230), // Further away
                CreatePoi(3, "Coffee Shop", 52.22972, 21.01222), // Closest
                CreatePoi(4, "Coffee Shop", 52.22975, 21.01225)  // Medium distance
            };

            var result = _matcher.FindMatch(poi, candidates);

            result.Should().NotBeNull();
            result!.Id.Should().Be(3); // Closest match
        }

        [Fact]
        public void NameSimilarity_EmptyName_ReturnsZero()
        {
            // Empty names should have zero similarity
            var a = CreatePoi(1, "", 52.22970, 21.01220);
            var b = CreatePoi(2, "Coffee Shop", 52.22975, 21.01225);

            _matcher.IsMatch(a, b).Should().BeFalse();
        }
    }
}
