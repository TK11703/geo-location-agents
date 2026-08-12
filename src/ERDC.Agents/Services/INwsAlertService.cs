using ERDC.Agents.Models;

namespace ERDC.Agents.Services;

public interface INwsAlertService
{
    Task<NwsAlertResult> GetActiveAlertsAsync(NwsAlertQuery query, CancellationToken cancellationToken);
}

public sealed record NwsAlert(
    string? Id,
    string? Event,
    string? Severity,
    string? Certainty,
    string? Urgency,
    string? Headline,
    string? Description,
    string? Instruction,
    string? Response,
    string? AreaDescription,
    string? Onset,
    string? Expires,
    string? Ends);

public sealed record NwsAlertResult(
    double Latitude,
    double Longitude,
    bool IsWithinCoverage,
    string? CoverageNote,
    int AlertCount,
    string MaxSeverity,
    bool HasEvacuationOrder,
    IReadOnlyList<NwsAlert> Alerts);
