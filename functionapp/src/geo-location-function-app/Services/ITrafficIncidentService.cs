using GeoLocation.Models;

namespace GeoLocation.Services;

public interface ITrafficIncidentService
{
    Task<TrafficIncidentResult> GetIncidentsAsync(
        TrafficIncidentQuery query,
        CancellationToken cancellationToken);
}

public sealed record TrafficIncident(
    long Id,
    string? IncidentType,
    string? Title,
    string? Description,
    int? Severity,
    bool IsRoadClosed,
    bool IsTrafficJam,
    double? DelaySeconds,
    string? StartTime,
    string? EndTime,
    double Latitude,
    double Longitude,
    double DistanceMeters);

public sealed record TrafficIncidentResult(
    double Latitude,
    double Longitude,
    int RadiusMeters,
    int IncidentCount,
    int RoadClosureCount,
    double? NearestIncidentMeters,
    IReadOnlyList<TrafficIncident> Incidents);
