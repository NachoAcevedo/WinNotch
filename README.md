# WinNotch — NachoAcevedo fork

This repository is NachoAcevedo's personal fork of [N3uralCreativity/WinNotch](https://github.com/N3uralCreativity/WinNotch). It contains the customized WinNotch source and Nacho_114's plugin projects in [Plugins](Plugins). The original plugin examples are in [Examples/Plugins](Examples/Plugins).

WinNotch is a Dynamic Island-inspired notch overlay for Windows. It brings media controls, calendar, battery, webcam, volume and brightness HUDs, file staging, and installable plugins into a compact surface that can live at the top or side of the screen.

Built with `WPF`, `.NET 8`, and `C#`.

![Windows 10/11](https://img.shields.io/badge/Windows-10%2F11-blue)
![.NET 8](https://img.shields.io/badge/.NET-8.0-purple)

## Upstream project and plugin resources

- Home page: `https://n3uralcreativity.github.io/WinNotch/`
- Documentation: `https://n3uralcreativity.github.io/WinNotch/documentation/`
- [Original WinNotch releases](https://github.com/N3uralCreativity/WinNotch/releases)
- Plugin library repo: `https://github.com/N3uralCreativity/WinNotch-Plugins`
- Plugin development guide: [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md)

## Features

- Dynamic notch with hover, click, drag, and scroll interactions
- Multi-monitor support: drag the island to any screen and dock it there; optional island on every screen with synchronized (or independent) open state
- Built-in media controls and audio visualizer
- Volume and brightness HUDs
- Calendar, battery, file shelf, and webcam mirror
- Light, dark, and auto theme support
- Position-aware layouts, including vertical plugin surfaces
- Plugin Manager and Plugin Browser with online library support
- Configurable plugins with built-in setup UI

## Build this fork from source

Requirements:

- Windows 10 or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```bash
git clone https://github.com/NachoAcevedo/WinNotch.git
cd WinNotch
dotnet run --project src/WinNotch/WinNotch.csproj
```

To create a publish folder:

```bash
dotnet publish src/WinNotch/WinNotch.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## Personal plugins

| Folder | Features |
| --- | --- |
| [BetterUI](Plugins/BetterUI) | Adaptive notch width, settings layout and native flyout hiding. Each is a separate plugin project. |
| [NectarTheme](Plugins/NectarTheme) | Nectar colors, typography and styles. |
| [BetterMedia](Plugins/BetterMedia) | Playback notifications with album art. |
| [BetterCalendar](Plugins/BetterCalendar) | Calendar events from all classic Outlook accounts. |
| [BetterTasks](Plugins/BetterTasks) | Basic and Outlook variants. Both use the same plugin ID, so install only one. |
| [BetterWeather](Plugins/BetterWeather) | Weather and forecast card. |
| [BetterFiles](Plugins/BetterFiles) | Files card. |
| [BetterAI](Plugins/BetterAI) | AI card backed by AgentBridge. |
| [earTrumpet](Plugins/earTrumpet) | Windows playback outputs and per-app volume, mute and routing. |

To compile a plugin on another PC, build its project from the repository root. For example:

```powershell
dotnet build Plugins/BetterCalendar/CalendarAccountsPlugin.csproj -c Release
```

Copy the plugin DLL from its `bin/Release` output into a dedicated subfolder of `%AppData%\WinNotch\Plugins`. For NectarTheme, copy the generated `Fonts` folder alongside the DLL. Install only one BetterTasks variant.

For BetterAI, publish AgentBridge separately:

```powershell
dotnet publish tools/AgentBridge/AgentBridge.csproj -c Release -o publish/AgentBridge
```

## Plugins

WinNotch supports several plugin types:

- `IUIPlugin`
- `IServicePlugin`
- `IAnimationPlugin`
- `IConfigurablePlugin`

Plugins can be installed in two ways:

1. From the in-app Plugin Browser.
2. Manually by placing a plugin DLL in `%AppData%\WinNotch\Plugins\<plugin-id>\`.

The in-app browser only shows plugins listed in the `WinNotch-Plugins` repository's `library.json`. If a plugin is not listed there, it will not appear in the browser even if the DLL exists elsewhere.

## Create plugins

If you want to build plugins:

- start with [PLUGIN_DEVELOPMENT.md](PLUGIN_DEVELOPMENT.md)
- browse the live examples in `Examples/Plugins`
- use the `WinNotch.PluginSdk` package from GitHub Packages if you do not want to clone the full repo

The package is intended for plugin authors, not end users. Runtime installation still happens through the WinNotch app or manual DLL deployment.

## Contributing

Contributions are welcome for both the app and the plugin ecosystem.

- app changes and plugin API changes belong in this repository
- community plugin browser listings belong in [WinNotch-Plugins](https://github.com/N3uralCreativity/WinNotch-Plugins)

If you want your plugin to appear in the browser, publish the DLL publicly, then add its manifest entry to `WinNotch-Plugins/library.json`.

## License

`GPL-3.0`
