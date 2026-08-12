using ERDC.Agents.Models;

namespace ERDC.Agents.Services;

public interface IMapImageService
{
    Task<MapImage> GetMapImageAsync(MapRenderRequest request, CancellationToken cancellationToken);
}