using System.Globalization;
using System.Text.Json;
using ERDC.Agents.Models;
using ERDC.Agents.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ERDC.Agents.Functions;

public sealed class GetMapFunction(
    IMapImageService mapImageService,
    IMapImageStore mapImageStore,
    ILogger<GetMapFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetMap")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "map")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        MapRequest? input;

        try
        {
            input = HttpMethods.IsPost(request.Method)
                ? await JsonSerializer.DeserializeAsync<MapRequest>(request.Body, JsonOptions, cancellationToken)
                : ParseQuery(request.Query);
        }
        catch (JsonException)
        {
            return BadRequest("The JSON request body is invalid.");
        }
        catch (FormatException exception)
        {
            return BadRequest(exception.Message);
        }

        if (input is null)
        {
            return BadRequest("A JSON request body is required.");
        }

        if (!input.TryNormalize(out var normalized, out var validationError))
        {
            return BadRequest(validationError!);
        }

        try
        {
            var image = await mapImageService.GetMapImageAsync(normalized!, cancellationToken);

            if (normalized!.Output == "url")
            {
                var stored = await mapImageStore.StoreAsync(image, cancellationToken);
                return new OkObjectResult(new
                {
                    imageUrl = stored.Url,
                    expiresOn = stored.ExpiresOn
                });
            }

            return new FileContentResult(image.Content, image.ContentType);
        }
        catch (AzureMapsConfigurationException exception)
        {
            logger.LogError(exception, "Azure Maps is not configured for the map function.");
            return new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Map service unavailable",
                Detail = "The map service is not configured."
            })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
        }
        catch (MapLocationNotFoundException)
        {
            return new NotFoundObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Location not found",
                Detail = "Azure Maps could not find the requested city."
            });
        }
        catch (AzureMapsException exception)
        {
            logger.LogError(exception, "Azure Maps returned status code {StatusCode}.", exception.StatusCode);
            return new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status502BadGateway,
                Title = "Map service unavailable",
                Detail = "Azure Maps could not complete the request."
            })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
    }

    private static MapRequest ParseQuery(IQueryCollection query) => new()
    {
        City = query["city"].FirstOrDefault(),
        Latitude = ParseDouble(query, "latitude"),
        Longitude = ParseDouble(query, "longitude"),
        Width = ParseInt(query, "width"),
        Height = ParseInt(query, "height"),
        Zoom = ParseInt(query, "zoom"),
        RadiusMeters = ParseInt(query, "radiusMeters"),
        MapType = query["mapType"].FirstOrDefault(),
        Output = query["output"].FirstOrDefault()
    };

    private static double? ParseDouble(IQueryCollection query, string name)
    {
        var value = query[name].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new FormatException($"{name} must be a valid number.");
    }

    private static int? ParseInt(IQueryCollection query, string name)
    {
        var value = query[name].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new FormatException($"{name} must be a valid integer.");
    }

    private static BadRequestObjectResult BadRequest(string detail) => new(new ProblemDetails
    {
        Status = StatusCodes.Status400BadRequest,
        Title = "Invalid map request",
        Detail = detail
    });
}