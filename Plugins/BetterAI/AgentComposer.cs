using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinNotch.Helpers;
using WinNotch.Services;

namespace PersonalCards;

internal sealed record VsCodeReply(string Provider, string Text, DateTime UpdatedUtc, string? SessionId = null);

/// <summary>
/// Campo para responder a Claude Code o Codex en VS Code desde la tarjeta de IA: texto y capturas
/// pegadas con Ctrl+V. Enter envía (Shift+Enter, salto de línea), Esc cierra sin enviar.
/// Mientras se escribe, el plugin mantiene el notch abierto (ver <see cref="Activity"/>).
/// </summary>
internal sealed class AgentComposer : StackPanel
{
    private const double ThumbSize = 44.0;

    private readonly TextBox _input = new TextBox
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MaxHeight = 90.0,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontSize = 12.0
    };

    private readonly TextBlock _placeholder = new TextBlock
    {
        Text = "Responder en VS Code… (Enter envía · Ctrl+V pega capturas)",
        FontSize = 12.0,
        IsHitTestVisible = false,
        Margin = new Thickness(14.0, 0.0, 0.0, 0.0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly WrapPanel _thumbs = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0.0, 0.0, 0.0, 6.0) };
    private readonly Button _send = new Button { Content = "Enviar", Margin = new Thickness(6.0, 0.0, 0.0, 0.0), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly List<BitmapSource> _images = new();
    private readonly Func<VsCodeReply?> _lastReply;
    private readonly Action<string> _setStatus;
    private VsCodeReply? _draftReply;

    /// <summary>Hubo actividad en el campo (clic, foco, tecleo): el notch tiene que seguir abierto.</summary>
    public static event Action? Activity;
    internal static void NotifyActivity() => Activity?.Invoke();

    /// <summary>Se envió o se canceló con Esc: el notch se puede cerrar.</summary>
    public static event Action? Finished;

    /// <summary>Registro del envío en el log de WinNotch (lo asigna el plugin).</summary>
    public static Action<string>? Log;

    public AgentComposer(Func<VsCodeReply?> lastReply, Action<string> setStatus)
    {
        _lastReply = lastReply;
        _setStatus = setStatus;
        Margin = new Thickness(0.0, 8.0, 0.0, 0.0);

        _input.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");

        var field = new Grid();
        field.Children.Add(_input);
        field.Children.Add(_placeholder);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(field);
        Grid.SetColumn(_send, 1);
        row.Children.Add(_send);

        Children.Add(_thumbs);
        Children.Add(row);

        // El notch es una ventana que no toma el foco: se habilita el teclado al tocar el campo.
        _input.PreviewMouseLeftButtonDown += (_, _) => { Log?.Invoke("clic en el campo"); EnableKeyboard(); };
        _input.GotKeyboardFocus += (_, _) => { UpdatePlaceholder(); Activity?.Invoke(); };
        _input.LostKeyboardFocus += (_, _) => UpdatePlaceholder();
        _input.TextChanged += (_, _) => { UpdateDraftTarget(); UpdatePlaceholder(); Activity?.Invoke(); };
        _input.PreviewKeyDown += OnInputKey;
        _send.Click += (_, _) => Send();
        UpdatePlaceholder();
    }

    private void EnableKeyboard()
    {
        if (Window.GetWindow(this) is { } window)
        {
            WindowHelper.EnableTextInput(window);
        }
        _input.Focus();
        Keyboard.Focus(_input);
        Activity?.Invoke();
    }

    private void UpdatePlaceholder() =>
        _placeholder.Visibility = _input.Text.Length == 0 && !_input.IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        Activity?.Invoke();
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            Send();
        }
        else if (e.Key == Key.Escape)
        {
            Log?.Invoke("Esc");
            e.Handled = true;
            Finished?.Invoke();
        }
        else if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && Clipboard.ContainsImage())
        {
            e.Handled = true;
            if (Clipboard.GetImage() is { } image)
            {
                AddImage(image);
            }
        }
    }

    private void AddImage(BitmapSource image)
    {
        image.Freeze();
        _images.Add(image);
        UpdateDraftTarget();

        var thumb = new Grid { Width = ThumbSize, Height = ThumbSize, Margin = new Thickness(0.0, 0.0, 6.0, 0.0) };
        thumb.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8.0),
            Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill }
        });
        var remove = new Button
        {
            Content = "✕",
            FontSize = 9.0,
            Padding = new Thickness(4.0, 1.0, 4.0, 1.0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0.0, 2.0, 2.0, 0.0)
        };
        remove.Click += (_, _) =>
        {
            _images.Remove(image);
            UpdateDraftTarget();
            _thumbs.Children.Remove(thumb);
            _thumbs.Visibility = _images.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        thumb.Children.Add(remove);
        _thumbs.Children.Add(thumb);
        _thumbs.Visibility = Visibility.Visible;
    }

    private void UpdateDraftTarget()
    {
        if (_input.Text.Length == 0 && _images.Count == 0)
            _draftReply = null;
        else
            _draftReply ??= _lastReply();
    }

    private async void Send()
    {
        if (!_send.IsEnabled) return;
        string text = _input.Text.Trim();
        if (text.Length == 0 && _images.Count == 0) return;

        var reply = _draftReply ?? _lastReply();
        string? session = reply?.SessionId;
        if (session == null && reply?.Provider == "Claude")
            session = ClaudeSessions.FindByReply(reply.Text);
        if (reply == null || session == null)
        {
            _setStatus("No encontré la conversación en VS Code. El borrador sigue disponible.");
            return;
        }

        _send.IsEnabled = false;
        _input.IsReadOnly = true;
        try
        {
            _setStatus("Abriendo la conversación en VS Code…");
            if (!await VsCodeAgent.SendAsync(reply.Provider, session, text, _images.ToArray(), Dispatcher, Log))
            {
                _setStatus("No pude completar el envío en VS Code. Revisá el chat antes de reintentar.");
                return;
            }

            _input.Clear();
            _images.Clear();
            _draftReply = null;
            _thumbs.Children.Clear();
            _thumbs.Visibility = Visibility.Collapsed;
            _setStatus("Envío solicitado en VS Code.");
        }
        finally
        {
            _input.IsReadOnly = false;
            _send.IsEnabled = true;
            Finished?.Invoke();
        }
    }
}

/// <summary>Busca la conversación de Claude Code que produjo una respuesta, por su texto.</summary>
internal static class ClaudeSessions
{
    private static readonly string ProjectsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    private const int TailBytes = 1024 * 1024;

    /// <summary>Id de la conversación que dio la respuesta.</summary>
    public static string? FindByReply(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply) || !Directory.Exists(ProjectsDir))
        {
            return null;
        }

        // Un fragmento sin comillas, barras ni saltos: aparece tal cual dentro del JSON.
        string first = reply.Trim().Split('\n')[0];
        int cut = first.IndexOfAny(new[] { '"', '\\', '\r', '\t' });
        string snippet = (cut >= 0 ? first[..cut] : first).Trim();
        if (snippet.Length > 60)
        {
            snippet = snippet[..60];
        }
        if (snippet.Length < 8)
        {
            return null;
        }

        var recent = new DirectoryInfo(ProjectsDir).EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
            .Where(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-2))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(40);
        foreach (var file in recent)
        {
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(Math.Max(0, stream.Length - TailBytes), SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                if (reader.ReadToEnd().Contains(snippet, StringComparison.Ordinal))
                {
                    return Path.GetFileNameWithoutExtension(file.Name);
                }
            }
            catch (IOException)
            {
            }
        }
        return null;
    }
}

/// <summary>
/// Entrega el mensaje al chat de Claude en VS Code: el enlace de la extensión trae VS Code al
/// frente (y abre la conversación si estaba cerrada); el texto y las capturas se pegan desde el
/// portapapeles en el chat activo y Enter envía.
/// </summary>
internal static class VsCodeAgent
{
    private const byte VkControl = 0x11;
    private const byte VkV = 0x56;
    private const byte VkReturn = 0x0D;
    private const uint KeyUp = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static async Task<bool> SendAsync(string provider, string session, string text, IReadOnlyList<BitmapSource> images,
        System.Windows.Threading.Dispatcher dispatcher, Action<string>? log)
    {
        try
        {
            return await SendCoreAsync(provider, session, text, images, dispatcher, log);
        }
        catch (Exception ex)
        {
            log?.Invoke("error al enviar: " + ex.Message);
            return false;
        }
    }

    private static async Task<bool> SendCoreAsync(string provider, string session, string text, IReadOnlyList<BitmapSource> images,
        System.Windows.Threading.Dispatcher dispatcher, Action<string>? log)
    {
        var uri = provider switch
        {
            "Claude" => "vscode://anthropic.claude-code/open?session=" + Uri.EscapeDataString(session),
            AgentProviders.CodexVsCode => "vscode://openai.chatgpt/local/" + Uri.EscapeDataString(session),
            _ => throw new ArgumentException("Proveedor de VS Code desconocido.", nameof(provider))
        };

        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        log?.Invoke("enlace abierto: " + uri.ToString()[..Math.Min(uri.Length, 120)]);

        // Esperar a que VS Code quede al frente con el chat abierto; si no, no se teclea nada.
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!IsVsCodeInFront())
        {
            if (DateTime.UtcNow > deadline)
            {
                log?.Invoke("VS Code no quedó al frente: no se tecleó nada");
                return false;
            }
            await Task.Delay(100);
        }
        var targetWindow = GetForegroundWindow();
        await Task.Delay(900);
        if (!HasFocus()) return false;

        bool HasFocus() => GetForegroundWindow() == targetWindow && IsVsCodeInFront();

        string? previousText = dispatcher.Invoke(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);


        try
        {
            if (text.Length > 0)
            {
                if (!HasFocus()) return false;
                dispatcher.Invoke(() => Clipboard.SetText(text));
                Press(VkControl, VkV);
                await Task.Delay(300);
            }

            foreach (var image in images)
            {
                if (!HasFocus()) return false;
                dispatcher.Invoke(() => Clipboard.SetImage(image));
                Press(VkControl, VkV);
                await Task.Delay(600);
            }
        }
        finally
        {
            if (previousText != null)
                dispatcher.Invoke(() => Clipboard.SetText(previousText));
        }

        if (HasFocus())
        {
            Press(VkReturn);
            log?.Invoke("Enter enviado en VS Code");
            return true;
        }

        return false;
    }

    private static void Press(params byte[] keys)
    {
        foreach (var key in keys)
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
        }
        foreach (var key in keys.Reverse())
        {
            keybd_event(key, 0, KeyUp, UIntPtr.Zero);
        }
    }

    private static bool IsVsCodeInFront()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.StartsWith("Code", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
