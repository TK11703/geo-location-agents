namespace GeoLocation.Models;

public sealed record NwsAlertRequest
{
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    public bool TryNormalize(out NwsAlertQuery? query, out string? error)
    {
        query = null;

        if (!CoordinateValidation.TryValidate(Latitude, Longitude, out error))
        {
            return false;
        }

        query = new NwsAlertQuery(Latitude!.Value, Longitude!.Value);
        return true;
    }
}

public sealed record NwsAlertQuery(double Latitude, double Longitude);
