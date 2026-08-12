using ERDC.Agents.Models;

namespace ERDC.Agents.Tests.Models;

public class TrafficIncidentRequestTests
{
    [Fact]
    public void TryNormalize_WithoutRadius_AppliesDefault()
    {
        var request = new TrafficIncidentRequest { Latitude = 47.6062, Longitude = -122.3321 };

        Assert.True(request.TryNormalize(out var query, out _));

        Assert.Equal(2000, query!.RadiusMeters);
    }

    [Fact]
    public void TryNormalize_WithRadius_KeepsRequestedRadius()
    {
        var request = new TrafficIncidentRequest
        {
            Latitude = 47.6062,
            Longitude = -122.3321,
            RadiusMeters = 500
        };

        Assert.True(request.TryNormalize(out var query, out _));

        Assert.Equal(500, query!.RadiusMeters);
    }

    [Theory]
    [InlineData(49)]
    [InlineData(25001)]
    public void TryNormalize_WithRadiusOutOfRange_Fails(int radiusMeters)
    {
        var request = new TrafficIncidentRequest
        {
            Latitude = 47.6062,
            Longitude = -122.3321,
            RadiusMeters = radiusMeters
        };

        Assert.False(request.TryNormalize(out var query, out var error));

        Assert.Null(query);
        Assert.Equal("radiusMeters must be between 50 and 25000.", error);
    }
}
