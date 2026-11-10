using System.Windows;
using WinNotch.Plugins;

namespace PersonalCards;

/// <summary>
/// Reordena la pantalla de ajustes integrada de WinNotch:
/// - "Notch mínimo siempre visible" pasa a APPEARANCE, debajo de "Show shadow".
/// - "Avisos de Caps Lock" pasa a SYSTEM HUD, debajo de Brightness HUD.
/// - Las tarjetas (clima, tareas, IA, archivos) quedan en su propia sección TARJETAS.
/// Los ajustes en sí no cambian: solo se mueven las filas de lugar.
/// </summary>
[WinNotchPlugin("personal.settings.layout", "Ajustes reordenados", "1.0.0", "Personal")]
public sealed class SettingsLayoutPlugin : PluginBase
{
    public override string Id => "personal.settings.layout";
    public override string Name => "Ajustes reordenados";
    public override string Version => "1.0.0";
    public override string Author => "Personal";
    public override string Description => "Reubica Notch mínimo en Appearance, Caps Lock en System HUD y separa las tarjetas en su sección.";

    public override async Task OnEnableAsync()
    {
        await base.OnEnableAsync();
        Application.Current.Dispatcher.Invoke(Apply);
    }

    private void Apply()
    {
        var views = SettingsDom.FindSettingsViews();
        if (views.Count == 0)
        {
            Context?.Log("Ajustes reordenados: no se encontró la pantalla de ajustes", PluginLogLevel.Warning);
            return;
        }

        foreach (var view in views)
        {
            foreach (var name in new[] { "AlwaysShowMinimumCheck", "ShowShadowCheck", "CapsLockFeedbackCheck", "VolumeHudCheck", "BrightnessHudCheck" })
            {
                Context?.Log("Ajustes reordenados: " + SettingsDom.DescribeParents(view, name), PluginLogLevel.Debug);
            }

            bool minimum = SettingsDom.MoveAfter(view, "AlwaysShowMinimumCheck", "ShowShadowCheck");

            // WinNotch trae la sección SYSTEM HUD (volumen, brillo) seguida de las tarjetas sin
            // título propio: Caps Lock pasa debajo de brillo y las tarjetas van en su sección.
            bool capsLock = SettingsDom.MoveAfter(view, "CapsLockFeedbackCheck", "BrightnessHudCheck");
            bool cards = SettingsDom.InsertHeaderBefore(view, "ShowWeatherCheck", "TARJETAS", "personal.cards.header");

            Context?.Log($"Ajustes reordenados: notch mínimo={minimum}, caps lock={capsLock}, sección tarjetas={cards}");
        }
    }
}
