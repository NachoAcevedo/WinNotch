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
    private readonly TextBlock _temperature = Label("—", 32, "AccentBrush");
    private readonly TextBlock _summary = Label("Seleccioná Clima para consultar el pronóstico.", 12, "TextSecondaryBrush");
    private readonly TextBlock _details = Label("", 11, "TextSecondaryBrush");
    private readonly TextBlock _status = Label("Open-Meteo · actualización cada 20 min", 10, "TextMutedBrush");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _busy;
    private DateTime _retryAfterUtc;

    public WeatherCardView()
    {
        var panel = new StackPanel { Margin = new Thickness(6, 2, 6, 0) };
        _temperature.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(Label("Buenos Aires", 13, "TextPrimaryBrush"));
        panel.Children.Add(_temperature);
        panel.Children.Add(_summary);
        _details.Margin = new Thickness(0, 5, 0, 0);
        panel.Children.Add(_details);
        _status.Margin = new Thickness(0, 5, 0, 0);
        panel.Children.Add(_status);
        Content = panel;
        Loaded += async (_, _) => { _timer.Start(); await RefreshAsync(); };
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += async (_, _) => { if (IsVisible) await RefreshAsync(); };
        _timer.Tick += async (_, _) => { if (IsVisible) await RefreshAsync(); };
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
        _details.Text = $"Sensación {_cached.FeelsLike:0}°  ·  Máx. {_cached.High:0}°  ·  Mín. {_cached.Low:0}°  ·  Lluvia {_cached.RainChance:0}%";
        _status.Text = $"Open-Meteo · actualizado {_updatedUtc.ToLocalTime():HH:mm} · cada 20 min";
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
