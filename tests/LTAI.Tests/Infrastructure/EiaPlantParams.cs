namespace LTAI.Tests.Infrastructure;

public sealed record EiaPlantParams(
    double SourceStrength,
    double WindSpeed,
    double StackHeight,
    double StackDiameter,
    double ExitTemperature,
    double AmbientTemperature,
    string Stability,
    GeoPoint Location)
{
    public string ToQueryString() =>
        $"Q={SourceStrength:F2} u={WindSpeed:F1} He={StackHeight:F0} D={StackDiameter:F1} " +
        $"Ts={ExitTemperature:F0} Ta={AmbientTemperature:F0} stability={Stability}";

    public string ToJson() =>
        System.Text.Json.JsonSerializer.Serialize(this);
}

public sealed record GeoPoint(double Latitude, double Longitude);
