using System.Globalization;
using System.Text.Json;
using GeoLocation.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace GeoLocation.Services;

public sealed class AzureMapsWeatherService(HttpClient httpClient, IConfiguration configuration)
    : IWeatherService
{
    private const string WeatherApiVersion = "1.1";

    public Task<JsonElement> GetCurrentConditionsAsync(
        WeatherQuery query,
        CancellationToken cancellationToken) =>
        GetAsync(
            "weather/currentConditions/json",
            query,
            "Azure Maps could not retrieve current weather conditions.",
            cancellationToken);

    public Task<JsonElement> GetSevereWeatherAlertsAsync(
        WeatherQuery query,
        CancellationToken cancellationToken) =>
        GetAsync(
            "weather/severe/alerts/json",
            query,
            "Azure Maps could not retrieve severe weather alerts.",
            cancellationToken);

    private async Task<JsonElement> GetAsync(
        string path,
        WeatherQuery query,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var latitude = query.Latitude.ToString(CultureInfo.InvariantCulture);
        var longitude = query.Longitude.ToString(CultureInfo.InvariantCulture);
        var uri = QueryHelpers.AddQueryString(
            $"{GetEndpoint()}/{path}",
            new Dictionary<string, string?>
            {
                ["api-version"] = WeatherApiVersion,
                ["query"] = $"{latitude},{longitude}",
                ["unit"] = query.Unit,
                ["details"] = "true"
            });

        using var message = CreateRequest(uri);
        using var response = await httpClient.SendAsync(message, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new AzureMapsException(failureMessage, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
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
        message.Headers.Accept.ParseAdd("application/json");
        return message;
    }

    private string GetEndpoint() =>
        (configuration["AzureMaps:Endpoint"] ?? "https://atlas.microsoft.com").TrimEnd('/');
}
