using GeoLocation.Models;

namespace GeoLocation.Tests.Models;

public sealed class RouteRequestTests
{
    [Fact]
    public void TryNormalize_AcceptsTruckRoute()
    {
        var input = ValidRequest with { TravelMode = " Truck " };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.True(valid);
        Assert.Null(error);
        Assert.Equal("truck", request!.TravelMode);
        Assert.Equal(47.6062, request.OriginLatitude);
        Assert.Equal(-122.3088, request.DestinationLongitude);
        Assert.Null(request.Zoom);
    }

    [Fact]
    public void TryNormalize_AcceptsPartialTruckVehicleSpec()
    {
        var vehicleSpec = new TruckVehicleSpec
        {
            AxleCount = 5,
            IsVehicleCommercial = true
        };
        var input = ValidRequest with
        {
            TravelMode = "truck",
            VehicleSpec = vehicleSpec
        };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.True(valid);
        Assert.Null(error);
        Assert.Same(vehicleSpec, request!.VehicleSpec);
        Assert.Null(request.VehicleSpec.Weight);
    }

    [Fact]
    public void TryNormalize_RejectsVehicleSpecForNonTruckRoute()
    {
        var input = ValidRequest with
        {
            TravelMode = "car",
            VehicleSpec = new TruckVehicleSpec { AxleCount = 5 }
        };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.False(valid);
        Assert.Null(request);
        Assert.Equal("Vehicle specifications are only supported for truck routes.", error);
    }

    [Fact]
    public void TryNormalize_RejectsInvalidPartialVehicleSpec()
    {
        var input = ValidRequest with
        {
            TravelMode = "truck",
            VehicleSpec = new TruckVehicleSpec { MaxSpeed = 251 }
        };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.False(valid);
        Assert.Null(request);
        Assert.Equal("Vehicle maximum speed must be between 0 and 250 kilometers per hour.", error);
    }

    [Fact]
    public void TryNormalize_RejectsEmptyVehicleSpec()
    {
        var input = ValidRequest with
        {
            TravelMode = "truck",
            VehicleSpec = new TruckVehicleSpec()
        };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.False(valid);
        Assert.Null(request);
        Assert.Equal("Vehicle specifications must include at least one known value.", error);
    }

    [Fact]
    public void TryNormalize_AcceptsZoomOverride()
    {
        var input = ValidRequest with { Zoom = 14 };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.True(valid);
        Assert.Null(error);
        Assert.Equal(14, request!.Zoom);
    }

    [Fact]
    public void TryNormalize_DefaultsToCar()
    {
        var valid = ValidRequest.TryNormalize(out var request, out var error);

        Assert.True(valid);
        Assert.Null(error);
        Assert.Equal("car", request!.TravelMode);
        Assert.Equal("map", request.Output);
    }

    [Fact]
    public void TryNormalize_AcceptsDetailsOutput()
    {
        var input = ValidRequest with { Output = " Details " };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.True(valid);
        Assert.Null(error);
        Assert.Equal("details", request!.Output);
    }

    [Fact]
    public void TryNormalize_RejectsUnsupportedOutput()
    {
        var input = ValidRequest with { Output = "both" };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.False(valid);
        Assert.Null(request);
        Assert.Equal("Output must be map, details, or url.", error);
    }

    [Theory]
    [InlineData(null, -122.3321, 47.4502, -122.3088, "Origin and destination latitude and longitude are required.")]
    [InlineData(91.0, -122.3321, 47.4502, -122.3088, "Latitude must be between -90 and 90.")]
    [InlineData(47.6062, -181.0, 47.4502, -122.3088, "Longitude must be between -180 and 180.")]
    public void TryNormalize_RejectsInvalidCoordinates(
        double? originLatitude,
        double? originLongitude,
        double? destinationLatitude,
        double? destinationLongitude,
        string expectedError)
    {
        var input = new RouteRequest
        {
            OriginLatitude = originLatitude,
            OriginLongitude = originLongitude,
            DestinationLatitude = destinationLatitude,
            DestinationLongitude = destinationLongitude
        };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.False(valid);
        Assert.Null(request);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void TryNormalize_RejectsUnsupportedTravelMode()
    {
        var input = ValidRequest with { TravelMode = "bicycle" };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.False(valid);
        Assert.Null(request);
        Assert.Equal("Travel mode must be car, truck, or pedestrian.", error);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(21)]
    public void TryNormalize_RejectsInvalidZoom(int zoom)
    {
        var input = ValidRequest with { Zoom = zoom };

        var valid = input.TryNormalize(out var request, out var error);

        Assert.False(valid);
        Assert.Null(request);
        Assert.Equal("Zoom must be between 0 and 20.", error);
    }

    private static RouteRequest ValidRequest => new()
    {
        OriginLatitude = 47.6062,
        OriginLongitude = -122.3321,
        DestinationLatitude = 47.4502,
        DestinationLongitude = -122.3088
    };
}