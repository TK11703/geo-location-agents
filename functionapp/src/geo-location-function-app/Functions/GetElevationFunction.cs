using System.Globalization;
using GeoLocation.Models;
using GeoLocation.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GeoLocation.Functions;

public class GetElevationFunction(ILogger<GetElevationFunction> logger, IElevationService elevationService)
{
    [Function("GetElevation")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "elevation")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        ElevationRequest input;

        try
        {
            input = new ElevationRequest
            {
                Latitude = ParseDouble(request.Query["latitude"], "latitude"),
                Longitude = ParseDouble(request.Query["longitude"], "longitude"),
                RadiusMeters = ParseInt(request.Query["radiusMeters"], "radiusMeters"),
                SampleCount = ParseInt(request.Query["sampleCount"], "sampleCount")
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
            var result = await elevationService.GetElevationAsync(query!, cancellationToken);
            return new OkObjectResult(result);
        }
        catch (ElevationException ex)
        {
            logger.LogError(ex, "Elevation request failed with status {StatusCode}.", ex.StatusCode);
            return new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status502BadGateway,
                Title = "Elevation request failed",
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
            Title = "Invalid elevation request",
            Detail = detail
        });
}
