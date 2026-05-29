using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Operations;

namespace LucidCartographer.Tests;

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

    // The /maps/place/ name segment is the *localized* display name and varies
    // by request language, so the matcher keys on the stable feature id
    // (!1s0x…:0x…) / KG mid (!16s…) instead. These tests pin that behaviour.

    // Same place, two different localized URL names (Russian vs Polish), but an
    // identical feature id — must match despite divergent names and personal labels.
    private const string ParkWilsonaRu =
        "https://www.google.com/maps/place/%D0%9F%D0%B0%D1%80%D0%BA+%D0%92%D0%B8%D0%BB%D1%8C%D1%81%D0%BE%D0%BD%D0%B0/@52.399486,16.8861559,15z/data=!4m6!3m5!1s0x470444d2e7e43305:0xc522afd5119f73c7!8m2!3d52.399486!4d16.9021787!16s%2Fg%2F1226snj_";
    private const string ParkWilsonaPl =
        "https://www.google.com/maps/place/Park+Wilsona/@52.399486,16.8861559,15z/data=!4m6!3m5!1s0x470444d2e7e43305:0xc522afd5119f73c7!8m2!3d52.399486!4d16.9021787!16s%2Fg%2F1226snj_";

    [Fact]
    public void IsMatch_SameFeatureId_MatchesAcrossLocalizedNames()
    {
        var a = CreatePoi(1, "Парк Вильсона", 52.399486, 16.9021787, ParkWilsonaRu);
        var b = CreatePoi(2, "Park Wilsona", 52.399490, 16.9021800, ParkWilsonaPl);

        _matcher.IsMatch(a, b).Should().BeTrue();
    }

    [Fact]
    public void IsMatch_DifferentFeatureId_DoesNotMatch_EvenWhenNameAndCoordsAlign()
    {
        // Same name, within tolerance, but different feature ids → distinct places.
        var a = CreatePoi(1, "Cafe", 52.4066425, 16.9351378,
            "https://www.google.com/maps/place/Cafe/@52.4066425,16.9351378,17z/data=!3m1!1s0x47045b3f13482675:0x4b73eb10afc87207");
        var b = CreatePoi(2, "Cafe", 52.4066500, 16.9351400,
            "https://www.google.com/maps/place/Cafe/@52.4066500,16.9351400,17z/data=!3m1!1s0x47045b3f13482675:0xDEADBEEFDEADBEEF");

        _matcher.IsMatch(a, b).Should().BeFalse();
    }

    [Fact]
    public void IsMatch_SameMid_WhenNoFeatureId_Matches()
    {
        // Neither URL has a feature id, but both carry the same KG mid.
        var a = CreatePoi(1, "X", 52.4, 16.9, "https://www.google.com/maps/place/X/data=!16s%2Fg%2F1226snj_");
        var b = CreatePoi(2, "Y", 52.6, 17.1, "https://www.google.com/maps/place/Y/data=!16s%2Fg%2F1226snj_");

        _matcher.IsMatch(a, b).Should().BeTrue();
    }

    [Fact]
    public void IsMatch_NoComparableId_FallsBackToNameAndProximity()
    {
        // A row with no place id (e.g. file import, no Google URL) falls back to
        // the Name + proximity rule, exactly as before.
        var a = CreatePoi(1, "Coffee Shop", 52.22970, 21.01220);
        var b = CreatePoi(2, "Coffee Shop", 52.22975, 21.01225);

        _matcher.IsMatch(a, b).Should().BeTrue();
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
        List<Poi> pois =
        [
            CreatePoi(1, "Coffee Shop", 52.22970, 21.01220),
            CreatePoi(2, "Coffee Shop", 52.22975, 21.01225),
            CreatePoi(3, "Restaurant", 50.0, 19.0)
        ];

        var groups = _matcher.FindDuplicateGroups(pois);

        groups.Should().HaveCount(1);
        groups[0].Should().HaveCount(2);
        groups[0].Select(p => p.Id).Should().Contain([1, 2]);
    }

    [Fact]
    public void FindDuplicateGroups_GroupsSameFeatureId_DespiteDivergentCoords()
    {
        // Same feature id but coords ~220m apart — well beyond the proximity
        // tolerance. The id tier must group them, proving it runs before (and
        // independently of) the latitude pre-filter.
        List<Poi> pois =
        [
            CreatePoi(1, "Парк Вильсона", 52.399486, 16.9021787, ParkWilsonaRu),
            CreatePoi(2, "Park Wilsona", 52.401500, 16.9050000, ParkWilsonaPl),
        ];

        var groups = _matcher.FindDuplicateGroups(pois);

        groups.Should().HaveCount(1);
        groups[0].Should().HaveCount(2);
    }

    [Fact]
    public void FindDuplicateGroups_UniquePois_ReturnsEmpty()
    {
        List<Poi> pois =
        [
            CreatePoi(1, "Coffee Shop", 52.2297, 21.0122),
            CreatePoi(2, "Bakery", 50.0647, 19.9450),
            CreatePoi(3, "Restaurant", 48.8566, 2.3522)
        ];

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