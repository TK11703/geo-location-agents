using System.Text.Json;

namespace ERDC.Agents.Orchestrator;

/// <summary>A limitation that follows from which tool answered rather than from what it said.</summary>
internal sealed record SourceNotice(string ToolName, string Text);

/// <summary>
/// Turns the <c>sources</c> a specialist reported into statements the host owes the user. The model
/// was told to make these statements and did so about four times in five, which is not a rate you
/// can put behind a heat warning, so the decision is made here instead of asked for.
/// </summary>
internal static class SourceNotices
{
    public static readonly IReadOnlyList<SourceNotice> All =
    [
        new(
            "getSevereWeatherAlerts",
            "Alerts for this point came from the worldwide severe-weather feed rather than the "
            + "United States National Weather Service, so protective instructions and evacuation "
            + "guidance are not available here.")
    ];

    public static void AddFrom(string? report, ISet<string> notices)
    {
        if (string.IsNullOrWhiteSpace(report))
        {
            return;
        }

        var sources = ReadSources(report);

        foreach (var notice in All)
        {
            // Foundry namespaces tool names by tool group, so a source reads
            // geo_weather_getSevereWeatherAlerts rather than the bare name.
            var used = sources.Count > 0
                ? sources.Any(source => source.EndsWith(notice.ToolName, StringComparison.OrdinalIgnoreCase))
                : report.Contains(notice.ToolName, StringComparison.OrdinalIgnoreCase);

            if (used)
            {
                notices.Add(notice.Text);
            }
        }
    }

    /// <summary>
    /// Returns an empty list for anything that is not the report we expect, which sends the caller
    /// to a raw text search rather than silently reporting that no tool was used.
    /// </summary>
    private static List<string> ReadSources(string report)
    {
        var sources = new List<string>();

        try
        {
            using var document = JsonDocument.Parse(report);
            Collect(document.RootElement, sources);
        }
        catch (JsonException)
        {
            return [];
        }

        return sources;
    }

    private static void Collect(JsonElement element, List<string> sources)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("sources") && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in property.Value.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                sources.Add(item.GetString()!);
                            }
                        }
                    }
                    else
                    {
                        Collect(property.Value, sources);
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, sources);
                }

                break;
        }
    }
}
