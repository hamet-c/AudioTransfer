# PhoneSpeaker

Plays your Windows PC's audio on your iPhone. It's free, with no ads and no limits. The phone only needs Safari; nothing gets installed on it.

```
PC (WASAPI loopback) ──► WebSocket, 48 kHz 16-bit stereo ──► Safari on iPhone
```

## Run it

Double-click `dist\PhoneSpeaker.exe`. It's self-contained and doesn't need .NET installed, so you can copy it to any Windows PC. Keep `dist\wwwroot` next to it.

From source instead:

```powershell
cd PhoneSpeaker
dotnet run
```

To rebuild the exe: `dotnet publish -c Release -r win-x64 --self-contained true -o dist`

The console shows a QR code and a URL such as `http://192.168.1.20:8080`. Scan the code with the iPhone camera, tap the link, then tap **Tap to start**.

Keys in the console:

| Key | Action |
|-----|--------|
| `M` | Mute or unmute the PC's own speakers. The phone keeps playing, and the volume is restored when you quit. |
| `T` | Test tone (440 Hz) |
| `Q` | Quit |

Use `dotnet run -- --port 8090` if port 8080 is taken.

## Connecting the phone and PC

The phone and PC must be on the same network, and that network must let devices talk to each other.

- **Home Wi‑Fi:** usually works right away. Set the network to **Private** in Windows Settings → Network & internet → Wi‑Fi → (your network).
- **School or work Wi‑Fi (eduroam, etc.):** these usually block device-to-device traffic. Use the iPhone's hotspot instead:
  - **USB (lowest lag):** plug the iPhone into the PC and turn on Settings → Personal Hotspot. The Apple Devices app or iTunes must be installed for the USB network driver. The PC gets a `172.20.10.x` address, which the console prints.
  - **Wi‑Fi hotspot:** connect the PC to the iPhone's Personal Hotspot.

  Phone audio streaming over a hotspot doesn't use mobile data, because the traffic stays between the PC and the phone.

### Firewall

If the page won't load on the phone, allow the port through Windows Firewall. Run this once in an **Administrator** PowerShell:

```powershell
New-NetFirewallRule -DisplayName "PhoneSpeaker" -Direction Inbound -Protocol TCP -LocalPort 8080 -Action Allow -Profile Private
```

If you connect over the hotspot and Windows marks that network as Public, either set it to Private or add `,Public` to `-Profile`. Anyone on the same network who opens the URL can hear your PC's audio, so be careful on shared networks.

## On the iPhone

- **Delay buffer:** lower means less lag. Raise it if you hear crackles; "gaps" in the stats means the buffer ran dry.
- **Silent switch:** on iOS 17 and later, audio plays even with the ring/silent switch on silent.
- **Keep playing when locked:** experimental. It routes audio through a media element so iOS may keep it alive in the background, at the cost of some extra delay. Otherwise, keep Safari open with the screen on, or set Auto‑Lock to Never while listening.
- **Add to Home Screen:** in Safari, tap Share → Add to Home Screen to open it full screen like an app.

## Notes

- This project targets .NET 8 and pins SDK 8.0.402 in `global.json`. On this PC, the .NET 9.0.7 and 10.0.0 runtimes are partly uninstalled and crash the newer SDKs. The exe in `dist` bundles its own runtime, so the broken installs don't affect it.
- Lag is roughly the delay buffer (80 ms by default) plus Wi‑Fi jitter.
