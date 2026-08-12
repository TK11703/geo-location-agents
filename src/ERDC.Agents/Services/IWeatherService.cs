using System.Text.Json;
using ERDC.Agents.Models;

namespace ERDC.Agents.Services;

public interface IWeatherService
{
    Task<JsonElement> GetCurrentConditionsAsync(WeatherQuery query, CancellationToken cancellationToken);

    Task<JsonElement> GetSevereWeatherAlertsAsync(WeatherQuery query, CancellationToken cancellationToken);
}
