namespace GeoLocation.Models;

public sealed record ReverseGeocodeRequest
{
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    public bool TryNormalize(out ReverseGeocodeQuery? query, out string? error)
    {
        query = null;

        if (!CoordinateValidation.TryValidate(Latitude, Longitude, out error))
        {
            return false;
        }

        query = new ReverseGeocodeQuery(Latitude!.Value, Longitude!.Value);
        return true;
    }
}

public sealed record ReverseGeocodeQuery(double Latitude, double Longitude);
