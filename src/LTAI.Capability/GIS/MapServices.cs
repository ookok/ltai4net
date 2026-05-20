using System.Text;
using System.Text.Json;
using LTAI.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace LTAI.Capability.GIS;

public sealed class UnifiedMapService
{
    private readonly ILogger<UnifiedMapService> _logger;
    private readonly BaiduMapService _baidu;
    private readonly AmapService _amap;
    private readonly TiandituService _tianditu;
    private readonly TencentMapService _tencent;

    public UnifiedMapService(ILogger<UnifiedMapService> logger, SecretVault vault)
    {
        _logger = logger;
        _baidu = new BaiduMapService(logger, vault.Get("baidu_map_ak"), vault.Get("baidu_map_sk"));
        _amap = new AmapService(logger, vault.Get("amap_key"));
        _tianditu = new TiandituService(logger, vault.Get("tianditu_key"));
        _tencent = new TencentMapService(logger, vault.Get("tencent_map_key"));
    }

    public async Task<GeoAddress?> GeocodeAsync(string address, string provider = "auto", CancellationToken ct = default)
    {
        if (provider == "baidu") return await _baidu.GeocodeAsync(address, ct);
        if (provider == "amap") return await _amap.GeocodeAsync(address, ct);
        if (provider == "tencent") return await _tencent.GeocodeAsync(address, ct);
        return await _tianditu.GeocodeAsync(address, ct) ??
               await _tencent.GeocodeAsync(address, ct) ??
               await _baidu.GeocodeAsync(address, ct) ??
               await _amap.GeocodeAsync(address, ct);
    }

    public async Task<GeoAddress?> ReverseGeocodeAsync(double lng, double lat, string provider = "auto", CancellationToken ct = default)
    {
        if (provider == "baidu") return await _baidu.ReverseGeocodeAsync(lng, lat, ct);
        if (provider == "amap") return await _amap.ReverseGeocodeAsync(lng, lat, ct);
        if (provider == "tencent") return await _tencent.ReverseGeocodeAsync(lng, lat, ct);
        return await _amap.ReverseGeocodeAsync(lng, lat, ct) ??
               await _tencent.ReverseGeocodeAsync(lng, lat, ct) ??
               await _baidu.ReverseGeocodeAsync(lng, lat, ct);
    }

    public async Task<List<POIResult>> SearchPOIAsync(string keyword, string? city = null, int limit = 10, string provider = "auto", CancellationToken ct = default)
    {
        if (provider == "baidu") return await _baidu.SearchPOIAsync(keyword, city, limit, ct);
        if (provider == "amap") return await _amap.SearchPOIAsync(keyword, city, limit, ct);
        if (provider == "tencent") return await _tencent.SearchPOIAsync(keyword, city, limit, ct);
        return await _amap.SearchPOIAsync(keyword, city, limit, ct) is { Count: > 0 } a ? a
            : await _tencent.SearchPOIAsync(keyword, city, limit, ct) is { Count: > 0 } t ? t
            : await _baidu.SearchPOIAsync(keyword, city, limit, ct);
    }

    public async Task<RouteResult?> GetRouteAsync(GeoPoint from, GeoPoint to, string mode = "driving", string provider = "auto", CancellationToken ct = default)
    {
        if (provider == "baidu") return await _baidu.GetRouteAsync(from, to, mode, ct);
        if (provider == "amap") return await _amap.GetRouteAsync(from, to, mode, ct);
        if (provider == "tencent") return await _tencent.GetRouteAsync(from, to, mode, ct);
        return await _amap.GetRouteAsync(from, to, mode, ct) ??
               await _tencent.GetRouteAsync(from, to, mode, ct) ??
               await _baidu.GetRouteAsync(from, to, mode, ct);
    }

    public async Task<WeatherResult?> GetWeatherAsync(string city, string provider = "amap", CancellationToken ct = default)
    {
        return await _amap.GetWeatherAsync(city, ct);
    }

    public async Task<IPLocation?> GetIPLocationAsync(string ip, CancellationToken ct = default)
    {
        return await _amap.GetIPLocationAsync(ip, ct);
    }

    public async Task<List<double>> ConvertBaiduToWGS84Async(double lng, double lat, CancellationToken ct = default)
    {
        var delta = await Task.FromResult((0.0065, 0.0060));
        return new List<double> { lng - delta.Item1, lat - delta.Item2 };
    }
}

internal sealed class BaiduMapService
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _ak;
    private readonly string _sk;

    public BaiduMapService(ILogger logger, string ak = "", string sk = "")
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _logger = logger;
        _ak = ak;
        _sk = sk;
    }

    public async Task<GeoAddress?> GeocodeAsync(string address, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.map.baidu.com/geocoding/v3/?address={Uri.EscapeDataString(address)}&output=json&ak={_ak}";
            var json = await _http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("status", out var s) && s.GetInt32() == 0 &&
                doc.RootElement.TryGetProperty("result", out var r))
            {
                var loc = r.GetProperty("location");
                return new GeoAddress
                {
                    Formatted = address,
                    Lng = loc.GetProperty("lng").GetDouble(),
                    Lat = loc.GetProperty("lat").GetDouble(),
                    Confidence = r.TryGetProperty("confidence", out var c) ? c.GetInt32() : 80
                };
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Baidu geocode failed"); }
        return null;
    }

    public async Task<GeoAddress?> ReverseGeocodeAsync(double lng, double lat, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.map.baidu.com/reverse_geocoding/v3/?location={lat},{lng}&output=json&ak={_ak}";
            var json = await _http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("formatted_address", out var fa))
            {
                var comp = r.GetProperty("addressComponent");
                return new GeoAddress
                {
                    Formatted = fa.GetString() ?? "",
                    Province = comp.TryGetProperty("province", out var p) ? p.GetString() ?? "" : "",
                    City = comp.TryGetProperty("city", out var ci) ? ci.GetString() ?? "" : "",
                    District = comp.TryGetProperty("district", out var d) ? d.GetString() ?? "" : "",
                    Lng = lng, Lat = lat
                };
            }
        }
        catch { /* non-fatal */ }
        return null;
    }

    public async Task<List<POIResult>> SearchPOIAsync(string keyword, string? city, int limit, CancellationToken ct)
    {
        var results = new List<POIResult>();
        try
        {
            var region = city != null ? Uri.EscapeDataString(city) : "全国";
            var url = $"https://api.map.baidu.com/place/v2/search?query={Uri.EscapeDataString(keyword)}&region={region}&page_size={limit}&output=json&ak={_ak}";
            var json = await _http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    results.Add(new POIResult
                    {
                        Name = item.GetProperty("name").GetString() ?? "",
                        Address = item.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "",
                        Lng = item.TryGetProperty("location", out var loc) ? loc.GetProperty("lng").GetDouble() : 0,
                        Lat = item.TryGetProperty("location", out loc) ? loc.GetProperty("lat").GetDouble() : 0,
                        Phone = item.TryGetProperty("telephone", out var t) ? t.GetString() ?? "" : ""
                    });
                }
            }
        }
        catch { /* non-fatal */ }
        return results;
    }

    public async Task<RouteResult?> GetRouteAsync(GeoPoint from, GeoPoint to, string mode, CancellationToken ct)
    {
        try
        {
            var type = mode switch { "walking" => "walking", "transit" => "transit", _ => "driving" };
            var url = $"https://api.map.baidu.com/direction/v2/{type}?origin={from.Lat},{from.Lng}&destination={to.Lat},{to.Lng}&ak={_ak}";
            var json = await _http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("routes", out var routes))
            {
                var route = routes[0];
                return new RouteResult
                {
                    DistanceMeters = route.GetProperty("distance").GetDouble(),
                    DurationSeconds = route.GetProperty("duration").GetDouble()
                };
            }
        }
        catch { /* non-fatal */ }
        return null;
    }
}

internal sealed class AmapService
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _key;

    public AmapService(ILogger logger, string key = "")
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _logger = logger;
        _key = key;
    }

    public async Task<GeoAddress?> GeocodeAsync(string address, CancellationToken ct)
    {
        try
        {
            var url = $"https://restapi.amap.com/v3/geocode/geo?address={Uri.EscapeDataString(address)}&key={_key}";
            var json = await _http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("geocodes", out var arr) && arr.GetArrayLength() > 0)
            {
                var item = arr[0];
                var loc = item.GetProperty("location").GetString()?.Split(',');
                return new GeoAddress
                {
                    Formatted = item.GetProperty("formatted_address").GetString() ?? "",
                    Province = item.TryGetProperty("province", out var p) ? p.GetString() ?? "" : "",
                    City = item.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "",
                    District = item.TryGetProperty("district", out var d) ? d.GetString() ?? "" : "",
                    Lng = loc?.Length > 0 ? double.Parse(loc[0]) : 0,
                    Lat = loc?.Length > 1 ? double.Parse(loc[1]) : 0
                };
            }
        }
        catch { /* non-fatal */ }
        return null;
    }

    public async Task<GeoAddress?> ReverseGeocodeAsync(double lng, double lat, CancellationToken ct)
    {
        try
        {
            var url = $"https://restapi.amap.com/v3/geocode/regeo?location={lng},{lat}&key={_key}";
            var json = await _http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("regeocode", out var r))
            {
                var comp = r.GetProperty("addressComponent");
                return new GeoAddress
                {
                    Formatted = r.GetProperty("formatted_address").GetString() ?? "",
                    Province = comp.TryGetProperty("province", out var p) ? p.GetString() ?? "" : "",
                    City = GetCity(comp),
                    District = comp.TryGetProperty("district", out var d) ? d.GetString() ?? "" : "",
                    Street = comp.TryGetProperty("streetNumber", out var s) ? s.GetProperty("street").GetString() ?? "" : "",
                    Lng = lng, Lat = lat
                };
            }
        }
        catch { /* non-fatal */ }
        return null;
    }

    private static string GetCity(JsonElement comp)
    {
        if (comp.TryGetProperty("city", out var c) && c.GetString() is { Length: > 0 } city) return city;
        return comp.TryGetProperty("province", out var p) ? p.GetString() ?? "" : "";
    }

    public async Task<List<POIResult>> SearchPOIAsync(string keyword, string? city, int limit, CancellationToken ct)
    {
        var results = new List<POIResult>();
        try
        {
            var url = $"https://restapi.amap.com/v3/place/text?keywords={Uri.EscapeDataString(keyword)}&city={city ?? ""}&offset={limit}&key={_key}";
            var json = await _http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("pois", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var loc = item.GetProperty("location").GetString()?.Split(',');
                    results.Add(new POIResult
                    {
                        Name = item.GetProperty("name").GetString() ?? "",
                        Address = item.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "",
                        Type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                        Lng = loc?.Length > 0 ? double.Parse(loc[0]) : 0,
                        Lat = loc?.Length > 1 ? double.Parse(loc[1]) : 0,
                        Distance = item.TryGetProperty("distance", out var d) ? d.GetDouble() : 0
                    });
                }
            }
        }
        catch { /* non-fatal */ }
        return results;
    }

    public async Task<RouteResult?> GetRouteAsync(GeoPoint from, GeoPoint to, string mode, CancellationToken ct)
    {
        try
        {
            var type = mode switch { "walking" => "1", "transit" => "0", _ => "0" };
            var url = $"https://restapi.amap.com/v3/direction/driving?origin={from.Lng},{from.Lat}&destination={to.Lng},{to.Lat}&key={_key}";
            var json = await _http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("route", out var r) && r.TryGetProperty("paths", out var paths))
            {
                var path = paths[0];
                return new RouteResult
                {
                    DistanceMeters = double.Parse(path.GetProperty("distance").GetString() ?? "0"),
                    DurationSeconds = double.Parse(path.GetProperty("duration").GetString() ?? "0")
                };
            }
        }
        catch { /* non-fatal */ }
        return null;
    }

    public async Task<WeatherResult?> GetWeatherAsync(string city, CancellationToken ct)
    {
        try
        {
            var geo = await GeocodeAsync(city, ct);
            if (geo == null) return null;
            var url = $"https://restapi.amap.com/v3/weather/weatherInfo?city={geo.Lng},{geo.Lat}&key={_key}";
            var json = await _http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("lives", out var arr) && arr.GetArrayLength() > 0)
            {
                var w = arr[0];
                return new WeatherResult
                {
                    Location = w.GetProperty("city").GetString() ?? "",
                    Temperature = $"{w.GetProperty("temperature").GetString()}°C",
                    Weather = w.GetProperty("weather").GetString() ?? "",
                    Humidity = $"{w.GetProperty("humidity").GetString()}%",
                    Wind = w.GetProperty("winddirection").GetString() + " " + w.GetProperty("windpower").GetString(),
                };
            }
        }
        catch { /* non-fatal */ }
        return null;
    }

    public async Task<IPLocation?> GetIPLocationAsync(string ip, CancellationToken ct)
    {
        try
        {
            var url = $"https://restapi.amap.com/v3/ip?ip={ip}&key={_key}";
            var json = await _http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            return new IPLocation
            {
                Ip = ip,
                Province = doc.RootElement.TryGetProperty("province", out var p) ? p.GetString() ?? "" : "",
                City = doc.RootElement.TryGetProperty("city", out var c) ? c.GetString() ?? "" : ""
            };
        }
        catch { /* non-fatal */ }
        return null;
    }
}

internal sealed class TencentMapService
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _key;

    public TencentMapService(ILogger logger, string key = "")
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _logger = logger;
        _key = key;
    }

    public async Task<GeoAddress?> GeocodeAsync(string address, CancellationToken ct)
    {
        try
        {
            var url = $"https://apis.map.qq.com/ws/geocoder/v1/?address={Uri.EscapeDataString(address)}&key={_key}";
            var json = await _http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("status", out var s) && s.GetInt32() == 0 &&
                doc.RootElement.TryGetProperty("result", out var r))
            {
                var loc = r.GetProperty("location");
                var comp = r.TryGetProperty("address_components", out var ac) ? ac : default;
                return new GeoAddress
                {
                    Formatted = address,
                    Province = comp.ValueKind != JsonValueKind.Undefined && comp.TryGetProperty("province", out var p) ? p.GetString() ?? "" : "",
                    City = comp.ValueKind != JsonValueKind.Undefined && comp.TryGetProperty("city", out var ci) ? ci.GetString() ?? "" : "",
                    District = comp.ValueKind != JsonValueKind.Undefined && comp.TryGetProperty("district", out var d) ? d.GetString() ?? "" : "",
                    Lng = loc.GetProperty("lng").GetDouble(),
                    Lat = loc.GetProperty("lat").GetDouble(),
                    Confidence = r.TryGetProperty("reliability", out var rel) ? rel.GetInt32() : 7
                };
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Tencent geocode failed"); }
        return null;
    }

    public async Task<GeoAddress?> ReverseGeocodeAsync(double lng, double lat, CancellationToken ct)
    {
        try
        {
            var url = $"https://apis.map.qq.com/ws/geocoder/v1/?location={lat},{lng}&key={_key}";
            var json = await _http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("result", out var r))
            {
                var comp = r.GetProperty("address_component");
                return new GeoAddress
                {
                    Formatted = r.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "",
                    Province = comp.GetProperty("province").GetString() ?? "",
                    City = comp.GetProperty("city").GetString() ?? "",
                    District = comp.GetProperty("district").GetString() ?? "",
                    Street = comp.TryGetProperty("street", out var s) ? s.GetString() ?? "" : "",
                    Lng = lng, Lat = lat
                };
            }
        }
        catch { /* non-fatal */ }
        return null;
    }

    public async Task<List<POIResult>> SearchPOIAsync(string keyword, string? city, int limit, CancellationToken ct)
    {
        var results = new List<POIResult>();
        try
        {
            var region = city ?? "全国";
            var url = $"https://apis.map.qq.com/ws/place/v1/search?keyword={Uri.EscapeDataString(keyword)}&boundary=region({Uri.EscapeDataString(region)})&page_size={limit}&key={_key}";
            var json = await _http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    results.Add(new POIResult
                    {
                        Name = item.GetProperty("title").GetString() ?? "",
                        Address = item.TryGetProperty("address", out var addr) ? addr.GetString() ?? "" : "",
                        Type = item.TryGetProperty("category", out var cat) ? cat.GetString() ?? "" : "",
                        Lng = item.TryGetProperty("location", out var loc) ? loc.GetProperty("lng").GetDouble() : 0,
                        Lat = item.TryGetProperty("location", out loc) ? loc.GetProperty("lat").GetDouble() : 0,
                        Distance = item.TryGetProperty("_distance", out var dist) ? dist.GetDouble() : 0
                    });
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Tencent POI search failed"); }
        return results;
    }

    public async Task<RouteResult?> GetRouteAsync(GeoPoint from, GeoPoint to, string mode, CancellationToken ct)
    {
        try
        {
            var type = mode switch { "walking" => "walking", "bicycling" => "bicycling", "transit" => "transit", _ => "driving" };
            var url = $"https://apis.map.qq.com/ws/direction/v1/{type}/?from={from.Lat},{from.Lng}&to={to.Lat},{to.Lng}&key={_key}";
            var json = await _http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("routes", out var routes))
            {
                var route = routes[0];
                return new RouteResult
                {
                    DistanceMeters = route.GetProperty("distance").GetDouble(),
                    DurationSeconds = route.GetProperty("duration").GetDouble()
                };
            }
        }
        catch { /* non-fatal */ }
        return null;
    }

    public async Task<string?> GetStaticMapUrlAsync(double lng, double lat, int zoom = 15, string size = "600*300", CancellationToken ct = default)
    {
        return await Task.FromResult(
            $"https://apis.map.qq.com/ws/staticmap/v2/?center={lat},{lng}&zoom={zoom}&size={size}&markers={lat},{lng}&key={_key}");
    }
}

internal sealed class TiandituService
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _tk;

    public TiandituService(ILogger logger, string tk = "")
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _logger = logger;
        _tk = tk;
    }

    public async Task<GeoAddress?> GeocodeAsync(string address, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.tianditu.gov.cn/geocoder?ds={{\"keyWord\":\"{Uri.EscapeDataString(address)}\"}}&type=geocode&tk={_tk}";
            var json = await _http.GetStringAsync(url, ct);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("location", out var loc))
            {
                return new GeoAddress
                {
                    Formatted = address,
                    Lng = loc.GetProperty("lon").GetDouble(),
                    Lat = loc.GetProperty("lat").GetDouble()
                };
            }
        }
        catch { /* non-fatal */ }
        return null;
    }
}
