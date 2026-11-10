using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinNotch.Plugins;

namespace WinNotch.Views;

public partial class PluginBrowserWindow : Window
{
    private readonly PluginLibraryService _libraryService;
    private readonly PluginManager? _pluginManager;

    public PluginBrowserWindow(PluginLibraryService libraryService, PluginManager? pluginManager)
    {
        InitializeComponent();

        _libraryService = libraryService;
        _pluginManager = pluginManager;

        CloseButton.Click += (_, _) => Close();
        SearchButton.Click += (_, _) => SearchPlugins();
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                SearchPlugins();
        };
        SearchBox.TextChanged += (_, _) => SearchPlugins();

        Loaded += async (_, _) =>
        {
            var success = await _libraryService.RefreshLibraryAsync();
            if (success)
            {
                DisplayPlugins(_libraryService.AvailablePlugins);
            }
            else
            {
                DisplayError(_libraryService.LastError ?? "Unknown error");
            }
        };
    }

    private void SearchPlugins()
    {
        DisplayPlugins(_libraryService.SearchPlugins(SearchBox.Text));
    }

    private void DisplayPlugins(IEnumerable<PluginManifest> plugins)
    {
        var pluginList = plugins
            .OrderByDescending(plugin => plugin.IsVerified)
            .ThenBy(plugin => plugin.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ShowPluginGrid();
        PluginGridPanel.Children.Clear();
        ResultsText.Text = pluginList.Count == 0
            ? "No matches"
            : $"{pluginList.Count} plugin{(pluginList.Count == 1 ? string.Empty : "s")}";

        if (pluginList.Count == 0)
        {
            ShowEmptyState(
                "No plugins found.",
                "Try a different search term or clear the search field.");
            return;
        }

        foreach (var plugin in pluginList)
        {
            PluginGridPanel.Children.Add(CreatePluginCard(plugin));
        }
    }

    private void DisplayError(string detail)
    {
        ShowPluginGrid();
        PluginGridPanel.Children.Clear();
        ResultsText.Text = "Library unavailable";
        ShowEmptyState(
            "Failed to load the plugin library.",
            detail);
    }

    private Border CreatePluginCard(PluginManifest manifest)
    {
        var card = new Border
        {
            Width = 236,
            Margin = new Thickness(0, 0, 14, 14),
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(24),
            Background = GetCardBrush()
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = manifest.Name,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = GetPrimaryBrush()
        };
        Grid.SetColumn(title, 0);
        header.Children.Add(title);

        if (manifest.IsVerified)
        {
            var verifiedTag = CreateTag("Verified", GetAccentSoftBrush(), GetAccentBrush());
            Grid.SetColumn(verifiedTag, 1);
            header.Children.Add(verifiedTag);
        }

        Grid.SetRow(header, 0);
        layout.Children.Add(header);

        var meta = new TextBlock
        {
            Text = $"v{manifest.Version} by {manifest.Author}",
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = GetMutedBrush()
        };
        Grid.SetRow(meta, 1);
        layout.Children.Add(meta);

        var description = new TextBlock
        {
            Text = manifest.Description,
            FontSize = 12,
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 54,
            Foreground = GetSecondaryBrush()
        };
        Grid.SetRow(description, 2);
        layout.Children.Add(description);

        var footer = new StackPanel
        {
            Margin = new Thickness(0, 14, 0, 0)
        };

        var tags = new WrapPanel();
        tags.Children.Add(CreateTag(manifest.Category.ToString(), GetTagBrush(), GetSecondaryBrush()));
        tags.Children.Add(CreateTag($"Min {manifest.MinimumWinNotchVersion}", GetTagBrush(), GetSecondaryBrush()));
        if (manifest.Permissions.Any())
        {
            tags.Children.Add(CreateTag(
                manifest.Permissions.Length == 1 ? manifest.Permissions[0] : $"{manifest.Permissions.Length} permissions",
                GetTagBrush(),
                GetMutedBrush()));
        }
        footer.Children.Add(tags);

        var actions = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var installButton = new Button
        {
            Content = IsPluginInstalled(manifest.Id) ? "Installed" : "Get",
            MinWidth = 88,
            Style = TryGetStyle(IsPluginInstalled(manifest.Id) ? "PluginSecondaryButton" : "PluginPrimaryButton"),
            IsEnabled = !IsPluginInstalled(manifest.Id)
        };
        installButton.Click += async (_, _) => await InstallPlugin(manifest, installButton);
        Grid.SetColumn(installButton, 0);
        actions.Children.Add(installButton);

        if (!string.IsNullOrWhiteSpace(manifest.Homepage))
        {
            var openPageButton = new Button
            {
                Content = "View",
                MinWidth = 72,
                Margin = new Thickness(10, 0, 0, 0),
                Style = TryGetStyle("PluginGhostButton")
            };
            openPageButton.Click += (_, _) => OpenExternal(manifest.Homepage);
            Grid.SetColumn(openPageButton, 1);
            actions.Children.Add(openPageButton);
        }

        footer.Children.Add(actions);

        Grid.SetRow(footer, 3);
        layout.Children.Add(footer);

        card.Child = layout;
        return card;
    }

    private void ShowEmptyState(string title, string body)
    {
        PluginGridPanel.Visibility = Visibility.Collapsed;
        EmptyStateTitleText.Text = title;
        EmptyStateBodyText.Text = body;
        EmptyStatePanel.Visibility = Visibility.Visible;
    }

    private void ShowPluginGrid()
    {
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        PluginGridPanel.Visibility = Visibility.Visible;
    }

    private Border CreateTag(string text, System.Windows.Media.Brush background, System.Windows.Media.Brush foreground)
    {
        var pill = new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 8)
        };
        pill.Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = foreground
        };
        return pill;
    }

    private bool IsPluginInstalled(string pluginId)
    {
        if (_pluginManager?.LoadedPlugins.Any(plugin => plugin.Id == pluginId) == true)
            return true;

        var pluginsDirectory = _pluginManager?.GetPluginsDirectory();
        if (string.IsNullOrWhiteSpace(pluginsDirectory))
            return false;

        var pluginDirectory = Path.Combine(pluginsDirectory, pluginId);
        if (!Directory.Exists(pluginDirectory))
            return false;

        return File.Exists(Path.Combine(pluginDirectory, "manifest.json"))
            || Directory.EnumerateFiles(pluginDirectory, "*.dll", SearchOption.TopDirectoryOnly).Any()
            || Directory.EnumerateFiles(pluginDirectory, "*.pending", SearchOption.TopDirectoryOnly).Any();
    }

    private async System.Threading.Tasks.Task InstallPlugin(PluginManifest manifest, Button button)
    {
        button.IsEnabled = false;
        button.Content = "Installing...";

        try
        {
            var targetDir = _pluginManager?.GetPluginsDirectory();
            if (targetDir == null)
            {
                MessageBox.Show("Plugin manager not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                button.IsEnabled = true;
                button.Content = "Get";
                return;
            }

            var progress = new Progress<double>(value =>
            {
                button.Content = $"{Math.Round(value):0}%";
            });

            var filePath = await _libraryService.DownloadPluginAsync(manifest.Id, targetDir, progress);

            if (filePath != null)
            {
                button.Content = "Installed";
                button.Style = TryGetStyle("PluginSecondaryButton");
                ShowRestartPopup($"{manifest.Name} has been installed.");
                DisplayPlugins(_libraryService.SearchPlugins(SearchBox.Text));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to install plugin: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            button.IsEnabled = true;
            button.Content = "Get";
            button.Style = TryGetStyle("PluginPrimaryButton");
        }
    }

    private static void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Best effort only.
        }
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        DragMove();
    }

    internal static void ShowRestartPopup(string reason)
    {
        var result = MessageBox.Show(
            $"{reason}\n\nWinNotch must restart to apply the changes. Restart now?",
            "Restart Required",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (result == MessageBoxResult.Yes)
        {
            RestartApp();
        }
    }

    internal static void RestartApp()
    {
        var exePath = Environment.ProcessPath;
        if (exePath != null)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c ping 127.0.0.1 -n 2 > nul && start \"\" \"{exePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }

        Application.Current?.Shutdown();
    }

    private System.Windows.Media.Brush GetPrimaryBrush() => GetBrush("TextPrimaryBrush", Color.FromRgb(255, 255, 255));
    private System.Windows.Media.Brush GetSecondaryBrush() => GetBrush("TextSecondaryBrush", Color.FromRgb(204, 204, 204));
    private System.Windows.Media.Brush GetMutedBrush() => GetBrush("TextMutedBrush", Color.FromRgb(136, 136, 136));
    private System.Windows.Media.Brush GetCardBrush() => GetBrush("SegmentedCheckedBgBrush", Color.FromRgb(42, 42, 46));
    private System.Windows.Media.Brush GetTagBrush() => GetBrush("HoverOverlayBrush", Color.FromArgb(24, 255, 255, 255));
    private System.Windows.Media.Brush GetAccentBrush() => GetBrush("AccentBrush", Color.FromRgb(10, 132, 255));
    private System.Windows.Media.Brush GetAccentSoftBrush() => GetBrush("AccentSoftBrush", Color.FromArgb(31, 10, 132, 255));

    private System.Windows.Media.Brush GetBrush(string resourceKey, Color fallback)
    {
        if (TryFindResource(resourceKey) is System.Windows.Media.Brush localBrush)
            return localBrush;

        if (Application.Current?.TryFindResource(resourceKey) is System.Windows.Media.Brush applicationBrush)
            return applicationBrush;

        return new SolidColorBrush(fallback);
    }

    private Style? TryGetStyle(string resourceKey)
    {
        return TryFindResource(resourceKey) as Style
            ?? Application.Current?.TryFindResource(resourceKey) as Style;
    }
}

