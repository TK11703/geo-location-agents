using System.Globalization;
using ERDC.Agents.Models;
using ERDC.Agents.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ERDC.Agents.Functions;

public class ReverseGeocodeFunction(
    ILogger<ReverseGeocodeFunction> logger,
    IReverseGeocodeService reverseGeocodeService)
{
    [Function("ReverseGeocode")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "geocode/reverse")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        ReverseGeocodeRequest input;

        try
        {
            input = new ReverseGeocodeRequest
            {
                Latitude = ParseDouble(request.Query["latitude"], "latitude"),
                Longitude = ParseDouble(request.Query["longitude"], "longitude")
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
            var result = await reverseGeocodeService.GetAddressAsync(query!, cancellationToken);
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
            logger.LogError(ex, "Azure Maps reverse geocode failed with status {StatusCode}.", ex.StatusCode);
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

    private static BadRequestObjectResult BadRequest(string detail) =>
        new(new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Invalid reverse geocode request",
            Detail = detail
        });
}
