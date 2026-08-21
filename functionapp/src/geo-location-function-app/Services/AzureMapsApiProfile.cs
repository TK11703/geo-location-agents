using Microsoft.Extensions.Configuration;

namespace GeoLocation.Services;

// Azure Government is missing two Azure Maps v2 API families: /geocode and /reverseGeocode reject
// every api-version, and /route/directions returns 404. Render and Traffic v2 work there, so only
// the geocoding and routing callers consult this switch.
internal static class AzureMapsApiProfile
{
    private const string PublicCloudEndpoint = "https://atlas.microsoft.com";

    public static string GetEndpoint(IConfiguration configuration) =>
        (configuration["AzureMaps:Endpoint"] ?? PublicCloudEndpoint).TrimEnd('/');

    public static bool UseLegacyApis(IConfiguration configuration)
    {
        if (bool.TryParse(configuration["AzureMaps:UseLegacyApis"], out var configured))
        {
            return configured;
        }

        return Uri.TryCreate(GetEndpoint(configuration), UriKind.Absolute, out var endpoint)
            && endpoint.Host.EndsWith(".azure.us", StringComparison.OrdinalIgnoreCase);
    }
}
