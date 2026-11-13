import argparse
import json
import time
from pathlib import Path

from halo_history import History
import logging

import providers

# Use the upstream hardware readers without the tray app, its settings or its scheduler.
def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--history", type=Path)
    parser.add_argument("--playstation-full-mode", action="store_true")
    args = parser.parse_args()
    history = History(str(args.history) if args.history else None)
    history.load()
    if args.history:
        # Each scan is a new process. Restore the preceding reading so Halo can count usage.
        try:
            previous = json.loads(args.history.read_text(encoding="utf-8")).get("devices", {})
            for key, entry in history.devices.items():
                entry["last"] = previous.get(key, {}).get("last")
        except (OSError, ValueError, AttributeError):
            pass
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
            reader = provider()
            if isinstance(reader, providers.PlayStationProvider):
                reader.switch_bluetooth = args.playstation_full_mode
            for device in reader.poll():
                history.record(device.key, device.level, device.charging, device.online,
                               time.time(), coarse=bool(device.approx))
                remaining = history.seconds_left(device.key, device.level) if (
                    device.online and not device.charging and not device.approx
                ) else None
                devices[device.key] = {
                    "key": device.key,
                    "name": device.name,
                    "level": device.level,
                    "charging": device.charging,
                    "online": device.online,
                    "kind": device.kind,
                    "approximate": bool(device.approx),
                    "secondsLeft": round(remaining) if remaining is not None else None,
                }
        except Exception:
            failures += 1
            logging.exception("Battery reader failed: %s", provider.__name__)
    if args.history:
        if failures == 0:
            for key in history.devices.keys() - devices.keys():
                history.devices[key]["last"] = None
        history.dirty = True
        history.save(force=True)
    print(json.dumps({"devices": list(devices.values()), "failures": failures}))


if __name__ == "__main__":
    main()
