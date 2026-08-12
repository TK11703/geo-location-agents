using ERDC.Agents.Common;

namespace ERDC.Agents.Models;

public sealed record MapRequest
{
    private static readonly Dictionary<string, string> SupportedMapTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["road"] = "microsoft.base.road",
            ["dark"] = "microsoft.base.darkgrey",
            ["satellite"] = "microsoft.imagery"
        };

    private static readonly HashSet<string> SupportedOutputs =
        new(StringComparer.OrdinalIgnoreCase) { "image", "url" };

    public string? City { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? Zoom { get; init; }
    public int? RadiusMeters { get; init; }
    public string? MapType { get; init; }
    public string? Output { get; init; }

    public bool TryNormalize(out MapRenderRequest? request, out string? error)
    {
        request = null;
        error = null;

        var city = City?.Trim();
        var hasCity = !string.IsNullOrEmpty(city);
        var hasLatitude = Latitude.HasValue;
        var hasLongitude = Longitude.HasValue;

        if (hasCity && (hasLatitude || hasLongitude))
        {
            error = "Provide either city or latitude and longitude, not both.";
            return false;
        }

        if (!hasCity && !hasLatitude && !hasLongitude)
        {
            error = "Provide a city or both latitude and longitude.";
            return false;
        }

        if (!hasCity && hasLatitude != hasLongitude)
        {
            error = "Latitude and longitude must be provided together.";
            return false;
        }

        if (city?.Length > 200)
        {
            error = "City must be 200 characters or fewer.";
            return false;
        }

        if (Latitude is < -90 or > 90)
        {
            error = "Latitude must be between -90 and 90.";
            return false;
        }

        if (Longitude is < -180 or > 180)
        {
            error = "Longitude must be between -180 and 180.";
            return false;
        }

        var width = Width ?? 512;
        var height = Height ?? 512;
        var mapType = string.IsNullOrWhiteSpace(MapType)
            ? "road"
            : MapType.Trim().ToLowerInvariant();

        if (width is < 80 or > 2000)
        {
            error = "Width must be between 80 and 2000 pixels.";
            return false;
        }

        if (height is < 80 or > 1500)
        {
            error = "Height must be between 80 and 1500 pixels.";
            return false;
        }

        if (!SupportedMapTypes.TryGetValue(mapType, out var tilesetId))
        {
            error = "Map type must be road, dark, or satellite. Terrain and 3D are not supported for static map images.";
            return false;
        }

        var maxZoom = mapType == "satellite" ? 19 : 20;

        var output = string.IsNullOrWhiteSpace(Output)
            ? "image"
            : Output.Trim().ToLowerInvariant();

        if (!SupportedOutputs.Contains(output))
        {
            error = "Output must be image or url.";
            return false;
        }

        if (Zoom.HasValue && RadiusMeters.HasValue)
        {
            error = "Provide either zoom or radiusMeters, not both.";
            return false;
        }

        if (RadiusMeters.HasValue && hasCity)
        {
            error = "radiusMeters requires latitude and longitude. Use zoom with city.";
            return false;
        }

        if (RadiusMeters is < 25 or > 500000)
        {
            error = "radiusMeters must be between 25 and 500000.";
            return false;
        }

        int zoom;

        if (RadiusMeters is int radiusMeters)
        {
            var center = new GeoPoint(Latitude!.Value, Longitude!.Value);
            zoom = Math.Min(GeoMath.ZoomForRadius(center, radiusMeters, width, height), maxZoom);
        }
        else
        {
            zoom = Zoom ?? 12;

            if (zoom is < 0 or > 20)
            {
                error = "Zoom must be between 0 and 20.";
                return false;
            }

            if (zoom > maxZoom)
            {
                error = "Satellite zoom must be between 0 and 19.";
                return false;
            }
        }

        request = new MapRenderRequest(city, Latitude, Longitude, width, height, zoom, tilesetId, output);
        return true;
    }
}

public sealed record MapRenderRequest(
    string? City,
    double? Latitude,
    double? Longitude,
    int Width,
    int Height,
    int Zoom,
    string TilesetId = "microsoft.base.road",
    string Output = "image");