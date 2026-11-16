using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinNotch.Services;

namespace WinNotch.Views;

public partial class CalendarView : UserControl
{
    private CalendarService? _calendarService;

    public CalendarView()
    {
        InitializeComponent();
        UpdateDateHeader();
    }

    public void Bind(CalendarService calendarService)
    {
        _calendarService = calendarService;
        _calendarService.EventsUpdated += () => Dispatcher.Invoke(UpdateEventsList);
        UpdateEventsList();

        // Re-filter every minute so a meeting disappears right when it ends,
        // without waiting for the 5-minute Outlook refresh.
        _expiryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _expiryTimer.Tick += (_, _) => UpdateEventsList();
        _expiryTimer.Start();
    }

    private DispatcherTimer? _expiryTimer;

    private static void OpenMeeting(string url)
    {
        // Teams links go straight to the desktop app (no browser "open Teams?" page);
        // fall back to the https link if the msteams: protocol isn't registered.
        const string teamsWeb = "https://teams.microsoft.com/";
        if (url.StartsWith(teamsWeb, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo("msteams:/" + url.Substring(teamsWeb.Length)) { UseShellExecute = true });
                return;
            }
            catch { }
        }
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private void UpdateDateHeader()
    {
        var now = DateTime.Now;
        DayOfWeekText.Text = now.ToString("dddd", CultureInfo.CurrentCulture);
        DayNumberText.Text = now.Day.ToString();
        MonthText.Text = now.ToString("MMM", CultureInfo.CurrentCulture);
    }

    private void UpdateEventsList()
    {
        UpdateDateHeader();
        EventsPanel.Children.Clear();

        var now = DateTime.Now;
        var events = _calendarService?.TodayEvents.Where(e => e.End > now).ToList();
        if (events == null || events.Count == 0)
        {
            var faintBrush = TryFindResource("TextFaintBrush") as SolidColorBrush
                ?? new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            EventsPanel.Children.Add(new TextBlock
            {
                Text = "No upcoming events",
                Foreground = faintBrush,
                FontSize = 12,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 4, 0, 0)
            });
            return;
        }

        var mutedBrush = TryFindResource("TextMutedBrush") as SolidColorBrush
            ?? new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        var primaryBrush = TryFindResource("TextPrimaryBrush") as SolidColorBrush
            ?? new SolidColorBrush(Colors.White);

        foreach (var ev in events)
        {
            var item = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 2)
            };

            // Color dot
            var dot = new Ellipse
            {
                Width = 6,
                Height = 6,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            try
            {
                dot.Fill = new SolidColorBrush(
                    (Color)System.Windows.Media.ColorConverter.ConvertFromString(ev.CalendarColor));
            }
            catch
            {
                dot.Fill = TryFindResource("AccentBrush") as SolidColorBrush
                    ?? new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9));
            }
            item.Children.Add(dot);

            // Time
            item.Children.Add(new TextBlock
            {
                Text = ev.TimeDisplay,
                Foreground = mutedBrush,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                MinWidth = 76
            });

            // Title
            item.Children.Add(new TextBlock
            {
                Text = ev.Title,
                Foreground = primaryBrush,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 170
            });

            var surface = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 3, 0, 3), Child = item };
            surface.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");

            if (!string.IsNullOrEmpty(ev.JoinUrl))
            {
                // Video-camera glyph (Segoe Fluent/MDL2) marks joinable meetings
                item.Children.Add(new TextBlock
                {
                    Text = "",
                    FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    Foreground = TryFindResource("AccentBrush") as SolidColorBrush
                        ?? new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9)),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0)
                });

                var url = ev.JoinUrl;
                surface.Cursor = System.Windows.Input.Cursors.Hand;
                surface.ToolTip = "Unirse a la reunión";
                surface.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    OpenMeeting(url);
                };
            }

            EventsPanel.Children.Add(surface);
        }
    }
}

