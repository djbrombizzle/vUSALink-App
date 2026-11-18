# vUSALink App

A Windows desktop app for the **vUSAlink EDST** that runs next to **CRC**. It shows the EDST from VATFLOW
(`https://vatflow.io/vusalink/edst/`) in its own window and connects it to CRC. You can amend an altitude or send a
CPDLC uplink by typing the command in CRC, without typing it again in the EDST.

The EDST is loaded live, so every update to VATFLOW's EDST shows up in the app without a new download.
CRC itself is never changed: the app doesn't read or write CRC's files or connect to vNAS.

## Download

1. Download the latest `vUSALink-App-vX.Y.Z.zip` from [Releases](../../releases).
2. Unzip it anywhere (for example `Documents\vUSALink App`). Keep the `.exe` and the three `.dll` files together.
3. Run `vUSALinkApp.exe` and sign in with VATSIM from the EDST, as on the website.

Needs Windows 10 or 11 with the Microsoft Edge WebView2 Runtime, which Windows 11 already includes. If it's missing,
the app tells you where to get it.

> **"Windows protected your PC" / antivirus warning:** the app isn't code-signed, and it uses a keyboard hook to see
> what you type into CRC. Windows SmartScreen and some antivirus programs flag that combination. Click
> **More info → Run anyway**, or build it yourself from this source. The hook only reads keys while a CRC window is in
> the foreground. Keystrokes are never stored or sent anywhere, and only the commands below are passed to the EDST.

## What it does

- **The full EDST:** ACL, DEP, GPD, PLANS, WX, SIG/NOT, GI, CPDLC ADV / HIST / MSGOUT, the MCA, everything the web EDST has.
- **Stays signed in:** your VATSIM sign-in, layout, filters and MCA position are saved by the app between launches.
- **Pop-up windows:** VATFLOW pages the EDST opens (CPDLC Map, list windows, sign-in) open as app windows that share
  your sign-in. Other links open in your normal browser.
- **15-second traffic refresh** to match the VATSIM data feed. The website refreshes every 30 s.
- **CRC mirroring** (below).

## CRC mirroring

While CRC is the focused window, the app watches what you type into CRC's MCA.

### Altitude amendment: nothing is sent

`QZ 240 DAL123` (no `/U`) sets the EDST **assigned altitude** to 240, saved like any EDST amendment. Nothing goes
to the pilot. When you next open DAL123's altitude menu in the EDST, 240 is already selected, so **UPLINK** is one
click. The app's top bar shows `✓ QZ 240 DAL123 AMENDED` in yellow. `FL240` and `24000` work too.

### CPDLC uplink: `/U`

Add `/U` and the command goes straight to the EDST's MCA and is sent, as if you'd typed it in the EDST:

| Type in CRC | Uplink |
|---|---|
| `QZ 350 DAL123 /U` | Assigned altitude |
| `QQ 240 DAL123 /U` | Interim altitude |
| `QU LUNNI DAL123 /U` | Proceed direct |
| `QS 270 DAL123 /U` / `QS /280 DAL123 /U` | Heading / speed |

The EDST's modifiers (`/PD /IMM /EXP /TFC /RES /WX`) pass through. You must be signed in and verified in the EDST, and the
aircraft must be logged on to CPDLC. Otherwise the EDST rejects it (`NOT AUTH`, `NO CPDLC`, `NO TRACK`) and the top bar
shows why in red.

### What isn't mirrored

- **Commands you finish by clicking an aircraft in CRC.** The app only sees typed text, so it never gets the callsign.
  Type the callsign.
- **CRC CIDs** (`QZ 240 4A2`). vNAS assigns them and they aren't in the VATSIM data, so the EDST can't match them.
  The top bar shows `CID NOT KNOWN TO EDST - USE CALLSIGN`.
- **CRC dot-aliases, data block work and everything else.** Ignored.

## Top bar

| Item | Effect |
|---|---|
| `CRC ●/○` | Whether CRC is running |
| CRC MIRROR | Turn CRC mirroring on or off (on by default) |
| CRC BAR | Switch the EDST to its compact CRC layout (`?crc=1`) |
| ON TOP | Keep the app (and its pop-ups) above CRC |
| − / + | Zoom (Ctrl+wheel works too) |
| RELOAD / BROWSER | Reload (F5) / open the current page in your browser |

Keys: **F11** full screen, **F12** developer tools.

Settings (window position, zoom, toggles) are in `%LOCALAPPDATA%\vUSALinkApp\settings.ini`:

| Setting | Default | |
|---|---|---|
| `RefreshSeconds` | `15` | EDST traffic refresh. `0` keeps the page's own rate |
| `Url` | `https://vatflow.io/vusalink/edst/` | EDST page to load. A URL on the command line overrides it |

## Limits

- Needs internet, because the EDST is served from vatflow.io. If it can't load, the top bar shows OFFLINE and retries every 15 s.
- Traffic is as fresh as the VATSIM data feed (about 15 s). CRC gets its traffic from vNAS over an encrypted
  connection and offers no local feed, so the app can't read CRC's traffic.

## Build from source

No SDK needed: the build uses the C# compiler included in Windows (.NET Framework 4.8).

```
powershell -ExecutionPolicy Bypass -File build.ps1
bin\vUSALinkApp.exe
```

`lib\` holds Microsoft's WebView2 SDK DLLs from the `Microsoft.Web.WebView2` NuGet package (1.0.4314.35, signed by
Microsoft, see `lib\WebView2-LICENSE.txt`). GitHub Actions builds every push. Pushing a tag such as `v0.2.0` publishes a
Release with the zip.

Debug switches (environment variables):

| Variable | Effect |
|---|---|
| `VUSALINK_SNAPSHOT=<file.png>` | Save a picture of the page once it loads, then exit |
| `VUSALINK_INJECT=<command>` | Send a command through the CRC path first (`{CS}` = first aircraft on the list) |
| `VUSALINK_PRE` / `VUSALINK_POST` | Page scripts to run before / after it |
| `VUSALINK_PROBE=<seconds>` | Log when the page refreshes traffic |

The log is `bin\vUSALinkApp.log`.

## License

[MIT](LICENSE). The WebView2 DLLs in `lib\` are under Microsoft's license (`lib\WebView2-LICENSE.txt`).
