using System.Net;
using ERDC.Agents.Functions;
using ERDC.Agents.Models;
using ERDC.Agents.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERDC.Agents.Tests.Functions;

public sealed class GeocodeFunctionTests
{
    [Fact]
    public async Task Run_PassesNormalizedQueryToService()
    {
        var service = new CapturingGeocodeService();
        var result = await Invoke(service, "?query=Springfield&countryRegion=us&top=3");

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Springfield", service.Query!.Text);
        Assert.Equal("US", service.Query.CountryRegion);
        Assert.Equal(3, service.Query.Top);
    }

    [Fact]
    public async Task Run_ReturnsGeocodeResult()
    {
        var service = new CapturingGeocodeService();
        var result = await Invoke(service, "?query=Boise, Idaho");

        var response = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<GeocodeResult>(response.Value);
        Assert.True(payload.HasMatch);
        Assert.Equal(43.6150, Assert.Single(payload.Candidates).Latitude);
    }

    [Fact]
    public async Task Run_WithoutQuery_ReturnsBadRequest()
    {
        var result = await Invoke(new CapturingGeocodeService(), "?top=3");

        var response = Assert.IsType<BadRequestObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(response.Value);
        Assert.Equal("query is required and must name a place or address.", problem.Detail);
    }

    [Fact]
    public async Task Run_WithNonNumericTop_ReturnsBadRequest()
    {
        var result = await Invoke(new CapturingGeocodeService(), "?query=Boise&top=lots");

        var response = Assert.IsType<BadRequestObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(response.Value);
        Assert.Equal("top must be a whole number.", problem.Detail);
    }

    [Fact]
    public async Task Run_WhenAzureMapsIsNotConfigured_ReturnsServiceUnavailable()
    {
        var result = await Invoke(
            new ThrowingGeocodeService(new AzureMapsConfigurationException()),
            "?query=Boise");

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        Assert.IsType<ProblemDetails>(response.Value);
    }

    [Fact]
    public async Task Run_WhenAzureMapsFails_ReturnsBadGateway()
    {
        var result = await Invoke(
            new ThrowingGeocodeService(new AzureMapsException("upstream said no", HttpStatusCode.Forbidden)),
            "?query=Boise");

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, response.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(response.Value);
        Assert.Equal("upstream said no", problem.Detail);
    }

    private static Task<IActionResult> Invoke(IGeocodeService service, string queryString)
    {
        var function = new GeocodeFunction(NullLogger<GeocodeFunction>.Instance, service);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.QueryString = new QueryString(queryString);
        return function.RunAsync(context.Request, CancellationToken.None);
    }

    private sealed class CapturingGeocodeService : IGeocodeService
    {
        public GeocodeQuery? Query { get; private set; }

        public Task<GeocodeResult> GetCoordinatesAsync(
            GeocodeQuery query,
            CancellationToken cancellationToken)
        {
            Query = query;
            return Task.FromResult(new GeocodeResult(
                query.Text,
                HasMatch: true,
                [new GeocodeCandidate(43.6150, -116.2023, "Boise, ID", "Boise", "US", "Geography", "High")]));
        }
    }

    private sealed class ThrowingGeocodeService(Exception exception) : IGeocodeService
    {
        public Task<GeocodeResult> GetCoordinatesAsync(
            GeocodeQuery query,
            CancellationToken cancellationToken) =>
            Task.FromException<GeocodeResult>(exception);
    }
}
