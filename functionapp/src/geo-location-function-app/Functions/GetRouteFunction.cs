using System.Globalization;
using System.Text.Json;
using GeoLocation.Models;
using GeoLocation.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace GeoLocation.Functions;

public sealed class GetRouteFunction(
    IMapRouteService mapRouteService,
    IMapImageStore mapImageStore,
    ILogger<GetRouteFunction> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Function("GetRoute")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "route")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        RouteRequest? input;

        try
        {
            input = HttpMethods.IsPost(request.Method)
                ? await JsonSerializer.DeserializeAsync<RouteRequest>(
                    request.Body,
                    JsonOptions,
                    cancellationToken)
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
            if (normalized!.Output == "details")
            {
                var details = await mapRouteService.GetRouteDetailsAsync(normalized, cancellationToken);
                return new OkObjectResult(details);
            }

            var image = await mapRouteService.GetRouteImageAsync(normalized, cancellationToken);

            if (normalized.Output == "url")
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
            logger.LogError(exception, "Azure Maps is not configured for the route function.");
            return new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Route service unavailable",
                Detail = "The route service is not configured."
            })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
        }
        catch (AzureMapsException exception)
        {
            logger.LogError(
                exception,
                "Azure Maps returned status code {StatusCode} while calculating a route.",
                exception.StatusCode);
            return new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status502BadGateway,
                Title = "Route service unavailable",
                Detail = "Azure Maps could not calculate the requested route."
            })
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
    }

    private static RouteRequest ParseQuery(IQueryCollection query) => new()
    {
        OriginLatitude = ParseDouble(query, "originLatitude"),
        OriginLongitude = ParseDouble(query, "originLongitude"),
        DestinationLatitude = ParseDouble(query, "destinationLatitude"),
        DestinationLongitude = ParseDouble(query, "destinationLongitude"),
        TravelMode = query["travelMode"].FirstOrDefault(),
        Zoom = ParseInt(query, "zoom"),
        VehicleSpec = ParseVehicleSpec(query),
        Output = query["output"].FirstOrDefault()
    };

    private static TruckVehicleSpec? ParseVehicleSpec(IQueryCollection query)
    {
        var axleCount = ParseInt(query, "axleCount");
        var axleWeight = ParseInt(query, "axleWeight");
        var height = ParseDouble(query, "height");
        var isVehicleCommercial = ParseBool(query, "isVehicleCommercial");
        var length = ParseDouble(query, "length");
        var loadType = query["loadType"]
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .OfType<string>()
            .ToArray();
        var maxSpeed = ParseInt(query, "maxSpeed");
        var weight = ParseInt(query, "weight");
        var width = ParseDouble(query, "width");

        if (axleCount is null
            && axleWeight is null
            && height is null
            && isVehicleCommercial is null
            && length is null
            && loadType.Length == 0
            && maxSpeed is null
            && weight is null
            && width is null)
        {
            return null;
        }

        return new TruckVehicleSpec
        {
            AxleCount = axleCount,
            AxleWeight = axleWeight,
            Height = height,
            IsVehicleCommercial = isVehicleCommercial,
            Length = length,
            LoadType = loadType.Length == 0 ? null : loadType,
            MaxSpeed = maxSpeed,
            Weight = weight,
            Width = width
        };
    }

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

    private static bool? ParseBool(IQueryCollection query, string name)
    {
        var value = query[name].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (bool.TryParse(value, out var result))
        {
            return result;
        }

        throw new FormatException($"{name} must be true or false.");
    }

    private static BadRequestObjectResult BadRequest(string detail) => new(new ProblemDetails
    {
        Status = StatusCodes.Status400BadRequest,
        Title = "Invalid route request",
        Detail = detail
    });
}