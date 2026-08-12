using System.Net;
using System.Text;
using ERDC.Agents.Models;
using ERDC.Agents.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace ERDC.Agents.Tests.Services;

public class AzureMapsTrafficServiceTests
{
    // Two incidents at the centre point plus one far outside the requested radius.
    private const string IncidentsJson = """
    {
      "features": [
        {
          "id": 1001,
          "geometry": { "type": "Point", "coordinates": [-122.3321, 47.6062] },
          "properties": {
            "incidentType": "RoadHazard",
            "title": "Bridge closed",
            "description": "Bridge closed for repairs",
            "severity": 4,
            "isRoadClosed": true,
            "isTrafficJam": false,
            "delay": 900,
            "startTime": "2024-05-01T12:00:00Z",
            "endTime": "2024-05-02T12:00:00Z"
          }
        },
        {
          "id": 1002,
          "geometry": { "type": "Point", "coordinates": [-122.3300, 47.6062] },
          "properties": {
            "incidentType": "Congestion",
            "title": "Heavy traffic",
            "severity": 2,
            "isRoadClosed": false,
            "isTrafficJam": true,
            "delay": 120
          }
        },
        {
          "id": 1003,
          "geometry": { "type": "Point", "coordinates": [-122.5000, 47.9000] },
          "properties": { "incidentType": "Accident", "isRoadClosed": true }
        }
      ]
    }
    """;

    [Fact]
    public async Task GetIncidentsAsync_SendsBoundingBoxAsMinLonMinLatMaxLonMaxLat()
    {
        var handler = new RecordingHandler(JsonResponse(IncidentsJson));
        var service = CreateService(handler);

        await service.GetIncidentsAsync(
            new TrafficIncidentQuery(47.6062, -122.3321, 2000),
            CancellationToken.None);

        var query = QueryHelpers.ParseQuery(handler.Requests.Single().Uri.Query);
        var bbox = query["bbox"].ToString().Split(',').Select(double.Parse).ToArray();

        Assert.Equal("2025-01-01", query["api-version"]);
        Assert.True(bbox[0] < -122.3321);
        Assert.True(bbox[1] < 47.6062);
        Assert.True(bbox[2] > -122.3321);
        Assert.True(bbox[3] > 47.6062);
    }

    [Fact]
    public async Task GetIncidentsAsync_SendsSubscriptionKeyAsHeaderNotQueryString()
    {
        var handler = new RecordingHandler(JsonResponse(IncidentsJson));
        var service = CreateService(handler);

        await service.GetIncidentsAsync(
            new TrafficIncidentQuery(47.6062, -122.3321, 2000),
            CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal("test-key", request.SubscriptionKey);
        Assert.DoesNotContain("test-key", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task GetIncidentsAsync_ExcludesIncidentsBeyondTheRequestedRadius()
    {
        var service = CreateService(new RecordingHandler(JsonResponse(IncidentsJson)));

        var result = await service.GetIncidentsAsync(
            new TrafficIncidentQuery(47.6062, -122.3321, 2000),
            CancellationToken.None);

        Assert.Equal(2, result.IncidentCount);
        Assert.DoesNotContain(result.Incidents, incident => incident.Id == 1003);
    }

    [Fact]
    public async Task GetIncidentsAsync_OrdersIncidentsByDistance()
    {
        var service = CreateService(new RecordingHandler(JsonResponse(IncidentsJson)));

        var result = await service.GetIncidentsAsync(
            new TrafficIncidentQuery(47.6062, -122.3321, 2000),
            CancellationToken.None);

        Assert.Equal([1001L, 1002L], result.Incidents.Select(incident => incident.Id));
        Assert.Equal(0, result.NearestIncidentMeters);
    }

    [Fact]
    public async Task GetIncidentsAsync_CountsRoadClosures()
    {
        var service = CreateService(new RecordingHandler(JsonResponse(IncidentsJson)));

        var result = await service.GetIncidentsAsync(
            new TrafficIncidentQuery(47.6062, -122.3321, 2000),
            CancellationToken.None);

        Assert.Equal(1, result.RoadClosureCount);
    }

    [Fact]
    public async Task GetIncidentsAsync_MapsIncidentProperties()
    {
        var service = CreateService(new RecordingHandler(JsonResponse(IncidentsJson)));

        var result = await service.GetIncidentsAsync(
            new TrafficIncidentQuery(47.6062, -122.3321, 2000),
            CancellationToken.None);

        var incident = result.Incidents[0];
        Assert.Equal("RoadHazard", incident.IncidentType);
        Assert.Equal("Bridge closed", incident.Title);
        Assert.Equal("Bridge closed for repairs", incident.Description);
        Assert.Equal(4, incident.Severity);
        Assert.True(incident.IsRoadClosed);
        Assert.False(incident.IsTrafficJam);
        Assert.Equal(900, incident.DelaySeconds);
        Assert.Equal("2024-05-01T12:00:00Z", incident.StartTime);
    }

    [Fact]
    public async Task GetIncidentsAsync_WithNoIncidents_ReturnsEmptyResult()
    {
        var service = CreateService(new RecordingHandler(JsonResponse("""{ "features": [] }""")));

        var result = await service.GetIncidentsAsync(
            new TrafficIncidentQuery(47.6062, -122.3321, 2000),
            CancellationToken.None);

        Assert.Equal(0, result.IncidentCount);
        Assert.Equal(0, result.RoadClosureCount);
        Assert.Null(result.NearestIncidentMeters);
        Assert.Empty(result.Incidents);
    }

    [Fact]
    public async Task GetIncidentsAsync_WhenAzureMapsFails_ThrowsAzureMapsException()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.BadRequest));
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<AzureMapsException>(() =>
            service.GetIncidentsAsync(
                new TrafficIncidentQuery(47.6062, -122.3321, 2000),
                CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
    }

    [Fact]
    public async Task GetIncidentsAsync_WithoutSubscriptionKey_ThrowsConfigurationException()
    {
        var service = new AzureMapsTrafficService(
            new HttpClient(new RecordingHandler(JsonResponse(IncidentsJson))),
            new ConfigurationBuilder().Build());

        await Assert.ThrowsAsync<AzureMapsConfigurationException>(() =>
            service.GetIncidentsAsync(
                new TrafficIncidentQuery(47.6062, -122.3321, 2000),
                CancellationToken.None));
    }

    private static AzureMapsTrafficService CreateService(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureMaps:SubscriptionKey"] = "test-key",
                ["AzureMaps:Endpoint"] = "https://atlas.microsoft.com"
            })
            .Build();
        return new AzureMapsTrafficService(new HttpClient(handler), configuration);
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
            Requests.Add(new RecordedRequest(
                request.RequestUri!,
                request.Headers.GetValues("subscription-key").Single()));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed record RecordedRequest(Uri Uri, string SubscriptionKey);
}
