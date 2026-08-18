using GeoLocation.Models;

namespace GeoLocation.Tests.Models;

public class CoordinateRequestTests
{
    [Fact]
    public void TryNormalize_WithValidCoordinates_ReturnsQuery()
    {
        var request = new ReverseGeocodeRequest { Latitude = 47.6062, Longitude = -122.3321 };

        Assert.True(request.TryNormalize(out var query, out var error));

        Assert.Null(error);
        Assert.Equal(47.6062, query!.Latitude);
        Assert.Equal(-122.3321, query.Longitude);
    }

    [Fact]
    public void TryNormalize_WithMissingLongitude_Fails()
    {
        var request = new ReverseGeocodeRequest { Latitude = 47.6062 };

        Assert.False(request.TryNormalize(out var query, out var error));

        Assert.Null(query);
        Assert.Equal("Both latitude and longitude are required.", error);
    }

    [Theory]
    [InlineData(90.1)]
    [InlineData(-90.1)]
    public void TryNormalize_WithLatitudeOutOfRange_Fails(double latitude)
    {
        var request = new ReverseGeocodeRequest { Latitude = latitude, Longitude = 0 };

        Assert.False(request.TryNormalize(out _, out var error));

        Assert.Equal("Latitude must be between -90 and 90.", error);
    }

    [Theory]
    [InlineData(180.1)]
    [InlineData(-180.1)]
    public void TryNormalize_WithLongitudeOutOfRange_Fails(double longitude)
    {
        var request = new ReverseGeocodeRequest { Latitude = 0, Longitude = longitude };

        Assert.False(request.TryNormalize(out _, out var error));

        Assert.Equal("Longitude must be between -180 and 180.", error);
    }

    [Fact]
    public void TryNormalize_NwsAlertRequestWithInvalidCoordinates_Fails()
    {
        var request = new NwsAlertRequest { Latitude = 91, Longitude = 0 };

        Assert.False(request.TryNormalize(out var query, out var error));

        Assert.Null(query);
        Assert.NotNull(error);
    }
}
