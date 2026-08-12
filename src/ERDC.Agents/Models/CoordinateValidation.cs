namespace ERDC.Agents.Models;

internal static class CoordinateValidation
{
    public static bool TryValidate(double? latitude, double? longitude, out string? error)
    {
        error = null;

        if (!latitude.HasValue || !longitude.HasValue)
        {
            error = "Both latitude and longitude are required.";
            return false;
        }

        if (latitude is < -90 or > 90)
        {
            error = "Latitude must be between -90 and 90.";
            return false;
        }

        if (longitude is < -180 or > 180)
        {
            error = "Longitude must be between -180 and 180.";
            return false;
        }

        return true;
    }
}
