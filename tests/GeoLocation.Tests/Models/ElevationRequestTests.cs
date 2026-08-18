using GeoLocation.Models;

namespace GeoLocation.Tests.Models;

public class ElevationRequestTests
{
    [Fact]
    public void TryNormalize_WithoutOptionalValues_AppliesDefaults()
    {
        var request = new ElevationRequest { Latitude = 47.6062, Longitude = -122.3321 };

        Assert.True(request.TryNormalize(out var query, out _));

        Assert.Equal(100, query!.RadiusMeters);
        Assert.Equal(8, query.SampleCount);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(5001)]
    public void TryNormalize_WithRadiusOutOfRange_Fails(int radiusMeters)
    {
        var request = new ElevationRequest
        {
            Latitude = 47.6062,
            Longitude = -122.3321,
            RadiusMeters = radiusMeters
        };

        Assert.False(request.TryNormalize(out _, out var error));

        Assert.Equal("radiusMeters must be between 10 and 5000.", error);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    public void TryNormalize_WithSampleCountOutOfRange_Fails(int sampleCount)
    {
        var request = new ElevationRequest
        {
            Latitude = 47.6062,
            Longitude = -122.3321,
            SampleCount = sampleCount
        };

        Assert.False(request.TryNormalize(out _, out var error));

        Assert.Equal("sampleCount must be between 4 and 16.", error);
    }
}
