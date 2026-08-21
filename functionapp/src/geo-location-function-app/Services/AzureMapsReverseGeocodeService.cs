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
    private const string LegacySearchApiVersion = "1.0";

    // Azure Maps does not expose road class, so private/unmaintained access is inferred from the address text.
    private static readonly string[] PrivateRoadKeywords =
        ["private", "pvt", "trail", "trl", "forest", "unnamed", "track", "easement"];

    public async Task<ReverseGeocodeResult> GetAddressAsync(
        ReverseGeocodeQuery query,
        CancellationToken cancellationToken)
    {
        var longitude = query.Longitude.ToString(CultureInfo.InvariantCulture);
        var latitude = query.Latitude.ToString(CultureInfo.InvariantCulture);
        var useLegacyApis = AzureMapsApiProfile.UseLegacyApis(configuration);
        // reverseGeocode has no top parameter, unlike geocode, and rejects the request if one is sent.
        var uri = useLegacyApis
            ? QueryHelpers.AddQueryString(
                $"{GetEndpoint()}/search/address/reverse/json",
                new Dictionary<string, string?>
                {
                    ["api-version"] = LegacySearchApiVersion,
                    // Search v1 orders the coordinate latitude first, the opposite of reverseGeocode.
                    ["query"] = $"{latitude},{longitude}"
                })
            : QueryHelpers.AddQueryString(
                $"{GetEndpoint()}/reverseGeocode",
                new Dictionary<string, string?>
                {
                    ["api-version"] = GeocodingApiVersion,
                    ["coordinates"] = $"{longitude},{latitude}"
                });

        using var message = CreateRequest(uri, useLegacyApis);
        using var response = await httpClient.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new AzureMapsException(
                "Azure Maps could not reverse geocode the requested coordinates.",
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return useLegacyApis
            ? ReadLegacyResult(document.RootElement, query)
            : ReadResult(document.RootElement, query);
    }

    private static ReverseGeocodeResult ReadResult(JsonElement root, ReverseGeocodeQuery query)
    {
        if (!root.TryGetProperty("features", out var features)
            || features.GetArrayLength() == 0)
        {
            return NoAddressMatch(query);
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

    // Search v1 reports neither a result type nor a confidence band, so both are left unset.
    private static ReverseGeocodeResult ReadLegacyResult(JsonElement root, ReverseGeocodeQuery query)
    {
        if (!root.TryGetProperty("addresses", out var addresses)
            || addresses.ValueKind != JsonValueKind.Array
            || addresses.GetArrayLength() == 0)
        {
            return NoAddressMatch(query);
        }

        var address = addresses[0].TryGetProperty("address", out var addressElement)
            ? addressElement
            : default;

        var formattedAddress = ReadString(address, "freeformAddress");
        var addressLine = ReadString(address, "streetNameAndNumber") ?? ReadString(address, "streetName");

        return new ReverseGeocodeResult(
            query.Latitude,
            query.Longitude,
            HasAddressMatch: true,
            formattedAddress,
            addressLine,
            ReadString(address, "municipality"),
            ReadString(address, "postalCode"),
            ReadString(address, "countryCode"),
            null,
            null,
            IsLikelyPrivateRoad(addressLine ?? formattedAddress));
    }

    private static ReverseGeocodeResult NoAddressMatch(ReverseGeocodeQuery query) =>
        new(
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

    private HttpRequestMessage CreateRequest(string uri, bool useLegacyApis)
    {
        var subscriptionKey = configuration["AzureMaps:SubscriptionKey"];
        if (string.IsNullOrWhiteSpace(subscriptionKey))
        {
            throw new AzureMapsConfigurationException();
        }

        var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.Add("subscription-key", subscriptionKey);
        message.Headers.Accept.ParseAdd(useLegacyApis ? "application/json" : "application/geo+json");
        return message;
    }

    private string GetEndpoint() => AzureMapsApiProfile.GetEndpoint(configuration);
}
