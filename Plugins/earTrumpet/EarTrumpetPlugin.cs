using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using WinNotch.Plugins;

namespace PersonalCards.Audio;

[WinNotchPlugin("personal.card.eartrumpet", "earTrumpet", "1.0.0", "Nacho_114")]
public sealed class EarTrumpetPlugin : CardPluginBase
{
    public override string Id => "personal.card.eartrumpet";
    public override string Name => "earTrumpet";
    public override string Author => "Nacho_114";
    public override string Description => "Salidas de audio y mezclador por aplicación.";
    public override string CardKey => "eartrumpet";
    public override string CardTitle => "Audio";
    public override int CardOrder => 65;
    public override double CardHeight => 285;
    protected override UIElement CreateCard() => new AudioMixer();
}

internal sealed class AudioMixer : StackPanel
{
    private sealed record Output(string? Id, string Name);
    private sealed record Session(AudioSessionControl Control, string OutputName);

    private readonly ComboBox _defaultOutput = new() { DisplayMemberPath = nameof(Output.Name), MinWidth = 150 };
    private readonly StackPanel _levels = new();
    private readonly StackPanel _apps = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly List<MMDevice> _devices = new();
    private readonly List<Session> _sessions = new();
    private MMDeviceEnumerator? _enumerator;
    private bool _refreshing;

    public AudioMixer()
    {
        var heading = new DockPanel();
        var refresh = new Button { Content = "Actualizar", Margin = new Thickness(8, 0, 0, 0) };
        refresh.Click += (_, _) => Refresh();
        DockPanel.SetDock(refresh, Dock.Right);
        heading.Children.Add(refresh);
        heading.Children.Add(_defaultOutput);
        _defaultOutput.ToolTip = "Salida principal de Windows";
        _defaultOutput.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _defaultOutput.SelectedItem is not Output { Id: { } id }) return;
            Change(() => WindowsAudioRouting.SetSystemOutput(id));
            Refresh(keepStatus: true);
        };

        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        Children.Add(heading);
        Children.Add(_levels);
        Children.Add(new ScrollViewer { Content = _apps, MaxHeight = 175, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Children.Add(_status);
        Loaded += (_, _) => { if (IsVisible) Refresh(); };
        Unloaded += (_, _) => ReleaseAudio();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && IsLoaded) Refresh();
            else ReleaseAudio();
        };
    }

    private void Refresh(bool keepStatus = false)
    {
        _refreshing = true;
        try
        {
            ReleaseAudio();
            _defaultOutput.ItemsSource = null;
            if (!keepStatus) _status.Text = "";
            _enumerator = new MMDeviceEnumerator();
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                _devices.Add(device);

            var outputs = _devices.Select(device => new Output(device.ID, device.FriendlyName)).ToArray();
            _defaultOutput.ItemsSource = outputs;
            if (outputs.Length == 0)
            {
                _status.Text = "No hay salidas de audio disponibles.";
                return;
            }

            using var current = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _defaultOutput.SelectedItem = outputs.FirstOrDefault(output => output.Id == current.ID);
            var master = _devices.FirstOrDefault(device => device.ID == current.ID)?.AudioEndpointVolume;
            if (master != null)
                _levels.Children.Add(VolumeControls(master.MasterVolumeLevelScalar, master.Mute,
                    volume => master.MasterVolumeLevelScalar = volume, mute => master.Mute = mute));

            foreach (var device in _devices)
            {
                var sessions = device.AudioSessionManager.Sessions;
                int count = sessions.Count;
                for (int index = 0; index < count; index++)
                {
                    var session = sessions[index];
                    if (session.State == AudioSessionState.AudioSessionStateExpired || session.IsSystemSoundsSession)
                        session.Dispose();
                    else
                        _sessions.Add(new Session(session, device.FriendlyName));
                }
            }

            var routes = new[] { new Output(null, "Salida predeterminada") }.Concat(outputs).ToArray();
            foreach (var app in _sessions.GroupBy(session => session.Control.GetProcessID).Where(app => app.Key > 0))
                _apps.Children.Add(AppRow(app.Key, app.ToArray(), routes));

            if (_apps.Children.Count == 0 && string.IsNullOrEmpty(_status.Text))
                _status.Text = "No hay aplicaciones con una sesión de audio.";
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        {
            ReleaseAudio();
            _status.Text = "No pude leer el audio de Windows. Probá actualizar.";
        }
        finally { _refreshing = false; }
    }

    private UIElement AppRow(uint processId, Session[] sessions, Output[] routes)
    {
        var first = sessions[0].Control.SimpleAudioVolume;
        var outputs = string.Join(", ", sessions.Select(session => session.OutputName).Distinct());
        var row = new StackPanel { Margin = new Thickness(0, 6, 0, 2) };
        var label = new TextBlock { Text = ProcessName(processId) + " · " + outputs, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        row.Children.Add(label);

        var route = new Button { Content = "Salida…", Margin = new Thickness(8, 0, 0, 0) };
        var menu = new ContextMenu { PlacementTarget = route };
        foreach (var output in routes)
        {
            var choice = new MenuItem { Header = output.Name };
            choice.Click += (_, _) =>
            {
                if (Change(() => WindowsAudioRouting.SetApplicationOutput(processId, output.Id)))
                    _status.Text = "Salida solicitada. Algunas apps aplican el cambio cuando reinician su audio.";
            };
            menu.Items.Add(choice);
        }
        route.ContextMenu = menu;
        route.Click += (_, _) => menu.IsOpen = true;
        var controls = VolumeControls(first.Volume, first.Mute,
            volume => Array.ForEach(sessions, session => session.Control.SimpleAudioVolume.Volume = volume),
            mute => Array.ForEach(sessions, session => session.Control.SimpleAudioVolume.Mute = mute));
        DockPanel.SetDock(route, Dock.Right);
        controls.Children.Insert(0, route);
        row.Children.Add(controls);
        return row;
    }

    private DockPanel VolumeControls(float volume, bool muted, Action<float> setVolume, Action<bool> setMute)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var mute = new CheckBox { Content = "Silenciar", IsChecked = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        mute.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        mute.Click += (_, _) => Change(() => setMute(mute.IsChecked == true));
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = volume * 100, MinWidth = 55, ToolTip = "Volumen" };
        slider.ValueChanged += (_, args) => Change(() => setVolume((float)(args.NewValue / 100)));
        row.Children.Add(mute);
        row.Children.Add(slider);
        return row;
    }

    private bool Change(Action action)
    {
        try
        {
            action();
            _status.Text = "";
            return true;
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        {
            _status.Text = "Windows no pudo aplicar el cambio. Probá actualizar.";
            return false;
        }
    }

    private static string ProcessName(uint id)
    {
        try
        {
            using var process = Process.GetProcessById((int)id);
            return process.ProcessName;
        }
        catch (ArgumentException) { return $"Aplicación {id}"; }
        catch (InvalidOperationException) { return $"Aplicación {id}"; }
    }

    private void ReleaseAudio()
    {
        _levels.Children.Clear();
        _apps.Children.Clear();
        foreach (var session in _sessions) session.Control.Dispose();
        _sessions.Clear();
        foreach (var device in _devices) device.Dispose();
        _devices.Clear();
        _enumerator?.Dispose();
        _enumerator = null;
    }
}
