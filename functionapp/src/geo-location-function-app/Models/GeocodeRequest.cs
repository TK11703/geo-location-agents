namespace GeoLocation.Models;

public sealed record GeocodeRequest
{
    private const int DefaultTop = 5;
    private const int MaxTop = 10;
    private const int MaxQueryLength = 250;

    public string? Query { get; init; }
    public string? CountryRegion { get; init; }
    public int? Top { get; init; }

    public bool TryNormalize(out GeocodeQuery? query, out string? error)
    {
        query = null;
        error = null;

        var text = Query?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "query is required and must name a place or address.";
            return false;
        }

        if (text.Length > MaxQueryLength)
        {
            error = $"query must be {MaxQueryLength} characters or fewer.";
            return false;
        }

        var countryRegion = CountryRegion?.Trim();
        if (countryRegion is { Length: > 0 } && countryRegion.Length != 2)
        {
            error = "countryRegion must be a two-letter ISO country code.";
            return false;
        }

        if (Top is < 1 or > MaxTop)
        {
            error = $"top must be between 1 and {MaxTop}.";
            return false;
        }

        query = new GeocodeQuery(
            text,
            string.IsNullOrEmpty(countryRegion) ? null : countryRegion.ToUpperInvariant(),
            Top ?? DefaultTop);
        return true;
    }
}

public sealed record GeocodeQuery(string Text, string? CountryRegion, int Top);
