using System.IO;
using System.Windows;
using System.Windows.Controls;
using WinNotch.Services;

namespace PersonalCards;

internal sealed record AgentModeSettings(bool Codex = true, bool VsCode = true, bool Claude = true, string SelectedMode = "Codex", string VsCodeAgent = "Codex", bool Gemini = true)
{
    public static AgentModeSettings Read() => LocalCards.Read<AgentModeSettings>("agent-mode-settings.json") ?? new();
    public bool Enabled(string mode) => mode switch { "Codex" => Codex, "VS Code" => VsCode, "Claude" => Claude, "Gemini" => Gemini, _ => false };
    public string[] Modes => new[] { "Codex", "VS Code", "Claude", "Gemini" }.Where(Enabled).ToArray();
}

internal sealed class AgentModeSettingsView : StackPanel
{
    public event Action? Saved;
    private readonly CheckBox _codex = new() { Content = "Codex", Margin = new Thickness(0, 9, 0, 0) };
    private readonly CheckBox _vscode = new() { Content = "VS Code", Margin = new Thickness(0, 9, 0, 0) };
    private readonly CheckBox _claude = new() { Content = "Claude (app)", Margin = new Thickness(0, 9, 0, 0) };
    private readonly CheckBox _gemini = new() { Content = "Gemini (Antigravity)", Margin = new Thickness(0, 9, 0, 0) };
    private readonly ComboBox _vscodeAgent = new() { ItemsSource = new[] { "Codex", "Claude Code" }, Margin = new Thickness(22, 5, 0, 0), MaxWidth = 180, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _message = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    public AgentModeSettingsView()
    {
        Children.Add(new TextBlock { Text = "Modos de IA", FontSize = 14, FontWeight = FontWeights.SemiBold });
        Children.Add(_codex); Children.Add(_vscode); Children.Add(_vscodeAgent); Children.Add(_claude); Children.Add(_gemini); Children.Add(_message);
        foreach (var control in new Control[] { _codex, _vscode, _claude, _gemini }) control.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _message.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        CodexPanel.StylePicker(_vscodeAgent);
        var done = new Button { Content = "Guardar", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 5, 12, 5) };
        done.Click += (_, _) =>
        {
            if (_codex.IsChecked != true && _vscode.IsChecked != true && _claude.IsChecked != true && _gemini.IsChecked != true) { _message.Text = "Dejá al menos un modo habilitado."; return; }
            var current = AgentModeSettings.Read();
            var settings = new AgentModeSettings(_codex.IsChecked == true, _vscode.IsChecked == true, _claude.IsChecked == true, current.SelectedMode, _vscodeAgent.SelectedItem as string ?? "Codex", _gemini.IsChecked == true);
            if (!settings.Enabled(settings.SelectedMode)) settings = settings with { SelectedMode = settings.Modes[0] };
            try { LocalCards.Write("agent-mode-settings.json", settings); Saved?.Invoke(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _message.Text = "No pude guardar: " + ex.Message; }
        };
        Children.Add(done);
    }

    public void Reload()
    {
        var settings = AgentModeSettings.Read();
        _codex.IsChecked = settings.Codex; _vscode.IsChecked = settings.VsCode; _claude.IsChecked = settings.Claude;
        _gemini.IsChecked = settings.Gemini;
        _vscodeAgent.SelectedItem = settings.VsCodeAgent;
        _message.Text = "Cada modo se puede activar por separado. Las cuentas se inician en sus propias aplicaciones.";
    }
}
