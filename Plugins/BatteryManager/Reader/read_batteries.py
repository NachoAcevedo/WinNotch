import json
import logging

import providers

# Use the upstream hardware readers without the tray app, its settings or its scheduler.
def main():
    devices = {}
    classes = (
        providers.RazerProvider, providers.AudezeProvider, providers.WLmouseProvider,
        providers.MchoseProvider, providers.HyperXAlpha2Provider, providers.HyperXCloud3Provider,
        providers.HyperXProvider, providers.KeychronProvider, providers.PulsarProvider,
        providers.JblProvider, providers.LogitechProvider, providers.SteelSeriesProvider,
        providers.XInputProvider, providers.PlayStationProvider, providers.EightBitDoProvider,
        providers.BarracudaProvider, providers.NintendoProvider, providers.AsusProvider,
        providers.GWolvesProvider, providers.LofreeProvider, providers.AstroProvider,
        providers.CorsairProvider, providers.LamzuProvider, providers.AmInfinityProvider,
        providers.SteelSeriesEliteProvider, providers.LogitechCenturionProvider,
        providers.HyperXCloud3SProvider, providers.NzxtProvider, providers.SteamProvider,
        providers.BluetoothProvider,
    )
    failures = 0
    for provider in classes:
        try:
            for device in provider().poll():
                devices[device.key] = {
                    "name": device.name,
                    "level": device.level,
                    "charging": device.charging,
                    "online": device.online,
                    "kind": device.kind,
                    "approximate": bool(device.approx),
                }
        except Exception:
            failures += 1
            logging.exception("Battery reader failed: %s", provider.__name__)
    print(json.dumps({"devices": list(devices.values()), "failures": failures}))


if __name__ == "__main__":
    main()
