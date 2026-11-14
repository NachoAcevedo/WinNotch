using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinNotch.Helpers;
using WinNotch.Services;

namespace PersonalCards;

internal sealed class CodexPanel : StackPanel
{
    private const int MaxVisibleChats = 5;
    private readonly ICodexConnection _connection;
    private readonly bool _persistSettings;
    // Nombre del agente en los textos, modo de la tarjeta al que pertenece y archivo de preferencias.
    private readonly string _agent;
    private readonly string _mode;
    private readonly string _settingsFile;
    // Todas las tarjetas de IA muestran solo los chats más recientes.
    private readonly int _chatLimit;
    private readonly ComboBox _chats = new() { MinWidth = 170, MaxWidth = 410, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly MenuItem _refresh = new() { Header = "Actualizar chats" };
    private readonly MenuItem _keep = new() { Header = "Mantener abierto al responder", IsCheckable = true };
    private readonly Button _options = new() { Content = "⋯", ToolTip = "Opciones de IA", FontSize = 21, Width = 30, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };
    private static readonly CodexChat NewChatChoice = new("", "+ Nuevo chat", null);
    private readonly Button _attach = new() { Content = new TextBlock { Text = "\uE723", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16 }, ToolTip = "Adjuntar fotos · Ctrl+V para pegar una imagen", Width = 30, Margin = new Thickness(3, 0, 0, 0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly WrapPanel _photos = new() { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly List<PhotoAttachment> _attachments = new();
    public event Action<string>? ModeRequested;
    public event Action? SettingsRequested;
    private readonly TextBox _conversation = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MaxHeight = 200, MinHeight = 24, FontSize = 12, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Margin = new Thickness(0, 4, 0, 1)
    };
    private readonly TextBox _input = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 36, MaxHeight = 80,
        FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly Button _send = new() { Content = "↑", ToolTip = "Enviar · Enter", FontSize = 18, Width = 28, Height = 28, Margin = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };
    private readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(4) };
    private CodexPreferences _preferences;
    private CancellationTokenSource? _lifetime;
    private bool _reading, _sending, _loading, _busy;
    private int _selectionVersion;
    private string? _previousConversation;
    private string? _pendingText;
    private string? _pendingThread;
    private DateTime _sentUtc;
    private string? _lastSnapshotStamp;
    private DateTime _lastQuestionCheckUtc;
    private string? _pendingQuestion;
    private bool _newChat, _pickingImages;
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff" };

    // Shared with the host plugin so the notch's auto-close timer respects the preference.
    public static bool HoldOpen { get; private set; }
    public static event Action? HoldChanged;
    public static event Action? HideRequested;
    // Hay un panel por agente: solo el que pidió mantener abierto el notch puede soltarlo.
    private static CodexPanel? _holder;

    public CodexPanel() : this(new CodexConnection()) { }

    internal CodexPanel(ICodexConnection connection, bool persistSettings = true, string agent = "Codex", string cardMode = "Codex", string settingsFile = "codex-plugin-settings.json", int chatLimit = 5)
    {
        _connection = connection;
        _persistSettings = persistSettings;
        _agent = agent;
        _mode = cardMode;
        _settingsFile = settingsFile;
        _chatLimit = Math.Clamp(chatLimit, 1, MaxVisibleChats);
        _preferences = LocalCards.Read<CodexPreferences>(settingsFile) ?? new();
        _input.ToolTip = $"Escribí a {agent}. Enter envía; Shift+Enter agrega una línea.";
        _status.Text = $"Conectando con {agent}…";
        var picker = new Grid();
        picker.ColumnDefinitions.Add(new ColumnDefinition());
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _chats.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _chats.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="Auto"/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Text="{Binding Title}" TextTrimming="CharacterEllipsis" ToolTip="{Binding Title}"/>
                    <TextBlock Grid.Column="1" Text="{Binding Status}" Margin="8,0,0,0"
                               Foreground="{DynamicResource TextMutedBrush}" FontSize="10"/>
                </Grid>
            </DataTemplate>
            """);
        picker.Children.Add(_chats);
        Grid.SetColumn(_options, 1); picker.Children.Add(_options);
        var options = new ContextMenu { PlacementTarget = _options, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        options.SetResourceReference(Control.BackgroundProperty, "SurfaceRaisedBrush");
        options.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        options.Resources[typeof(MenuItem)] = OptionsItemStyle();
        _options.ContextMenu = options;
        options.Items.Add(_keep); options.Items.Add(_refresh);
        var settingsItem = new MenuItem { Header = "Configurar modos…" };
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();
        options.Items.Add(settingsItem);
        options.Items.Add(new Separator());
        var modeItems = new Dictionary<string, MenuItem>();
        foreach (string mode in new[] { "Codex", "VS Code", "Claude" })
        {
            var item = new MenuItem { Header = "Usar " + mode, IsCheckable = true, IsChecked = mode == _mode, IsEnabled = mode != _mode };
            item.Click += (_, _) => ModeRequested?.Invoke(mode);
            options.Items.Add(item);
            modeItems[mode] = item;
        }
        options.Opened += (_, _) => { var settings = AgentModeSettings.Read(); foreach (var pair in modeItems) pair.Value.Visibility = settings.Enabled(pair.Key) ? Visibility.Visible : Visibility.Collapsed; };
        _options.Click += (_, _) => { AgentComposer.NotifyActivity(); options.IsOpen = true; };
        Children.Add(picker); Children.Add(_conversation); Children.Add(_photos);
        var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var field = new Grid();
        field.Children.Add(_input);
        var placeholder = new TextBlock { Text = $"Escribí a {_agent}…", FontSize = _input.FontSize, FontFamily = _input.FontFamily, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0) };
        placeholder.SetBinding(TextBlock.FontFamilyProperty, new System.Windows.Data.Binding(nameof(Control.FontFamily)) { Source = _input });
        placeholder.SetBinding(TextBlock.FontWeightProperty, new System.Windows.Data.Binding(nameof(Control.FontWeight)) { Source = _input });
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        field.Children.Add(placeholder);
        row.Children.Add(_attach);
        Grid.SetColumn(field, 1); row.Children.Add(field); Grid.SetColumn(_send, 2); row.Children.Add(_send);
        var inputBorder = new Border { CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Child = row };
        inputBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");
        inputBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        row.Margin = new Thickness(0);
        inputBorder.Margin = new Thickness(0, 4, 0, 0);
        Children.Add(inputBorder); Children.Add(_status);
        foreach (var control in new Control[] { _input, _conversation, _chats, _keep, _options })
            control.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        _input.Background = System.Windows.Media.Brushes.Transparent;
        // Isolate the editor from the host theme: focus belongs to the single outer border.
        _input.Style = (Style)System.Windows.Markup.XamlReader.Parse("""
          <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TextBox">
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
              <ScrollViewer x:Name="PART_ContentHost" Focusable="False" Background="Transparent"/>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          """);
        _input.BorderThickness = new Thickness(0);
        _input.Padding = new Thickness(9, 8, 0, 8);
        _send.SetResourceReference(Control.ForegroundProperty, "AccentTextBrush");
        _send.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
        _send.Padding = new Thickness(0);
        _options.Style = FlatButtonStyle();
        _attach.Style = FlatButtonStyle();
        _attach.SetResourceReference(Control.ForegroundProperty, "TextMutedBrush");
        _input.GotKeyboardFocus += (_, _) => inputBorder.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        _input.LostKeyboardFocus += (_, _) => inputBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _attach.Click += (_, _) => PickImages();
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        var statusStyle = new Style(typeof(TextBlock));
        var emptyStatus = new Trigger { Property = TextBlock.TextProperty, Value = "" };
        emptyStatus.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        statusStyle.Triggers.Add(emptyStatus); _status.Style = statusStyle;
        _keep.IsChecked = _preferences.KeepOpenWhileThinking;
        _keep.Click += (_, _) => { Save(); UpdateHold(); if (_busy && _keep.IsChecked != true) HideRequested?.Invoke(); };
        _refresh.Click += async (_, _) => await LoadChatsAsync();
        _chats.DropDownOpened += (_, _) => AgentComposer.NotifyActivity();
        _chats.DropDownClosed += (_, _) => AgentComposer.NotifyActivity();
        _chats.Items.Add(NewChatChoice);
        _chats.SelectionChanged += async (_, _) =>
        {
            if (_loading) return;
            if (_chats.SelectedItem is CodexChat { Id.Length: 0 }) { if (!_newChat) StartNewChat(); return; }
            if (_newChat && _chats.SelectedItem == null) return;
            _newChat = false;
            ++_selectionVersion;
            _lastSnapshotStamp = null;
            _pendingQuestion = null;
            _lastQuestionCheckUtc = DateTime.MinValue;
            _previousConversation = null;
            _conversation.Text = "Cargando conversación…";
            _busy = false; UpdateHold(); Save();
            await ReadAsync();
        };
        _send.Click += async (_, _) => await SendAsync();
        _input.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (Window.GetWindow(this) is { } window) WindowHelper.EnableTextInput(window);
            _input.Focus(); Keyboard.Focus(_input); AgentComposer.NotifyActivity();
        };
        _input.TextChanged += (_, _) => { placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; AgentComposer.NotifyActivity(); };
        _input.PreviewKeyDown += async (_, e) =>
        {
            AgentComposer.NotifyActivity();
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            { e.Handled = true; await SendAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; HideRequested?.Invoke(); }
            else if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && Clipboard.ContainsImage())
            { e.Handled = true; PasteImage(); }
        };
        row.AllowDrop = true;
        row.PreviewDragOver += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        row.PreviewDrop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            { e.Handled = true; foreach (string file in files) if (ImageExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())) AddPhotoFile(file); }
        };
        Loaded += async (_, _) =>
        {
            if (!IsVisible) return;
            _lifetime ??= new CancellationTokenSource();
            _poll.Start();
            if (_chats.Items.Count <= 1) await LoadChatsAsync();
            else await ReadAsync();
        };
        // Hiding the tab never cancels a send: it only reduces status polling to an idle timer.
        _poll.Tick += async (_, _) => { if (IsVisible || (_pendingThread != null && _pendingThread == (_chats.SelectedItem as CodexChat)?.Id)) await PollAsync(); };
        IsVisibleChanged += async (_, _) =>
        {
            UpdateHold();
            if (IsVisible)
            {
                _lifetime ??= new CancellationTokenSource();
                _poll.Start();
                if (_chats.Items.Count <= 1) await LoadChatsAsync();
                else await ReadAsync();
            }
            else if (_pendingThread == null) _poll.Stop();
        };
        Unloaded += (_, _) => { _poll.Stop(); _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null; HoldOpen = false; HoldChanged?.Invoke(); };
    }

    private static Style FlatButtonStyle() => (Style)System.Windows.Markup.XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Setter Property="Cursor" Value="Hand"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
            <Border x:Name="Surface" Background="Transparent" CornerRadius="7"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="{DynamicResource SurfaceRaisedBrush}"/></Trigger>
              <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """);

    private static Style OptionsItemStyle() => (Style)System.Windows.Markup.XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="MenuItem">
          <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/>
          <Setter Property="Background" Value="{DynamicResource SurfaceRaisedBrush}"/>
          <Setter Property="FontSize" Value="12"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="MenuItem">
            <Border x:Name="Surface" Background="{TemplateBinding Background}" CornerRadius="5" Padding="8,7">
              <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="20"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                <TextBlock x:Name="Check" Text="✓" Visibility="Collapsed" Foreground="{DynamicResource AccentBrush}"/>
                <ContentPresenter Grid.Column="1" ContentSource="Header" RecognizesAccessKey="True"/>
              </Grid>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsChecked" Value="True"><Setter TargetName="Check" Property="Visibility" Value="Visible"/></Trigger>
              <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Surface" Property="Background" Value="{DynamicResource SurfaceBrush}"/></Trigger>
              <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """);

    internal static void StylePicker(ComboBox picker)
    {
        // The system's light ComboBox template ignores dark text resources in some themes.
        picker.Style = (Style)System.Windows.Markup.XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ComboBox">
          <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/>
          <Setter Property="Background" Value="{DynamicResource SurfaceRaisedBrush}"/>
          <Setter Property="Padding" Value="8,5"/>
          <Setter Property="ItemTemplate"><Setter.Value><DataTemplate><TextBlock Text="{Binding}" TextTrimming="CharacterEllipsis" Foreground="{DynamicResource TextPrimaryBrush}"/></DataTemplate></Setter.Value></Setter>
          <Setter Property="ItemContainerStyle">
            <Setter.Value><Style TargetType="ComboBoxItem">
              <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/>
              <Setter Property="Background" Value="{DynamicResource SurfaceRaisedBrush}"/>
              <Setter Property="Padding" Value="8,6"/>
              <Style.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter Property="Background" Value="{DynamicResource SurfaceBrush}"/></Trigger></Style.Triggers>
            </Style></Setter.Value>
          </Setter>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
            <Grid>
              <ToggleButton Focusable="False" Foreground="{DynamicResource TextPrimaryBrush}" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                  <Border CornerRadius="6" Background="{DynamicResource SurfaceRaisedBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1"><ContentPresenter Margin="8,5"/></Border>
                </ControlTemplate></ToggleButton.Template>
                <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                  <ContentPresenter Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" VerticalAlignment="Center"/>
                  <TextBlock Grid.Column="1" Text="⌄" Margin="8,0,0,0" Foreground="{DynamicResource TextPrimaryBrush}"/>
                </Grid>
              </ToggleButton>
              <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False">
                <Border Background="{DynamicResource SurfaceRaisedBrush}" CornerRadius="6" BorderThickness="1" BorderBrush="{DynamicResource BorderBrush}" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}">
                  <ScrollViewer MaxHeight="300" CanContentScroll="True"><ItemsPresenter/></ScrollViewer>
                </Border>
              </Popup>
            </Grid>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """);
    }

    private void Save()
    {
        _preferences = new(_keep.IsChecked == true, (_chats.SelectedItem as CodexChat)?.Id ?? _preferences.ChatId);
        if (!_persistSettings) return;
        try { LocalCards.Write(_settingsFile, _preferences); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = "No pude guardar la preferencia: " + ex.Message; }
    }

    internal void StartNewChat()
    {
        if (_sending) return;
        _newChat = true;
        ++_selectionVersion;
        _chats.SelectedItem = NewChatChoice;
        _pendingThread = _pendingText = _pendingQuestion = _previousConversation = null;
        _busy = false;
        _conversation.Text = "Escribí tu primer mensaje para iniciar el chat.";
        _status.Text = "";
        UpdateHold(); AgentComposer.NotifyActivity();
    }

    private void PickImages()
    {
        _pickingImages = true; UpdateHold();
        try
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"Adjuntar fotos a {_agent}", Multiselect = true,
                Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.tif;*.tiff"
            };
            if (picker.ShowDialog(Window.GetWindow(this)) == true)
                foreach (string file in picker.FileNames) AddPhotoFile(file);
        }
        finally { _pickingImages = false; UpdateHold(); AgentComposer.NotifyActivity(); }
    }

    private static string AttachmentDirectory()
    {
        string directory = Path.Combine(LocalCards.DirectoryPath, "Attachments");
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal void AddPhotoFile(string source)
    {
        try
        {
            if (_attachments.Count >= 8) { _status.Text = "Podés adjuntar hasta 8 fotos por mensaje."; return; }
            string extension = Path.GetExtension(source).ToLowerInvariant();
            if (!ImageExtensions.Contains(extension)) { _status.Text = "Elegí un archivo de imagen compatible."; return; }
            if (new FileInfo(source).Length > 25 * 1024 * 1024) { _status.Text = "Esa foto supera los 25 MB."; return; }
            string destination = Path.Combine(AttachmentDirectory(), Guid.NewGuid().ToString("N") + extension);
            File.Copy(source, destination);
            AddPhoto(new PhotoAttachment(Path.GetFileName(source), destination));
        }
        catch (Exception ex) { _status.Text = "No pude adjuntar la foto: " + ex.Message; }
    }

    private void PasteImage()
    {
        try
        {
            if (_attachments.Count >= 8) { _status.Text = "Podés adjuntar hasta 8 fotos por mensaje."; return; }
            if (Clipboard.GetImage() is not { } source) return;
            string path = Path.Combine(AttachmentDirectory(), Guid.NewGuid().ToString("N") + ".png");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = File.Create(path)) encoder.Save(stream);
            AddPhoto(new PhotoAttachment("Captura.png", path));
        }
        catch (Exception ex) { _status.Text = "No pude pegar la imagen: " + ex.Message; }
    }

    private void AddPhoto(PhotoAttachment photo)
    {
        _attachments.Add(photo);
        var tile = new Grid { Width = 52, Height = 52, Margin = new Thickness(0, 0, 7, 0), ToolTip = photo.Name };
        var frame = new Border { CornerRadius = new CornerRadius(7), ClipToBounds = true };
        frame.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");
        try
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 104; bitmap.UriSource = new Uri(photo.Path); bitmap.EndInit(); bitmap.Freeze();
            frame.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
        }
        catch { frame.Child = new TextBlock { Text = "Foto", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; }
        tile.Children.Add(frame);
        var remove = new Button { Content = "×", Width = 19, Height = 19, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, ToolTip = "Quitar foto" };
        remove.Style = FlatButtonStyle(); remove.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        remove.Click += (_, _) =>
        {
            _attachments.Remove(photo); _photos.Children.Remove(tile);
            _photos.Visibility = _attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            try { File.Delete(photo.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
        tile.Children.Add(remove); _photos.Children.Add(tile); _photos.Visibility = Visibility.Visible;
        _status.Text = ""; AgentComposer.NotifyActivity();
    }

    internal static string BuildPrompt(string text, IEnumerable<PhotoAttachment> attachments)
    {
        var photos = attachments.ToArray();
        if (photos.Length == 0) return text;
        if (string.IsNullOrWhiteSpace(text)) text = "Analizá las fotos adjuntas.";
        return text + "\n\nFotos adjuntas (archivos locales seleccionados por el usuario):\n"
            + string.Join("\n", photos.Select(p => "- " + p.Name.Replace('\n', ' ').Replace('\r', ' ') + ": " + p.Path))
            + "\nAbrí estas fotos con la herramienta de imágenes antes de responder.";
    }

    private void UpdateHold()
    {
        bool hold = IsVisible && (_pickingImages || ((_busy || _sending) && _keep.IsChecked == true));
        if (!hold && _holder != null && _holder != this) return;
        _holder = hold ? this : null;
        if (hold == HoldOpen) return;
        HoldOpen = hold; HoldChanged?.Invoke();
    }

    private async Task LoadChatsAsync()
    {
        if (_loading) return;
        _loading = true; _refresh.IsEnabled = false;
        try
        {
            string? selected = (_chats.SelectedItem as CodexChat)?.Id ?? _preferences.ChatId;
            var arguments = new Dictionary<string, object> { ["mode"] = "history", ["source"] = "codex", ["limit"] = _chatLimit };
            var result = await _connection.CallAsync("list_threads", arguments, _lifetime?.Token ?? default);
            _chats.Items.Clear();
            _chats.Items.Add(NewChatChoice);
            foreach (var chat in result.GetProperty("threads").EnumerateArray())
            {
                if (chat.TryGetProperty("kind", out var kind) && kind.GetString() != "codex") continue;
                string id = chat.GetProperty("id").GetString()!;
                string title = chat.GetProperty("title").GetString() ?? id;
                if (title == "Guardian review" || _chats.Items.Cast<CodexChat>().Any(c => c.Id == id)) continue;
                string? host = chat.TryGetProperty("hostId", out var h) ? h.GetString() : null;
                string status = chat.TryGetProperty("status", out var state) && state.ValueKind == JsonValueKind.String
                    ? state.GetString() switch { "active" => "En curso", "idle" => "En espera", _ => "" }
                    : "";
                _chats.Items.Add(new CodexChat(id, title, host, status));
                if (_chats.Items.Count == _chatLimit + 1) break;
            }
            _chats.SelectedItem = _newChat ? NewChatChoice : _chats.Items.Cast<CodexChat>().FirstOrDefault(c => c.Id == selected && c.Id.Length > 0) ?? _chats.Items.Cast<CodexChat>().FirstOrDefault(c => c.Id.Length > 0) ?? NewChatChoice;
            _status.Text = _newChat ? "" : _chats.Items.Count <= 1 ? $"No hay chats de {_agent} disponibles." : "Elegí el chat al que querés escribir.";
        }
        catch (Exception ex) { _status.Text = "No pude cargar los chats: " + UserError(ex); }
        finally { _loading = false; _refresh.IsEnabled = true; }
        if (_chats.SelectedItem is CodexChat { Id.Length: 0 } && !_newChat) StartNewChat();
        ++_selectionVersion;
        await ReadAsync();
    }

    internal static string UserError(Exception ex) => ex is OperationCanceledException ? "La conexión tardó demasiado; podés volver a intentar." : ex.Message;

    private async Task PollAsync()
    {
        if (_newChat || _reading || _sending || _loading || _chats.SelectedItem is not CodexChat chat) return;
        int version = _selectionVersion;
        _reading = true;
        bool readTranscript = false;
        try
        {
            var list = await _connection.CallAsync("list_threads", new { limit = _chatLimit }, _lifetime?.Token ?? default);
            if (version != _selectionVersion) return;
            var entries = list.GetProperty("threads").EnumerateArray().ToList();
            if (list.TryGetProperty("pinnedThreads", out var pinned)) entries.AddRange(pinned.EnumerateArray());
            var summary = entries.FirstOrDefault(t => t.GetProperty("id").GetString() == chat.Id);
            if (summary.ValueKind == JsonValueKind.Undefined) { readTranscript = true; }
            else
            {
                string state = summary.GetProperty("status").GetString() ?? "";
                string stamp = summary.GetProperty("updatedAt").ToString() + ":" + state;
                if (state == "active")
                {
                    _busy = true; _status.Text = "";
                    if (DateTime.UtcNow - _lastQuestionCheckUtc >= TimeSpan.FromSeconds(8))
                    {
                        _pendingQuestion = await ReadQuestionAsync(chat.Id);
                        _lastQuestionCheckUtc = DateTime.UtcNow;
                        if (version != _selectionVersion) return;
                    }
                    _previousConversation = _pendingQuestion ?? $"{_agent} está pensando…";
                    _conversation.Text = _previousConversation;
                    UpdateHold();
                }
                else if (_busy || stamp != _lastSnapshotStamp) readTranscript = true;
                _lastSnapshotStamp = stamp;
            }
        }
        catch (Exception ex) { if (version == _selectionVersion) _status.Text = $"No pude actualizar {_agent}: " + UserError(ex); }
        finally { _reading = false; }
        // Full transcripts are fetched only on selection or completion, not on every thinking tick.
        if (readTranscript && version == _selectionVersion) await ReadAsync();
        if (!IsVisible && _pendingThread == null) _poll.Stop();
    }

    private async Task SendAsync()
    {
        if (_sending || (string.IsNullOrWhiteSpace(_input.Text) && _attachments.Count == 0)) return;
        var chat = _chats.SelectedItem as CodexChat;
        if (!_newChat && chat == null) { _status.Text = "Elegí un chat o tocá + para iniciar uno nuevo."; return; }
        string text = _input.Text.Trim();
        string prompt = BuildPrompt(text, _attachments);
        bool creating = _newChat;
        _sending = true; _send.IsEnabled = false; _chats.IsEnabled = false;
        _attach.IsEnabled = _photos.IsEnabled = false;
        _status.Text = $"Enviando a {_agent}…"; UpdateHold();
        try
        {
            _sentUtc = DateTime.UtcNow;
            // Never retry a mutation automatically: a lost response must not duplicate a message.
            if (creating)
            {
                var result = await _connection.CallAsync("create_thread", new { prompt, target = new { type = "projectless" } });
                if (!result.TryGetProperty("threadId", out var id) || string.IsNullOrEmpty(id.GetString()))
                {
                    _input.Clear(); _attachments.Clear(); _photos.Children.Clear(); _photos.Visibility = Visibility.Collapsed;
                    _newChat = false;
                    _loading = true;
                    try { _chats.SelectedItem = null; } finally { _loading = false; }
                    _conversation.Text = "El chat se está preparando. Actualizá la lista para seleccionarlo cuando aparezca.";
                    _status.Text = "La solicitud ya fue enviada; no hace falta repetirla.";
                    _busy = false;
                    return;
                }
                string? host = result.TryGetProperty("hostId", out var h) ? h.GetString() : null;
                chat = new CodexChat(id.GetString()!, "Nuevo chat", host);
                _loading = true;
                try
                {
                    _newChat = false;
                    _chats.Items.Insert(1, chat);
                    if (_chats.Items.Count > _chatLimit + 1)
                        _chats.Items.RemoveAt(_chats.Items.Count - 1);
                    _chats.SelectedItem = chat;
                }
                finally { _loading = false; }
                Save();
            }
            else
            {
                _connection.ContextThreadId = chat!.Id;
                var args = new Dictionary<string, object> { ["threadId"] = chat.Id, ["prompt"] = prompt };
                if (chat.HostId != null && chat.HostId != "local") args["hostId"] = chat.HostId;
                await _connection.CallAsync("send_message_to_thread", args);
            }
            _input.Clear();
            _attachments.Clear(); _photos.Children.Clear(); _photos.Visibility = Visibility.Collapsed;
            _pendingThread = chat!.Id; _pendingText = prompt[..Math.Min(prompt.Length, 200)];
            _busy = true; _status.Text = "";
            _previousConversation = null;
            _conversation.Text = $"{_agent} está pensando…";
            if (_keep.IsChecked != true) HideRequested?.Invoke();
        }
        catch (Exception ex) { _status.Text = "No se confirmó el envío. Revisá el chat antes de reenviar. " + UserError(ex); }
        finally { _sending = false; _send.IsEnabled = true; _chats.IsEnabled = true; _attach.IsEnabled = _photos.IsEnabled = true; UpdateHold(); }
    }

    private async Task ReadAsync()
    {
        if (_newChat || _reading || _sending || _loading || _chats.SelectedItem is not CodexChat chat) return;
        int version = _selectionVersion;
        _reading = true;
        try
        {
            _connection.ContextThreadId = chat.Id;
            var args = new Dictionary<string, object> { ["threadId"] = chat.Id, ["turnLimit"] = 3, ["includeOutputs"] = false, ["maxOutputCharsPerItem"] = 20000 };
            if (chat.HostId != null && chat.HostId != "local") args["hostId"] = chat.HostId;
            var result = await _connection.CallAsync("read_thread", args, _lifetime?.Token ?? default);
            _pendingQuestion = await ReadQuestionAsync(chat.Id);
            if (version != _selectionVersion) return;
            var thread = result.GetProperty("thread");
            string state = "";
            if (thread.TryGetProperty("status", out var status))
                state = status.ValueKind == JsonValueKind.String ? status.GetString()! : status.GetProperty("type").GetString()!;
            string? latestReply = null;
            bool sawPending = false;
            string? turnState = null;
            var turns = result.GetProperty("turns").EnumerateArray().ToArray();
            if (turns.Length > 0) turnState = turns[0].GetProperty("status").GetString();
            foreach (var turn in turns.Reverse())
            {
                foreach (var item in turn.GetProperty("items").EnumerateArray())
                {
                    string? type = item.GetProperty("type").GetString();
                    if (type == "userMessage")
                    {
                        string text = string.Join("\n", item.GetProperty("content").EnumerateArray().Where(c => c.TryGetProperty("text", out _)).Select(c => c.GetProperty("text").GetString()));
                        if (chat.Id == _pendingThread && _pendingText != null && text.Contains(_pendingText, StringComparison.Ordinal)) sawPending = true;
                    }
                    else if (type == "agentMessage" && item.TryGetProperty("text", out var message))
                    {
                        if (item.TryGetProperty("phase", out var phase) && phase.GetString() == "commentary") continue;
                        if (!string.IsNullOrWhiteSpace(message.GetString())) latestReply = message.GetString();
                    }
                }
            }
            bool busy = state == "active" || turnState == "inProgress";
            if (chat.Id == _pendingThread)
            {
                // A successful send may precede the new turn becoming visible in the snapshot.
                if (!sawPending && DateTime.UtcNow - _sentUtc < TimeSpan.FromSeconds(30)) busy = true;
                else if (sawPending && !busy) { _pendingThread = null; _pendingText = null; }
                else if (!sawPending && !busy) { _pendingThread = null; _pendingText = null; }
            }
            _busy = busy;
            bool needsAttention = state.Contains("input", StringComparison.OrdinalIgnoreCase) || state.Contains("attention", StringComparison.OrdinalIgnoreCase)
                || (status.ValueKind == JsonValueKind.Object && status.TryGetProperty("activeFlags", out var flags) && flags.EnumerateArray().Any(f => f.ToString().Contains("waiting", StringComparison.OrdinalIgnoreCase)));
            _status.Text = needsAttention ? $"{_agent} necesita tu atención. Revisá la solicitud en la app." : "";
            string conversation = busy ? _pendingQuestion ?? $"{_agent} está pensando…" : latestReply ?? "";
            if (conversation.Length > 40000) conversation = conversation[..40000] + "\n\n… (continuá leyendo en el chat)";
            if (conversation.Length == 0) conversation = $"Escribí abajo para continuar este chat de {_agent}.";
            if (_previousConversation != conversation)
            {
                _previousConversation = conversation; _conversation.Text = conversation; _conversation.ScrollToHome();
            }
            UpdateHold();
        }
        catch (Exception ex) { if (version == _selectionVersion) _status.Text = $"No pude actualizar {_agent}: " + UserError(ex); }
        finally { _reading = false; }
    }

    // Las preguntas pendientes (request_user_input) solo existen en la app de Codex.
    private Task<string?> ReadQuestionAsync(string threadId) =>
        _connection is CodexConnection ? CodexQuestions.ReadAsync(threadId) : Task.FromResult<string?>(null);
}

internal sealed record PhotoAttachment(string Name, string Path);
