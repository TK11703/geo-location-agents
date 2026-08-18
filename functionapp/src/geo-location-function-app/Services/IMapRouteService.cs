using GeoLocation.Models;
using System.Text.Json;

namespace GeoLocation.Services;

public interface IMapRouteService
{
    Task<MapImage> GetRouteImageAsync(
        RouteCalculationRequest request,
        CancellationToken cancellationToken);

    Task<JsonElement> GetRouteDetailsAsync(
        RouteCalculationRequest request,
        CancellationToken cancellationToken);
}