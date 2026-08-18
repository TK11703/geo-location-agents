namespace GeoLocation.Models;

public sealed record ElevationRequest
{
    private const int DefaultRadiusMeters = 100;
    private const int MinRadiusMeters = 10;
    private const int MaxRadiusMeters = 5000;
    private const int DefaultSampleCount = 8;
    private const int MinSampleCount = 4;
    private const int MaxSampleCount = 16;

    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public int? RadiusMeters { get; init; }
    public int? SampleCount { get; init; }

    public bool TryNormalize(out ElevationQuery? query, out string? error)
    {
        query = null;

        if (!CoordinateValidation.TryValidate(Latitude, Longitude, out error))
        {
            return false;
        }

        var radius = RadiusMeters ?? DefaultRadiusMeters;
        if (radius is < MinRadiusMeters or > MaxRadiusMeters)
        {
            error = $"radiusMeters must be between {MinRadiusMeters} and {MaxRadiusMeters}.";
            return false;
        }

        var sampleCount = SampleCount ?? DefaultSampleCount;
        if (sampleCount is < MinSampleCount or > MaxSampleCount)
        {
            error = $"sampleCount must be between {MinSampleCount} and {MaxSampleCount}.";
            return false;
        }

        query = new ElevationQuery(Latitude!.Value, Longitude!.Value, radius, sampleCount);
        return true;
    }
}

public sealed record ElevationQuery(
    double Latitude,
    double Longitude,
    int RadiusMeters,
    int SampleCount);
