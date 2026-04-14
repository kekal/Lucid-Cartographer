using FluentAssertions;
using LucidCartographer.Services;

namespace LucidCartographer.Tests
{
    public class GeoUtilsTests
    {
        [Fact]
        public void HaversineDistance_SamePoint_ReturnsZero()
        {
            var distance = GeoUtils.HaversineDistance(52.2297, 21.0122, 52.2297, 21.0122);

            distance.Should().Be(0);
        }

        [Fact]
        public void HaversineDistance_WarsawToKrakow_IsApproximately252km()
        {
            // Warsaw: 52.2297, 21.0122
            // Krakow: 50.0647, 19.9450
            var distance = GeoUtils.HaversineDistance(52.2297, 21.0122, 50.0647, 19.9450);

            var distanceKm = distance / 1000.0;
            distanceKm.Should().BeApproximately(252, 10);
        }

        [Fact]
        public void HaversineDistance_IsSymmetric()
        {
            double lat1 = 52.2297, lon1 = 21.0122;
            double lat2 = 50.0647, lon2 = 19.9450;

            var distanceAB = GeoUtils.HaversineDistance(lat1, lon1, lat2, lon2);
            var distanceBA = GeoUtils.HaversineDistance(lat2, lon2, lat1, lon1);

            distanceAB.Should().BeApproximately(distanceBA, 0.001);
        }

        [Fact]
        public void HaversineDistance_SmallDistance_WithinExpectedRange()
        {
            // Two points ~1060 meters apart (0.00955 degrees latitude at ~52N)
            double lat1 = 52.2297;
            double lon1 = 21.0122;
            double lat2 = 52.22015;
            double lon2 = 21.0122;

            var distance = GeoUtils.HaversineDistance(lat1, lon1, lat2, lon2);

            distance.Should().BeApproximately(1062, 50);
        }

        [Fact]
        public void HaversineDistance_AntipodalPoints_IsApproximately20000km()
        {
            // North Pole to South Pole
            var distance = GeoUtils.HaversineDistance(90, 0, -90, 0);

            var distanceKm = distance / 1000.0;
            distanceKm.Should().BeApproximately(20015, 20);
        }
    }
}
