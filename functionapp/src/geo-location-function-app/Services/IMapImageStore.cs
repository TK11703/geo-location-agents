using GeoLocation.Models;

namespace GeoLocation.Services;

public interface IMapImageStore
{
    Task<StoredMapImage> StoreAsync(MapImage image, CancellationToken cancellationToken);
}
