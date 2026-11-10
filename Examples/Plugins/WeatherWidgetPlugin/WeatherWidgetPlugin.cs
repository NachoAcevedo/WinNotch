using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinNotch.Models;
using WinNotch.Plugins;
using WinNotch.Services;

namespace WeatherWidgetPlugin;

[WinNotchPlugin("com.winnotch.weatherwidget", "Weather", "1.0.1", "WinNotch Team")]
public sealed class WeatherWidgetPlugin : PluginBase, IUIPlugin
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(10);

    public override string Id => "com.winnotch.weatherwidget";
    public override string Name => "Weather";
    public override string Version => "1.0.1";
    public override string Author => "WinNotch Team";
    public override string Description => "Shows current weather in the expanded notch.";
    public override string MinimumWinNotchVersion => "0.3.8";

    private ThemeService? _themeService;
    private DispatcherTimer? _refreshTimer;
    private WeatherView? _accessoryView;
    private WeatherView? _verticalView;
    private WeatherLocation? _location;
    private WeatherSnapshot? _weather;
    private string _statusText = "Loading weather...";
    private bool _isLight;
    private bool _isRefreshing;
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    public override async Task InitializeAsync(IPluginContext context)
    {
        await base.InitializeAsync(context);

        _themeService = context.ThemeService;
        _isLight = _themeService.IsLight;
        _themeService.ThemeChanged += OnThemeChanged;

        _refreshTimer = new DispatcherTimer { Interval = RefreshInterval };
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();

        _ = RefreshWeatherAsync(forceLocationRefresh: true);
    }

    public UIElement? GetUIElement(UIPluginLocation location)
    {
        return location switch
        {
            UIPluginLocation.OpenAccessory => BuildAccessoryView(),
            UIPluginLocation.VerticalOpenContent => BuildVerticalView(),
            _ => null
        };
    }

    public void OnNotchStateChanged(NotchState newState)
    {
        if (newState == NotchState.Open && DateTime.UtcNow - _lastRefreshUtc > StaleThreshold)
        {
            _ = RefreshWeatherAsync();
        }
    }

    private UIElement BuildAccessoryView()
    {
        if (_accessoryView != null)
            return _accessoryView.Root;

        _accessoryView = CreateView(width: 168, centered: false, largeTemperature: 28);
        ApplyTheme();
        UpdateViews();
        return _accessoryView.Root;
    }

    private UIElement BuildVerticalView()
    {
        if (_verticalView != null)
            return _verticalView.Root;

        _verticalView = CreateView(width: 136, centered: true, largeTemperature: 26);
        _verticalView.Root.Margin = new Thickness(0, 0, 0, 2);
        ApplyTheme();
        UpdateViews();
        return _verticalView.Root;
    }

    private WeatherView CreateView(double width, bool centered, double largeTemperature)
    {
        var root = new Border
        {
            Width = width,
            Padding = new Thickness(0),
            Margin = new Thickness(0)
        };

        var stack = new StackPanel();

        var cityText = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left
        };
        stack.Children.Add(cityText);

        var temperatureText = new TextBlock
        {
            FontSize = largeTemperature,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 2, 0, 0),
            TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left
        };
        stack.Children.Add(temperatureText);

        var summaryText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left
        };
        stack.Children.Add(summaryText);

        var detailsText = new TextBlock
        {
            FontSize = 10,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left
        };
        stack.Children.Add(detailsText);

        var footerText = new TextBlock
        {
            FontSize = 9,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left
        };
        stack.Children.Add(footerText);

        root.Child = stack;

        return new WeatherView(root, cityText, temperatureText, summaryText, detailsText, footerText);
    }

    private async void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        await RefreshWeatherAsync();
    }

    private async Task RefreshWeatherAsync(bool forceLocationRefresh = false)
    {
        if (_isRefreshing)
            return;

        _isRefreshing = true;
        _statusText = "Refreshing weather...";
        UpdateViews();

        try
        {
            if (forceLocationRefresh || _location == null)
            {
                _location = await FetchLocationAsync();
            }

            if (_location == null)
            {
                _weather = null;
                _statusText = "Location unavailable.";
                return;
            }

            _weather = await FetchWeatherAsync(_location);
            _statusText = _weather != null ? "Weather updated." : "Weather unavailable.";
            _lastRefreshUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _weather = null;
            _statusText = "Weather unavailable.";
            Context?.Log($"Weather plugin refresh failed: {ex.Message}", PluginLogLevel.Warning);
        }
        finally
        {
            _isRefreshing = false;
            UpdateViews();
        }
    }

    private async Task<WeatherLocation?> FetchLocationAsync()
    {
        var path = System.IO.Path.Combine(Context!.GetPluginDataPath(Id), "location.json");
        if (!System.IO.File.Exists(path))
            return null;
        var response = System.Text.Json.JsonSerializer.Deserialize<WeatherLocation>(
            await System.IO.File.ReadAllTextAsync(path));
        if (response == null || string.IsNullOrWhiteSpace(response.City)
            || !double.IsFinite(response.Latitude) || !double.IsFinite(response.Longitude)
            || Math.Abs(response.Latitude) > 90 || Math.Abs(response.Longitude) > 180)
            return null;
        return response;
    }

    private async Task<WeatherSnapshot?> FetchWeatherAsync(WeatherLocation location)
    {
        var url =
            FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={location.Latitude:0.####}&longitude={location.Longitude:0.####}") +
            "&current=temperature_2m,weather_code,is_day,apparent_temperature" +
            "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
            "&timezone=auto&forecast_days=1";

        var response = await HttpClient.GetFromJsonAsync<OpenMeteoResponse>(url);
        if (response?.Current == null || response.Daily == null)
            return null;

        double high = response.Daily.TemperatureMax?.Length > 0 ? response.Daily.TemperatureMax[0] : response.Current.Temperature;
        double low = response.Daily.TemperatureMin?.Length > 0 ? response.Daily.TemperatureMin[0] : response.Current.Temperature;
        double rainChance = response.Daily.PrecipitationProbabilityMax?.Length > 0
            ? response.Daily.PrecipitationProbabilityMax[0]
            : 0;

        return new WeatherSnapshot(
            location.City,
            location.CountryCode,
            response.Current.Temperature,
            response.Current.ApparentTemperature,
            high,
            low,
            rainChance,
            MapWeatherCodeToSummary(response.Current.WeatherCode, response.Current.IsDay == 1),
            DateTime.Now);
    }

    private void OnThemeChanged()
    {
        _isLight = _themeService?.IsLight ?? false;
        ApplyTheme();
    }

    private void UpdateViews()
    {
        UpdateView(_accessoryView);
        UpdateView(_verticalView);
        ApplyTheme();
    }

    private void UpdateView(WeatherView? view)
    {
        if (view == null)
            return;

        if (_weather == null)
        {
            view.CityText.Text = _location != null ? $"{_location.City} weather" : "Weather";
            view.TemperatureText.Text = "--";
            view.SummaryText.Text = _statusText;
            view.DetailsText.Text = "Waiting for a weather update.";
            view.FooterText.Text = "Data updates every 20 minutes.";
            return;
        }

        view.CityText.Text = string.IsNullOrWhiteSpace(_weather.CountryCode)
            ? _weather.City
            : $"{_weather.City}, {_weather.CountryCode}";
        view.TemperatureText.Text = FormatTemperature(_weather.Temperature);
        view.SummaryText.Text = _weather.Summary;
        view.DetailsText.Text =
            $"Feels {FormatTemperature(_weather.FeelsLike)}  H {FormatTemperature(_weather.High)}  L {FormatTemperature(_weather.Low)}";
        view.FooterText.Text = $"Rain {Math.Round(_weather.RainChance):0}%  Updated {_weather.ObservedAt:HH:mm}";
    }

    private void ApplyTheme()
    {
        ApplyTheme(_accessoryView);
        ApplyTheme(_verticalView);
    }

    private void ApplyTheme(WeatherView? view)
    {
        if (view == null)
            return;

        view.Root.Background = Brushes.Transparent;
        view.Root.BorderBrush = Brushes.Transparent;
        view.Root.BorderThickness = new Thickness(0);
        view.CityText.Foreground = GetSecondaryForeground();
        view.TemperatureText.Foreground = GetPrimaryForeground();
        view.SummaryText.Foreground = GetPrimaryForeground();
        view.DetailsText.Foreground = GetSecondaryForeground();
        view.FooterText.Foreground = GetMutedForeground();
    }

    private static string FormatTemperature(double value)
    {
        return $"{Math.Round(value):0}C";
    }

    private static string MapWeatherCodeToSummary(int code, bool isDay)
    {
        return code switch
        {
            0 => isDay ? "Clear skies" : "Clear night",
            1 => isDay ? "Mostly clear" : "Mostly clear night",
            2 => "Partly cloudy",
            3 => "Overcast",
            45 or 48 => "Fog",
            51 or 53 or 55 or 56 or 57 => "Drizzle",
            61 or 63 or 65 or 66 or 67 => "Rain",
            71 or 73 or 75 or 77 => "Snow",
            80 or 81 or 82 => "Rain showers",
            85 or 86 => "Snow showers",
            95 => "Thunderstorm",
            96 or 99 => "Storm and hail",
            _ => "Weather update"
        };
    }

    private Brush GetPrimaryForeground()
    {
        return new SolidColorBrush(_isLight
            ? Color.FromRgb(29, 34, 42)
            : Color.FromRgb(241, 244, 248));
    }

    private Brush GetSecondaryForeground()
    {
        return new SolidColorBrush(_isLight
            ? Color.FromRgb(97, 105, 117)
            : Color.FromRgb(179, 185, 194));
    }

    private Brush GetMutedForeground()
    {
        return new SolidColorBrush(_isLight
            ? Color.FromRgb(121, 128, 139)
            : Color.FromRgb(148, 156, 166));
    }

    public override Task ShutdownAsync()
    {
        if (_themeService != null)
            _themeService.ThemeChanged -= OnThemeChanged;

        if (_refreshTimer != null)
            _refreshTimer.Tick -= OnRefreshTimerTick;

        _refreshTimer?.Stop();
        _refreshTimer = null;

        return base.ShutdownAsync();
    }

    public override void Dispose()
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
        base.Dispose();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.Add("User-Agent", "WinNotch-Weather/1.0.1");
        return client;
    }

    private sealed record WeatherView(
        Border Root,
        TextBlock CityText,
        TextBlock TemperatureText,
        TextBlock SummaryText,
        TextBlock DetailsText,
        TextBlock FooterText);

    private sealed record WeatherLocation(string City, string CountryCode, double Latitude, double Longitude);

    private sealed record WeatherSnapshot(
        string City,
        string CountryCode,
        double Temperature,
        double FeelsLike,
        double High,
        double Low,
        double RainChance,
        string Summary,
        DateTime ObservedAt);

    private sealed class IpWhoIsResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("city")]
        public string? City { get; set; }

        [JsonPropertyName("country_code")]
        public string? CountryCode { get; set; }

        [JsonPropertyName("latitude")]
        public double Latitude { get; set; }

        [JsonPropertyName("longitude")]
        public double Longitude { get; set; }
    }

    private sealed class OpenMeteoResponse
    {
        [JsonPropertyName("current")]
        public OpenMeteoCurrent? Current { get; set; }

        [JsonPropertyName("daily")]
        public OpenMeteoDaily? Daily { get; set; }
    }

    private sealed class OpenMeteoCurrent
    {
        [JsonPropertyName("temperature_2m")]
        public double Temperature { get; set; }

        [JsonPropertyName("weather_code")]
        public int WeatherCode { get; set; }

        [JsonPropertyName("is_day")]
        public int IsDay { get; set; }

        [JsonPropertyName("apparent_temperature")]
        public double ApparentTemperature { get; set; }
    }

    private sealed class OpenMeteoDaily
    {
        [JsonPropertyName("temperature_2m_max")]
        public double[]? TemperatureMax { get; set; }

        [JsonPropertyName("temperature_2m_min")]
        public double[]? TemperatureMin { get; set; }

        [JsonPropertyName("precipitation_probability_max")]
        public double[]? PrecipitationProbabilityMax { get; set; }
    }
}
