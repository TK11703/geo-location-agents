using ERDC.Agents.Models;

namespace ERDC.Agents.Tests.Models;

public class WeatherRequestTests
{
    [Fact]
    public void TryNormalize_WithoutUnit_DefaultsToMetric()
    {
        var request = new WeatherRequest { Latitude = 47.6062, Longitude = -122.3321 };

        Assert.True(request.TryNormalize(out var query, out _));

        Assert.Equal("metric", query!.Unit);
    }

    [Fact]
    public void TryNormalize_WithMixedCaseUnit_NormalizesToLowercase()
    {
        var request = new WeatherRequest
        {
            Latitude = 47.6062,
            Longitude = -122.3321,
            Unit = "Imperial"
        };

        Assert.True(request.TryNormalize(out var query, out _));

        Assert.Equal("imperial", query!.Unit);
    }

    [Fact]
    public void TryNormalize_WithUnsupportedUnit_Fails()
    {
        var request = new WeatherRequest
        {
            Latitude = 47.6062,
            Longitude = -122.3321,
            Unit = "kelvin"
        };

        Assert.False(request.TryNormalize(out var query, out var error));

        Assert.Null(query);
        Assert.Equal("unit must be metric or imperial.", error);
    }
}
