namespace GeoLocation.Models;

public sealed record RouteRequest
{
    private static readonly HashSet<string> SupportedTravelModes =
        new(StringComparer.OrdinalIgnoreCase) { "car", "truck", "pedestrian" };
    private static readonly HashSet<string> SupportedOutputs =
        new(StringComparer.OrdinalIgnoreCase) { "map", "details", "url" };

    public double? OriginLatitude { get; init; }
    public double? OriginLongitude { get; init; }
    public double? DestinationLatitude { get; init; }
    public double? DestinationLongitude { get; init; }
    public string? TravelMode { get; init; }
    public int? Zoom { get; init; }
    public TruckVehicleSpec? VehicleSpec { get; init; }
    public string? Output { get; init; }

    public bool TryNormalize(out RouteCalculationRequest? request, out string? error)
    {
        request = null;
        error = null;

        if (!OriginLatitude.HasValue
            || !OriginLongitude.HasValue
            || !DestinationLatitude.HasValue
            || !DestinationLongitude.HasValue)
        {
            error = "Origin and destination latitude and longitude are required.";
            return false;
        }

        if (OriginLatitude is < -90 or > 90 || DestinationLatitude is < -90 or > 90)
        {
            error = "Latitude must be between -90 and 90.";
            return false;
        }

        if (OriginLongitude is < -180 or > 180 || DestinationLongitude is < -180 or > 180)
        {
            error = "Longitude must be between -180 and 180.";
            return false;
        }

        var travelMode = string.IsNullOrWhiteSpace(TravelMode)
            ? "car"
            : TravelMode.Trim().ToLowerInvariant();

        if (!SupportedTravelModes.Contains(travelMode))
        {
            error = "Travel mode must be car, truck, or pedestrian.";
            return false;
        }

        if (Zoom is < 0 or > 20)
        {
            error = "Zoom must be between 0 and 20.";
            return false;
        }

        var output = string.IsNullOrWhiteSpace(Output)
            ? "map"
            : Output.Trim().ToLowerInvariant();

        if (!SupportedOutputs.Contains(output))
        {
            error = "Output must be map, details, or url.";
            return false;
        }

        if (VehicleSpec is not null && travelMode != "truck")
        {
            error = "Vehicle specifications are only supported for truck routes.";
            return false;
        }

        if (VehicleSpec is not null && !VehicleSpec.TryValidate(out error))
        {
            return false;
        }

        request = new RouteCalculationRequest(
            OriginLatitude.Value,
            OriginLongitude.Value,
            DestinationLatitude.Value,
            DestinationLongitude.Value,
            travelMode,
            Zoom,
            VehicleSpec,
            output);
        return true;
    }
}

public sealed record RouteCalculationRequest(
    double OriginLatitude,
    double OriginLongitude,
    double DestinationLatitude,
    double DestinationLongitude,
    string TravelMode,
    int? Zoom = null,
    TruckVehicleSpec? VehicleSpec = null,
    string Output = "map");

public sealed record TruckVehicleSpec
{
    private static readonly HashSet<string> SupportedLoadTypes =
        new(StringComparer.Ordinal)
        {
            "USHazmatClass1",
            "USHazmatClass2",
            "USHazmatClass3",
            "USHazmatClass4",
            "USHazmatClass5",
            "USHazmatClass6",
            "USHazmatClass7",
            "USHazmatClass8",
            "USHazmatClass9",
            "otherHazmatExplosive",
            "otherHazmatGeneral",
            "otherHazmatHarmfulToWater"
        };

    public int? AxleCount { get; init; }
    public int? AxleWeight { get; init; }
    public double? Height { get; init; }
    public bool? IsVehicleCommercial { get; init; }
    public double? Length { get; init; }
    public string[]? LoadType { get; init; }
    public int? MaxSpeed { get; init; }
    public int? Weight { get; init; }
    public double? Width { get; init; }

    public bool TryValidate(out string? error)
    {
        error = null;

        if (AxleCount is null
            && AxleWeight is null
            && Height is null
            && IsVehicleCommercial is null
            && Length is null
            && LoadType is null
            && MaxSpeed is null
            && Weight is null
            && Width is null)
        {
            error = "Vehicle specifications must include at least one known value.";
        }
        else if (AxleCount is <= 0)
        {
            error = "Vehicle axle count must be greater than zero.";
        }
        else if (AxleWeight is < 0 or > 1_000_000 || Weight is < 0 or > 1_000_000)
        {
            error = "Vehicle weight and axle weight must be between 0 and 1000000 kilograms.";
        }
        else if (Height is < 0 or > 1_000_000
                 || Length is < 0 or > 1_000_000
                 || Width is < 0 or > 1_000_000)
        {
            error = "Vehicle height, length, and width must be between 0 and 1000000 meters.";
        }
        else if (MaxSpeed is < 0 or > 250)
        {
            error = "Vehicle maximum speed must be between 0 and 250 kilometers per hour.";
        }
        else if (LoadType is { Length: 0 }
                 || LoadType?.Any(value => !SupportedLoadTypes.Contains(value)) is true)
        {
            error = "Vehicle load type contains an unsupported Azure Maps cargo classification.";
        }

        return error is null;
    }
}