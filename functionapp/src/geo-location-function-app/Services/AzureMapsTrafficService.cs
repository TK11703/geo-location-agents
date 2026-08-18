using System.Globalization;
using System.Text.Json;
using GeoLocation.Common;
using GeoLocation.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace GeoLocation.Services;

public sealed class AzureMapsTrafficService(HttpClient httpClient, IConfiguration configuration)
    : ITrafficIncidentService
{
    private const string TrafficApiVersion = "2025-01-01";

    public async Task<TrafficIncidentResult> GetIncidentsAsync(
        TrafficIncidentQuery query,
        CancellationToken cancellationToken)
    {
        var center = new GeoPoint(query.Latitude, query.Longitude);
        var box = GeoMath.BoundingBox(center, query.RadiusMeters);
        var uri = QueryHelpers.AddQueryString(
            $"{GetEndpoint()}/traffic/incident",
            new Dictionary<string, string?>
            {
                ["api-version"] = TrafficApiVersion,
                ["bbox"] = FormattableString.Invariant(
                    $"{box.MinLongitude},{box.MinLatitude},{box.MaxLongitude},{box.MaxLatitude}")
            });

        using var message = CreateRequest(uri);
        using var response = await httpClient.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new AzureMapsException(
                "Azure Maps could not retrieve traffic incidents for the requested area.",
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var incidents = ReadIncidents(document.RootElement, center, query.RadiusMeters);

        return new TrafficIncidentResult(
            query.Latitude,
            query.Longitude,
            query.RadiusMeters,
            incidents.Count,
            incidents.Count(incident => incident.IsRoadClosed),
            incidents.Count == 0 ? null : incidents[0].DistanceMeters,
            incidents);
    }

    private static List<TrafficIncident> ReadIncidents(
        JsonElement root,
        GeoPoint center,
        int radiusMeters)
    {
        if (!root.TryGetProperty("features", out var features)
            || features.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var incidents = new List<TrafficIncident>();

        foreach (var feature in features.EnumerateArray())
        {
            if (!TryReadCoordinates(feature, out var location))
            {
                continue;
            }

            // The bounding box is square, so trim the corners back to the requested radius.
            var distance = GeoMath.DistanceMeters(center, location);
            if (distance > radiusMeters)
            {
                continue;
            }

            var properties = feature.TryGetProperty("properties", out var propertiesElement)
                ? propertiesElement
                : default;

            incidents.Add(new TrafficIncident(
                feature.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                    ? id.GetInt64()
                    : 0,
                ReadString(properties, "incidentType"),
                ReadString(properties, "title"),
                ReadString(properties, "description"),
                ReadInt(properties, "severity"),
                ReadBool(properties, "isRoadClosed"),
                ReadBool(properties, "isTrafficJam"),
                ReadDouble(properties, "delay"),
                ReadString(properties, "startTime"),
                ReadString(properties, "endTime"),
                location.Latitude,
                location.Longitude,
                Math.Round(distance, 1)));
        }

        incidents.Sort((left, right) => left.DistanceMeters.CompareTo(right.DistanceMeters));
        return incidents;
    }

    private static bool TryReadCoordinates(JsonElement feature, out GeoPoint location)
    {
        location = default;

        if (!feature.TryGetProperty("geometry", out var geometry)
            || !geometry.TryGetProperty("coordinates", out var coordinates)
            || coordinates.ValueKind != JsonValueKind.Array
            || coordinates.GetArrayLength() < 2)
        {
            return false;
        }

        location = new GeoPoint(coordinates[1].GetDouble(), coordinates[0].GetDouble());
        return true;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static int? ReadInt(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : null;

    private static double? ReadDouble(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : null;

    private static bool ReadBool(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.True;

    private HttpRequestMessage CreateRequest(string uri)
    {
        var subscriptionKey = configuration["AzureMaps:SubscriptionKey"];
        if (string.IsNullOrWhiteSpace(subscriptionKey))
        {
            throw new AzureMapsConfigurationException();
        }

        var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.Add("subscription-key", subscriptionKey);
        message.Headers.Accept.ParseAdd("application/geo+json");
        return message;
    }

    private string GetEndpoint() =>
        (configuration["AzureMaps:Endpoint"] ?? "https://atlas.microsoft.com").TrimEnd('/');
}
