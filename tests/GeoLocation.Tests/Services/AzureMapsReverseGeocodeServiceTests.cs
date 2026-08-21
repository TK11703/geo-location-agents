using System.Net;
using System.Text;
using GeoLocation.Models;
using GeoLocation.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace GeoLocation.Tests.Services;

public class AzureMapsReverseGeocodeServiceTests
{
    private const string MatchJson = """
    {
      "features": [
        {
          "properties": {
            "type": "Address",
            "confidence": "High",
            "address": {
              "addressLine": "1600 Private Forest Trail",
              "formattedAddress": "1600 Private Forest Trail, Redmond, WA 98052",
              "locality": "Redmond",
              "postalCode": "98052",
              "countryRegion": { "ISO": "US" }
            }
          }
        }
      ]
    }
    """;

    [Fact]
    public async Task GetAddressAsync_SendsCoordinatesAsLongitudeThenLatitude()
    {
        var handler = new RecordingHandler(JsonResponse(MatchJson));
        var service = CreateService(handler);

        await service.GetAddressAsync(new ReverseGeocodeQuery(47.6062, -122.3321), CancellationToken.None);

        var query = QueryHelpers.ParseQuery(handler.Requests.Single().Uri.Query);
        Assert.Equal("-122.3321,47.6062", query["coordinates"]);
        Assert.Equal("2026-01-01", query["api-version"]);
        Assert.False(query.ContainsKey("top"));
    }

    [Fact]
    public async Task GetAddressAsync_SendsSubscriptionKeyAsHeaderNotQueryString()
    {
        var handler = new RecordingHandler(JsonResponse(MatchJson));
        var service = CreateService(handler);

        await service.GetAddressAsync(new ReverseGeocodeQuery(47.6062, -122.3321), CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal("test-key", request.SubscriptionKey);
        Assert.DoesNotContain("test-key", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task GetAddressAsync_WithMatch_MapsAddressFields()
    {
        var service = CreateService(new RecordingHandler(JsonResponse(MatchJson)));

        var result = await service.GetAddressAsync(
            new ReverseGeocodeQuery(47.6062, -122.3321),
            CancellationToken.None);

        Assert.True(result.HasAddressMatch);
        Assert.Equal("1600 Private Forest Trail, Redmond, WA 98052", result.FormattedAddress);
        Assert.Equal("1600 Private Forest Trail", result.AddressLine);
        Assert.Equal("Redmond", result.Locality);
        Assert.Equal("98052", result.PostalCode);
        Assert.Equal("US", result.CountryCode);
        Assert.Equal("Address", result.ResultType);
        Assert.Equal("High", result.Confidence);
    }

    [Fact]
    public async Task GetAddressAsync_WithPrivateRoadKeyword_FlagsLikelyPrivateRoad()
    {
        var service = CreateService(new RecordingHandler(JsonResponse(MatchJson)));

        var result = await service.GetAddressAsync(
            new ReverseGeocodeQuery(47.6062, -122.3321),
            CancellationToken.None);

        Assert.True(result.IsLikelyPrivateRoad);
    }

    [Fact]
    public async Task GetAddressAsync_WithPublicRoadName_DoesNotFlagPrivateRoad()
    {
        const string json = """
        {
          "features": [
            { "properties": { "address": { "addressLine": "400 Broad Street" } } }
          ]
        }
        """;
        var service = CreateService(new RecordingHandler(JsonResponse(json)));

        var result = await service.GetAddressAsync(
            new ReverseGeocodeQuery(47.6062, -122.3321),
            CancellationToken.None);

        Assert.False(result.IsLikelyPrivateRoad);
    }

    [Fact]
    public async Task GetAddressAsync_WithSubstringMatchOnly_DoesNotFlagPrivateRoad()
    {
        const string json = """
        {
          "features": [
            { "properties": { "address": { "addressLine": "12 Trailside Commons" } } }
          ]
        }
        """;
        var service = CreateService(new RecordingHandler(JsonResponse(json)));

        var result = await service.GetAddressAsync(
            new ReverseGeocodeQuery(47.6062, -122.3321),
            CancellationToken.None);

        Assert.False(result.IsLikelyPrivateRoad);
    }

    [Fact]
    public async Task GetAddressAsync_WithNoFeatures_ReportsNoAddressMatch()
    {
        var service = CreateService(new RecordingHandler(JsonResponse("""{ "features": [] }""")));

        var result = await service.GetAddressAsync(
            new ReverseGeocodeQuery(47.6062, -122.3321),
            CancellationToken.None);

        Assert.False(result.HasAddressMatch);
        Assert.Null(result.FormattedAddress);
        Assert.False(result.IsLikelyPrivateRoad);
        Assert.Equal(47.6062, result.Latitude);
        Assert.Equal(-122.3321, result.Longitude);
    }

    [Fact]
    public async Task GetAddressAsync_WhenAzureMapsFails_ThrowsAzureMapsException()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<AzureMapsException>(() =>
            service.GetAddressAsync(new ReverseGeocodeQuery(47.6062, -122.3321), CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [Fact]
    public async Task GetAddressAsync_WithoutSubscriptionKey_ThrowsConfigurationException()
    {
        var configuration = new ConfigurationBuilder().Build();
        var service = new AzureMapsReverseGeocodeService(
            new HttpClient(new RecordingHandler(JsonResponse(MatchJson))),
            configuration);

        await Assert.ThrowsAsync<AzureMapsConfigurationException>(() =>
            service.GetAddressAsync(new ReverseGeocodeQuery(47.6062, -122.3321), CancellationToken.None));
    }

    private static AzureMapsReverseGeocodeService CreateService(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureMaps:SubscriptionKey"] = "test-key",
                ["AzureMaps:Endpoint"] = "https://atlas.microsoft.com"
            })
            .Build();
        return new AzureMapsReverseGeocodeService(new HttpClient(handler), configuration);
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
