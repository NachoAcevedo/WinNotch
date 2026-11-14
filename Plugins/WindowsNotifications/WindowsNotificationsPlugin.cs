using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using WinNotch.Plugins;

namespace PersonalCards.Notifications;

[WinNotchPlugin("personal.card.notifications", "Notifications", "0.3.0", "Nacho_114")]
public sealed class WindowsNotificationsPlugin : CardPluginBase
{
    public override string Id => "personal.card.notifications";
    public override string Name => "Notifications";
    public override string Version => "0.3.0";
    public override string Author => "Nacho_114";
    public override string Description => "Muestra las notificaciones actuales del centro de Windows.";
    public override string CardKey => "notifications";
    public override string CardTitle => "Notifications";
    public override int CardOrder => 67;
    public override double CardHeight => 260;
    protected override UIElement CreateCard() => new NotificationCard();
}

internal sealed class NotificationCard : DockPanel
{
    private readonly Button _allow = new() { Content = "Permitir acceso", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _list = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private Notice[] _shown = [];
    private bool _busy;

    public NotificationCard()
    {
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        SetDock(_allow, Dock.Top);
        SetDock(_status, Dock.Top);
        Children.Add(_allow);
        Children.Add(_status);
        Children.Add(new ScrollViewer { Content = _list, MaxHeight = 205, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        _allow.Click += async (_, _) => await RefreshAsync(requestAccess: true);
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += (_, _) => UpdatePolling();
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => UpdatePolling();
    }

    private async void UpdatePolling()
    {
        if (!IsLoaded || !IsVisible)
        {
            _timer.Stop();
            return;
        }

        _timer.Start();
        await RefreshAsync();
    }

    private async Task RefreshAsync(bool requestAccess = false, uint? dismissId = null)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var listener = UserNotificationListener.Current;
            if (requestAccess) _allow.IsEnabled = false;
            var access = requestAccess ? await listener.RequestAccessAsync() : listener.GetAccessStatus();
            _allow.IsEnabled = true;
            _allow.Visibility = access == UserNotificationListenerAccessStatus.Allowed ? Visibility.Collapsed : Visibility.Visible;
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                Display([]);
                SetStatus(access == UserNotificationListenerAccessStatus.Denied
                    ? "Windows denegó el acceso a las notificaciones de WinNotch."
                    : "Dale permiso a WinNotch para ver tus notificaciones.");
                return;
            }

            if (dismissId is uint id) listener.RemoveNotification(id);
            var entries = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            var notices = entries.Select(ReadNotice).OrderByDescending(notice => notice.Time).ToArray();
            Display(notices);
            SetStatus(dismissId is uint removed && notices.Any(notice => notice.Id == removed)
                ? "Windows todavía muestra este aviso. Probá de nuevo."
                : notices.Length == 0 ? "El centro de notificaciones está vacío." : "");
        }
        catch (Exception error)
        {
            if (dismissId == null) Display([]);
            _allow.IsEnabled = true;
            SetStatus(dismissId == null ? $"No se pudieron leer las notificaciones de Windows (0x{error.HResult:X8})." : "No se pudo quitar el aviso. Probá de nuevo.");
        }
        finally { _busy = false; }
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        _status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Notice ReadNotice(UserNotification entry)
    {
        try
        {
            var binding = entry.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            var lines = binding?.GetTextElements().Select(item => item.Text).ToArray() ?? [];
            return new(entry.Id, entry.CreationTime, entry.AppInfo?.DisplayInfo?.DisplayName ?? "Windows",
                lines.FirstOrDefault() ?? "Notificación", string.Join("\n", lines.Skip(1)));
        }
        catch (Exception)
        {
            return new(entry.Id, entry.CreationTime, "Windows", "Notificación", "");
        }
    }

    private void Display(Notice[] notices)
    {
        if (_shown.SequenceEqual(notices)) return;
        _shown = notices;
        _list.Children.Clear();
        foreach (var notice in notices)
        {
            var content = new StackPanel();
            var header = new DockPanel();
            string time = notice.Time.LocalDateTime.ToString(
                notice.Time.LocalDateTime.Date == DateTime.Today ? "HH:mm" : "dd/MM HH:mm");
            var clock = Label(time, 11, "TextSecondaryBrush");
            clock.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(clock, Dock.Right);
            header.Children.Add(clock);
            header.Children.Add(Label(notice.App, 11, "TextSecondaryBrush"));
            content.Children.Add(header);

            var title = Label(notice.Title, 14, "TextPrimaryBrush");
            title.FontWeight = FontWeights.Bold;
            title.Margin = new Thickness(0, 5, 0, 0);
            content.Children.Add(title);

            if (notice.Body.Length > 0)
            {
                var body = Label(notice.Body, 12, "TextPrimaryBrush");
                body.Margin = new Thickness(0, 3, 0, 0);
                body.LineHeight = 17;
                content.Children.Add(body);
            }

            var dismiss = new Button
            {
                Content = "Marcar como leída",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(8, 3, 8, 3),
                ToolTip = "Quita este aviso del centro de notificaciones de Windows"
            };
            dismiss.Click += async (_, _) => await RefreshAsync(dismissId: notice.Id);
            content.Children.Add(dismiss);

            var row = new Border
            {
                Child = content, Padding = new Thickness(2, 8, 8, 12),
                Margin = new Thickness(0, 0, 0, 4), BorderThickness = new Thickness(0, 0, 0, 1)
            };
            row.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            _list.Children.Add(row);
        }
    }

    private static TextBlock Label(string text, double size, string brush)
    {
        var label = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return label;
    }

    private sealed record Notice(uint Id, DateTimeOffset Time, string App, string Title, string Body);
}
