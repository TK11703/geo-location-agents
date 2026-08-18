using GeoLocation.Models;

namespace GeoLocation.Services;

public interface IGeocodeService
{
    Task<GeocodeResult> GetCoordinatesAsync(
        GeocodeQuery query,
        CancellationToken cancellationToken);
}

public sealed record GeocodeResult(
    string Query,
    bool HasMatch,
    IReadOnlyList<GeocodeCandidate> Candidates);

public sealed record GeocodeCandidate(
    double Latitude,
    double Longitude,
    string? FormattedAddress,
    string? Locality,
    string? CountryCode,
    string? ResultType,
    string? Confidence);
