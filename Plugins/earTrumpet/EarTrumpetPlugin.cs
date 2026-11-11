using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using WinNotch.Plugins;

namespace PersonalCards.Audio;

[WinNotchPlugin("personal.card.eartrumpet", "earTrumpet", "1.0.3", "Nacho_114")]
public sealed class EarTrumpetPlugin : CardPluginBase
{
    public override string Id => "personal.card.eartrumpet";
    public override string Name => "earTrumpet";
    public override string Version => "1.0.3";
    public override string Author => "Nacho_114";
    public override string Description => "Control de volumen general, volumen por aplicación y salidas de audio.";
    public override string CardKey => "eartrumpet";
    public override string CardTitle => "Audio";
    public override int CardOrder => 65;
    public override double CardHeight => 285;
    protected override UIElement CreateCard() => new AudioMixer();
}

internal sealed class AudioMixer : StackPanel
{
    private sealed record Output(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly ComboBox _defaultOutput = new() { DisplayMemberPath = nameof(Output.Name), MinWidth = 150, MinHeight = 28 };
    private readonly StackPanel _levels = new();
    private readonly StackPanel _apps = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private StackPanel? _openRoutes;
    private readonly List<MMDevice> _devices = new();
    private readonly List<AudioSessionControl> _sessions = new();
    private MMDeviceEnumerator? _enumerator;
    private bool _refreshing;

    public AudioMixer()
    {
        var heading = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var refresh = new Button { Content = "Actualizar", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
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
        Children.Add(SectionLabel("Salida principal"));
        Children.Add(heading);
        Children.Add(_levels);
        Children.Add(SectionLabel("Aplicaciones", 10));
        Children.Add(new ScrollViewer { Content = _apps, MaxHeight = 175, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _status.Margin = new Thickness(0, 5, 0, 0);
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
                        _sessions.Add(session);
                }
            }

            var routes = new[] { new Output(null, "Salida predeterminada") }.Concat(outputs).ToArray();
            foreach (var app in _sessions.GroupBy(session => session.GetProcessID).Where(app => app.Key > 0))
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

    private UIElement AppRow(uint processId, AudioSessionControl[] sessions, Output[] routes)
    {
        var row = new StackPanel { Margin = new Thickness(0, 6, 0, 2) };
        var label = new TextBlock { Text = ProcessName(processId), FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var route = new Button { Content = "Salida…", Margin = new Thickness(8, 0, 0, 0) };
        var choices = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var output in routes)
        {
            var choice = new Button
            {
                Content = output.Name, HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 3)
            };
            choice.Click += (_, _) =>
            {
                if (!Change(() => WindowsAudioRouting.SetApplicationOutput(processId, output.Id))) return;
                ToggleRoutes(choices);
                _status.Text = "Salida solicitada. Algunas apps aplican el cambio cuando reinician su audio.";
            };
            choices.Children.Add(choice);
        }
        route.Click += (_, _) => ToggleRoutes(choices);
        var header = new DockPanel();
        DockPanel.SetDock(route, Dock.Right);
        header.Children.Add(route);
        header.Children.Add(label);
        row.Children.Add(header);

        var volume = sessions[0].SimpleAudioVolume;
        row.Children.Add(VolumeControls(volume.Volume, volume.Mute,
            value => Array.ForEach(sessions, session => session.SimpleAudioVolume.Volume = value),
            mute => Array.ForEach(sessions, session => session.SimpleAudioVolume.Mute = mute)));
        row.Children.Add(choices);
        var divider = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 6), Child = row };
        divider.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return divider;
    }

    private void ToggleRoutes(StackPanel choices)
    {
        bool opening = _openRoutes != choices;
        if (_openRoutes != null) _openRoutes.Visibility = Visibility.Collapsed;
        _openRoutes = opening ? choices : null;
        if (opening)
        {
            choices.Visibility = Visibility.Visible;
            choices.BringIntoView();
        }
    }

    private static TextBlock SectionLabel(string text, double top = 0)
    {
        var label = new TextBlock
        {
            Text = text, Visibility = Visibility.Hidden, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, top, 0, 2)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return label;
    }

    private DockPanel VolumeControls(float volume, bool muted, Action<float> setVolume, Action<bool> setMute)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var mute = new CheckBox { Content = "Silenciar", IsChecked = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        mute.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        mute.Click += (_, _) => Change(() => setMute(mute.IsChecked == true));
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = volume * 100, MinWidth = 55, ToolTip = "Volumen" };
        slider.ValueChanged += (_, args) => Change(() => setVolume((float)(args.NewValue / 100)));
        var percentage = new TextBlock { Width = 38, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        percentage.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        percentage.SetBinding(TextBlock.TextProperty, new Binding(nameof(Slider.Value))
        {
            Source = slider, StringFormat = "{0:0}%"
        });
        DockPanel.SetDock(percentage, Dock.Right);
        row.Children.Add(mute);
        row.Children.Add(percentage);
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
        _openRoutes = null;
        _apps.Children.Clear();
        foreach (var session in _sessions) session.Dispose();
        _sessions.Clear();
        foreach (var device in _devices) device.Dispose();
        _devices.Clear();
        _enumerator?.Dispose();
        _enumerator = null;
    }
}
