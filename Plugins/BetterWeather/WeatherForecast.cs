using System.Text.Json;

namespace WinNotch.Services;

public sealed record WeatherForecast(double Temperature, double FeelsLike, double High,
    double Low, double RainChance, int Code, bool IsDay)
{
    public static WeatherForecast Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var current = document.RootElement.GetProperty("current");
        var daily = document.RootElement.GetProperty("daily");
        return new(current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("apparent_temperature").GetDouble(),
            daily.GetProperty("temperature_2m_max")[0].GetDouble(),
            daily.GetProperty("temperature_2m_min")[0].GetDouble(),
            daily.GetProperty("precipitation_probability_max")[0].GetDouble(),
            current.GetProperty("weather_code").GetInt32(),
            current.GetProperty("is_day").GetInt32() == 1);
    }

    public string Summary => Code switch
    {
        0 => IsDay ? "Despejado" : "Noche despejada",
        1 => "Mayormente despejado",
        2 => "Parcialmente nublado",
        3 => "Nublado",
        45 or 48 => "Niebla",
        51 or 53 or 55 or 56 or 57 => "Llovizna",
        61 or 63 or 65 or 66 or 67 => "Lluvia",
        71 or 73 or 75 or 77 or 85 or 86 => "Nieve",
        80 or 81 or 82 => "Chaparrones",
        95 or 96 or 99 => "Tormenta",
        _ => "Condiciones variables"
    };
}
