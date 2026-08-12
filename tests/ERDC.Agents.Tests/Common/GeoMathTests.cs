using ERDC.Agents.Common;

namespace ERDC.Agents.Tests.Common;

public class GeoMathTests
{
    [Fact]
    public void BoundingBox_AtEquator_IsRoughlySymmetric()
    {
        var box = GeoMath.BoundingBox(new GeoPoint(0, 0), 1000);

        Assert.Equal(0.00898, box.MaxLatitude, 4);
        Assert.Equal(-0.00898, box.MinLatitude, 4);
        Assert.Equal(0.00898, box.MaxLongitude, 4);
        Assert.Equal(-0.00898, box.MinLongitude, 4);
    }

    [Fact]
    public void BoundingBox_AtHighLatitude_WidensLongitudeSpan()
    {
        var box = GeoMath.BoundingBox(new GeoPoint(60, 0), 1000);

        var latitudeSpan = box.MaxLatitude - box.MinLatitude;
        var longitudeSpan = box.MaxLongitude - box.MinLongitude;

        Assert.True(longitudeSpan > latitudeSpan * 1.9);
    }

    [Fact]
    public void BoundingBox_NearPole_StaysWithinValidRange()
    {
        var box = GeoMath.BoundingBox(new GeoPoint(89.999, 179.999), 50000);

        Assert.True(box.MaxLatitude <= 90);
        Assert.True(box.MinLatitude >= -90);
        Assert.True(box.MaxLongitude <= 180);
        Assert.True(box.MinLongitude >= -180);
    }

    [Fact]
    public void DistanceMeters_ForSamePoint_IsZero()
    {
        var distance = GeoMath.DistanceMeters(new GeoPoint(47.6062, -122.3321), new GeoPoint(47.6062, -122.3321));

        Assert.Equal(0, distance, 6);
    }

    [Fact]
    public void DistanceMeters_ForKnownPair_MatchesExpectedDistance()
    {
        var seattle = new GeoPoint(47.6062, -122.3321);
        var portland = new GeoPoint(45.5152, -122.6784);

        var distance = GeoMath.DistanceMeters(seattle, portland);

        Assert.InRange(distance, 232_000, 236_000);
    }

    [Fact]
    public void Offset_MovesTheRequestedDistance()
    {
        var origin = new GeoPoint(47.6062, -122.3321);

        var moved = GeoMath.Offset(origin, 90, 500);

        Assert.Equal(500, GeoMath.DistanceMeters(origin, moved), 0);
    }

    [Fact]
    public void Offset_NorthBearing_IncreasesLatitude()
    {
        var origin = new GeoPoint(47.6062, -122.3321);

        var moved = GeoMath.Offset(origin, 0, 500);

        Assert.True(moved.Latitude > origin.Latitude);
        Assert.Equal(origin.Longitude, moved.Longitude, 6);
    }

    [Fact]
    public void RingBearings_ProducesEvenlySpacedBearings()
    {
        var bearings = GeoMath.RingBearings(4);

        Assert.Equal([0d, 90d, 180d, 270d], bearings);
    }

    [Fact]
    public void SlopePercent_ForKnownRiseAndRun_ReturnsPercentage()
    {
        Assert.Equal(10, GeoMath.SlopePercent(10, 100));
    }

    [Fact]
    public void SlopePercent_ForDescent_ReturnsPositiveMagnitude()
    {
        Assert.Equal(10, GeoMath.SlopePercent(-10, 100));
    }

    [Fact]
    public void SlopePercent_ForZeroRun_ReturnsZero()
    {
        Assert.Equal(0, GeoMath.SlopePercent(10, 0));
    }
}
