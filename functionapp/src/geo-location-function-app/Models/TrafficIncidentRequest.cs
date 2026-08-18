namespace GeoLocation.Models;

public sealed record TrafficIncidentRequest
{
    private const int DefaultRadiusMeters = 2000;
    private const int MinRadiusMeters = 50;
    private const int MaxRadiusMeters = 25000;

    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public int? RadiusMeters { get; init; }

    public bool TryNormalize(out TrafficIncidentQuery? query, out string? error)
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

        query = new TrafficIncidentQuery(Latitude!.Value, Longitude!.Value, radius);
        return true;
    }
}

public sealed record TrafficIncidentQuery(double Latitude, double Longitude, int RadiusMeters);
