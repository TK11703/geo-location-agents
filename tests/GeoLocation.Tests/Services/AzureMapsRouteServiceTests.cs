using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GeoLocation.Models;
using GeoLocation.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace GeoLocation.Tests.Services;

public sealed class AzureMapsRouteServiceTests
{
    [Fact]
        public async Task GetRouteImageAsync_CalculatesAndRendersTruckRoute()
    {
                const string routeJson = """
            {
              "type": "FeatureCollection",
                            "features": [
                                {
                                    "type": "Feature",
                                    "geometry": {
                                        "type": "MultiLineString",
                                        "coordinates": [[
                                            [-122.3321, 47.6062],
                                            [-122.3200, 47.5300],
                                            [-122.3088, 47.4502]
                                        ]]
                                    },
                                    "properties": { "type": "RoutePath" }
                                }
                            ]
            }
            """;
                var handler = new RecordingHandler(
                        JsonResponse(routeJson),
                        ImageResponse([1, 2, 3]));
        var service = CreateService(handler);
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
                IsVehicleCommercial = true
            });

        var result = await service.GetRouteImageAsync(request, CancellationToken.None);

        Assert.Equal([1, 2, 3], result.Content);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(2, handler.Requests.Count);

        var routeRequest = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, routeRequest.Method);
        Assert.Equal("/route/directions", routeRequest.Uri.AbsolutePath);
        var query = QueryHelpers.ParseQuery(routeRequest.Uri.Query);
        Assert.Equal("2025-01-01", query["api-version"]);
        Assert.False(query.ContainsKey("travelMode"));

        using var body = JsonDocument.Parse(routeRequest.Body!);
        Assert.Equal("truck", body.RootElement.GetProperty("travelMode").GetString());
        Assert.Equal("routePath", body.RootElement.GetProperty("routeOutputOptions")[0].GetString());
        Assert.Equal("itinerary", body.RootElement.GetProperty("routeOutputOptions")[1].GetString());
        var vehicleSpec = body.RootElement.GetProperty("vehicleSpec");
        Assert.Equal(5, vehicleSpec.GetProperty("axleCount").GetInt32());
        Assert.True(vehicleSpec.GetProperty("isVehicleCommercial").GetBoolean());
        Assert.False(vehicleSpec.TryGetProperty("weight", out _));
        Assert.False(vehicleSpec.TryGetProperty("axleWeight", out _));
        Assert.False(vehicleSpec.TryGetProperty("height", out _));
        Assert.False(vehicleSpec.TryGetProperty("length", out _));
        Assert.False(vehicleSpec.TryGetProperty("loadType", out _));
        Assert.False(vehicleSpec.TryGetProperty("maxSpeed", out _));
        Assert.False(vehicleSpec.TryGetProperty("width", out _));
        var features = body.RootElement.GetProperty("features");
        Assert.Equal(-122.3321, features[0].GetProperty("geometry").GetProperty("coordinates")[0].GetDouble());
        Assert.Equal(47.6062, features[0].GetProperty("geometry").GetProperty("coordinates")[1].GetDouble());
        Assert.Equal(-122.3088, features[1].GetProperty("geometry").GetProperty("coordinates")[0].GetDouble());
        Assert.Equal(1, features[1].GetProperty("properties").GetProperty("pointIndex").GetInt32());

        var renderRequest = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, renderRequest.Method);
        Assert.Equal("/map/static", renderRequest.Uri.AbsolutePath);
        var renderQuery = QueryHelpers.ParseQuery(renderRequest.Uri.Query);
        Assert.Equal("2024-04-01", renderQuery["api-version"]);
        Assert.Contains("-122.3321 47.6062", renderQuery["path"].Single());
        Assert.Contains("-122.3088 47.4502", renderQuery["path"].Single());
        Assert.Contains("-122.3321 47.6062", renderQuery["pins"].Single());
        Assert.False(string.IsNullOrWhiteSpace(renderQuery["center"].Single()));
        Assert.Equal("14", renderQuery["zoom"]);
        Assert.Equal("800", renderQuery["width"]);
        Assert.Equal("600", renderQuery["height"]);
        Assert.False(renderQuery.ContainsKey("bbox"));
        Assert.All(handler.Requests, sent => Assert.Equal("test-key", sent.SubscriptionKey));
        Assert.All(handler.Requests, sent => Assert.DoesNotContain("test-key", sent.Uri.AbsoluteUri));
    }

        [Fact]
        public async Task GetRouteDetailsAsync_ReturnsFullRouteGeoJsonWithoutRenderingMap()
        {
                const string routeJson = """
                        {
                            "type": "FeatureCollection",
                            "features": [
                                {
                                    "type": "Feature",
                                    "geometry": {
                                        "type": "MultiLineString",
                                        "coordinates": [[
                                            [-122.3321, 47.6062],
                                            [-122.3200, 47.5300],
                                            [-122.3088, 47.4502]
                                        ]]
                                    },
                                    "properties": {
                                        "type": "RoutePath",
                                        "distanceInMeters": 24560,
                                        "durationInSeconds": 1735
                                    }
                                },
                                {
                                    "type": "Feature",
                                    "geometry": {
                                        "type": "Point",
                                        "coordinates": [-122.3200, 47.5300]
                                    },
                                    "properties": {
                                        "type": "ManeuverPoint",
                                        "instruction": { "text": "Keep right" }
                                    }
                                }
                            ]
                        }
                        """;
                var handler = new RecordingHandler(JsonResponse(routeJson));
                var service = CreateService(handler);
                var request = new RouteCalculationRequest(
                        47.6062,
                        -122.3321,
                        47.4502,
                        -122.3088,
                        "truck");

                var result = await service.GetRouteDetailsAsync(request, CancellationToken.None);

                Assert.Equal("FeatureCollection", result.GetProperty("type").GetString());
                var routePath = result.GetProperty("features")[0];
                Assert.Equal(24560, routePath.GetProperty("properties").GetProperty("distanceInMeters").GetInt32());
                Assert.Equal(3, routePath.GetProperty("geometry").GetProperty("coordinates")[0].GetArrayLength());
                Assert.Equal(
                        "Keep right",
                        result.GetProperty("features")[1]
                                .GetProperty("properties")
                                .GetProperty("instruction")
                                .GetProperty("text")
                                .GetString());
                Assert.Single(handler.Requests);
                Assert.Equal("/route/directions", handler.Requests[0].Uri.AbsolutePath);
        }

    private static AzureMapsRouteService CreateService(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureMaps:SubscriptionKey"] = "test-key",
                ["AzureMaps:Endpoint"] = "https://atlas.microsoft.com"
            })
            .Build();

        return new AzureMapsRouteService(new HttpClient(handler), configuration);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/geo+json")
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