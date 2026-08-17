namespace ERDC.Agents.Orchestrator;

/// <summary>What a specialist's tool takes from the orchestrator model.</summary>
internal enum SpecialistInput
{
    PlaceName,
    Coordinate,
    CoordinatePair
}

/// <summary>A specialist prompt agent exposed to the orchestrator model as a callable tool.</summary>
internal sealed record Specialist(string AgentName, string ToolName, SpecialistInput Input, string Description);

internal static class Specialists
{
    // Descriptions are what the orchestrator model routes on, so they name the concrete questions
    // each specialist answers rather than describing the agent in the abstract.
    public static readonly IReadOnlyList<Specialist> All =
    [
        new(
            "place-resolver",
            "resolve_place_to_coordinates",
            SpecialistInput.PlaceName,
            """
            The latitude and longitude of a written place name, street address, or landmark. This is
            the only way to obtain a coordinate; every other specialist requires one and none of them
            can look one up. Call this first whenever the user names a place instead of giving
            numbers.
            """),
        new(
            "weather-specialist",
            "ask_weather_specialist",
            SpecialistInput.Coordinate,
            """
            Current weather conditions and active severe-weather alerts at a coordinate, including
            evacuation orders. Use for questions about temperature, wind, precipitation, visibility,
            storms, warnings, or whether conditions are safe.
            """),
        new(
            "terrain-specialist",
            "ask_terrain_specialist",
            SpecialistInput.Coordinate,
            """
            Ground elevation and slope around a coordinate. Use for questions about how high, hilly,
            steep, flat, or trafficable the ground is, or whether vehicles can move across it.
            """),
        new(
            "mobility-specialist",
            "ask_mobility_specialist",
            SpecialistInput.CoordinatePair,
            """
            Road traffic incidents near a coordinate, and driving routes between two coordinates
            including distance, duration, and truck restrictions. Supply the destination coordinate
            only for a routing question; leave it out for traffic near a single point.
            """),
        new(
            "location-specialist",
            "ask_location_specialist",
            SpecialistInput.Coordinate,
            """
            The street address or place at a coordinate, and a map image URL for it. Use to identify
            what is at a location or to illustrate one.
            """)
    ];
}
