using System.Globalization;
using System.Net;
using System.Text.Json;
using GeoLocation.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace GeoLocation.Services;

public sealed class NwsAlertService(HttpClient httpClient, IConfiguration configuration) : INwsAlertService
{
    // Ordered least to most urgent so the highest value present wins.
    private static readonly string[] SeverityRanking =
        ["Unknown", "Minor", "Moderate", "Severe", "Extreme"];

    public async Task<NwsAlertResult> GetActiveAlertsAsync(
        NwsAlertQuery query,
        CancellationToken cancellationToken)
    {
        var latitude = query.Latitude.ToString("0.####", CultureInfo.InvariantCulture);
        var longitude = query.Longitude.ToString("0.####", CultureInfo.InvariantCulture);
        var uri = QueryHelpers.AddQueryString(
            $"{GetEndpoint()}/alerts/active",
            new Dictionary<string, string?>
            {
                ["point"] = $"{latitude},{longitude}"
            });

        using var message = CreateRequest(uri);
        using var response = await httpClient.SendAsync(message, cancellationToken);

        // The NWS only covers the United States and its territories, and rejects points outside it.
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            return new NwsAlertResult(
                query.Latitude,
                query.Longitude,
                IsWithinCoverage: false,
                "The National Weather Service does not cover this location. It serves the United States and its territories only.",
                AlertCount: 0,
                "Unknown",
                HasEvacuationOrder: false,
                []);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new NwsException(
                "The National Weather Service could not return active alerts.",
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var alerts = ReadAlerts(document.RootElement);

        return new NwsAlertResult(
            query.Latitude,
            query.Longitude,
            IsWithinCoverage: true,
            null,
            alerts.Count,
            MaxSeverity(alerts),
            alerts.Any(alert => string.Equals(alert.Response, "Evacuate", StringComparison.OrdinalIgnoreCase)),
            alerts);
    }

    private static List<NwsAlert> ReadAlerts(JsonElement root)
    {
        if (!root.TryGetProperty("features", out var features)
            || features.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var alerts = new List<NwsAlert>();

        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            alerts.Add(new NwsAlert(
                ReadString(properties, "id"),
                ReadString(properties, "event"),
                ReadString(properties, "severity"),
                ReadString(properties, "certainty"),
                ReadString(properties, "urgency"),
                ReadString(properties, "headline"),
                ReadString(properties, "description"),
                ReadString(properties, "instruction"),
                ReadString(properties, "response"),
                ReadString(properties, "areaDesc"),
                ReadString(properties, "onset"),
                ReadString(properties, "expires"),
                ReadString(properties, "ends")));
        }

        return alerts;
    }

    private static string MaxSeverity(List<NwsAlert> alerts)
    {
        var highest = 0;

        foreach (var alert in alerts)
        {
            var rank = Array.FindIndex(
                SeverityRanking,
                severity => string.Equals(severity, alert.Severity, StringComparison.OrdinalIgnoreCase));
            highest = Math.Max(highest, rank);
        }

        return SeverityRanking[highest];
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private HttpRequestMessage CreateRequest(string uri)
    {
        var userAgent = configuration["Nws:UserAgent"];
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            throw new NwsConfigurationException("Nws__UserAgent");
        }

        var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.UserAgent.ParseAdd(userAgent);
        message.Headers.Accept.ParseAdd("application/geo+json");
        return message;
    }

    private string GetEndpoint()
    {
        var endpoint = configuration["Nws:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new NwsConfigurationException("Nws__Endpoint");
        }

        return endpoint.TrimEnd('/');
    }
}

public sealed class NwsConfigurationException(string settingName)
    : Exception($"National Weather Service configuration is missing. Set {settingName}.");

public sealed class NwsException(string message, HttpStatusCode statusCode) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
