namespace ERDC.Agents.Orchestrator;

/// <summary>A specialist prompt agent exposed to the orchestrator model as a callable tool.</summary>
internal sealed record Specialist(string AgentName, string ToolName, string Description);

internal static class Specialists
{
    // Descriptions are what the orchestrator model routes on, so they name the concrete questions
    // each specialist answers rather than describing the agent in the abstract.
    public static readonly IReadOnlyList<Specialist> All =
    [
        new(
            "weather-specialist",
            "ask_weather_specialist",
            """
            Current weather conditions and active severe-weather alerts at a coordinate, including
            evacuation orders. Use for questions about temperature, wind, precipitation, visibility,
            storms, warnings, or whether conditions are safe. Requires latitude and longitude.
            """),
        new(
            "terrain-specialist",
            "ask_terrain_specialist",
            """
            Ground elevation and slope around a coordinate. Use for questions about how high, hilly,
            steep, flat, or trafficable the ground is, or whether vehicles can move across it.
            Requires latitude and longitude.
            """),
        new(
            "mobility-specialist",
            "ask_mobility_specialist",
            """
            Road traffic incidents near a coordinate, and driving routes between two coordinates
            including distance, duration, and truck restrictions. Requires latitude and longitude;
            routing requires both an origin and a destination coordinate.
            """),
        new(
            "location-specialist",
            "ask_location_specialist",
            """
            The street address or place at a coordinate, and a map image URL for it. Use to identify
            what is at a location or to illustrate one. Requires latitude and longitude.
            """)
    ];
}
