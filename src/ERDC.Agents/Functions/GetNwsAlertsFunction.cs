using System.Globalization;
using ERDC.Agents.Models;
using ERDC.Agents.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ERDC.Agents.Functions;

public class GetNwsAlertsFunction(ILogger<GetNwsAlertsFunction> logger, INwsAlertService nwsAlertService)
{
    [Function("GetNwsAlerts")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "alerts/nws")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        NwsAlertRequest input;

        try
        {
            input = new NwsAlertRequest
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
            var result = await nwsAlertService.GetActiveAlertsAsync(query!, cancellationToken);
            return new OkObjectResult(result);
        }
        catch (NwsConfigurationException ex)
        {
            logger.LogError(ex, "National Weather Service configuration is missing.");
            return new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "National Weather Service is not configured",
                Detail = "A contact User-Agent is required by the National Weather Service and is not configured."
            })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
        }
        catch (NwsException ex)
        {
            logger.LogError(ex, "National Weather Service request failed with status {StatusCode}.", ex.StatusCode);
            return new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status502BadGateway,
                Title = "National Weather Service request failed",
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
            Title = "Invalid alert request",
            Detail = detail
        });
}
