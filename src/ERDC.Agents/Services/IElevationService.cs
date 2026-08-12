using ERDC.Agents.Models;

namespace ERDC.Agents.Services;

public interface IElevationService
{
    Task<ElevationResult> GetElevationAsync(ElevationQuery query, CancellationToken cancellationToken);
}

public sealed record ElevationSample(
    double Latitude,
    double Longitude,
    double BearingDegrees,
    double DistanceMeters,
    double? ElevationMeters);

public sealed record ElevationResult(
    double Latitude,
    double Longitude,
    int RadiusMeters,
    double? CenterElevationMeters,
    double? MinElevationMeters,
    double? MaxElevationMeters,
    double? ElevationRangeMeters,
    double? MaxSlopePercent,
    IReadOnlyList<ElevationSample> Samples);
