using System.Net;
using System.Net.Http.Headers;
using System.Text;
using GeoLocation.Models;
using GeoLocation.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace GeoLocation.Tests.Services;

public sealed class AzureMapsServiceTests
{
    [Fact]
    public async Task GetMapImageAsync_GeocodesCityAndReturnsPng()
    {
        var handler = new RecordingHandler(
            JsonResponse("""
                {
                  "features": [
                    { "geometry": { "coordinates": [-122.3321, 47.6062] } }
                  ]
                }
                """),
            ImageResponse([1, 2, 3]));
        var service = CreateService(handler);
        var request = new MapRenderRequest("Seattle", null, null, 640, 480, 10);

        var result = await service.GetMapImageAsync(request, CancellationToken.None);

        Assert.Equal([1, 2, 3], result.Content);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("/geocode", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Contains("query=Seattle", handler.Requests[0].Uri.Query);
        Assert.Contains("/map/static", handler.Requests[1].Uri.AbsoluteUri);
        var renderQuery = QueryHelpers.ParseQuery(handler.Requests[1].Uri.Query);
        Assert.Equal("-122.3321,47.6062", renderQuery["center"]);
        Assert.All(handler.Requests, request => Assert.Equal("test-key", request.SubscriptionKey));
        Assert.DoesNotContain("test-key", handler.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task GetMapImageAsync_UsesCoordinatesWithoutGeocoding()
    {
        var handler = new RecordingHandler(ImageResponse([4, 5, 6]));
        var service = CreateService(handler);
        var request = new MapRenderRequest(
            null,
            47.6062,
            -122.3321,
            512,
            512,
            12,
            "microsoft.imagery");

        await service.GetMapImageAsync(request, CancellationToken.None);

        var sentRequest = Assert.Single(handler.Requests);
        Assert.Contains("/map/static", sentRequest.Uri.AbsoluteUri);
        Assert.Contains("width=512", sentRequest.Uri.Query);
        Assert.Contains("height=512", sentRequest.Uri.Query);
        var renderQuery = QueryHelpers.ParseQuery(sentRequest.Uri.Query);
        Assert.Equal("microsoft.imagery", renderQuery["tilesetId"]);
    }

    [Fact]
    public async Task GetMapImageAsync_ThrowsWhenCityIsNotFound()
    {
        var handler = new RecordingHandler(JsonResponse("{ \"features\": [] }"));
        var service = CreateService(handler);
        var request = new MapRenderRequest("Unknown", null, null, 512, 512, 12);

        await Assert.ThrowsAsync<MapLocationNotFoundException>(
            () => service.GetMapImageAsync(request, CancellationToken.None));
    }

    private static AzureMapsService CreateService(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureMaps:SubscriptionKey"] = "test-key",
                ["AzureMaps:Endpoint"] = "https://atlas.microsoft.com"
            })
            .Build();

        return new AzureMapsService(new HttpClient(handler), configuration);
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