using System.Globalization;
using System.Net;
using System.Text.Json;
using ERDC.Agents.Common;
using ERDC.Agents.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace ERDC.Agents.Services;

public sealed class UsgsElevationService(HttpClient httpClient, IConfiguration configuration)
    : IElevationService
{
    private const string DefaultEndpoint = "https://epqs.nationalmap.gov/v1/json";

    // The USGS point query service returns this sentinel where the 3DEP raster has no data.
    private const double NoDataValue = -1_000_000;

    public async Task<ElevationResult> GetElevationAsync(
        ElevationQuery query,
        CancellationToken cancellationToken)
    {
        var center = new GeoPoint(query.Latitude, query.Longitude);
        var points = new List<(GeoPoint Point, double Bearing, double Distance)>
        {
            (center, 0, 0)
        };

        foreach (var bearing in GeoMath.RingBearings(query.SampleCount))
        {
            points.Add((GeoMath.Offset(center, bearing, query.RadiusMeters), bearing, query.RadiusMeters));
        }

        var elevations = await Task.WhenAll(
            points.Select(point => GetPointElevationAsync(point.Point, cancellationToken)));

        var samples = points
            .Select((point, index) => new ElevationSample(
                point.Point.Latitude,
                point.Point.Longitude,
                point.Bearing,
                point.Distance,
                elevations[index]))
            .ToArray();

        var measured = samples
            .Where(sample => sample.ElevationMeters.HasValue)
            .Select(sample => sample.ElevationMeters!.Value)
            .ToArray();

        if (measured.Length == 0)
        {
            return new ElevationResult(
                query.Latitude,
                query.Longitude,
                query.RadiusMeters,
                null,
                null,
                null,
                null,
                null,
                samples);
        }

        var centerElevation = samples[0].ElevationMeters;
        var minimum = measured.Min();
        var maximum = measured.Max();

        return new ElevationResult(
            query.Latitude,
            query.Longitude,
            query.RadiusMeters,
            centerElevation,
            Math.Round(minimum, 2),
            Math.Round(maximum, 2),
            Math.Round(maximum - minimum, 2),
            MaxSlopePercent(samples, centerElevation),
            samples);
    }

    private static double? MaxSlopePercent(IReadOnlyList<ElevationSample> samples, double? centerElevation)
    {
        if (centerElevation is null)
        {
            return null;
        }

        double? steepest = null;

        foreach (var sample in samples.Where(sample =>
            sample.DistanceMeters > 0 && sample.ElevationMeters.HasValue))
        {
            var slope = GeoMath.SlopePercent(
                sample.ElevationMeters!.Value - centerElevation.Value,
                sample.DistanceMeters);
            steepest = steepest is null ? slope : Math.Max(steepest.Value, slope);
        }

        return steepest is null ? null : Math.Round(steepest.Value, 2);
    }

    private async Task<double?> GetPointElevationAsync(GeoPoint point, CancellationToken cancellationToken)
    {
        var uri = QueryHelpers.AddQueryString(
            GetEndpoint(),
            new Dictionary<string, string?>
            {
                ["x"] = point.Longitude.ToString(CultureInfo.InvariantCulture),
                ["y"] = point.Latitude.ToString(CultureInfo.InvariantCulture),
                ["units"] = "Meters",
                ["wkid"] = "4326",
                ["includeDate"] = "false"
            });

        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.Accept.ParseAdd("application/json");
        using var response = await httpClient.SendAsync(message, cancellationToken);

        // Points outside the 3DEP coverage area come back as errors rather than a no-data value.
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ElevationException(
                "The elevation service could not return data for the requested point.",
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        // Outside the 3DEP coverage area the service answers 200 with a JSON content type but a
        // plain-text body ("Invalid or missing input parameters."), so parsing is the only signal.
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("value", out var value))
            {
                return null;
            }

            var elevation = value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDouble(),
                JsonValueKind.String when double.TryParse(
                    value.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed) => parsed,
                _ => (double?)null
            };

            return elevation is null || Math.Abs(elevation.Value - NoDataValue) < 1 ? null : elevation;
        }
    }

    private string GetEndpoint() => configuration["Elevation:Endpoint"] ?? DefaultEndpoint;
}

public sealed class ElevationException(string message, HttpStatusCode statusCode) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
