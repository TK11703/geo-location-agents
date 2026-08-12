using ERDC.Agents.Models;
using System.Text.Json;

namespace ERDC.Agents.Services;

public interface IMapRouteService
{
    Task<MapImage> GetRouteImageAsync(
        RouteCalculationRequest request,
        CancellationToken cancellationToken);

    Task<JsonElement> GetRouteDetailsAsync(
        RouteCalculationRequest request,
        CancellationToken cancellationToken);
}