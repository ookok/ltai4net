using System.Text.Json;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LTAI.Tools.Integration;

public sealed record WeatherData(
    string City,
    string Weather,
    string Description,
    double Temperature,
    double FeelsLike,
    int Humidity,
    double WindSpeed,
    int Pressure,
    int Visibility,
    string Icon,
    long Sunrise,
    long Sunset,
    string Source);

public sealed class WeatherService
{
    private readonly HttpClient _http;
    private readonly ILogger<WeatherService> _logger;
    private readonly string _owmBaseUrl;
    private readonly string _qweatherGeoUrl;
    private readonly string _qweatherNowUrl;

    public string OpenWeatherMapKey { get; set; } = "";
    public string QWeatherKey { get; set; } = "";

    public WeatherService(
        IOptions<LTAIOptions> options,
        ILogger<WeatherService>? logger = null)
    {
        _logger = logger ?? NullLogger<WeatherService>.Instance;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _owmBaseUrl = options.Value.IntegrationUrls.WeatherOpenWeatherMap;
        _qweatherGeoUrl = options.Value.IntegrationUrls.WeatherQWeatherGeo;
        _qweatherNowUrl = options.Value.IntegrationUrls.WeatherQWeatherNow;
    }

    public async Task<WeatherData?> GetWeatherAsync(string city, string source = "openweathermap")
    {
        if (string.IsNullOrWhiteSpace(city))
            return null;

        return source.ToLowerInvariant() switch
        {
            "qweather" => await GetQWeatherAsync(city),
            _ => await GetOpenWeatherAsync(city)
        };
    }

    private async Task<WeatherData?> GetOpenWeatherAsync(string city)
    {
        if (string.IsNullOrWhiteSpace(OpenWeatherMapKey))
            return null;

        try
        {
            var url = $"{_owmBaseUrl}?q={Uri.EscapeDataString(city)}&appid={OpenWeatherMapKey}&units=metric&lang=zh_cn";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            var main = doc.RootElement.GetProperty("main");
            var weather = doc.RootElement.GetProperty("weather")[0];
            var wind = doc.RootElement.GetProperty("wind");
            var sys = doc.RootElement.GetProperty("sys");

            return new WeatherData(
                doc.RootElement.GetProperty("name").GetString() ?? city,
                weather.GetProperty("main").GetString() ?? "",
                weather.GetProperty("description").GetString() ?? "",
                main.GetProperty("temp").GetDouble(),
                main.GetProperty("feels_like").GetDouble(),
                main.GetProperty("humidity").GetInt32(),
                wind.GetProperty("speed").GetDouble(),
                main.GetProperty("pressure").GetInt32(),
                doc.RootElement.TryGetProperty("visibility", out var v) ? v.GetInt32() : 0,
                weather.GetProperty("icon").GetString() ?? "",
                sys.GetProperty("sunrise").GetInt64(),
                sys.GetProperty("sunset").GetInt64(),
                "openweathermap"
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenWeatherMap query failed for {City}", city);
            return await GetQWeatherAsync(city);
        }
    }

    private async Task<WeatherData?> GetQWeatherAsync(string city)
    {
        if (string.IsNullOrWhiteSpace(QWeatherKey))
            return null;

        try
        {
            var geoUrl = $"{_qweatherGeoUrl}?location={Uri.EscapeDataString(city)}&key={QWeatherKey}";
            var geoJson = await _http.GetStringAsync(geoUrl);
            using var geoDoc = JsonDocument.Parse(geoJson);

            if (!geoDoc.RootElement.TryGetProperty("location", out var locations) || locations.GetArrayLength() == 0)
                return null;

            var loc = locations[0];
            var cityName = loc.GetProperty("name").GetString() ?? city;
            var locId = loc.GetProperty("id").GetString() ?? "";

            var weatherUrl = $"{_qweatherNowUrl}?location={locId}&key={QWeatherKey}";
            var weatherJson = await _http.GetStringAsync(weatherUrl);
            using var weatherDoc = JsonDocument.Parse(weatherJson);

            if (!weatherDoc.RootElement.TryGetProperty("now", out var now))
                return null;

            return new WeatherData(
                cityName,
                now.GetProperty("text").GetString() ?? "",
                now.GetProperty("text").GetString() ?? "",
                double.TryParse(now.GetProperty("temp").GetString(), out var t) ? t : 0,
                double.TryParse(now.GetProperty("feelsLike").GetString(), out var fl) ? fl : 0,
                int.TryParse(now.GetProperty("humidity").GetString(), out var h) ? h : 0,
                double.TryParse(now.GetProperty("windSpeed").GetString(), out var ws) ? ws : 0,
                int.TryParse(now.GetProperty("pressure").GetString(), out var p) ? p : 0,
                int.TryParse(now.GetProperty("vis").GetString(), out var vis) ? vis * 1000 : 0,
                now.GetProperty("icon").GetString() ?? "",
                0, 0,
                "qweather"
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QWeather query failed for {City}", city);
            return null;
        }
    }
}
