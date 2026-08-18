using System.Net;
using System.Text;
using GeoLocation.Models;
using GeoLocation.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace GeoLocation.Tests.Services;

public class NwsAlertServiceTests
{
    private const string AlertsJson = """
    {
      "features": [
        {
          "properties": {
            "id": "urn:oid:2.49.0.1.840.0.1",
            "event": "Flash Flood Warning",
            "severity": "Severe",
            "certainty": "Likely",
            "urgency": "Immediate",
            "headline": "Flash Flood Warning issued",
            "description": "Flooding is occurring on low-lying roads.",
            "instruction": "Do not drive through flooded roadways.",
            "response": "Avoid",
            "areaDesc": "King County, WA",
            "onset": "2024-05-01T12:00:00-07:00",
            "expires": "2024-05-01T18:00:00-07:00",
            "ends": "2024-05-01T20:00:00-07:00"
          }
        },
        {
          "properties": {
            "event": "Wildfire Evacuation",
            "severity": "Extreme",
            "response": "Evacuate"
          }
        }
      ]
    }
    """;

    [Fact]
    public async Task GetActiveAlertsAsync_SendsPointAsLatitudeThenLongitude()
    {
        var handler = new RecordingHandler(JsonResponse(AlertsJson));
        var service = CreateService(handler);

        await service.GetActiveAlertsAsync(new NwsAlertQuery(47.6062, -122.3321), CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal("api.weather.gov", request.Uri.Host);
        Assert.Equal("/alerts/active", request.Uri.AbsolutePath);
        Assert.Equal("47.6062,-122.3321", QueryHelpers.ParseQuery(request.Uri.Query)["point"]);
    }

    [Fact]
    public async Task GetActiveAlertsAsync_SendsConfiguredUserAgent()
    {
        var handler = new RecordingHandler(JsonResponse(AlertsJson));
        var service = CreateService(handler);

        await service.GetActiveAlertsAsync(new NwsAlertQuery(47.6062, -122.3321), CancellationToken.None);

        Assert.Equal("TestApp (test@example.com)", handler.Requests.Single().UserAgent);
    }

    [Fact]
    public async Task GetActiveAlertsAsync_MapsAlertProperties()
    {
        var service = CreateService(new RecordingHandler(JsonResponse(AlertsJson)));

        var result = await service.GetActiveAlertsAsync(
            new NwsAlertQuery(47.6062, -122.3321),
            CancellationToken.None);

        var alert = result.Alerts[0];
        Assert.Equal("urn:oid:2.49.0.1.840.0.1", alert.Id);
        Assert.Equal("Flash Flood Warning", alert.Event);
        Assert.Equal("Severe", alert.Severity);
        Assert.Equal("Likely", alert.Certainty);
        Assert.Equal("Immediate", alert.Urgency);
        Assert.Equal("Do not drive through flooded roadways.", alert.Instruction);
        Assert.Equal("King County, WA", alert.AreaDescription);
        Assert.Equal("2024-05-01T18:00:00-07:00", alert.Expires);
    }

    [Fact]
    public async Task GetActiveAlertsAsync_ReportsHighestSeverityAcrossAlerts()
    {
        var service = CreateService(new RecordingHandler(JsonResponse(AlertsJson)));

        var result = await service.GetActiveAlertsAsync(
            new NwsAlertQuery(47.6062, -122.3321),
            CancellationToken.None);

        Assert.Equal(2, result.AlertCount);
        Assert.Equal("Extreme", result.MaxSeverity);
    }

    [Fact]
    public async Task GetActiveAlertsAsync_WithEvacuateResponse_FlagsEvacuationOrder()
    {
        var service = CreateService(new RecordingHandler(JsonResponse(AlertsJson)));

        var result = await service.GetActiveAlertsAsync(
            new NwsAlertQuery(47.6062, -122.3321),
            CancellationToken.None);

        Assert.True(result.HasEvacuationOrder);
    }

    [Fact]
    public async Task GetActiveAlertsAsync_WithNoAlerts_ReportsUnknownSeverityAndNoEvacuation()
    {
        var service = CreateService(new RecordingHandler(JsonResponse("""{ "features": [] }""")));

        var result = await service.GetActiveAlertsAsync(
            new NwsAlertQuery(47.6062, -122.3321),
            CancellationToken.None);

        Assert.True(result.IsWithinCoverage);
        Assert.Equal(0, result.AlertCount);
        Assert.Equal("Unknown", result.MaxSeverity);
        Assert.False(result.HasEvacuationOrder);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task GetActiveAlertsAsync_ForPointOutsideCoverage_ReportsNoCoverageInsteadOfFailing(
        HttpStatusCode statusCode)
    {
        var service = CreateService(new RecordingHandler(new HttpResponseMessage(statusCode)));

        var result = await service.GetActiveAlertsAsync(
            new NwsAlertQuery(51.5072, -0.1276),
            CancellationToken.None);

        Assert.False(result.IsWithinCoverage);
        Assert.NotNull(result.CoverageNote);
        Assert.Equal(0, result.AlertCount);
        Assert.Empty(result.Alerts);
    }

    [Fact]
    public async Task GetActiveAlertsAsync_WhenServiceFails_ThrowsNwsException()
    {
        var service = CreateService(new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var exception = await Assert.ThrowsAsync<NwsException>(() =>
            service.GetActiveAlertsAsync(new NwsAlertQuery(47.6062, -122.3321), CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    [Fact]
    public async Task GetActiveAlertsAsync_WithoutUserAgent_ThrowsConfigurationException()
    {
        var service = new NwsAlertService(
            new HttpClient(new RecordingHandler(JsonResponse(AlertsJson))),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nws:Endpoint"] = "https://api.weather.gov"
                })
                .Build());

        await Assert.ThrowsAsync<NwsConfigurationException>(() =>
            service.GetActiveAlertsAsync(new NwsAlertQuery(47.6062, -122.3321), CancellationToken.None));
    }

    [Fact]
    public async Task GetActiveAlertsAsync_WithoutEndpoint_ThrowsConfigurationException()
    {
        var service = new NwsAlertService(
            new HttpClient(new RecordingHandler(JsonResponse(AlertsJson))),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nws:UserAgent"] = "TestApp (test@example.com)"
                })
                .Build());

        var exception = await Assert.ThrowsAsync<NwsConfigurationException>(() =>
            service.GetActiveAlertsAsync(new NwsAlertQuery(47.6062, -122.3321), CancellationToken.None));

        Assert.Contains("Nws__Endpoint", exception.Message);
    }

    private static NwsAlertService CreateService(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nws:UserAgent"] = "TestApp (test@example.com)",
                ["Nws:Endpoint"] = "https://api.weather.gov"
            })
            .Build();
        return new NwsAlertService(new HttpClient(handler), configuration);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/geo+json")
    };

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(request.RequestUri!, request.Headers.UserAgent.ToString()));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed record RecordedRequest(Uri Uri, string UserAgent);
}
