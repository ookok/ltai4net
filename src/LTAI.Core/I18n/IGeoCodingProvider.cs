namespace LTAI.Core.I18n;

/// <summary>
/// C3: Abstract geocoding service. Implementations can target Chinese providers
/// (Gaode/Tencent/Baidu/Tianditu) or global ones (Google/Mapbox/OpenStreetMap).
/// Registered via DI override — the default implementation uses Chinese providers.
/// </summary>
public interface IGeoGodingProvider
{
    /// <summary>Address → (lat, lng, formattedAddress). Returns null on failure.</summary>
    Task<(double lat, double lng, string display)?> GeocodeAsync(string address, CancellationToken ct = default);

    /// <summary>Coordinates → address. Returns null on failure.</summary>
    Task<string?> ReverseGeocodeAsync(double lat, double lng, CancellationToken ct = default);

    /// <summary>POI search by keyword near location. Returns list of (name, address, lat, lng).</summary>
    Task<IReadOnlyList<(string name, string address, double lat, double lng)>> SearchPoiAsync(
        string keyword, string? city = null, CancellationToken ct = default);
}
