using System.Text.Json;
using System.Text.Json.Serialization;

namespace LTAI.Capability.GIS;

public sealed class GeoPoint
{
    [JsonPropertyName("lng")] public double Lng { get; init; }
    [JsonPropertyName("lat")] public double Lat { get; init; }
}

public sealed class GeoAddress
{
    [JsonPropertyName("formatted")] public string Formatted { get; init; } = "";
    [JsonPropertyName("province")] public string Province { get; init; } = "";
    [JsonPropertyName("city")] public string City { get; init; } = "";
    [JsonPropertyName("district")] public string District { get; init; } = "";
    [JsonPropertyName("street")] public string Street { get; init; } = "";
    [JsonPropertyName("lng")] public double Lng { get; init; }
    [JsonPropertyName("lat")] public double Lat { get; init; }
    [JsonPropertyName("confidence")] public int Confidence { get; init; }
}

public sealed class POIResult
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("address")] public string Address { get; init; } = "";
    [JsonPropertyName("lng")] public double Lng { get; init; }
    [JsonPropertyName("lat")] public double Lat { get; init; }
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("distance")] public double Distance { get; init; }
    [JsonPropertyName("phone")] public string Phone { get; init; } = "";
}

public sealed class RouteResult
{
    [JsonPropertyName("distance")] public double DistanceMeters { get; init; }
    [JsonPropertyName("duration")] public double DurationSeconds { get; init; }
    [JsonPropertyName("steps")] public List<RouteStep> Steps { get; init; } = new();
}

public sealed class RouteStep
{
    [JsonPropertyName("instruction")] public string Instruction { get; init; } = "";
    [JsonPropertyName("distance")] public double Distance { get; init; }
    [JsonPropertyName("duration")] public double Duration { get; init; }
}

public sealed class WeatherResult
{
    [JsonPropertyName("location")] public string Location { get; init; } = "";
    [JsonPropertyName("temperature")] public string Temperature { get; init; } = "";
    [JsonPropertyName("weather")] public string Weather { get; init; } = "";
    [JsonPropertyName("humidity")] public string Humidity { get; init; } = "";
    [JsonPropertyName("wind")] public string Wind { get; init; } = "";
    [JsonPropertyName("aqi")] public int Aqi { get; init; }
}

public sealed class IPLocation
{
    [JsonPropertyName("ip")] public string Ip { get; init; } = "";
    [JsonPropertyName("country")] public string Country { get; init; } = "";
    [JsonPropertyName("province")] public string Province { get; init; } = "";
    [JsonPropertyName("city")] public string City { get; init; } = "";
    [JsonPropertyName("isp")] public string Isp { get; init; } = "";
}
