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

    private readonly DockPanel _heading = new() { Margin = new Thickness(0, 4, 0, 0) };
    private Button? _outputTitle;
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
        var refresh = new Button { Content = "Actualizar", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
        refresh.Click += (_, _) => Refresh();
        DockPanel.SetDock(refresh, Dock.Right);
        _heading.Children.Add(refresh);

        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        Children.Add(SectionLabel("Salida principal"));
        Children.Add(_heading);
        Children.Add(_levels);
        var divider = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 6) };
        divider.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        Children.Add(divider);
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
            if (_outputTitle != null) _heading.Children.Remove(_outputTitle);
            _outputTitle = null;
            if (!keepStatus) _status.Text = "";
            _enumerator = new MMDeviceEnumerator();
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                _devices.Add(device);

            var outputs = _devices.Select(device => new Output(device.ID, device.FriendlyName)).ToArray();
            if (outputs.Length == 0)
            {
                _status.Text = "No hay salidas de audio disponibles.";
                return;
            }

            using var current = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            string outputId = current.ID;
            var choices = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
            _outputTitle = CreateRouteTitle(current.FriendlyName, "Elegir salida principal de Windows", choices);
            _heading.Children.Add(_outputTitle);
            foreach (var output in outputs)
            {
                var choice = new Button
                {
                    Content = output.Id == outputId ? $"✓  {output.Name}" : output.Name,
                    HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 3)
                };
                choice.Click += (_, _) =>
                {
                    if (output.Id is { } id && Change(() => WindowsAudioRouting.SetSystemOutput(id)))
                        Refresh(keepStatus: true);
                };
                choices.Children.Add(choice);
            }
            var master = _devices.FirstOrDefault(device => device.ID == current.ID)?.AudioEndpointVolume;
            if (master != null)
                _levels.Children.Add(VolumeControls(master.MasterVolumeLevelScalar, master.Mute,
                    volume => master.MasterVolumeLevelScalar = volume, mute => master.Mute = mute));
            _levels.Children.Add(choices);

            var sessionOutputs = new Dictionary<AudioSessionControl, string>();
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
                    {
                        _sessions.Add(session);
                        sessionOutputs.Add(session, device.ID);
                    }
                }
            }

            var routes = new[] { new Output(null, "Salida predeterminada") }.Concat(outputs).ToArray();
            foreach (var app in _sessions.GroupBy(session => session.GetProcessID).Where(app => app.Key > 0))
            {
                var activeOutputs = app.Where(session => session.State == AudioSessionState.AudioSessionStateActive)
                    .Select(session => sessionOutputs[session]).Distinct().ToArray();
                _apps.Children.Add(AppRow(app.Key, app.ToArray(), routes, activeOutputs));
            }

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

    private UIElement AppRow(uint processId, AudioSessionControl[] sessions, Output[] routes, string[] activeOutputs)
    {
        var row = new StackPanel { Margin = new Thickness(0, 6, 0, 2) };
        var choices = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var output in routes)
        {
            var choice = new Button
            {
                Content = activeOutputs.Contains(output.Id) ? $"✓  {output.Name}" : output.Name, HorizontalContentAlignment = HorizontalAlignment.Left,
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
        string name = ProcessName(processId);
        var title = CreateRouteTitle(name, $"Elegir salida de audio de {name}", choices);
        var controlsRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var volume = sessions[0].SimpleAudioVolume;
        controlsRow.Children.Add(VolumeControls(volume.Volume, volume.Mute,
            value => Array.ForEach(sessions, session => session.SimpleAudioVolume.Volume = value),
            mute => Array.ForEach(sessions, session => session.SimpleAudioVolume.Mute = mute)));
        row.Children.Add(title);
        row.Children.Add(controlsRow);
        row.Children.Add(choices);
        var divider = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 6), Child = row };
        divider.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return divider;
    }

    private Button CreateRouteTitle(string name, string accessibleName, StackPanel choices)
    {
        var label = new TextBlock
        {
            Text = name, FontSize = 15, FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        };
        var arrowDown = System.Windows.Media.Geometry.Parse("M 2,4 L 6,8 L 10,4");
        var arrowUp = System.Windows.Media.Geometry.Parse("M 2,8 L 6,4 L 10,8");
        var arrow = new System.Windows.Shapes.Path
        {
            Data = arrowDown, Width = 12, Height = 12, StrokeThickness = 1.5,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0)
        };
        arrow.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondaryBrush");
        var heading = new DockPanel();
        DockPanel.SetDock(arrow, Dock.Right);
        heading.Children.Add(arrow);
        heading.Children.Add(label);
        var title = new Button
        {
            Content = heading, Padding = new Thickness(2, 3, 2, 3),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent,
            ToolTip = accessibleName
        };
        System.Windows.Automation.AutomationProperties.SetName(title, accessibleName);
        choices.IsVisibleChanged += (_, _) => arrow.Data = choices.IsVisible ? arrowUp : arrowDown;
        title.Click += (_, _) => ToggleRoutes(choices);
        return title;
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
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = 0, Maximum = 100, MinWidth = 55, IsMoveToPointEnabled = true, ToolTip = "Volumen" };
        bool updatingSlider = false;
        void ShowVolume(bool animate = false)
        {
            double from = slider.Value;
            double target = muted ? 0 : volume * 100;
            updatingSlider = true;
            slider.BeginAnimation(Slider.ValueProperty, null);
            slider.Value = target;
            slider.IsEnabled = !muted && !animate;

            if (!animate || !SystemParameters.ClientAreaAnimation || Math.Abs(from - target) < 0.01)
            {
                slider.IsEnabled = !muted;
                updatingSlider = false;
                return;
            }

            var transition = new System.Windows.Media.Animation.DoubleAnimation(from, target,
                TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                },
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
            };
            transition.Completed += (_, _) =>
            {
                slider.IsEnabled = !muted;
                updatingSlider = false;
            };
            slider.BeginAnimation(Slider.ValueProperty, transition);
        }

        ShowVolume();
        slider.ValueChanged += (_, args) =>
        {
            if (updatingSlider || !slider.IsEnabled) return;
            var next = (float)(args.NewValue / 100);
            if (Change(() => setVolume(next))) volume = next;
            else ShowVolume();
        };

        var mute = new Button
        {
            Width = 30, Height = 28, Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        const string speakerShape = "M 2,6 H 5 L 9,3 V 13 L 5,10 H 2 Z";
        var audibleSpeaker = System.Windows.Media.Geometry.Parse(speakerShape + " M 11.5,5 Q 15,8 11.5,11");
        var mutedSpeaker = System.Windows.Media.Geometry.Parse(speakerShape + " M 11.5,6 L 15,10 M 15,6 L 11.5,10");
        var icon = new System.Windows.Shapes.Path
        {
            Width = 16, Height = 16,
            StrokeThickness = 1.5,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round
        };
        icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = mute });
        mute.Content = icon;
        void UpdateSpeaker()
        {
            icon.Data = muted ? mutedSpeaker : audibleSpeaker;
            mute.ToolTip = muted ? "Activar sonido" : "Silenciar";
            System.Windows.Automation.AutomationProperties.SetName(mute, (string)mute.ToolTip);
        }
        UpdateSpeaker();
        mute.Click += (_, _) =>
        {
            if (!Change(() => setMute(!muted))) return;
            muted = !muted;
            ShowVolume(animate: true);
            UpdateSpeaker();
        };
        mute.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");

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
