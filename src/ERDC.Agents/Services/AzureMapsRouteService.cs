using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ERDC.Agents.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace ERDC.Agents.Services;

public sealed class AzureMapsRouteService(HttpClient httpClient, IConfiguration configuration)
    : IMapRouteService
{
    private const string RouteApiVersion = "2025-01-01";
    private const string RenderApiVersion = "2024-04-01";
    private const int MaxLocationsPerPath = 100;
    private const int MaxPathParameters = 10;
    private const int MaxRenderedLocations = 200;
    private const int ImageWidth = 800;
    private const int ImageHeight = 600;
    private const int TileSize = 256;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<MapImage> GetRouteImageAsync(
        RouteCalculationRequest request,
        CancellationToken cancellationToken)
    {
        using var route = await CalculateRouteAsync(request, cancellationToken);
        var coordinates = ReduceCoordinates(ExtractRouteCoordinates(route.RootElement));
        return await RenderRouteAsync(coordinates, request, cancellationToken);
    }

    public async Task<JsonElement> GetRouteDetailsAsync(
        RouteCalculationRequest request,
        CancellationToken cancellationToken)
    {
        using var route = await CalculateRouteAsync(request, cancellationToken);
        return route.RootElement.Clone();
    }

    private async Task<JsonDocument> CalculateRouteAsync(
        RouteCalculationRequest request,
        CancellationToken cancellationToken)
    {
        var uri = QueryHelpers.AddQueryString(
            $"{GetEndpoint()}/route/directions",
            new Dictionary<string, string?>
            {
                ["api-version"] = RouteApiVersion
            });

        var routeRequest = new GeoJsonFeatureCollection(
            "FeatureCollection",
            [
                CreateWaypoint(0, request.OriginLongitude, request.OriginLatitude),
                CreateWaypoint(1, request.DestinationLongitude, request.DestinationLatitude)
            ],
            GetAzureMapsTravelMode(request.TravelMode),
            ["routePath", "itinerary"],
            request.VehicleSpec);

        using var message = CreateRequest(HttpMethod.Post, uri, "application/geo+json");
        message.Content = JsonContent.Create(
            routeRequest,
            new MediaTypeHeaderValue("application/geo+json"),
            JsonOptions);

        using var response = await httpClient.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AzureMapsException(
                "Azure Maps could not calculate the requested route.",
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var coordinates = ExtractRouteCoordinates(document.RootElement);
        if (coordinates.Count < 2)
        {
            document.Dispose();
            throw new AzureMapsException(
                "Azure Maps returned no route geometry.",
                HttpStatusCode.BadGateway);
        }

        return document;
    }

    private async Task<MapImage> RenderRouteAsync(
        List<RouteCoordinate> coordinates,
        RouteCalculationRequest request,
        CancellationToken cancellationToken)
    {
        var viewport = CreateViewport(coordinates);
        var uri = QueryHelpers.AddQueryString(
            $"{GetEndpoint()}/map/static",
            new Dictionary<string, string?>
            {
                ["api-version"] = RenderApiVersion,
                ["tilesetId"] = "microsoft.base.road",
                ["center"] = FormatCoordinate(viewport.Center),
                ["zoom"] = (request.Zoom ?? viewport.Zoom).ToString(CultureInfo.InvariantCulture),
                ["width"] = ImageWidth.ToString(CultureInfo.InvariantCulture),
                ["height"] = ImageHeight.ToString(CultureInfo.InvariantCulture),
                ["pins"] = FormattableString.Invariant(
                    $"default||{request.OriginLongitude} {request.OriginLatitude}|{request.DestinationLongitude} {request.DestinationLatitude}")
            });

        foreach (var path in CreatePaths(coordinates))
        {
            uri = QueryHelpers.AddQueryString(uri, "path", path);
        }

        using var message = CreateRequest(HttpMethod.Get, uri, "image/png");
        using var response = await httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AzureMapsException(
                "Azure Maps could not render the requested route.",
                response.StatusCode);
        }

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/png";
        return new MapImage(content, contentType);
    }

    private static GeoJsonFeature CreateWaypoint(int pointIndex, double longitude, double latitude) =>
        new(
            "Feature",
            new GeoJsonPoint("Point", [longitude, latitude]),
            new WaypointProperties(pointIndex, "waypoint"));

    private static string GetAzureMapsTravelMode(string travelMode) => travelMode switch
    {
        "car" => "driving",
        "pedestrian" => "walking",
        _ => travelMode
    };

    private static List<RouteCoordinate> ExtractRouteCoordinates(JsonElement response)
    {
        var coordinates = new List<RouteCoordinate>();
        if (!response.TryGetProperty("features", out var features))
        {
            return coordinates;
        }

        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("properties", out var properties)
                || !properties.TryGetProperty("type", out var featureType)
                || featureType.GetString() != "RoutePath"
                || !feature.TryGetProperty("geometry", out var geometry)
                || !geometry.TryGetProperty("coordinates", out var lineStrings))
            {
                continue;
            }

            foreach (var lineString in lineStrings.EnumerateArray())
            {
                foreach (var position in lineString.EnumerateArray())
                {
                    coordinates.Add(new RouteCoordinate(
                        position[0].GetDouble(),
                        position[1].GetDouble()));
                }
            }
        }

        return coordinates;
    }

    private static List<RouteCoordinate> ReduceCoordinates(List<RouteCoordinate> coordinates)
    {
        var maxLocations = Math.Min(
            MaxRenderedLocations,
            MaxPathParameters * (MaxLocationsPerPath - 1) + 1);
        if (coordinates.Count <= maxLocations)
        {
            return coordinates;
        }

        var reduced = new List<RouteCoordinate>(maxLocations);
        for (var index = 0; index < maxLocations; index++)
        {
            var sourceIndex = (int)Math.Round(
                index * (coordinates.Count - 1d) / (maxLocations - 1));
            reduced.Add(coordinates[sourceIndex]);
        }

        return reduced;
    }

    private static IEnumerable<string> CreatePaths(List<RouteCoordinate> coordinates)
    {
        for (var start = 0; start < coordinates.Count - 1; start += MaxLocationsPerPath - 1)
        {
            var count = Math.Min(MaxLocationsPerPath, coordinates.Count - start);
            var locations = string.Join(
                '|',
                coordinates.GetRange(start, count).Select(FormatPathCoordinate));
            yield return $"lc0078D4|lw5|la0.85||{locations}";
        }
    }

    private static MapViewport CreateViewport(List<RouteCoordinate> coordinates)
    {
        var minLongitude = coordinates.Min(point => point.Longitude);
        var maxLongitude = coordinates.Max(point => point.Longitude);
        var minLatitude = coordinates.Min(point => point.Latitude);
        var maxLatitude = coordinates.Max(point => point.Latitude);
        var longitudeSpan = Math.Max((maxLongitude - minLongitude) / 360d, double.Epsilon);
        var northY = ToMercatorY(maxLatitude);
        var southY = ToMercatorY(minLatitude);
        var latitudeSpan = Math.Max(southY - northY, double.Epsilon);
        var longitudeZoom = Math.Log2(ImageWidth / (TileSize * longitudeSpan));
        var latitudeZoom = Math.Log2(ImageHeight / (TileSize * latitudeSpan));
        var zoom = Math.Clamp((int)Math.Floor(Math.Min(longitudeZoom, latitudeZoom)) - 1, 0, 20);
        var centerY = (northY + southY) / 2;

        return new MapViewport(
            new RouteCoordinate(
                (minLongitude + maxLongitude) / 2,
                FromMercatorY(centerY)),
            zoom);
    }

    private static double ToMercatorY(double latitude)
    {
        var radians = Math.Clamp(latitude, -85.05112878, 85.05112878) * Math.PI / 180;
        return (1 - Math.Log(Math.Tan(radians) + 1 / Math.Cos(radians)) / Math.PI) / 2;
    }

    private static double FromMercatorY(double y) =>
        Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y))) * 180 / Math.PI;

    private static string FormatCoordinate(RouteCoordinate coordinate) =>
        FormattableString.Invariant($"{coordinate.Longitude},{coordinate.Latitude}");

    private static string FormatPathCoordinate(RouteCoordinate coordinate) =>
        FormattableString.Invariant($"{coordinate.Longitude} {coordinate.Latitude}");

    private HttpRequestMessage CreateRequest(HttpMethod method, string uri, string accept)
    {
        var subscriptionKey = configuration["AzureMaps:SubscriptionKey"];
        if (string.IsNullOrWhiteSpace(subscriptionKey))
        {
            throw new AzureMapsConfigurationException();
        }

        var message = new HttpRequestMessage(method, uri);
        message.Headers.Add("subscription-key", subscriptionKey);
        message.Headers.Accept.ParseAdd(accept);
        return message;
    }

    private string GetEndpoint() =>
        (configuration["AzureMaps:Endpoint"] ?? "https://atlas.microsoft.com").TrimEnd('/');

    private sealed record GeoJsonFeatureCollection(
        string Type,
        GeoJsonFeature[] Features,
        string TravelMode,
        string[] RouteOutputOptions,
        TruckVehicleSpec? VehicleSpec);
    private sealed record GeoJsonFeature(
        string Type,
        GeoJsonPoint Geometry,
        WaypointProperties Properties);
    private sealed record GeoJsonPoint(string Type, double[] Coordinates);
    private sealed record WaypointProperties(int PointIndex, string PointType);
    private sealed record RouteCoordinate(double Longitude, double Latitude);
    private sealed record MapViewport(RouteCoordinate Center, int Zoom);
}