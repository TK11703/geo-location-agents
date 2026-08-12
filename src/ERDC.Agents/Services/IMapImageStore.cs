using ERDC.Agents.Models;

namespace ERDC.Agents.Services;

public interface IMapImageStore
{
    Task<StoredMapImage> StoreAsync(MapImage image, CancellationToken cancellationToken);
}
