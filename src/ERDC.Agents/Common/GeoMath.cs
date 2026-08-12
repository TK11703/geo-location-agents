namespace ERDC.Agents.Common;

public readonly record struct GeoPoint(double Latitude, double Longitude);

public readonly record struct GeoBoundingBox(
    double MinLatitude,
    double MinLongitude,
    double MaxLatitude,
    double MaxLongitude);

public static class GeoMath
{
    private const double EarthRadiusMeters = 6_378_137;

    // Meters covered by one pixel of a 256-pixel tile at zoom 0 on the equator.
    private static readonly double EquatorMetersPerPixel = 2 * Math.PI * EarthRadiusMeters / 256;

    public static GeoBoundingBox BoundingBox(GeoPoint center, double radiusMeters)
    {
        var latitudeDelta = ToDegrees(radiusMeters / EarthRadiusMeters);

        // Longitude degrees shrink toward the poles; the floor keeps the box finite near them.
        var cosine = Math.Max(Math.Cos(ToRadians(center.Latitude)), 1e-6);
        var longitudeDelta = latitudeDelta / cosine;

        return new GeoBoundingBox(
            Math.Clamp(center.Latitude - latitudeDelta, -90, 90),
            Math.Clamp(center.Longitude - longitudeDelta, -180, 180),
            Math.Clamp(center.Latitude + latitudeDelta, -90, 90),
            Math.Clamp(center.Longitude + longitudeDelta, -180, 180));
    }

    public static double DistanceMeters(GeoPoint from, GeoPoint to)
    {
        var latitudeDelta = ToRadians(to.Latitude - from.Latitude);
        var longitudeDelta = ToRadians(to.Longitude - from.Longitude);
        var a = (Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2))
            + (Math.Cos(ToRadians(from.Latitude))
                * Math.Cos(ToRadians(to.Latitude))
                * Math.Sin(longitudeDelta / 2)
                * Math.Sin(longitudeDelta / 2));
        return EarthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public static GeoPoint Offset(GeoPoint origin, double bearingDegrees, double distanceMeters)
    {
        var angularDistance = distanceMeters / EarthRadiusMeters;
        var bearing = ToRadians(bearingDegrees);
        var latitude = ToRadians(origin.Latitude);
        var longitude = ToRadians(origin.Longitude);

        var targetLatitude = Math.Asin(
            (Math.Sin(latitude) * Math.Cos(angularDistance))
            + (Math.Cos(latitude) * Math.Sin(angularDistance) * Math.Cos(bearing)));
        var targetLongitude = longitude + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angularDistance) * Math.Cos(latitude),
            Math.Cos(angularDistance) - (Math.Sin(latitude) * Math.Sin(targetLatitude)));

        return new GeoPoint(
            ToDegrees(targetLatitude),
            ((ToDegrees(targetLongitude) + 540) % 360) - 180);
    }

    public static IReadOnlyList<double> RingBearings(int sampleCount)
    {
        var step = 360d / sampleCount;
        return [.. Enumerable.Range(0, sampleCount).Select(index => index * step)];
    }

    public static double SlopePercent(double riseMeters, double runMeters) =>
        runMeters <= 0 ? 0 : Math.Abs(riseMeters) / runMeters * 100;

    public static int ZoomForRadius(GeoPoint center, double radiusMeters, int widthPixels, int heightPixels)
    {
        // The shorter side has to span the full diameter for the radius to be visible in every direction.
        var metersPerPixel = 2 * radiusMeters / Math.Min(widthPixels, heightPixels);
        var cosine = Math.Max(Math.Cos(ToRadians(center.Latitude)), 1e-6);
        var zoom = Math.Log2(EquatorMetersPerPixel * cosine / metersPerPixel);

        // Rounding down widens the view, so the requested radius always fits.
        return (int)Math.Clamp(Math.Floor(zoom), 0, 20);
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;

    private static double ToDegrees(double radians) => radians * 180 / Math.PI;
}
