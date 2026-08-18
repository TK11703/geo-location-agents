using System.Text.Json;
using GeoLocation.Models;

namespace GeoLocation.Services;

public interface IWeatherService
{
    Task<JsonElement> GetCurrentConditionsAsync(WeatherQuery query, CancellationToken cancellationToken);

    Task<JsonElement> GetSevereWeatherAlertsAsync(WeatherQuery query, CancellationToken cancellationToken);
}
