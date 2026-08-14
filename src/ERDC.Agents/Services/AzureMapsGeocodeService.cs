using System.Globalization;
using System.Text.Json;
using ERDC.Agents.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace ERDC.Agents.Services;

public sealed class AzureMapsGeocodeService(HttpClient httpClient, IConfiguration configuration)
    : IGeocodeService
{
    private const string GeocodingApiVersion = "2026-01-01";

    public async Task<GeocodeResult> GetCoordinatesAsync(
        GeocodeQuery query,
        CancellationToken cancellationToken)
    {
        var uri = QueryHelpers.AddQueryString(
            $"{GetEndpoint()}/geocode",
            new Dictionary<string, string?>
            {
                ["api-version"] = GeocodingApiVersion,
                ["query"] = query.Text,
                ["top"] = query.Top.ToString(CultureInfo.InvariantCulture),
                ["countryRegion"] = query.CountryRegion
            });

        using var message = CreateRequest(uri);
        using var response = await httpClient.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new AzureMapsException(
                "Azure Maps could not geocode the requested place.",
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("features", out var features)
            || features.ValueKind != JsonValueKind.Array)
        {
            return new GeocodeResult(query.Text, HasMatch: false, []);
        }

        var candidates = new List<GeocodeCandidate>(features.GetArrayLength());
        foreach (var feature in features.EnumerateArray())
        {
            if (ReadCandidate(feature) is { } candidate)
            {
                candidates.Add(candidate);
            }
        }

        return new GeocodeResult(query.Text, candidates.Count > 0, candidates);
    }

    // A feature without a usable position is dropped rather than reported as a candidate the caller
    // cannot act on.
    private static GeocodeCandidate? ReadCandidate(JsonElement feature)
    {
        if (feature.ValueKind != JsonValueKind.Object
            || !feature.TryGetProperty("geometry", out var geometry)
            || geometry.ValueKind != JsonValueKind.Object
            || !geometry.TryGetProperty("coordinates", out var coordinates)
            || coordinates.ValueKind != JsonValueKind.Array
            || coordinates.GetArrayLength() < 2)
        {
            return null;
        }

        var properties = feature.TryGetProperty("properties", out var propertiesElement)
            ? propertiesElement
            : default;

        var address = properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty("address", out var addressElement)
                ? addressElement
                : default;

        // GeoJSON orders a position longitude first. Read the other way round it yields a plausible
        // coordinate somewhere else entirely, which nothing downstream can detect.
        return new GeocodeCandidate(
            coordinates[1].GetDouble(),
            coordinates[0].GetDouble(),
            ReadString(address, "formattedAddress"),
            ReadString(address, "locality"),
            ReadCountryCode(address),
            ReadString(properties, "type"),
            ReadString(properties, "confidence"));
    }

    private static string? ReadCountryCode(JsonElement address)
    {
        if (address.ValueKind != JsonValueKind.Object
            || !address.TryGetProperty("countryRegion", out var countryRegion))
        {
            return null;
        }

        return ReadString(countryRegion, "ISO") ?? ReadString(countryRegion, "name");
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

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
