using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinNotch.Models;
using WinNotch.Services;

namespace WinNotch.Views;

/// <summary>
/// Calendar card. Compact by default: a one-line header ("Miércoles 18 Nov · 1 evento hoy")
/// and the day's meetings. Clicking the header expands it: the notch grows wider
/// (<see cref="ExpandedExtraWidth"/>), a month grid slides in on the left, and any day
/// can be picked to see its meetings, plus the next ones under "Próximos".
/// </summary>
public partial class CalendarView : UserControl
{
    /// <summary>Extra notch width while expanded (the month column).</summary>
    public const double ExpandedExtraWidth = 230;

    private const double MonthHostWidth = 225;
    private static readonly CultureInfo Es = new("es-AR");

    private CalendarService? _calendarService;
    private DispatcherTimer? _expiryTimer;

    private DateTime _selected = DateTime.Today;
    private DateTime _viewMonth = FirstOfMonth(DateTime.Today);
    private DateTime _lastToday = DateTime.Today;

    // Events per month (key: first day of the month), covering the month plus two weeks after it
    // so "Próximos" works at month end. Filled on demand from Outlook.
    private readonly Dictionary<DateTime, List<CalendarEvent>> _monthCache = new();
    private readonly HashSet<DateTime> _loading = new();

    private readonly List<Button> _dayButtons = new();

    public bool IsExpanded { get; private set; }

    /// <summary>Raised when the card expands or collapses, so the notch can resize.</summary>
    public event Action? ExpandedChanged;

    public CalendarView()
    {
        InitializeComponent();
        BuildDayButtons();
        UpdateHeader();
    }

    public void Bind(CalendarService calendarService)
    {
        _calendarService = calendarService;
        _calendarService.EventsUpdated += () => Dispatcher.Invoke(OnEventsUpdated);
        UpdateEventsList();

        // Re-filter every minute so a meeting disappears right when it ends,
        // without waiting for the 5-minute Outlook refresh.
        _expiryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _expiryTimer.Tick += (_, _) => UpdateEventsList();
        _expiryTimer.Start();
    }

    // Rounded camera body plus lens, on the same 16x16 grid as the transport icons.
    private static readonly Geometry MeetingIcon = Geometry.Parse(
        "M 3.5,4.5 H 8.5 A 1.5,1.5 0 0 1 10,6 V 10 A 1.5,1.5 0 0 1 8.5,11.5 H 3.5 A 1.5,1.5 0 0 1 2,10 V 6 A 1.5,1.5 0 0 1 3.5,4.5 Z " +
        "M 10,7 L 13,5.3 Q 14,4.8 14,5.8 V 10.2 Q 14,11.2 13,10.7 L 10,9");

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

    #region Expand / collapse

    private void OnHeaderClick(object sender, RoutedEventArgs e) => SetExpanded(!IsExpanded, animate: true);

    /// <summary>
    /// Back to the compact view of today, without animation and without raising
    /// <see cref="ExpandedChanged"/>. Called when the notch closes.
    /// </summary>
    public void ResetToToday()
    {
        _selected = DateTime.Today;
        _viewMonth = FirstOfMonth(_selected);
        if (IsExpanded)
            ApplyExpandedVisuals(false, animate: false);
        IsExpanded = false;
        UpdateEventsList();
    }

    private void SetExpanded(bool expanded, bool animate)
    {
        if (IsExpanded == expanded) return;
        IsExpanded = expanded;

        if (expanded)
        {
            EnsureMonthLoaded(_viewMonth);
            EnsureMonthLoaded(FirstOfMonth(_selected));
            RenderDays();
            RenderUpcoming();
        }

        ApplyExpandedVisuals(expanded, animate && SystemParameters.ClientAreaAnimation);
        ExpandedChanged?.Invoke();
    }

    private void ApplyExpandedVisuals(bool expanded, bool animate)
    {
        HeaderButton.ToolTip = expanded ? "Contraer" : "Ver el mes";
        System.Windows.Automation.AutomationProperties.SetName(HeaderButton,
            expanded ? "Contraer calendario" : "Expandir calendario");

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Width 0 still reserves the month's height: take it out of the layout when compact,
        // once the closing animation is done.
        if (expanded)
        {
            MonthHost.Visibility = Visibility.Visible;
            Animate(MonthHost, WidthProperty, MonthHostWidth, 420, 0, ease, animate);
        }
        else if (!animate)
        {
            Animate(MonthHost, WidthProperty, 0, 0, 0, null, false);
            MonthHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            var close = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease };
            close.Completed += (_, _) =>
            {
                if (!IsExpanded) MonthHost.Visibility = Visibility.Collapsed;
            };
            MonthHost.BeginAnimation(WidthProperty, close, HandoffBehavior.SnapshotAndReplace);
        }
        Animate(MonthHost, OpacityProperty, expanded ? 1 : 0, expanded ? 300 : 150, expanded ? 120 : 0, null, animate);
        Animate(MonthShift, TranslateTransform.XProperty, expanded ? 0 : -16, 420, 0, ease, animate);
        Animate(ChevronRotate, RotateTransform.AngleProperty, expanded ? 180 : 0, 350, 0, ease, animate);

        if (expanded && ShowUpcoming())
        {
            UpcomingPanel.Visibility = Visibility.Visible;
            Animate(UpcomingPanel, OpacityProperty, 1, 300, 300, null, animate);
            Animate(UpcomingShift, TranslateTransform.YProperty, 0, 400, 300, ease, animate);
            if (animate) StaggerDays();
        }
        else if (expanded) { }   // day with events: "Próximos" stays hidden
        else
        {
            // Collapse at once (no stagger) so the notch shrinks right away.
            Animate(UpcomingPanel, OpacityProperty, 0, 0, 0, null, false);
            Animate(UpcomingShift, TranslateTransform.YProperty, 8, 0, 0, null, false);
            UpcomingPanel.Visibility = Visibility.Collapsed;
        }
    }

    private static void Animate(DependencyObject target, DependencyProperty property, double to,
        int durationMs, int delayMs, IEasingFunction? ease, bool animate)
    {
        if (target is not IAnimatable animatable) return;
        if (!animate)
        {
            animatable.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(durationMs))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease
        };
        animatable.BeginAnimation(property, anim, HandoffBehavior.SnapshotAndReplace);
    }

    #endregion

    #region Day selection / month navigation

    private void OnTodayClick(object sender, RoutedEventArgs e) => Select(DateTime.Today);

    private void OnPrevMonth(object sender, RoutedEventArgs e) => ShowMonth(_viewMonth.AddMonths(-1));

    private void OnNextMonth(object sender, RoutedEventArgs e) => ShowMonth(_viewMonth.AddMonths(1));

    private void ShowMonth(DateTime month)
    {
        _viewMonth = FirstOfMonth(month);
        EnsureMonthLoaded(_viewMonth);
        RenderDays();
        FadeIn(DaysGrid, null);
    }

    private void Select(DateTime day)
    {
        _selected = day.Date;
        var month = FirstOfMonth(_selected);
        bool monthChanged = month != _viewMonth;
        _viewMonth = month;
        EnsureMonthLoaded(month);

        UpdateHeader();
        RenderEvents();
        if (IsExpanded)
        {
            RenderDays();
            RenderUpcoming();
            if (monthChanged) FadeIn(DaysGrid, null);
        }
        FadeIn(EventsPanel, EventsShift);
    }

    private static void FadeIn(UIElement element, TranslateTransform? shift)
    {
        bool animate = SystemParameters.ClientAreaAnimation;
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 0;
        Animate(element, OpacityProperty, 1, 250, 0, null, animate);
        if (shift != null)
        {
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            shift.Y = 6;
            Animate(shift, TranslateTransform.YProperty, 0, 300, 0, new CubicEase { EasingMode = EasingMode.EaseOut }, animate);
        }
    }

    #endregion

    #region Data

    private void OnEventsUpdated()
    {
        // Outlook refreshed: drop cached months and re-read the ones on screen.
        var keep = new HashSet<DateTime> { _viewMonth, FirstOfMonth(_selected) };
        foreach (var key in _monthCache.Keys.Where(k => !keep.Contains(k)).ToList())
            _monthCache.Remove(key);
        if (IsExpanded)
            foreach (var month in keep)
                EnsureMonthLoaded(month, force: true);
        UpdateEventsList();
    }

    private async void EnsureMonthLoaded(DateTime month, bool force = false)
    {
        if (_calendarService == null || _loading.Contains(month)) return;
        if (!force && _monthCache.ContainsKey(month)) return;

        _loading.Add(month);
        try
        {
            var events = await _calendarService.FetchRangeAsync(month, month.AddMonths(1).AddDays(14));
            _monthCache[month] = events;
        }
        catch { }
        finally
        {
            _loading.Remove(month);
        }
        UpdateHeader();
        RenderEvents();
        if (IsExpanded)
        {
            RenderDays();
            RenderUpcoming();
        }
    }

    /// <summary>
    /// Placeholder blocks that only exist to decline meetings in that slot (lunch):
    /// never listed, counted or dotted.
    /// </summary>
    private static bool IsVisible(CalendarEvent e) =>
        e.Title.IndexOf("almuerzo", StringComparison.OrdinalIgnoreCase) < 0;

    private List<CalendarEvent> TodayRemaining()
    {
        var now = DateTime.Now;
        return _calendarService?.TodayEvents.Where(e => e.End > now && IsVisible(e)).ToList() ?? new List<CalendarEvent>();
    }

    /// <summary>The selected day's events; null while that month is still loading.</summary>
    private List<CalendarEvent>? EventsFor(DateTime day)
    {
        if (day == DateTime.Today) return TodayRemaining();
        if (!_monthCache.TryGetValue(FirstOfMonth(day), out var month)) return null;
        var next = day.AddDays(1);
        return month.Where(e => e.Start < next && e.End > day && IsVisible(e)).OrderBy(e => e.Start).ToList();
    }

    private bool HasEvents(DateTime day)
    {
        if (day == DateTime.Today && _calendarService?.TodayEvents.Any(IsVisible) == true) return true;
        if (!_monthCache.TryGetValue(FirstOfMonth(day), out var month)) return false;
        var next = day.AddDays(1);
        return month.Any(e => e.Start < next && e.End > day && IsVisible(e));
    }

    #endregion

    #region Rendering

    private void UpdateEventsList()
    {
        // Past midnight: the compact view follows the new day.
        if (DateTime.Today != _lastToday)
        {
            if (!IsExpanded || _selected == _lastToday)
            {
                _selected = DateTime.Today;
                _viewMonth = FirstOfMonth(_selected);
            }
            _lastToday = DateTime.Today;
        }

        UpdateHeader();
        RenderEvents();
        if (IsExpanded)
        {
            RenderDays();
            RenderUpcoming();
        }
    }

    private void UpdateHeader()
    {
        bool isToday = _selected == DateTime.Today;
        TitleText.Text = $"{Cap(_selected.ToString("dddd", Es))} {_selected.Day} {Cap(_selected.ToString("MMM", Es).TrimEnd('.'))}";

        var events = EventsFor(_selected);
        string count = events == null ? "…"
            : events.Count == 0 ? "Sin eventos"
            : events.Count == 1 ? "1 evento"
            : $"{events.Count} eventos";
        CountText.Text = isToday && events != null ? count + " hoy" : count;

        TodayButton.Visibility = isToday ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderEvents()
    {
        EventsPanel.Children.Clear();

        var events = EventsFor(_selected);
        // No events: the header already says "Sin eventos (hoy)", so nothing is listed.
        if (events != null && events.Count == 0) return;
        if (events == null)
        {
            var faintBrush = TryFindResource("TextFaintBrush") as SolidColorBrush
                ?? new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            EventsPanel.Children.Add(new TextBlock
            {
                Text = "Cargando…",
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
            // DockPanel: dot and time on the left, meeting icon on the right,
            // title fills whatever width is left.
            var item = new DockPanel
            {
                LastChildFill = true,
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

            var title = new TextBlock
            {
                Text = ev.Title,
                Foreground = primaryBrush,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            var surface = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 3, 0, 3), Child = item };
            surface.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");

            if (!string.IsNullOrEmpty(ev.JoinUrl))
            {
                // Video camera marking joinable meetings, drawn in the same stroke language
                // as the transport icons (16x16 grid, 1.8 stroke, round caps and joins).
                var icon = new Path
                {
                    Data = MeetingIcon,
                    Width = 16,
                    Height = 16,
                    Stroke = TryFindResource("AccentBrush") as SolidColorBrush
                        ?? new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9)),
                    StrokeThickness = 1.8,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0)
                };
                DockPanel.SetDock(icon, Dock.Right);
                item.Children.Add(icon);

                var url = ev.JoinUrl;
                surface.Cursor = System.Windows.Input.Cursors.Hand;
                surface.ToolTip = "Unirse a la reunión";
                surface.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    OpenMeeting(url);
                };
            }

            item.Children.Add(title); // last child fills the remaining width
            EventsPanel.Children.Add(surface);
        }
    }

    private void BuildDayButtons()
    {
        for (int i = 0; i < 42; i++)
        {
            var label = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
            var dot = new Ellipse { Width = 4, Height = 4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) };
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(label);
            content.Children.Add(dot);

            var button = new Button
            {
                Style = (Style)FindResource("CalButton"),
                Height = 26,
                Margin = new Thickness(1),
                Content = content,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };
            button.Click += (s, _) =>
            {
                if (((Button)s).Tag is DateTime day) Select(day);
            };
            _dayButtons.Add(button);
            DaysGrid.Children.Add(button);
        }
    }

    private void RenderDays()
    {
        MonthTitleText.Text = Cap(_viewMonth.ToString("MMMM yyyy", Es));

        var accent = TryFindResource("AccentBrush") as SolidColorBrush
            ?? new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9));
        var onAccent = new SolidColorBrush(Luminance(accent.Color) > 0.55 ? Color.FromRgb(0x1C, 0x1C, 0x1C) : Colors.White);
        int lead = ((int)_viewMonth.DayOfWeek + 6) % 7;   // Monday first
        int daysInMonth = DateTime.DaysInMonth(_viewMonth.Year, _viewMonth.Month);

        for (int i = 0; i < _dayButtons.Count; i++)
        {
            var button = _dayButtons[i];
            int d = i - lead + 1;
            if (d < 1 || d > daysInMonth)
            {
                button.Visibility = Visibility.Hidden;
                button.Tag = null;
                continue;
            }

            var day = new DateTime(_viewMonth.Year, _viewMonth.Month, d);
            bool selected = day == _selected;
            bool today = day == DateTime.Today;
            bool weekend = i % 7 >= 5;

            var content = (StackPanel)button.Content;
            var label = (TextBlock)content.Children[0];
            var dot = (Ellipse)content.Children[1];

            button.Visibility = Visibility.Visible;
            button.Tag = day;
            label.Text = d.ToString();
            label.FontWeight = selected || today ? FontWeights.Bold : FontWeights.Normal;
            button.ToolTip = Cap(day.ToString("dddd d 'de' MMMM", Es));

            if (selected)
            {
                button.Background = accent;
                button.BorderThickness = new Thickness(0);
                label.Foreground = onAccent;
            }
            else
            {
                button.Background = Brushes.Transparent;
                button.BorderBrush = today ? accent : Brushes.Transparent;
                button.BorderThickness = new Thickness(today ? 1 : 0);
                if (today) label.Foreground = accent;
                else label.SetResourceReference(TextBlock.ForegroundProperty, weekend ? "TextMutedBrush" : "TextPrimaryBrush");
            }

            dot.Fill = HasEvents(day) ? (selected ? onAccent : accent) : Brushes.Transparent;
        }
    }

    /// <summary>Days pop in one after another when the month opens.</summary>
    private void StaggerDays()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (int i = 0; i < _dayButtons.Count; i++)
        {
            var button = _dayButtons[i];
            var scale = (ScaleTransform)button.RenderTransform;
            int delay = 150 + i * 8;

            button.BeginAnimation(OpacityProperty, null);
            button.Opacity = 0;
            Animate(button, OpacityProperty, 1, 250, delay, null, true);

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = scale.ScaleY = 0.7;
            Animate(scale, ScaleTransform.ScaleXProperty, 1, 300, delay, ease, true);
            Animate(scale, ScaleTransform.ScaleYProperty, 1, 300, delay, ease, true);
        }
    }

    /// <summary>"Próximos" only fills an empty day: a day with meetings shows just those.</summary>
    private bool ShowUpcoming() => EventsFor(_selected) is { Count: 0 };

    private void RenderUpcoming()
    {
        UpcomingList.Children.Clear();

        if (!IsExpanded || !ShowUpcoming())
        {
            UpcomingPanel.BeginAnimation(OpacityProperty, null);
            UpcomingPanel.Opacity = 0;
            UpcomingPanel.Visibility = Visibility.Collapsed;
            return;
        }
        if (UpcomingPanel.Visibility != Visibility.Visible)
        {
            UpcomingPanel.Visibility = Visibility.Visible;
            FadeIn(UpcomingPanel, UpcomingShift);
        }

        var after = _selected.AddDays(1);
        IEnumerable<CalendarEvent> pool = _monthCache.TryGetValue(FirstOfMonth(_selected), out var month)
            ? month
            : Enumerable.Empty<CalendarEvent>();
        var next = pool.Where(e => e.Start >= after && IsVisible(e)).OrderBy(e => e.Start).Take(3).ToList();

        if (next.Count == 0)
        {
            var none = new TextBlock
            {
                Text = month == null ? "Cargando…" : "Nada en las próximas semanas",
                FontSize = 12,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 2, 0, 0)
            };
            none.SetResourceReference(TextBlock.ForegroundProperty, "TextFaintBrush");
            UpcomingList.Children.Add(none);
            return;
        }

        foreach (var ev in next)
        {
            string when = $"{Cap(ev.Start.ToString("ddd", Es).TrimEnd('.'))} {ev.Start.Day}"
                + (ev.IsAllDay ? "" : $" · {ev.Start:HH:mm}");

            var whenText = new TextBlock { Text = when, FontSize = 12, Margin = new Thickness(8, 0, 0, 0) };
            whenText.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            DockPanel.SetDock(whenText, Dock.Right);
            var titleText = new TextBlock { Text = ev.Title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

            var row = new DockPanel { LastChildFill = true };
            row.Children.Add(whenText);
            row.Children.Add(titleText);

            var day = ev.Start.Date;
            var button = new Button
            {
                Style = (Style)FindResource("CalButton"),
                Content = row,
                Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(-6, 0, -6, 0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ToolTip = "Ir a ese día"
            };
            button.Click += (_, _) => Select(day);
            UpcomingList.Children.Add(button);
        }
    }

    #endregion

    private static DateTime FirstOfMonth(DateTime d) => new(d.Year, d.Month, 1);

    private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0], Es) + s.Substring(1);

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
