using GeoLocation.Models;

namespace GeoLocation.Services;

public interface IMapImageService
{
    Task<MapImage> GetMapImageAsync(MapRenderRequest request, CancellationToken cancellationToken);
}