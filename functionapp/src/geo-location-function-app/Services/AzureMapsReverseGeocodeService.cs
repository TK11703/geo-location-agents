using System.Globalization;
using System.Text.Json;
using GeoLocation.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace GeoLocation.Services;

public sealed class AzureMapsReverseGeocodeService(HttpClient httpClient, IConfiguration configuration)
    : IReverseGeocodeService
{
    private const string GeocodingApiVersion = "2026-01-01";

    // Azure Maps does not expose road class, so private/unmaintained access is inferred from the address text.
    private static readonly string[] PrivateRoadKeywords =
        ["private", "pvt", "trail", "trl", "forest", "unnamed", "track", "easement"];

    public async Task<ReverseGeocodeResult> GetAddressAsync(
        ReverseGeocodeQuery query,
        CancellationToken cancellationToken)
    {
        var longitude = query.Longitude.ToString(CultureInfo.InvariantCulture);
        var latitude = query.Latitude.ToString(CultureInfo.InvariantCulture);
        var uri = QueryHelpers.AddQueryString(
            $"{GetEndpoint()}/reverseGeocode",
            new Dictionary<string, string?>
            {
                ["api-version"] = GeocodingApiVersion,
                ["coordinates"] = $"{longitude},{latitude}",
                ["top"] = "1"
            });

        using var message = CreateRequest(uri);
        using var response = await httpClient.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new AzureMapsException(
                "Azure Maps could not reverse geocode the requested coordinates.",
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("features", out var features)
            || features.GetArrayLength() == 0)
        {
            return new ReverseGeocodeResult(
                query.Latitude,
                query.Longitude,
                HasAddressMatch: false,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                IsLikelyPrivateRoad: false);
        }

        var properties = features[0].GetProperty("properties");
        var address = properties.TryGetProperty("address", out var addressElement)
            ? addressElement
            : default;

        var formattedAddress = ReadString(address, "formattedAddress");
        var addressLine = ReadString(address, "addressLine");

        return new ReverseGeocodeResult(
            query.Latitude,
            query.Longitude,
            HasAddressMatch: true,
            formattedAddress,
            addressLine,
            ReadString(address, "locality"),
            ReadString(address, "postalCode"),
            ReadCountryCode(address),
            ReadString(properties, "type"),
            ReadString(properties, "confidence"),
            IsLikelyPrivateRoad(addressLine ?? formattedAddress));
    }

    private static bool IsLikelyPrivateRoad(string? addressLine)
    {
        if (string.IsNullOrWhiteSpace(addressLine))
        {
            return false;
        }

        var tokens = addressLine.Split(
            [' ', ',', '.', '-'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Any(token => PrivateRoadKeywords.Contains(token, StringComparer.OrdinalIgnoreCase));
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
