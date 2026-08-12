namespace ERDC.Agents.Models;

public sealed record MapImage(byte[] Content, string ContentType);

public sealed record StoredMapImage(Uri Url, DateTimeOffset ExpiresOn);