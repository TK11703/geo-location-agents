using System.Globalization;
using System.Text.Json;
using GeoLocation.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace GeoLocation.Services;

public sealed class AzureMapsGeocodeService(HttpClient httpClient, IConfiguration configuration)
    : IGeocodeService
{
    private const string GeocodingApiVersion = "2026-01-01";
    private const string LegacySearchApiVersion = "1.0";

    public async Task<GeocodeResult> GetCoordinatesAsync(
        GeocodeQuery query,
        CancellationToken cancellationToken)
    {
        var useLegacyApis = AzureMapsApiProfile.UseLegacyApis(configuration);
        // Azure Maps rejects countryRegion outright when it is sent alongside a free-form query, so
        // the restriction is applied to the candidates below instead of upstream.
        var uri = useLegacyApis
            ? QueryHelpers.AddQueryString(
                $"{GetEndpoint()}/search/address/json",
                new Dictionary<string, string?>
                {
                    ["api-version"] = LegacySearchApiVersion,
                    ["query"] = query.Text,
                    ["limit"] = query.Top.ToString(CultureInfo.InvariantCulture)
                })
            : QueryHelpers.AddQueryString(
                $"{GetEndpoint()}/geocode",
                new Dictionary<string, string?>
                {
                    ["api-version"] = GeocodingApiVersion,
                    ["query"] = query.Text,
                    ["top"] = query.Top.ToString(CultureInfo.InvariantCulture)
                });

        using var message = CreateRequest(uri, useLegacyApis);
        using var response = await httpClient.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new AzureMapsException(
                "Azure Maps could not geocode the requested place.",
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var candidates = useLegacyApis
            ? ReadLegacyCandidates(document.RootElement, query.CountryRegion)
            : ReadCandidates(document.RootElement, query.CountryRegion);

        return new GeocodeResult(query.Text, candidates.Count > 0, candidates);
    }

    private static List<GeocodeCandidate> ReadCandidates(JsonElement root, string? countryRegion)
    {
        if (!root.TryGetProperty("features", out var features)
            || features.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var candidates = new List<GeocodeCandidate>(features.GetArrayLength());
        foreach (var feature in features.EnumerateArray())
        {
            if (ReadCandidate(feature) is { } candidate && IsInCountry(candidate, countryRegion))
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private static List<GeocodeCandidate> ReadLegacyCandidates(JsonElement root, string? countryRegion)
    {
        if (!root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var candidates = new List<GeocodeCandidate>(results.GetArrayLength());
        foreach (var result in results.EnumerateArray())
        {
            if (ReadLegacyCandidate(result) is { } candidate && IsInCountry(candidate, countryRegion))
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    // Search v1 scores are only comparable within a single result set, so they cannot be reported as
    // the absolute confidence band the newer geocoder returns and the field is left unset.
    private static GeocodeCandidate? ReadLegacyCandidate(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("position", out var position)
            || position.ValueKind != JsonValueKind.Object
            || !position.TryGetProperty("lat", out var latitude)
            || !position.TryGetProperty("lon", out var longitude))
        {
            return null;
        }

        var address = result.TryGetProperty("address", out var addressElement)
            ? addressElement
            : default;

        return new GeocodeCandidate(
            latitude.GetDouble(),
            longitude.GetDouble(),
            ReadString(address, "freeformAddress"),
            ReadString(address, "municipality"),
            ReadString(address, "countryCode"),
            ReadString(result, "type"),
            null);
    }

    // A candidate whose country the provider did not report is kept rather than guessed at, so the
    // filter only ever removes places it can positively place somewhere else.
    private static bool IsInCountry(GeocodeCandidate candidate, string? countryRegion) =>
        string.IsNullOrEmpty(countryRegion)
        || candidate.CountryCode is null
        || string.Equals(candidate.CountryCode, countryRegion, StringComparison.OrdinalIgnoreCase);

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
