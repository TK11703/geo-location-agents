using GeoLocation.Functions;
using GeoLocation.Models;
using GeoLocation.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace GeoLocation.Tests.Functions;

public sealed class GetRouteFunctionTests
{
    [Fact]
    public async Task Run_PassesZoomOverrideFromQueryToService()
    {
        var service = new CapturingRouteService();
        var function = new GetRouteFunction(service, new StubMapImageStore(), NullLogger<GetRouteFunction>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.QueryString = new QueryString(
            "?originLatitude=47.6062&originLongitude=-122.3321" +
            "&destinationLatitude=47.4502&destinationLongitude=-122.3088" +
            "&travelMode=truck&zoom=14");

        var result = await function.Run(context.Request, CancellationToken.None);

        Assert.IsType<FileContentResult>(result);
        Assert.Equal(14, service.Request!.Zoom);
    }

    [Fact]
    public async Task Run_PassesPartialTruckVehicleSpecFromQueryToService()
    {
        var service = new CapturingRouteService();
        var function = new GetRouteFunction(service, new StubMapImageStore(), NullLogger<GetRouteFunction>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.QueryString = new QueryString(
            "?originLatitude=47.6062&originLongitude=-122.3321" +
            "&destinationLatitude=47.4502&destinationLongitude=-122.3088" +
            "&travelMode=truck&axleCount=5&isVehicleCommercial=true");

        var result = await function.Run(context.Request, CancellationToken.None);

        Assert.IsType<FileContentResult>(result);
        Assert.Equal(5, service.Request!.VehicleSpec!.AxleCount);
        Assert.True(service.Request.VehicleSpec.IsVehicleCommercial);
        Assert.Null(service.Request.VehicleSpec.Weight);
    }

    [Fact]
    public async Task Run_ReturnsRouteDetailsWhenRequested()
    {
        var service = new CapturingRouteService();
        var function = new GetRouteFunction(service, new StubMapImageStore(), NullLogger<GetRouteFunction>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.QueryString = new QueryString(
            "?originLatitude=47.6062&originLongitude=-122.3321" +
            "&destinationLatitude=47.4502&destinationLongitude=-122.3088" +
            "&travelMode=truck&output=details");

        var result = await function.Run(context.Request, CancellationToken.None);

        var response = Assert.IsType<OkObjectResult>(result);
        var details = Assert.IsType<JsonElement>(response.Value);
        Assert.Equal("FeatureCollection", details.GetProperty("type").GetString());
        Assert.Equal("details", service.Request!.Output);
    }

    [Fact]
    public async Task Run_ReturnsSasUrlWhenUrlOutputRequested()
    {
        var service = new CapturingRouteService();
        var store = new StubMapImageStore();
        var function = new GetRouteFunction(service, store, NullLogger<GetRouteFunction>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.QueryString = new QueryString(
            "?originLatitude=47.6062&originLongitude=-122.3321" +
            "&destinationLatitude=47.4502&destinationLongitude=-122.3088" +
            "&output=url");

        var result = await function.Run(context.Request, CancellationToken.None);

        var response = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("url", service.Request!.Output);
        Assert.Equal([1, 2, 3], store.Stored!.Content);

        var imageUrl = response.Value!.GetType().GetProperty("imageUrl")!.GetValue(response.Value);
        Assert.Equal(StubMapImageStore.Url, imageUrl);
    }

    [Fact]
    public async Task Run_ReturnsServiceUnavailableWhenAzureMapsIsNotConfigured()
    {
        var function = new GetRouteFunction(
            new MissingConfigurationRouteService(),
            new StubMapImageStore(),
            NullLogger<GetRouteFunction>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.QueryString = new QueryString(
            "?originLatitude=47.6062&originLongitude=-122.3321" +
            "&destinationLatitude=47.4502&destinationLongitude=-122.3088" +
            "&travelMode=truck");

        var result = await function.Run(context.Request, CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(response.Value);
        Assert.Equal("The route service is not configured.", problem.Detail);
    }

    private sealed class MissingConfigurationRouteService : IMapRouteService
    {
        public Task<MapImage> GetRouteImageAsync(
            RouteCalculationRequest request,
            CancellationToken cancellationToken) =>
            throw new AzureMapsConfigurationException();

        public Task<JsonElement> GetRouteDetailsAsync(
            RouteCalculationRequest request,
            CancellationToken cancellationToken) =>
            throw new AzureMapsConfigurationException();
    }

    private sealed class CapturingRouteService : IMapRouteService
    {
        public RouteCalculationRequest? Request { get; private set; }

        public Task<MapImage> GetRouteImageAsync(
            RouteCalculationRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new MapImage([1, 2, 3], "image/png"));
        }

        public Task<JsonElement> GetRouteDetailsAsync(
            RouteCalculationRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            using var document = JsonDocument.Parse("""{ "type": "FeatureCollection" }""");
            return Task.FromResult(document.RootElement.Clone());
        }
    }

    private sealed class StubMapImageStore : IMapImageStore
    {
        public static readonly Uri Url = new("https://example.invalid/map.png?sig=test");

        public MapImage? Stored { get; private set; }

        public Task<StoredMapImage> StoreAsync(MapImage image, CancellationToken cancellationToken)
        {
            Stored = image;
            return Task.FromResult(new StoredMapImage(Url, DateTimeOffset.UtcNow.AddMinutes(15)));
        }
    }
}