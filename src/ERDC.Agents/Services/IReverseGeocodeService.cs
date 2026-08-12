using ERDC.Agents.Models;

namespace ERDC.Agents.Services;

public interface IReverseGeocodeService
{
    Task<ReverseGeocodeResult> GetAddressAsync(
        ReverseGeocodeQuery query,
        CancellationToken cancellationToken);
}

public sealed record ReverseGeocodeResult(
    double Latitude,
    double Longitude,
    bool HasAddressMatch,
    string? FormattedAddress,
    string? AddressLine,
    string? Locality,
    string? PostalCode,
    string? CountryCode,
    string? ResultType,
    string? Confidence,
    bool IsLikelyPrivateRoad);
