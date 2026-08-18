namespace GeoLocation.Models;

public sealed record WeatherRequest
{
    private static readonly string[] SupportedUnits = ["metric", "imperial"];

    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? Unit { get; init; }

    public bool TryNormalize(out WeatherQuery? query, out string? error)
    {
        query = null;

        if (!CoordinateValidation.TryValidate(Latitude, Longitude, out error))
        {
            return false;
        }

        var unit = string.IsNullOrWhiteSpace(Unit) ? "metric" : Unit.Trim().ToLowerInvariant();
        if (!SupportedUnits.Contains(unit))
        {
            error = "unit must be metric or imperial.";
            return false;
        }

        query = new WeatherQuery(Latitude!.Value, Longitude!.Value, unit);
        return true;
    }
}

public sealed record WeatherQuery(double Latitude, double Longitude, string Unit);
