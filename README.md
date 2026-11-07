<p align="center"><img src="src/Tether/Assets/Tether.png" width="96" alt="Tether icon"></p>

<h1 align="center">Tether</h1>
<p align="center">Bluetooth devices, one click away. A lightweight Windows tray app.</p>
<p align="center"><a href="https://github.com/larosa023/Tether/releases/latest/download/Tether.exe"><b>Download Tether.exe</b></a></p>

---

Tether lives in the notification area and replaces the trip into **Settings → Bluetooth** with a single click.

- **One-click connect / disconnect** for earbuds, headphones, speakers, controllers, keyboards and mice
- **Live battery levels** in the flyout, the tray tooltip, and optionally as the tray icon itself
- **Fast audio switching**: pick any playback device, or make a Bluetooth device the output with one button
- **Per-device profiles** that run when a device connects:
  - make it the default output (and the calls device)
  - set its volume, natively or through **Faderly**
  - switch the **Tonely** EQ preset
  - switch back to the previous output when it disconnects
- **Low battery alerts**, optional connect/disconnect notifications
- **Light, dark or system theme** with selectable accent colors
- **Built-in updater** that installs new releases from this repo (SHA-256 verified)
- Event-driven, no polling loops apart from a 2-minute battery refresh; idles near 0% CPU

## Install

Download `Tether.exe` from [Releases](https://github.com/larosa023/Tether/releases) and run it. It's a single self-contained exe (no .NET install needed). Put it somewhere permanent, e.g. `%LOCALAPPDATA%\Programs\Tether\`, and turn on **Start with Windows** in Settings.

Requires Windows 10 20H1 (19041) or later. Devices must be paired once in Windows Settings; Tether manages them from then on.

## Using it

| Action | How |
|---|---|
| Open the flyout | Left-click the tray icon |
| Quick connect / disconnect | Right-click the tray icon, pick a device |
| Device options (profile, rename, hide) | Right-click a device card in the flyout |
| Make a headset the output | Speaker button on its card |
| Battery in the tray | Settings → General → Tray icon shows → Battery level |

## How connect / disconnect works

Windows has no single public "connect this device" API, so Tether uses the best mechanism per device type:

| Device | Method | Notes |
|---|---|---|
| Classic Bluetooth audio (earbuds, headphones, speakers) | `KSPROPERTY_ONESHOT_RECONNECT` / `DISCONNECT` on the device's audio endpoint, the same call the Settings app's Connect button makes | Fast and clean, no admin needed |
| Other classic devices (mice, keyboards, many controllers) | Briefly disables and re-enables the device's Bluetooth services | Some drivers require **administrator** mode (Settings → General) |
| Bluetooth LE devices | Opens a GATT session to connect | Windows keeps LE input devices connected on its own, so disconnecting them isn't always possible |

## Battery levels

Tether reads the same value Windows Settings shows (`DEVPKEY_Bluetooth_Battery`), falling back to the standard GATT Battery Service for LE devices. If Settings shows no battery for a device, the device doesn't report it over a standard profile.

## Tonely and Faderly integration

Profiles hand off to the companion apps with a command line. Each app is located in this order:

1. The path set in **Settings → Integrations**
2. `%LOCALAPPDATA%\Programs\<App>\<App>.exe`, the folder next to Tether, `Program Files\<App>\`, or `PATH`
3. The app's registered URI scheme (`tonely://`, `faderly://`)

Default commands (editable in Settings):

```
Tonely.exe  --preset "<preset name>"        or  tonely://preset/<preset name>
Faderly.exe --volume <0-100>                or  faderly://volume/<0-100>
```

For this to feel instant, Tonely and Faderly should be single-instance and forward these arguments to the already-running copy (e.g. through a named pipe) instead of opening a second window.

## Updates

Tether checks `api.github.com/repos/larosa023/Tether/releases` 30 seconds after launch and every 6 hours (configurable, with an opt-in pre-release channel). When a newer version exists it shows a notification and a banner in the flyout. Installing:

1. downloads `Tether.exe` and its `Tether.exe.sha256` from the release,
2. refuses the file if the hash doesn't match,
3. renames the running exe to `Tether.exe.old`, drops the new one in place, restarts, and cleans up.

If Tether lives in a protected folder (Program Files), the swap is done by a UAC-elevated helper.

## Releasing

CI (`.github/workflows/build.yml`) builds and smoke-tests every push on a Windows runner. To ship a version, bump `<Version>` in `src/Tether/Tether.csproj` and push to `main`:

```xml
<Version>0.2.0</Version>
```

If no `v0.2.0` release exists yet, that build becomes the release: CI tags the commit, publishes the single-file exe, writes `Tether.exe.sha256` and attaches both. A version with a dash (e.g. `0.3.0-beta.1`) is marked as a pre-release. Pushing a `v*` tag yourself works too. Existing installs pick the release up automatically.

## Building locally

```powershell
dotnet publish src/Tether/Tether.csproj -c Release -r win-x64 -o publish
```

Needs the .NET 8 SDK. For a quick debug run: `dotnet run --project src/Tether`.

## Files

| What | Where |
|---|---|
| Settings | `%APPDATA%\Tether\settings.json` |
| Log | `%LOCALAPPDATA%\Tether\tether.log` |
| Downloaded updates | `%LOCALAPPDATA%\Tether\updates\` |

## Project layout

```
src/Tether/
  Core/Bluetooth   device watcher, battery, connect strategies
  Core/Audio       Core Audio: endpoints, default device, volume, BT audio one-shots
  Core/Native      COM / Win32 interop definitions
  Core/Profiles    per-device profiles, Tonely / Faderly launcher
  Core/Updates     GitHub Releases updater
  Core/Settings    JSON settings
  UI               tray icon, flyout, settings window
  Themes           brushes and control styles
```
