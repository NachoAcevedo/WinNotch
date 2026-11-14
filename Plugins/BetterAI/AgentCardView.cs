using PersonalCards.Agent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinNotch.Services;

namespace WinNotch.Views;

public sealed class AgentCardView : UserControl
{
    private readonly ComboBox _provider = new() { Width = 150, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 7) };
    private readonly TextBox _reply = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Height = 210, FontSize = 12 };
    private readonly TextBlock _status = new() { FontSize = 10, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public AgentCardView()
    {
        var panel = new StackPanel();
        _provider.ItemsSource = AgentProviders.All.Prepend("Automático").ToArray();
        _provider.SelectedIndex = 0;
        panel.Children.Add(_provider);
        _reply.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(_reply);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        panel.Children.Add(_status);
        Content = panel;
        _provider.SelectionChanged += (_, _) => Refresh();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        Loaded += (_, _) => StartWatching();
        Unloaded += (_, _) => { _watcher?.Dispose(); _watcher = null; _debounce.Stop(); };
        Refresh();
    }
    private void StartWatching()
    {
        Directory.CreateDirectory(LocalCards.DirectoryPath);
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(LocalCards.DirectoryPath, "reply-*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, EnableRaisingEvents = true };
        FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
        _watcher.Changed += changed;
        _watcher.Created += changed;
        _watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
        Refresh();
    }
    private void Refresh()
    {
        var selected = _provider.SelectedItem as string ?? "Automático";
        var latest = AgentProviders.All.Where(provider => selected == "Automático" || selected == provider)
            .Select(provider => LocalCards.Read<AgentReply>($"reply-{provider}.json"))
            .Where(reply => reply != null).OrderByDescending(reply => reply!.UpdatedUtc).FirstOrDefault();
        if (latest == null)
        {
            _reply.Text = "Acá aparecerá la última respuesta terminada del proveedor seleccionado.\n\nSolo lectura: esta tarjeta no envía mensajes ni ejecuta las instrucciones de las respuestas.";
            _status.Text = "Esperando una respuesta del proveedor seleccionado.";
        }
        else
        {
            if (_reply.Text != latest.Text) _reply.Text = latest.Text;
            _status.Text = $"{latest.Provider} · {latest.UpdatedUtc.ToLocalTime():dd/MM HH:mm} · guardado localmente";
        }
    }
}
