using WinNotch.Services;
using Xunit;

namespace WinNotch.Tests;

public class WeatherForecastTests
{
    [Fact]
    public void ParsesTemperaturesAndRainFromForecast()
    {
        var forecast = WeatherForecast.Parse("""
        {"current":{"temperature_2m":18.4,"apparent_temperature":19.7,"weather_code":1,"is_day":0},
         "daily":{"temperature_2m_max":[24.2],"temperature_2m_min":[15.1],"precipitation_probability_max":[35]}}
        """);
        Assert.Equal(18.4, forecast.Temperature);
        Assert.Equal(19.7, forecast.FeelsLike);
        Assert.Equal(35, forecast.RainChance);
        Assert.False(forecast.IsDay);
    }

    [Theory]
    [InlineData(0, false, "Noche despejada")]
    [InlineData(0, true, "Despejado")]
    [InlineData(95, true, "Tormenta")]
    [InlineData(999, true, "Condiciones variables")]
    public void MapsWeatherConditions(int code, bool isDay, string expected)
    {
        Assert.Equal(expected, new WeatherForecast(0, 0, 0, 0, 0, code, isDay).Summary);
    }
}
