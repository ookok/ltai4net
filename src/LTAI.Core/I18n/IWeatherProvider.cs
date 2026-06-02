namespace LTAI.Core.I18n;

/// <summary>
/// C3: Abstract weather service. Default implementation uses HeFeng (和风天气).
/// Replace via DI to use OpenWeatherMap, WeatherAPI, etc.
/// </summary>
public interface IWeatherProvider
{
    /// <summary>Get current weather for a city name. Returns structured result or null.</summary>
    Task<WeatherResult?> GetCurrentAsync(string city, CancellationToken ct = default);

    /// <summary>Get weather forecast (next N days).</summary>
    Task<IReadOnlyList<WeatherResult>> GetForecastAsync(string city, int days = 3, CancellationToken ct = default);
}

public sealed record WeatherResult(
    string City,
    double TemperatureC,
    string Condition,
    int Humidity,
    double WindSpeedKmh,
    string Description);
