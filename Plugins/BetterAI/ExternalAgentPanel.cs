using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinNotch.Helpers;
using WinNotch.Services;

namespace PersonalCards;

internal sealed class ExternalAgentPanel : StackPanel
{
    private readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _description = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 8) };
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 38, MaxHeight = 100, FontSize = 12, Padding = new Thickness(8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _open = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 7, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
    private readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) };
    private readonly TextBox _lastReply = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 160, FontSize = 12, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Visibility = Visibility.Collapsed };
    private readonly AgentComposer _composer;
    private VsCodeReply? _reply;
    private FileSystemWatcher? _watcher;
    private string _mode = "VS Code";
    public ExternalAgentPanel()
    {
        Children.Add(_title); Children.Add(_description); Children.Add(_lastReply);
        _composer = new AgentComposer(() => _reply, status => _status.Text = status);
        Children.Add(_composer);
        Children.Add(_input); Children.Add(_open); Children.Add(_status);
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _description.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        _input.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        _input.SetResourceReference(Control.BackgroundProperty, "SurfaceRaisedBrush");
        _input.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        _lastReply.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        _open.Click += (_, _) => Open();
        _input.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (Window.GetWindow(this) is { } window) WindowHelper.EnableTextInput(window);
            _input.Focus(); Keyboard.Focus(_input); AgentComposer.NotifyActivity();
        };
        _input.TextChanged += (_, _) => AgentComposer.NotifyActivity();
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; Open(); }
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) WatchReplies(); else { _watcher?.Dispose(); _watcher = null; } };
        Unloaded += (_, _) => { _watcher?.Dispose(); _watcher = null; };
    }

    public void SetMode(string mode)
    {
        _watcher?.Dispose();
        _watcher = null;
        _mode = mode;
        string agent = AgentModeSettings.Read().VsCodeAgent;
        _title.Text = mode switch { "Claude" => "Claude Desktop · Code", "Gemini" => "Gemini · Antigravity", _ => "VS Code · " + agent };
        _open.Content = mode == "Claude" ? "Abrir en Claude" : "Abrir en VS Code";
        _description.Text = mode == "Claude"
            ? "Abrí Claude con este texto. El inicio de sesión, el envío y las respuestas se completan en la app."
            : mode == "Gemini" ? "Acá aparece la última respuesta de Antigravity." : agent == "Claude Code"
                ? "Abrí Claude Code con este texto. La última respuesta aparecerá acá al terminar. La primera vez requiere iniciar sesión."
                : "Abrí Codex en VS Code. El texto queda copiado para pegarlo en el chat que elijas.";
        _status.Text = "";
        RefreshReply();
        if (IsVisible) WatchReplies();
    }

    private string ReplyProvider => _mode switch
    {
        "Claude" => AgentProviders.ClaudeDesktop,
        "Gemini" => AgentProviders.Gemini,
        _ => AgentModeSettings.Read().VsCodeAgent == "Claude Code" ? "Claude" : AgentProviders.CodexVsCode
    };

    private void WatchReplies()
    {
        RefreshReply();
        if (_watcher != null) return;
        Directory.CreateDirectory(LocalCards.DirectoryPath);
        _watcher = new FileSystemWatcher(LocalCards.DirectoryPath, $"reply-{ReplyProvider}.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, EnableRaisingEvents = true };
        FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(RefreshReply);
        _watcher.Created += changed; _watcher.Changed += changed;
        _watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(RefreshReply);
    }

    private void RefreshReply()
    {
        var reply = LocalCards.Read<VsCodeReply>($"reply-{ReplyProvider}.json");
        _reply = reply;
        bool canReply = _mode == "VS Code" && reply != null;
        _composer.Visibility = canReply ? Visibility.Visible : Visibility.Collapsed;
        _input.Visibility = canReply || _mode == "Gemini" ? Visibility.Collapsed : Visibility.Visible;
        _open.Visibility = canReply || _mode == "Gemini" ? Visibility.Collapsed : Visibility.Visible;
        _lastReply.Visibility = reply == null ? Visibility.Collapsed : Visibility.Visible;
        if (reply != null && _lastReply.Text != reply.Text) { _lastReply.Text = reply.Text; _lastReply.ScrollToHome(); }
    }

    private void Open()
    {
        try
        {
            string text = _input.Text.Trim();
            string uri;
            if (_mode == "Claude")
            {
                uri = "claude://code/new" + (text.Length == 0 ? "" : "?q=" + Uri.EscapeDataString(text));
                _status.Text = "Abrí Claude con el texto. Completá el inicio de sesión y el envío allí.";
            }
            else if (AgentModeSettings.Read().VsCodeAgent == "Claude Code")
            {
                uri = "vscode://anthropic.claude-code/open" + (text.Length == 0 ? "" : "?prompt=" + Uri.EscapeDataString(text));
                _status.Text = "Abrí Claude Code en VS Code con el texto. Confirmá el envío en la extensión.";
            }
            else
            {
                if (text.Length > 0) Clipboard.SetText(text);
                uri = "vscode://openai.chatgpt/";
                _status.Text = text.Length > 0 ? "Texto copiado. Elegí el chat en VS Code y pegalo para enviarlo." : "Abrí Codex en VS Code.";
            }
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex) { _status.Text = "No pude abrir la aplicación: " + ex.Message; }
    }
}
