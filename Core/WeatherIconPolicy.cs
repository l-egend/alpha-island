namespace alpha_island.Core;

internal enum WeatherIconKind { Unknown, Sunny, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, FreezingRain, Snow, Thunderstorm }

/// <summary>Use the same forecast codes as WeatherService.Describe, without interpreting translated text.</summary>
internal static class WeatherIconPolicy
{
    internal static WeatherIconKind Resolve(int? code) => code switch
    {
        0 => WeatherIconKind.Sunny,
        1 or 2 => WeatherIconKind.PartlyCloudy,
        3 => WeatherIconKind.Cloudy,
        45 or 48 => WeatherIconKind.Fog,
        51 or 53 or 55 => WeatherIconKind.Drizzle,
        56 or 57 or 66 or 67 => WeatherIconKind.FreezingRain,
        61 or 63 or 65 or 80 or 81 or 82 => WeatherIconKind.Rain,
        71 or 73 or 75 or 77 or 85 or 86 => WeatherIconKind.Snow,
        95 or 96 or 97 or 99 => WeatherIconKind.Thunderstorm,
        _ => WeatherIconKind.Unknown
    };
}
