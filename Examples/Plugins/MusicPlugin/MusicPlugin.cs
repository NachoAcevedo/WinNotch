using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinNotch.Models;
using WinNotch.Plugins;
using WinNotch.Services;
using WinNotch.Views;

namespace PersonalCards;

/// <summary>
/// Ajustes de música de WinNotch, todos en un solo plugin:
/// - Aviso de música: al cambiar de canción o darle play se muestra la portada en lugar del
///   ícono. En pausa (y en el resto de los avisos, como Caps Lock) queda el ícono de WinNotch.
/// </summary>
[WinNotchPlugin("personal.music", "Música", "1.0.0", "Nacho_114")]
public sealed class MusicPlugin : PluginBase
{
    // Íconos que HudOverlay pone en FeedbackIcon: nueva canción (♫) y play (▶).
    private const string NewSongGlyph = "";
    private const string PlayGlyph = "";

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly FieldInfo? HudField = typeof(NotchWindow).GetField("HudOverlay", Flags);
    private static readonly FieldInfo? MediaServiceField = typeof(NotchWindow).GetField("_mediaService", Flags);
    private static readonly FieldInfo? FeedbackIconField = typeof(HudOverlay).GetField("FeedbackIcon", Flags);

    private readonly List<Action> _undo = new();

    public override string Id => "personal.music";
    public override string Name => "Música";
    public override string Version => "1.0.0";
    public override string Author => "Nacho_114";
    public override string Description => "Ajustes de música: portada del disco en el aviso de cambio de canción y de play.";

    public override async Task OnEnableAsync()
    {
        await base.OnEnableAsync();

        if (HudField == null || MediaServiceField == null || FeedbackIconField == null)
        {
            Context?.Log("Música: la versión de WinNotch no es compatible", PluginLogLevel.Error);
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var window in Application.Current.Windows.OfType<NotchWindow>())
            {
                Attach(window);
            }
        });
    }

    public override Task OnDisableAsync()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var undo in _undo)
            {
                undo();
            }
            _undo.Clear();
        });
        return base.OnDisableAsync();
    }

    private void Attach(NotchWindow window)
    {
        if (HudField!.GetValue(window) is not HudOverlay hud
            || FeedbackIconField!.GetValue(hud) is not TextBlock icon
            || icon.Parent is not Border badge
            || MediaServiceField!.GetValue(window) is not MediaService mediaService)
        {
            Context?.Log("Música: no se encontró el aviso de música", PluginLogLevel.Warning);
            return;
        }

        MediaInfo media = mediaService.MediaInfo;
        var originalBackground = badge.Background;
        var originalRadius = badge.CornerRadius;

        void Update()
        {
            var art = media.AlbumArt;
            bool showCover = (icon.Text == NewSongGlyph || icon.Text == PlayGlyph) && art != null;
            icon.Visibility = showCover ? Visibility.Hidden : Visibility.Visible;
            badge.CornerRadius = showCover ? new CornerRadius(6) : originalRadius;
            badge.Background = showCover
                ? new ImageBrush(art) { Stretch = Stretch.UniformToFill }
                : originalBackground;
        }

        var textProperty = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        EventHandler onIcon = (_, _) => Update();
        textProperty.AddValueChanged(icon, onIcon);

        // La portada a veces llega después del aviso: se actualiza si el aviso sigue a la vista.
        PropertyChangedEventHandler onMedia = (_, e) =>
        {
            if (e.PropertyName == nameof(MediaInfo.AlbumArt))
            {
                Application.Current.Dispatcher.BeginInvoke(Update);
            }
        };
        ((INotifyPropertyChanged)(object)media).PropertyChanged += onMedia;
        Update();

        _undo.Add(() =>
        {
            textProperty.RemoveValueChanged(icon, onIcon);
            ((INotifyPropertyChanged)(object)media).PropertyChanged -= onMedia;
            icon.Visibility = Visibility.Visible;
            badge.Background = originalBackground;
            badge.CornerRadius = originalRadius;
        });
    }
}
