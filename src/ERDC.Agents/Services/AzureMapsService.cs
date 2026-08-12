using System.Globalization;
using System.Net;
using System.Text.Json;
using ERDC.Agents.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace ERDC.Agents.Services;

public sealed class AzureMapsService(HttpClient httpClient, IConfiguration configuration) : IMapImageService
{
    private const string GeocodingApiVersion = "2026-01-01";
    private const string RenderApiVersion = "2024-04-01";

    public async Task<MapImage> GetMapImageAsync(
        MapRenderRequest request,
        CancellationToken cancellationToken)
    {
        var coordinates = request.City is not null
            ? await GeocodeAsync(request.City, cancellationToken)
            : new Coordinates(request.Longitude!.Value, request.Latitude!.Value);

        var longitude = coordinates.Longitude.ToString(CultureInfo.InvariantCulture);
        var latitude = coordinates.Latitude.ToString(CultureInfo.InvariantCulture);
        var uri = QueryHelpers.AddQueryString(
            $"{GetEndpoint()}/map/static",
            new Dictionary<string, string?>
            {
                ["api-version"] = RenderApiVersion,
                ["tilesetId"] = request.TilesetId,
                ["center"] = $"{longitude},{latitude}",
                ["zoom"] = request.Zoom.ToString(CultureInfo.InvariantCulture),
                ["width"] = request.Width.ToString(CultureInfo.InvariantCulture),
                ["height"] = request.Height.ToString(CultureInfo.InvariantCulture),
                ["pins"] = $"default||{longitude} {latitude}"
            });

        using var message = CreateRequest(uri, "image/png");
        using var response = await httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new AzureMapsException("Azure Maps could not render the requested map.", response.StatusCode);
        }

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/png";
        return new MapImage(content, contentType);
    }

    private async Task<Coordinates> GeocodeAsync(string city, CancellationToken cancellationToken)
    {
        var uri = QueryHelpers.AddQueryString(
            $"{GetEndpoint()}/geocode",
            new Dictionary<string, string?>
            {
                ["api-version"] = GeocodingApiVersion,
                ["query"] = city,
                ["top"] = "1"
            });

        using var message = CreateRequest(uri, "application/geo+json");
        using var response = await httpClient.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new AzureMapsException("Azure Maps could not geocode the requested city.", response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("features", out var features)
            || features.GetArrayLength() == 0)
        {
            throw new MapLocationNotFoundException(city);
        }

        var coordinateArray = features[0].GetProperty("geometry").GetProperty("coordinates");
        return new Coordinates(coordinateArray[0].GetDouble(), coordinateArray[1].GetDouble());
    }

    private HttpRequestMessage CreateRequest(string uri, string accept)
    {
        var subscriptionKey = configuration["AzureMaps:SubscriptionKey"];
        if (string.IsNullOrWhiteSpace(subscriptionKey))
        {
            throw new AzureMapsConfigurationException();
        }

        var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.Add("subscription-key", subscriptionKey);
        message.Headers.Accept.ParseAdd(accept);
        return message;
    }

    private string GetEndpoint() =>
        (configuration["AzureMaps:Endpoint"] ?? "https://atlas.microsoft.com").TrimEnd('/');

    private sealed record Coordinates(double Longitude, double Latitude);
}

public sealed class MapLocationNotFoundException(string city)
    : Exception($"No location was found for '{city}'.");

public sealed class AzureMapsException(string message, HttpStatusCode statusCode) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}