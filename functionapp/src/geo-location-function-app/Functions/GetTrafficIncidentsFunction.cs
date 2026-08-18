using System.Globalization;
using GeoLocation.Models;
using GeoLocation.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GeoLocation.Functions;

public class GetTrafficIncidentsFunction(
    ILogger<GetTrafficIncidentsFunction> logger,
    ITrafficIncidentService trafficIncidentService)
{
    [Function("GetTrafficIncidents")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "traffic/incidents")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        TrafficIncidentRequest input;

        try
        {
            input = new TrafficIncidentRequest
            {
                Latitude = ParseDouble(request.Query["latitude"], "latitude"),
                Longitude = ParseDouble(request.Query["longitude"], "longitude"),
                RadiusMeters = ParseInt(request.Query["radiusMeters"], "radiusMeters")
            };
        }
        catch (FormatException ex)
        {
            return BadRequest(ex.Message);
        }

        if (!input.TryNormalize(out var query, out var validationError))
        {
            return BadRequest(validationError!);
        }

        try
        {
            var result = await trafficIncidentService.GetIncidentsAsync(query!, cancellationToken);
            return new OkObjectResult(result);
        }
        catch (AzureMapsConfigurationException ex)
        {
            logger.LogError(ex, "Azure Maps configuration is missing.");
            return new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Azure Maps is not configured",
                Detail = "The Azure Maps subscription key is not configured for this application."
            })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
        }
        catch (AzureMapsException ex)
        {
            logger.LogError(ex, "Azure Maps traffic request failed with status {StatusCode}.", ex.StatusCode);
            return new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status502BadGateway,
                Title = "Azure Maps request failed",
                Detail = ex.Message
            })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
    }

    private static double? ParseDouble(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new FormatException($"{name} must be a valid number.");
    }

    private static int? ParseInt(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new FormatException($"{name} must be a valid whole number.");
    }

    private static BadRequestObjectResult BadRequest(string detail) =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Invalid traffic incident request",
            Detail = detail
        });
}
