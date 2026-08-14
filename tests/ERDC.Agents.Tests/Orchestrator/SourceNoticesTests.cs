using ERDC.Agents.Orchestrator;

namespace ERDC.Agents.Tests.Orchestrator;

public class SourceNoticesTests
{
    private const string WorldwideFeed = "worldwide severe-weather feed";
    private const string LookedUpCoordinate = "looked up from the place name";

    private static string Report(params string[] sources) =>
        $$"""
        {
          "status": "ok",
          "summary": "Amber Warning for Extreme Heat.",
          "findings": [{ "label": "Alert", "value": "Amber Warning", "unit": null }],
          "sources": [{{string.Join(", ", sources.Select(source => $"\"{source}\""))}}],
          "caveats": []
        }
        """;

    private static SortedSet<string> NoticesFor(string report)
    {
        var notices = new SortedSet<string>(StringComparer.Ordinal);
        SourceNotices.AddFrom(report, notices);
        return notices;
    }

    [Fact]
    public void Reports_the_worldwide_feed_when_its_namespaced_tool_answered()
    {
        var notices = NoticesFor(Report(
            "geo_weather_getWeatherConditions",
            "geo_weather_getNwsAlerts",
            "geo_weather_getSevereWeatherAlerts"));

        Assert.Contains(notices, notice => notice.Contains(WorldwideFeed, StringComparison.Ordinal));
    }

    [Fact]
    public void Reports_nothing_when_only_the_national_weather_service_answered()
    {
        var notices = NoticesFor(Report("geo_weather_getWeatherConditions", "geo_weather_getNwsAlerts"));

        Assert.Empty(notices);
    }

    [Fact]
    public void Matches_an_unprefixed_tool_name()
    {
        var notices = NoticesFor(Report("getSevereWeatherAlerts"));

        Assert.Single(notices);
    }

    /// <summary>
    /// A tool named in prose but absent from <c>sources</c> did not answer. Taking the array as the
    /// record is what keeps a specialist explaining its fallback from producing a false warning.
    /// </summary>
    [Fact]
    public void Ignores_a_tool_named_outside_the_sources_array()
    {
        var report = """
            {
              "status": "ok",
              "summary": "getNwsAlerts covered this point, so getSevereWeatherAlerts was not needed.",
              "findings": [],
              "sources": ["geo_weather_getNwsAlerts"],
              "caveats": []
            }
            """;

        Assert.Empty(NoticesFor(report));
    }

    /// <summary>
    /// Falls back to the raw text rather than reporting no source at all, so a report shape we did
    /// not anticipate errs towards warning.
    /// </summary>
    [Fact]
    public void Falls_back_to_raw_text_when_the_report_is_not_json()
    {
        Assert.Single(NoticesFor("the specialist called geo_weather_getSevereWeatherAlerts"));
    }

    [Fact]
    public void States_a_notice_once_however_many_reports_carry_it()
    {
        var notices = new SortedSet<string>(StringComparer.Ordinal);
        SourceNotices.AddFrom(Report("geo_weather_getSevereWeatherAlerts"), notices);
        SourceNotices.AddFrom(Report("geo_weather_getSevereWeatherAlerts"), notices);

        Assert.Single(notices);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Reports_nothing_for_an_empty_result(string? report)
    {
        Assert.Empty(NoticesFor(report!));
    }

    [Fact]
    public void Reports_a_looked_up_coordinate_when_the_resolver_answered()
    {
        var notices = NoticesFor(Report("geo_place_geocodePlace"));

        Assert.Contains(notices, notice => notice.Contains(LookedUpCoordinate, StringComparison.Ordinal));
    }

    /// <summary>
    /// A coordinate the user typed carries no such caveat, so the notice must follow the resolver
    /// having run rather than the question having named a place.
    /// </summary>
    [Fact]
    public void Reports_nothing_when_no_place_was_resolved()
    {
        Assert.Empty(NoticesFor(Report("geo_terrain_getElevation")));
    }
}
