using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using ERDC.Agents.Models;
using ERDC.Agents.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace ERDC.Agents.Tests.Services;

public class UsgsElevationServiceTests
{
    [Fact]
    public async Task GetElevationAsync_QueriesTheCenterPointPlusOneSamplePerBearing()
    {
        var handler = new RecordingHandler(_ => Elevation("100"));
        var service = CreateService(handler);

        await service.GetElevationAsync(
            new ElevationQuery(47.6062, -122.3321, 100, 4),
            CancellationToken.None);

        Assert.Equal(5, handler.Requests.Count);
    }

    [Fact]
    public async Task GetElevationAsync_SendsCoordinatesAsXLongitudeAndYLatitudeInMeters()
    {
        var handler = new RecordingHandler(_ => Elevation("100"));
        var service = CreateService(handler);

        await service.GetElevationAsync(
            new ElevationQuery(47.6062, -122.3321, 100, 4),
            CancellationToken.None);

        var center = handler.Requests.Single(uri => IsCenter(uri, 47.6062, -122.3321));
        var query = QueryHelpers.ParseQuery(center.Query);
        Assert.Equal("-122.3321", query["x"]);
        Assert.Equal("47.6062", query["y"]);
        Assert.Equal("Meters", query["units"]);
        Assert.Equal("4326", query["wkid"]);
    }

    [Fact]
    public async Task GetElevationAsync_ReturnsCenterSampleFirstWithZeroDistance()
    {
        var service = CreateService(new RecordingHandler(_ => Elevation("100")));

        var result = await service.GetElevationAsync(
            new ElevationQuery(47.6062, -122.3321, 100, 4),
            CancellationToken.None);

        Assert.Equal(5, result.Samples.Count);
        Assert.Equal(0, result.Samples[0].DistanceMeters);
        Assert.Equal(47.6062, result.Samples[0].Latitude);
        Assert.Equal(-122.3321, result.Samples[0].Longitude);
        Assert.All(result.Samples.Skip(1), sample => Assert.Equal(100, sample.DistanceMeters));
    }

    [Fact]
    public async Task GetElevationAsync_OnFlatTerrain_ReportsZeroRangeAndSlope()
    {
        var service = CreateService(new RecordingHandler(_ => Elevation("100")));

        var result = await service.GetElevationAsync(
            new ElevationQuery(47.6062, -122.3321, 100, 4),
            CancellationToken.None);

        Assert.Equal(100, result.CenterElevationMeters);
        Assert.Equal(0, result.ElevationRangeMeters);
        Assert.Equal(0, result.MaxSlopePercent);
    }

    [Fact]
    public async Task GetElevationAsync_OnSlopedTerrain_ReportsRangeAndMaxSlope()
    {
        // Centre reads 100 m; every ring sample 100 m away reads 110 m, giving a 10% grade.
        var handler = new RecordingHandler(uri =>
            IsCenter(uri, 47.6062, -122.3321) ? Elevation("100") : Elevation("110"));
        var service = CreateService(handler);

        var result = await service.GetElevationAsync(
            new ElevationQuery(47.6062, -122.3321, 100, 4),
            CancellationToken.None);

        Assert.Equal(100, result.MinElevationMeters);
        Assert.Equal(110, result.MaxElevationMeters);
        Assert.Equal(10, result.ElevationRangeMeters);
        Assert.Equal(10, result.MaxSlopePercent);
    }

    [Fact]
    public async Task GetElevationAsync_WithNoDataSentinel_ReportsNullElevation()
    {
        var service = CreateService(new RecordingHandler(_ => Elevation("-1000000")));

        var result = await service.GetElevationAsync(
            new ElevationQuery(47.6062, -122.3321, 100, 4),
            CancellationToken.None);

        Assert.Null(result.CenterElevationMeters);
        Assert.Null(result.MinElevationMeters);
        Assert.Null(result.MaxSlopePercent);
        Assert.All(result.Samples, sample => Assert.Null(sample.ElevationMeters));
    }

    [Fact]
    public async Task GetElevationAsync_WithNumericValue_ParsesElevation()
    {
        var service = CreateService(new RecordingHandler(_ => Json("""{ "value": 42.5 }""")));

        var result = await service.GetElevationAsync(
            new ElevationQuery(47.6062, -122.3321, 100, 4),
            CancellationToken.None);

        Assert.Equal(42.5, result.CenterElevationMeters);
    }

    [Fact]
    public async Task GetElevationAsync_ForPointOutsideCoverage_ReportsNullElevation()
    {
        var service = CreateService(new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest)));

        var result = await service.GetElevationAsync(
            new ElevationQuery(51.5072, -0.1276, 100, 4),
            CancellationToken.None);

        Assert.Null(result.CenterElevationMeters);
        Assert.All(result.Samples, sample => Assert.Null(sample.ElevationMeters));
    }

    [Fact]
    public async Task GetElevationAsync_WhenBodyIsNotJson_ReportsNullElevation()
    {
        // What the live service actually returns outside the United States: 200, a JSON content
        // type, and a plain-text body. Parsing this as JSON used to escape as a 500.
        var service = CreateService(new RecordingHandler(
            _ => Json("Invalid or missing input parameters.")));

        var result = await service.GetElevationAsync(
            new ElevationQuery(51.5072, -0.1276, 100, 4),
            CancellationToken.None);

        Assert.Null(result.CenterElevationMeters);
        Assert.Null(result.ElevationRangeMeters);
        Assert.All(result.Samples, sample => Assert.Null(sample.ElevationMeters));
    }

    [Fact]
    public async Task GetElevationAsync_WhenServiceFails_ThrowsElevationException()
    {
        var service = CreateService(new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var exception = await Assert.ThrowsAsync<ElevationException>(() =>
            service.GetElevationAsync(
                new ElevationQuery(47.6062, -122.3321, 100, 4),
                CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
    }

    private static bool IsCenter(Uri uri, double latitude, double longitude)
    {
        var query = QueryHelpers.ParseQuery(uri.Query);
        return double.Parse(query["y"]!, CultureInfo.InvariantCulture) == latitude
            && double.Parse(query["x"]!, CultureInfo.InvariantCulture) == longitude;
    }

    private static UsgsElevationService CreateService(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Elevation:Endpoint"] = "https://epqs.nationalmap.gov/v1/json"
            })
            .Build();
        return new UsgsElevationService(new HttpClient(handler), configuration);
    }

    private static HttpResponseMessage Elevation(string value) => Json($$"""{ "value": "{{value}}" }""");

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    // Points are fetched in parallel, so responses are produced per request rather than dequeued.
    private sealed class RecordingHandler(Func<Uri, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Uri> _requests = new();

        public IReadOnlyList<Uri> Requests => [.. _requests];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requests.Enqueue(request.RequestUri!);
            return Task.FromResult(responder(request.RequestUri!));
        }
    }
}
