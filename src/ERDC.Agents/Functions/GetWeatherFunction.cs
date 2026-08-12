using System.Globalization;
using ERDC.Agents.Models;
using ERDC.Agents.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ERDC.Agents.Functions;

public class GetWeatherFunction(ILogger<GetWeatherFunction> logger, IWeatherService weatherService)
{
    [Function("GetWeatherConditions")]
    public Task<IActionResult> GetConditionsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "weather/conditions")] HttpRequest request,
        CancellationToken cancellationToken) =>
        RunAsync(request, weatherService.GetCurrentConditionsAsync, cancellationToken);

    [Function("GetWeatherAlerts")]
    public Task<IActionResult> GetAlertsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "weather/alerts")] HttpRequest request,
        CancellationToken cancellationToken) =>
        RunAsync(request, weatherService.GetSevereWeatherAlertsAsync, cancellationToken);

    private async Task<IActionResult> RunAsync(
        HttpRequest request,
        Func<WeatherQuery, CancellationToken, Task<System.Text.Json.JsonElement>> operation,
        CancellationToken cancellationToken)
    {
        WeatherRequest input;

        try
        {
            input = new WeatherRequest
            {
                Latitude = ParseDouble(request.Query["latitude"], "latitude"),
                Longitude = ParseDouble(request.Query["longitude"], "longitude"),
                Unit = request.Query["unit"]
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
            var result = await operation(query!, cancellationToken);
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
            logger.LogError(ex, "Azure Maps weather request failed with status {StatusCode}.", ex.StatusCode);
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
            Title = "Invalid weather request",
            Detail = detail
        });
}
