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
/// Ajustes de media de WinNotch, todos en un solo plugin:
/// - Aviso de media: al cambiar de canción o darle play se muestra la portada en lugar del
///   ícono. En pausa (y en el resto de los avisos, como Caps Lock) queda el ícono de WinNotch.
/// </summary>
[WinNotchPlugin("personal.media", "Media", "1.1.3", "Nacho_114")]
public sealed class MediaPlugin : PluginBase
{
    // Íconos que HudOverlay pone en FeedbackIcon: siguiente canción (♫) y play (▶).
    private const string NewSongGlyph = "";
    private const string PlayGlyph = "";

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly FieldInfo? HudField = typeof(NotchWindow).GetField("HudOverlay", Flags);
    private static readonly FieldInfo? MediaServiceField = typeof(NotchWindow).GetField("_mediaService", Flags);
    private static readonly FieldInfo? FeedbackIconField = typeof(HudOverlay).GetField("FeedbackIcon", Flags);

    private static readonly FieldInfo? FeedbackDetailField = typeof(HudOverlay).GetField("FeedbackDetail", Flags);
    private readonly List<Action> _undo = new();

    public override string Id => "personal.media";
    public override string Name => "Media";
    public override string Version => "1.1.3";
    public override string Author => "Nacho_114";
    public override string Description => "Controles multimedia y avisos con portada, título y artista.";

    public override async Task OnEnableAsync()
    {
        await base.OnEnableAsync();

        if (HudField == null || MediaServiceField == null || FeedbackIconField == null)
        {
            Context?.Log("Media: la versión de WinNotch no es compatible", PluginLogLevel.Error);
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
        if (typeof(NotchWindow).GetField("MusicCardTab", Flags)?.GetValue(window) is TabItem mediaTab)
        {
            var originalHeader = mediaTab.Header;
            mediaTab.SetCurrentValue(HeaderedContentControl.HeaderProperty, "Media");
            _undo.Add(() => mediaTab.SetCurrentValue(HeaderedContentControl.HeaderProperty, originalHeader));
            Context?.Log("Media: título de pestaña actualizado a Media", PluginLogLevel.Info);
        }
        else
        {
            Context?.Log("Media: no se encontró la pestaña Música", PluginLogLevel.Warning);
        }
        if (HudField!.GetValue(window) is not HudOverlay hud
            || FeedbackIconField!.GetValue(hud) is not TextBlock icon
            || icon.Parent is not Border badge
            || MediaServiceField!.GetValue(window) is not MediaService mediaService)
        {
            Context?.Log("Media: no se encontró el aviso de media", PluginLogLevel.Warning);
            return;
        }

        MediaInfo media = mediaService.MediaInfo;
        if (typeof(NotchWindow).GetField("MusicPlayer", Flags)?.GetValue(window) is FrameworkElement player
            && (player.FindName("AlbumArtGrid") ?? player.FindName("AlbumArtImage")) is FrameworkElement cover)
        {
            _undo.Add(MediaAppLauncher.Bind(cover, mediaService, message => Context?.Log(message, PluginLogLevel.Warning)));
            Context?.Log("Media: portada vinculada a la app multimedia activa", PluginLogLevel.Info);
        }
        _undo.Add(MediaAppLauncher.Bind(badge, mediaService, message => Context?.Log(message, PluginLogLevel.Warning)));
        if (FeedbackDetailField?.GetValue(hud) is TextBlock detail)
        {
            var detailProperty = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            EventHandler onDetail = (_, _) =>
            {
                bool isMedia = icon.Text == NewSongGlyph || icon.Text == PlayGlyph || icon.Text == "\uE769";
                string artist = media.Artist ?? string.Empty;
                if (isMedia && detail.Text != artist)
                    detail.Text = artist;
            };
            detailProperty.AddValueChanged(detail, onDetail);
            _undo.Add(() => detailProperty.RemoveValueChanged(detail, onDetail));
        }
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
