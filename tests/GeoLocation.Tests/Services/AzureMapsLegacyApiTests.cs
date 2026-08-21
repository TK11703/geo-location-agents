using System.Net;
using System.Net.Http.Headers;
using System.Text;
using GeoLocation.Models;
using GeoLocation.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace GeoLocation.Tests.Services;

// Azure Government only serves the v1 Azure Maps routes, so every service swaps request and
// response shapes when it is pointed at atlas.azure.us. These cover that second shape.
public sealed class AzureMapsLegacyApiTests
{
    private const string GovernmentEndpoint = "https://atlas.azure.us";
    private const string PublicEndpoint = "https://atlas.microsoft.com";

    [Theory]
    [InlineData(GovernmentEndpoint, true)]
    [InlineData("https://atlas.azure.us/", true)]
    [InlineData(PublicEndpoint, false)]
    [InlineData(null, false)]
    public void UseLegacyApis_FollowsTheSovereignCloudHost(string? endpoint, bool expected)
    {
        var configuration = Configuration(endpoint);

        Assert.Equal(expected, AzureMapsApiProfile.UseLegacyApis(configuration));
    }

    [Theory]
    [InlineData(GovernmentEndpoint, "false", false)]
    [InlineData(PublicEndpoint, "true", true)]
    public void UseLegacyApis_HonoursTheExplicitOverride(string endpoint, string configured, bool expected)
    {
        var configuration = Configuration(endpoint, configured);

        Assert.Equal(expected, AzureMapsApiProfile.UseLegacyApis(configuration));
    }

    [Fact]
    public async Task Geocode_UsesSearchAddressAndReadsResults()
    {
        const string json = """
        {
          "results": [
            {
              "type": "Geography",
              "score": 14.51,
              "address": {
                "freeformAddress": "Boise, ID, United States",
                "municipality": "Boise",
                "countryCode": "US"
              },
              "position": { "lat": 43.6150, "lon": -116.2023 }
            }
          ]
        }
        """;
        var handler = new RecordingHandler(JsonResponse(json));
        var service = new AzureMapsGeocodeService(new HttpClient(handler), Configuration(GovernmentEndpoint));

        var result = await service.GetCoordinatesAsync(
            new GeocodeQuery("Boise, Idaho", null, 5),
            CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal("/search/address/json", request.Uri.AbsolutePath);
        var query = QueryHelpers.ParseQuery(request.Uri.Query);
        Assert.Equal("1.0", query["api-version"]);
        Assert.Equal("Boise, Idaho", query["query"]);
        Assert.Equal("5", query["limit"]);
        Assert.False(query.ContainsKey("top"));

        var candidate = Assert.Single(result.Candidates);
        Assert.True(result.HasMatch);
        Assert.Equal(43.6150, candidate.Latitude);
        Assert.Equal(-116.2023, candidate.Longitude);
        Assert.Equal("Boise, ID, United States", candidate.FormattedAddress);
        Assert.Equal("Boise", candidate.Locality);
        Assert.Equal("US", candidate.CountryCode);
        Assert.Equal("Geography", candidate.ResultType);
        // The v1 score is only comparable inside one result set, so it cannot become a confidence band.
        Assert.Null(candidate.Confidence);
    }

    [Fact]
    public async Task Geocode_StillFiltersCandidatesToTheRequestedCountry()
    {
        const string json = """
        {
          "results": [
            {
              "address": { "freeformAddress": "Springfield, IL", "countryCode": "US" },
              "position": { "lat": 39.7817, "lon": -89.6501 }
            },
            {
              "address": { "freeformAddress": "Springfield, England", "countryCode": "GB" },
              "position": { "lat": 51.9912, "lon": -1.1743 }
            }
          ]
        }
        """;
        var service = new AzureMapsGeocodeService(
            new HttpClient(new RecordingHandler(JsonResponse(json))),
            Configuration(GovernmentEndpoint));

        var result = await service.GetCoordinatesAsync(
            new GeocodeQuery("Springfield", "US", 5),
            CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Springfield, IL", candidate.FormattedAddress);
    }

    // Reverse geocode v1 takes latitude first while v2 takes longitude first, so a copied
    // parameter would silently look up a point on the other side of the world.
    [Fact]
    public async Task ReverseGeocode_SendsLatitudeBeforeLongitude()
    {
        var handler = new RecordingHandler(JsonResponse(ReverseAddressJson));
        var service = new AzureMapsReverseGeocodeService(
            new HttpClient(handler),
            Configuration(GovernmentEndpoint));

        await service.GetAddressAsync(new ReverseGeocodeQuery(37.33709, -121.88982), CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal("/search/address/reverse/json", request.Uri.AbsolutePath);
        var query = QueryHelpers.ParseQuery(request.Uri.Query);
        Assert.Equal("1.0", query["api-version"]);
        Assert.Equal("37.33709,-121.88982", query["query"]);
        Assert.False(query.ContainsKey("coordinates"));
    }

    [Fact]
    public async Task ReverseGeocode_ReadsTheAddressesArray()
    {
        var service = new AzureMapsReverseGeocodeService(
            new HttpClient(new RecordingHandler(JsonResponse(ReverseAddressJson))),
            Configuration(GovernmentEndpoint));

        var result = await service.GetAddressAsync(
            new ReverseGeocodeQuery(37.33709, -121.88982),
            CancellationToken.None);

        Assert.True(result.HasAddressMatch);
        Assert.Equal("31 N 2nd St, San Jose CA 95113", result.FormattedAddress);
        Assert.Equal("31 N 2nd St", result.AddressLine);
        Assert.Equal("San Jose", result.Locality);
        Assert.Equal("95113", result.PostalCode);
        Assert.Equal("US", result.CountryCode);
        Assert.Null(result.ResultType);
        Assert.Null(result.Confidence);
    }

    [Fact]
    public async Task ReverseGeocode_ReportsNoMatchWhenAddressesIsEmpty()
    {
        var service = new AzureMapsReverseGeocodeService(
            new HttpClient(new RecordingHandler(JsonResponse("{ \"addresses\": [] }"))),
            Configuration(GovernmentEndpoint));

        var result = await service.GetAddressAsync(
            new ReverseGeocodeQuery(37.33709, -121.88982),
            CancellationToken.None);

        Assert.False(result.HasAddressMatch);
        Assert.Equal(37.33709, result.Latitude);
        Assert.Equal(-121.88982, result.Longitude);
    }

    [Fact]
    public async Task Route_UsesGetWithColonDelimitedWaypointsAndVehicleQueryParameters()
    {
        const string routeJson = """
        {
          "routes": [
            {
              "legs": [
                {
                  "points": [
                    { "latitude": 47.6062, "longitude": -122.3321 },
                    { "latitude": 47.5300, "longitude": -122.3200 },
                    { "latitude": 47.4502, "longitude": -122.3088 }
                  ]
                }
              ]
            }
          ]
        }
        """;
        var handler = new RecordingHandler(JsonResponse(routeJson), ImageResponse([1, 2, 3]));
        var service = new AzureMapsRouteService(new HttpClient(handler), Configuration(GovernmentEndpoint));
        var request = new RouteCalculationRequest(
            47.6062,
            -122.3321,
            47.4502,
            -122.3088,
            "truck",
            14,
            new TruckVehicleSpec
            {
                AxleCount = 5,
                Weight = 20000,
                Height = 4.2,
                IsVehicleCommercial = true,
                LoadType = ["USHazmatClass1", "otherHazmatExplosive"]
            });

        var result = await service.GetRouteImageAsync(request, CancellationToken.None);

        Assert.Equal([1, 2, 3], result.Content);
        Assert.Equal(2, handler.Requests.Count);

        var routeRequest = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, routeRequest.Method);
        Assert.Equal("/route/directions/json", routeRequest.Uri.AbsolutePath);
        Assert.Null(routeRequest.Body);
        var query = QueryHelpers.ParseQuery(routeRequest.Uri.Query);
        Assert.Equal("1.0", query["api-version"]);
        Assert.Equal("47.6062,-122.3321:47.4502,-122.3088", query["query"]);
        Assert.Equal("truck", query["travelMode"]);
        Assert.Equal("polyline", query["routeRepresentation"]);
        Assert.Equal("20000", query["vehicleWeight"]);
        Assert.Equal("4.2", query["vehicleHeight"]);
        Assert.Equal("true", query["vehicleCommercial"]);
        Assert.Equal(["USHazmatClass1", "otherHazmatExplosive"], query["vehicleLoadType"]);
        // Route v1 has no axle-count restriction, so the value is dropped rather than mistranslated.
        Assert.DoesNotContain("axle", routeRequest.Uri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Route_ReadsLegPointsAndStillRendersThroughTheV2StaticMap()
    {
        const string routeJson = """
        {
          "routes": [
            {
              "legs": [
                {
                  "points": [
                    { "latitude": 47.6062, "longitude": -122.3321 },
                    { "latitude": 47.4502, "longitude": -122.3088 }
                  ]
                }
              ]
            }
          ]
        }
        """;
        var handler = new RecordingHandler(JsonResponse(routeJson), ImageResponse([9]));
        var service = new AzureMapsRouteService(new HttpClient(handler), Configuration(GovernmentEndpoint));

        await service.GetRouteImageAsync(
            new RouteCalculationRequest(47.6062, -122.3321, 47.4502, -122.3088, "car", 14),
            CancellationToken.None);

        Assert.Equal("car", QueryHelpers.ParseQuery(handler.Requests[0].Uri.Query)["travelMode"]);

        var renderRequest = handler.Requests[1];
        Assert.Equal("/map/static", renderRequest.Uri.AbsolutePath);
        var renderQuery = QueryHelpers.ParseQuery(renderRequest.Uri.Query);
        Assert.Equal("2024-04-01", renderQuery["api-version"]);
        Assert.Equal("microsoft.base.road", renderQuery["tilesetId"]);
        Assert.False(renderQuery.ContainsKey("layer"));
        Assert.Contains("-122.3321 47.6062", renderQuery["path"].Single());
        Assert.Contains("-122.3088 47.4502", renderQuery["path"].Single());
    }

    [Fact]
    public async Task Route_ThrowsWhenTheLegacyResponseCarriesNoGeometry()
    {
        var service = new AzureMapsRouteService(
            new HttpClient(new RecordingHandler(JsonResponse("{ \"routes\": [] }"))),
            Configuration(GovernmentEndpoint));

        await Assert.ThrowsAsync<AzureMapsException>(() => service.GetRouteImageAsync(
            new RouteCalculationRequest(47.6062, -122.3321, 47.4502, -122.3088, "car"),
            CancellationToken.None));
    }

    [Fact]
    public async Task StaticMap_GeocodesTheCityThroughSearchAddress()
    {
        const string geocodeJson = """
        { "results": [ { "position": { "lat": 47.6062, "lon": -122.3321 } } ] }
        """;
        var handler = new RecordingHandler(JsonResponse(geocodeJson), ImageResponse([1]));
        var service = new AzureMapsService(new HttpClient(handler), Configuration(GovernmentEndpoint));

        await service.GetMapImageAsync(
            new MapRenderRequest("Seattle", null, null, 640, 480, 10),
            CancellationToken.None);

        var geocodeRequest = handler.Requests[0];
        Assert.Equal("/search/address/json", geocodeRequest.Uri.AbsolutePath);
        var geocodeQuery = QueryHelpers.ParseQuery(geocodeRequest.Uri.Query);
        Assert.Equal("Seattle", geocodeQuery["query"]);
        Assert.Equal("1", geocodeQuery["limit"]);

        var renderQuery = QueryHelpers.ParseQuery(handler.Requests[1].Uri.Query);
        Assert.Equal("-122.3321,47.6062", renderQuery["center"]);
    }

    [Fact]
    public async Task StaticMap_ThrowsWhenSearchAddressReturnsNothing()
    {
        var service = new AzureMapsService(
            new HttpClient(new RecordingHandler(JsonResponse("{ \"results\": [] }"))),
            Configuration(GovernmentEndpoint));

        await Assert.ThrowsAsync<MapLocationNotFoundException>(() => service.GetMapImageAsync(
            new MapRenderRequest("Unknown", null, null, 512, 512, 12),
            CancellationToken.None));
    }

    private const string ReverseAddressJson = """
    {
      "addresses": [
        {
          "address": {
            "streetNumber": "31",
            "streetName": "N 2nd St",
            "streetNameAndNumber": "31 N 2nd St",
            "municipality": "San Jose",
            "postalCode": "95113",
            "countryCode": "US",
            "freeformAddress": "31 N 2nd St, San Jose CA 95113"
          },
          "position": "37.337090,-121.889820"
        }
      ]
    }
    """;

    private static IConfiguration Configuration(string? endpoint, string? useLegacyApis = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureMaps:SubscriptionKey"] = "test-key",
                ["AzureMaps:Endpoint"] = endpoint,
                ["AzureMaps:UseLegacyApis"] = useLegacyApis
            })
            .Build();

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage ImageResponse(byte[] content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return response;
    }

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.GetValues("subscription-key").Single(),
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _responses.Dequeue();
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string SubscriptionKey,
        string? Body);
}
