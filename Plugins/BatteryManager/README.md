# BatteryManager

BatteryManager adds the **Baterías** card to WinNotch. It reads supported Bluetooth and receiver-connected devices using Halo Battery's hardware providers. The bundled reader runs only on demand while the card is visible, at most once per minute unless you press **Actualizar**.

## Build

On Windows x64, use the .NET 8 SDK and Python 3.10 or newer. From the repository root:

```powershell
dotnet build Plugins/BatteryManager/BatteryManager.csproj -c Release
python -m pip install -r Plugins/BatteryManager/Reader/requirements-build.txt
python -m PyInstaller --onedir --name BatteryReader Plugins/BatteryManager/Reader/read_batteries.py
```

Put `BatteryManager.dll` from the Release build in `%AppData%\WinNotch\Plugins\personal.card.batterymanager`. Copy the entire `dist\BatteryReader` directory to `%AppData%\WinNotch\BatteryManager\Reader`. Keep its `_internal` directory in place. Restart WinNotch and open **Baterías**.

The provider sources come from [Halo Battery at 2251f05](https://github.com/HeyOkay/HaloBattery/tree/2251f0534a8e886c9692ead42056059b4f69d8c0/providers) and retain their [MIT license](Reader/LICENSE.HaloBattery).
