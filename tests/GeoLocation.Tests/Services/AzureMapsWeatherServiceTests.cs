using System.Net;
using System.Text;
using GeoLocation.Models;
using GeoLocation.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace GeoLocation.Tests.Services;

public class AzureMapsWeatherServiceTests
{
    private const string ConditionsJson = """
    {
      "results": [
        {
          "dateTime": "2024-05-01T12:00:00-07:00",
          "phrase": "Heavy rain",
          "hasPrecipitation": true,
          "temperature": { "value": 8.3, "unit": "C", "unitType": 17 }
        }
      ]
    }
    """;

    [Fact]
    public async Task GetCurrentConditionsAsync_RequestsCurrentConditionsPathWithLatitudeThenLongitude()
    {
        var handler = new RecordingHandler(JsonResponse(ConditionsJson));
        var service = CreateService(handler);

        await service.GetCurrentConditionsAsync(
            new WeatherQuery(47.6062, -122.3321, "metric"),
            CancellationToken.None);

        var request = handler.Requests.Single();
        var query = QueryHelpers.ParseQuery(request.Uri.Query);

        Assert.Equal("/weather/currentConditions/json", request.Uri.AbsolutePath);
        Assert.Equal("47.6062,-122.3321", query["query"]);
        Assert.Equal("1.1", query["api-version"]);
        Assert.Equal("metric", query["unit"]);
    }

    [Fact]
    public async Task GetSevereWeatherAlertsAsync_RequestsSevereAlertsPath()
    {
        var handler = new RecordingHandler(JsonResponse("""{ "results": [] }"""));
        var service = CreateService(handler);

        await service.GetSevereWeatherAlertsAsync(
            new WeatherQuery(47.6062, -122.3321, "imperial"),
            CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal("/weather/severe/alerts/json", request.Uri.AbsolutePath);
        Assert.Equal("imperial", QueryHelpers.ParseQuery(request.Uri.Query)["unit"]);
    }

    [Fact]
    public async Task GetCurrentConditionsAsync_SendsSubscriptionKeyAsHeaderNotQueryString()
    {
        var handler = new RecordingHandler(JsonResponse(ConditionsJson));
        var service = CreateService(handler);

        await service.GetCurrentConditionsAsync(
            new WeatherQuery(47.6062, -122.3321, "metric"),
            CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal("test-key", request.SubscriptionKey);
        Assert.DoesNotContain("test-key", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task GetCurrentConditionsAsync_ReturnsUpstreamPayloadUnchanged()
    {
        var service = CreateService(new RecordingHandler(JsonResponse(ConditionsJson)));

        var result = await service.GetCurrentConditionsAsync(
            new WeatherQuery(47.6062, -122.3321, "metric"),
            CancellationToken.None);

        var conditions = result.GetProperty("results")[0];
        Assert.Equal("Heavy rain", conditions.GetProperty("phrase").GetString());
        Assert.True(conditions.GetProperty("hasPrecipitation").GetBoolean());
        Assert.Equal(8.3, conditions.GetProperty("temperature").GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task GetCurrentConditionsAsync_WhenAzureMapsFails_ThrowsAzureMapsException()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<AzureMapsException>(() =>
            service.GetCurrentConditionsAsync(
                new WeatherQuery(47.6062, -122.3321, "metric"),
                CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
    }

    [Fact]
    public async Task GetCurrentConditionsAsync_WithoutSubscriptionKey_ThrowsConfigurationException()
    {
        var service = new AzureMapsWeatherService(
            new HttpClient(new RecordingHandler(JsonResponse(ConditionsJson))),
            new ConfigurationBuilder().Build());

        await Assert.ThrowsAsync<AzureMapsConfigurationException>(() =>
            service.GetCurrentConditionsAsync(
                new WeatherQuery(47.6062, -122.3321, "metric"),
                CancellationToken.None));
    }

    private static AzureMapsWeatherService CreateService(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureMaps:SubscriptionKey"] = "test-key",
                ["AzureMaps:Endpoint"] = "https://atlas.microsoft.com"
            })
            .Build();
        return new AzureMapsWeatherService(new HttpClient(handler), configuration);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.RequestUri!,
                request.Headers.GetValues("subscription-key").Single()));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed record RecordedRequest(Uri Uri, string SubscriptionKey);
}
