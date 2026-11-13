using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinNotch.Services;

namespace WinNotch.Views;

public sealed class WeatherCardView : UserControl
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(20);
    private static readonly System.Threading.SemaphoreSlim RefreshLock = new(1, 1);
    private static WeatherForecast? _cached;
    private static DateTime _updatedUtc;
    private readonly TextBlock _temperature = Label("—", 44, "TextPrimaryBrush");
    private readonly TextBlock _summary = Label("Pronóstico pendiente", 12, "TextSecondaryBrush");
    private readonly TextBlock _feelsLike = Label("—", 13, "TextPrimaryBrush");
    private readonly TextBlock _range = Label("—", 13, "TextPrimaryBrush");
    private readonly TextBlock _rain = Label("—", 13, "TextPrimaryBrush");
    private readonly TextBlock _status = Label("", 11, "TextMutedBrush");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly WeatherIcon _icon = new() { Width = 40, Height = 40 };
    private bool _busy;
    private DateTime _retryAfterUtc;

    public WeatherCardView()
    {
        var panel = new StackPanel { Margin = new Thickness(4, 3, 4, 0) };
        var current = new DockPanel();
        var openWeather = new Button { Content = _icon, ToolTip = "Abrir el pronóstico", Padding = new Thickness(3), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(openWeather, "Abrir el pronóstico");
        openWeather.Click += (_, _) => OpenWeather();
        DockPanel.SetDock(openWeather, Dock.Left);
        current.Children.Add(openWeather);
        _temperature.FontWeight = FontWeights.SemiBold;
        _temperature.Margin = new Thickness(0, 0, 14, 0);
        _temperature.VerticalAlignment = VerticalAlignment.Center;
        current.Children.Add(_temperature);
        var location = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var city = Label("Buenos Aires", 14, "TextPrimaryBrush");
        city.FontWeight = FontWeights.SemiBold;
        location.Children.Add(city);
        location.Children.Add(_summary);
        current.Children.Add(location);
        panel.Children.Add(current);

        var details = new System.Windows.Controls.Primitives.UniformGrid
        {
            Columns = 3, Margin = new Thickness(0, 10, 0, 0)
        };
        details.Children.Add(Metric("Sensación", _feelsLike));
        details.Children.Add(Metric("Mín. / máx.", _range));
        details.Children.Add(Metric("Lluvia", _rain));
        panel.Children.Add(details);
        _status.Margin = new Thickness(0, 7, 0, 0);
        _status.HorizontalAlignment = HorizontalAlignment.Left;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Opacity = 0.65;
        panel.Children.Add(_status);
        Content = panel;
        Loaded += async (_, _) => { _timer.Start(); await RefreshAsync(); };
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += async (_, _) => { if (IsVisible) await RefreshAsync(); };
        _timer.Tick += async (_, _) => { if (IsVisible) await RefreshAsync(); };
    }

    private static StackPanel Metric(string title, TextBlock value)
    {
        var column = new StackPanel();
        column.Children.Add(Label(title, 11, "TextSecondaryBrush"));
        column.Children.Add(value);
        return column;
    }

    private static TextBlock Label(string text, double size, string brush)
    {
        var label = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return label;
    }

    private void Render()
    {
        if (_cached == null) return;
        _temperature.Text = $"{_cached.Temperature:0} °C";
        _summary.Text = _cached.Summary;
        _icon.Code = _cached.Code;
        _icon.IsDay = _cached.IsDay;
        _icon.InvalidateVisual();
        _feelsLike.Text = $"{_cached.FeelsLike:0}°";
        _range.Text = $"{_cached.Low:0}° / {_cached.High:0}°";
        _rain.Text = $"{_cached.RainChance:0}%";
        _status.Text = $"Open-Meteo · {_updatedUtc.ToLocalTime():HH:mm}";
    }

    private void OpenWeather()
    {
        try
        {
            using var protocol = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("bingweather");
            string destination = protocol != null ? "bingweather:" : "https://www.msn.com/es-ar/eltiempo";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(destination) { UseShellExecute = true });
        }
        catch (Exception error) { _status.Text = "No se pudo abrir el pronóstico: " + error.Message; }
    }

    private async Task RefreshAsync()
    {
        if (_busy || DateTime.UtcNow < _retryAfterUtc) return;
        Render();
        if (_cached != null && DateTime.UtcNow - _updatedUtc < CacheDuration) return;
        _busy = true;
        try
        {
            await RefreshLock.WaitAsync();
            try
            {
                if (_cached == null || DateTime.UtcNow - _updatedUtc >= CacheDuration)
                {
                    _status.Text = "Consultando clima…";
                    const string url = "https://api.open-meteo.com/v1/forecast?latitude=-34.6037&longitude=-58.3816&current=temperature_2m,weather_code,is_day,apparent_temperature&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=America%2FArgentina%2FBuenos_Aires&forecast_days=1";
                    _cached = WeatherForecast.Parse(await Http.GetStringAsync(url));
                    _updatedUtc = DateTime.UtcNow;
                }
                Render();
            }
            finally { RefreshLock.Release(); }
        }
        catch (Exception ex)
        {
            _retryAfterUtc = DateTime.UtcNow.AddMinutes(2);
            _status.Text = _cached == null ? "No se pudo consultar el clima. Reintentaremos en 2 min." : "Sin conexión · mostrando la última actualización.";
            System.Diagnostics.Trace.TraceWarning($"Weather card: {ex.Message}");
        }
        finally { _busy = false; }
    }
}
