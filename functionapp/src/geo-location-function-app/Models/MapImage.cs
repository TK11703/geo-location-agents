namespace GeoLocation.Models;

public sealed record MapImage(byte[] Content, string ContentType);

public sealed record StoredMapImage(Uri Url, DateTimeOffset ExpiresOn);