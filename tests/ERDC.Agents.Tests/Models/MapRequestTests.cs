using ERDC.Agents.Models;

namespace ERDC.Agents.Tests.Models;

public sealed class MapRequestTests
{
    [Fact]
    public void TryNormalize_AcceptsCityAndAppliesDefaults()
    {
        var input = new MapRequest { City = "  Seattle  " };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.True(valid);
        Assert.Null(error);
        Assert.Equal("Seattle", request!.City);
        Assert.Equal(512, request.Width);
        Assert.Equal(512, request.Height);
        Assert.Equal(12, request.Zoom);
        Assert.Equal("microsoft.base.road", request.TilesetId);
    }

    [Theory]
    [InlineData(" ROAD ", "microsoft.base.road")]
    [InlineData("dark", "microsoft.base.darkgrey")]
    [InlineData("Satellite", "microsoft.imagery")]
    public void TryNormalize_AcceptsSupportedMapType(string mapType, string expectedTilesetId)
    {
        var input = new MapRequest { City = "Seattle", MapType = mapType };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.True(valid);
        Assert.Null(error);
        Assert.Equal(expectedTilesetId, request!.TilesetId);
    }

    [Fact]
    public void TryNormalize_AcceptsCoordinates()
    {
        var input = new MapRequest { Latitude = 47.6062, Longitude = -122.3321 };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.True(valid);
        Assert.Null(error);
        Assert.Equal(47.6062, request!.Latitude);
        Assert.Equal(-122.3321, request.Longitude);
    }

    [Theory]
    [InlineData("Seattle", 47.6, -122.3, "Provide either city or latitude and longitude, not both.")]
    [InlineData(null, 47.6, null, "Latitude and longitude must be provided together.")]
    [InlineData(null, null, null, "Provide a city or both latitude and longitude.")]
    [InlineData(null, 91.0, 0.0, "Latitude must be between -90 and 90.")]
    [InlineData(null, 0.0, 181.0, "Longitude must be between -180 and 180.")]
    public void TryNormalize_RejectsInvalidLocations(
        string? city,
        double? latitude,
        double? longitude,
        string expectedError)
    {
        var input = new MapRequest
        {
            City = city,
            Latitude = latitude,
            Longitude = longitude
        };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.False(valid);
        Assert.Null(request);
        Assert.Equal(expectedError, error);
    }

    [Theory]
    [InlineData(79, 512, 12, "Width must be between 80 and 2000 pixels.")]
    [InlineData(512, 1501, 12, "Height must be between 80 and 1500 pixels.")]
    [InlineData(512, 512, 21, "Zoom must be between 0 and 20.")]
    public void TryNormalize_RejectsInvalidRenderingOptions(
        int width,
        int height,
        int zoom,
        string expectedError)
    {
        var input = new MapRequest
        {
            City = "Seattle",
            Width = width,
            Height = height,
            Zoom = zoom
        };

        var valid = input.TryNormalize(out _, out var error);

        Assert.False(valid);
        Assert.Equal(expectedError, error);
    }

    [Theory]
    [InlineData("terrain")]
    [InlineData("3d")]
    [InlineData("streets")]
    public void TryNormalize_RejectsUnsupportedMapType(string mapType)
    {
        var input = new MapRequest { City = "Seattle", MapType = mapType };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.False(valid);
        Assert.Null(request);
        Assert.Equal(
            "Map type must be road, dark, or satellite. Terrain and 3D are not supported for static map images.",
            error);
    }

    [Fact]
    public void TryNormalize_RejectsSatelliteZoomTwenty()
    {
        var input = new MapRequest { City = "Seattle", MapType = "satellite", Zoom = 20 };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.False(valid);
        Assert.Null(request);
        Assert.Equal("Satellite zoom must be between 0 and 19.", error);
    }

    [Theory]
    [InlineData(100, 18)]
    [InlineData(1000, 14)]
    [InlineData(25000, 10)]
    public void TryNormalize_DerivesZoomFromRadius(int radiusMeters, int expectedZoom)
    {
        var input = new MapRequest
        {
            Latitude = 47.6062,
            Longitude = -122.3321,
            RadiusMeters = radiusMeters
        };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.True(valid);
        Assert.Null(error);
        Assert.Equal(expectedZoom, request!.Zoom);
    }

    [Fact]
    public void TryNormalize_DerivedZoomKeepsRadiusInsideImage()
    {
        var input = new MapRequest
        {
            Latitude = 47.6062,
            Longitude = -122.3321,
            RadiusMeters = 250,
            Width = 512,
            Height = 512
        };

        input.TryNormalize(out var request, out _);

        var metersPerPixel = 156543.03392 * Math.Cos(47.6062 * Math.PI / 180) / Math.Pow(2, request!.Zoom);
        var halfImageMeters = metersPerPixel * request.Height / 2;

        Assert.True(halfImageMeters >= 250);
    }

    [Fact]
    public void TryNormalize_UsesSmallerImageSideForRadius()
    {
        var square = new MapRequest { Latitude = 0, Longitude = 0, RadiusMeters = 1000, Width = 512, Height = 512 };
        var wide = new MapRequest { Latitude = 0, Longitude = 0, RadiusMeters = 1000, Width = 2000, Height = 512 };

        square.TryNormalize(out var squareRequest, out _);
        wide.TryNormalize(out var wideRequest, out _);

        Assert.Equal(squareRequest!.Zoom, wideRequest!.Zoom);
    }

    [Fact]
    public void TryNormalize_CapsDerivedSatelliteZoomAtNineteen()
    {
        var input = new MapRequest
        {
            Latitude = 47.6062,
            Longitude = -122.3321,
            MapType = "satellite",
            RadiusMeters = 25
        };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.True(valid);
        Assert.Null(error);
        Assert.Equal(19, request!.Zoom);
    }

    [Fact]
    public void TryNormalize_RejectsZoomWithRadius()
    {
        var input = new MapRequest
        {
            Latitude = 47.6062,
            Longitude = -122.3321,
            Zoom = 15,
            RadiusMeters = 500
        };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.False(valid);
        Assert.Null(request);
        Assert.Equal("Provide either zoom or radiusMeters, not both.", error);
    }

    [Fact]
    public void TryNormalize_RejectsRadiusWithCity()
    {
        var input = new MapRequest { City = "Seattle", RadiusMeters = 500 };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.False(valid);
        Assert.Null(request);
        Assert.Equal("radiusMeters requires latitude and longitude. Use zoom with city.", error);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(500001)]
    public void TryNormalize_RejectsRadiusOutOfRange(int radiusMeters)
    {
        var input = new MapRequest
        {
            Latitude = 47.6062,
            Longitude = -122.3321,
            RadiusMeters = radiusMeters
        };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.False(valid);
        Assert.Null(request);
        Assert.Equal("radiusMeters must be between 25 and 500000.", error);
    }
}