using System.Net;
using System.Text;
using ERDC.Agents.Models;
using ERDC.Agents.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace ERDC.Agents.Tests.Services;

public class AzureMapsGeocodeServiceTests
{
    private const string MatchJson = """
    {
      "features": [
        {
          "geometry": { "coordinates": [-116.2023, 43.6150] },
          "properties": {
            "type": "Geography",
            "confidence": "High",
            "address": {
              "formattedAddress": "Boise, ID, United States",
              "locality": "Boise",
              "countryRegion": { "ISO": "US" }
            }
          }
        }
      ]
    }
    """;

    [Fact]
    public async Task GetCoordinatesAsync_SendsQueryAndDefaults()
    {
        var handler = new RecordingHandler(JsonResponse(MatchJson));
        var service = CreateService(handler);

        await service.GetCoordinatesAsync(new GeocodeQuery("Boise, Idaho", null, 5), CancellationToken.None);

        var query = QueryHelpers.ParseQuery(handler.Requests.Single().Uri.Query);
        Assert.Equal("Boise, Idaho", query["query"]);
        Assert.Equal("5", query["top"]);
        Assert.Equal("2026-01-01", query["api-version"]);
        Assert.False(query.ContainsKey("countryRegion"));
    }

    // Azure Maps answers a free-form query with countryRegion attached with a 400, not a filtered
    // result, so sending it at all takes the endpoint down for every restricted search.
    [Fact]
    public async Task GetCoordinatesAsync_WithCountryRegion_DoesNotSendItUpstream()
    {
        var handler = new RecordingHandler(JsonResponse(MatchJson));
        var service = CreateService(handler);

        await service.GetCoordinatesAsync(new GeocodeQuery("Springfield", "US", 3), CancellationToken.None);

        var query = QueryHelpers.ParseQuery(handler.Requests.Single().Uri.Query);
        Assert.False(query.ContainsKey("countryRegion"));
        Assert.Equal("3", query["top"]);
    }

    [Fact]
    public async Task GetCoordinatesAsync_WithCountryRegion_DropsCandidatesElsewhere()
    {
        const string json = """
        {
          "features": [
            {
              "geometry": { "coordinates": [-89.6501, 39.7817] },
              "properties": {
                "address": {
                  "formattedAddress": "Springfield, IL",
                  "countryRegion": { "ISO": "US" }
                }
              }
            },
            {
              "geometry": { "coordinates": [-1.1743, 51.9912] },
              "properties": {
                "address": {
                  "formattedAddress": "Springfield, England",
                  "countryRegion": { "ISO": "GB" }
                }
              }
            }
          ]
        }
        """;
        var service = CreateService(new RecordingHandler(JsonResponse(json)));

        var result = await service.GetCoordinatesAsync(
            new GeocodeQuery("Springfield", "US", 5),
            CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Springfield, IL", candidate.FormattedAddress);
    }

    [Fact]
    public async Task GetCoordinatesAsync_SendsSubscriptionKeyAsHeaderNotQueryString()
    {
        var handler = new RecordingHandler(JsonResponse(MatchJson));
        var service = CreateService(handler);

        await service.GetCoordinatesAsync(new GeocodeQuery("Boise, Idaho", null, 5), CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal("test-key", request.SubscriptionKey);
        Assert.DoesNotContain("test-key", request.Uri.AbsoluteUri);
    }

    // GeoJSON writes a position longitude first. Swapping the two here would put Boise off the coast
    // of Somalia while every field around it still looked right.
    [Fact]
    public async Task GetCoordinatesAsync_ReadsLongitudeBeforeLatitude()
    {
        var service = CreateService(new RecordingHandler(JsonResponse(MatchJson)));

        var result = await service.GetCoordinatesAsync(
            new GeocodeQuery("Boise, Idaho", null, 5),
            CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(43.6150, candidate.Latitude);
        Assert.Equal(-116.2023, candidate.Longitude);
    }

    [Fact]
    public async Task GetCoordinatesAsync_WithMatch_MapsCandidateFields()
    {
        var service = CreateService(new RecordingHandler(JsonResponse(MatchJson)));

        var result = await service.GetCoordinatesAsync(
            new GeocodeQuery("Boise, Idaho", null, 5),
            CancellationToken.None);

        Assert.True(result.HasMatch);
        Assert.Equal("Boise, Idaho", result.Query);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Boise, ID, United States", candidate.FormattedAddress);
        Assert.Equal("Boise", candidate.Locality);
        Assert.Equal("US", candidate.CountryCode);
        Assert.Equal("Geography", candidate.ResultType);
        Assert.Equal("High", candidate.Confidence);
    }

    [Fact]
    public async Task GetCoordinatesAsync_WithSeveralFeatures_KeepsProviderOrder()
    {
        const string json = """
        {
          "features": [
            {
              "geometry": { "coordinates": [-89.6501, 39.7817] },
              "properties": { "address": { "formattedAddress": "Springfield, IL" } }
            },
            {
              "geometry": { "coordinates": [-93.2923, 37.2090] },
              "properties": { "address": { "formattedAddress": "Springfield, MO" } }
            }
          ]
        }
        """;
        var service = CreateService(new RecordingHandler(JsonResponse(json)));

        var result = await service.GetCoordinatesAsync(
            new GeocodeQuery("Springfield", "US", 5),
            CancellationToken.None);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("Springfield, IL", result.Candidates[0].FormattedAddress);
        Assert.Equal("Springfield, MO", result.Candidates[1].FormattedAddress);
    }

    [Fact]
    public async Task GetCoordinatesAsync_WithNoFeatures_ReportsNoMatch()
    {
        var service = CreateService(new RecordingHandler(JsonResponse("""{ "features": [] }""")));

        var result = await service.GetCoordinatesAsync(
            new GeocodeQuery("Nowhere At All", null, 5),
            CancellationToken.None);

        Assert.False(result.HasMatch);
        Assert.Empty(result.Candidates);
        Assert.Equal("Nowhere At All", result.Query);
    }

    [Fact]
    public async Task GetCoordinatesAsync_WithFeatureMissingGeometry_DropsIt()
    {
        const string json = """
        {
          "features": [
            { "properties": { "address": { "formattedAddress": "Somewhere" } } }
          ]
        }
        """;
        var service = CreateService(new RecordingHandler(JsonResponse(json)));

        var result = await service.GetCoordinatesAsync(
            new GeocodeQuery("Somewhere", null, 5),
            CancellationToken.None);

        Assert.False(result.HasMatch);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task GetCoordinatesAsync_WhenAzureMapsFails_ThrowsAzureMapsException()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<AzureMapsException>(() =>
            service.GetCoordinatesAsync(new GeocodeQuery("Boise, Idaho", null, 5), CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [Fact]
    public async Task GetCoordinatesAsync_WithoutSubscriptionKey_ThrowsConfigurationException()
    {
        var configuration = new ConfigurationBuilder().Build();
        var service = new AzureMapsGeocodeService(
            new HttpClient(new RecordingHandler(JsonResponse(MatchJson))),
            configuration);

        await Assert.ThrowsAsync<AzureMapsConfigurationException>(() =>
            service.GetCoordinatesAsync(new GeocodeQuery("Boise, Idaho", null, 5), CancellationToken.None));
    }

    private static AzureMapsGeocodeService CreateService(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureMaps:SubscriptionKey"] = "test-key",
                ["AzureMaps:Endpoint"] = "https://atlas.microsoft.com"
            })
            .Build();
        return new AzureMapsGeocodeService(new HttpClient(handler), configuration);
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
