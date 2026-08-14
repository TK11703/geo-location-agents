using ERDC.Agents.Models;

namespace ERDC.Agents.Tests.Models;

public sealed class GeocodeRequestTests
{
    [Fact]
    public void TryNormalize_TrimsQueryAndAppliesDefaults()
    {
        var input = new GeocodeRequest { Query = "  Boise, Idaho  " };

        var valid = input.TryNormalize(out var query, out var error);

        Assert.True(valid);
        Assert.Null(error);
        Assert.Equal("Boise, Idaho", query!.Text);
        Assert.Null(query.CountryRegion);
        Assert.Equal(5, query.Top);
    }

    [Fact]
    public void TryNormalize_UppercasesCountryRegion()
    {
        var input = new GeocodeRequest { Query = "Springfield", CountryRegion = " us " };

        var valid = input.TryNormalize(out var query, out var error);

        Assert.True(valid);
        Assert.Null(error);
        Assert.Equal("US", query!.CountryRegion);
    }

    [Fact]
    public void TryNormalize_AcceptsExplicitTop()
    {
        var input = new GeocodeRequest { Query = "Springfield", Top = 10 };

        var valid = input.TryNormalize(out var query, out var error);

        Assert.True(valid);
        Assert.Null(error);
        Assert.Equal(10, query!.Top);
    }

    [Theory]
    [InlineData(null, null, null, "query is required and must name a place or address.")]
    [InlineData("   ", null, null, "query is required and must name a place or address.")]
    [InlineData("Boise", "USA", null, "countryRegion must be a two-letter ISO country code.")]
    [InlineData("Boise", null, 0, "top must be between 1 and 10.")]
    [InlineData("Boise", null, 11, "top must be between 1 and 10.")]
    public void TryNormalize_RejectsInvalidInput(
        string? queryText,
        string? countryRegion,
        int? top,
        string expectedError)
    {
        var input = new GeocodeRequest { Query = queryText, CountryRegion = countryRegion, Top = top };

        var valid = input.TryNormalize(out var query, out var error);

        Assert.False(valid);
        Assert.Null(query);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void TryNormalize_RejectsOverlongQuery()
    {
        var input = new GeocodeRequest { Query = new string('a', 251) };

        var valid = input.TryNormalize(out var query, out var error);

        Assert.False(valid);
        Assert.Null(query);
        Assert.Equal("query must be 250 characters or fewer.", error);
    }
}
