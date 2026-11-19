# vUSALink App

A Windows desktop app for the **vUSAlink EDST** that runs next to **CRC**. It shows the EDST from VATFLOW
(`https://vatflow.io/vusalink/edst/`) in its own window and connects it to CRC. You can amend an altitude or send a
CPDLC uplink by typing the command in CRC, without typing it again in the EDST.

The EDST is loaded live, so every update to VATFLOW's EDST shows up in the app without a new download.
CRC itself is never changed: the app doesn't read or write CRC's files or connect to vNAS.

**The web EDST is the standard; this app is another way to use it.** All EDST behaviour lives in the web page:
what a command means, amendments, menus and the refresh rate. The app adds only what a browser can't do:
it passes on what you type in CRC, keeps the window on top, remembers your sign-in and opens pop-ups as windows.
It talks to the page through one small, stable host API (see [How the app talks to the EDST](#how-the-app-talks-to-the-edst)).

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
- **CRC mirroring** (below).

## CRC mirroring

While CRC is the focused window, the app watches what you type into CRC's MCA. Lines starting with `QZ`, `QQ`,
`QU` or `QS`, and handoff lines (`<sector> <callsign>`), are handed to the EDST, which handles them exactly as if
you had typed them into the EDST's own MCA. Nothing else typed in CRC (including chat) is passed on.

### Altitude amendment: nothing is sent

`QZ 240 DAL123` (no `/U`) sets the EDST **assigned altitude** to 240, saved like any EDST amendment. Nothing goes
to the pilot. When you next open DAL123's altitude menu in the EDST, 240 is already selected, so **UPLINK** is one
click. The app's top bar shows `✓ QZ 240 DAL123 AMENDED` in yellow. `FL240` and `24000` work too.

### CPDLC uplink: `/U`

Add `/U` and the command goes straight to the EDST's MCA and is sent, as if you'd typed it in the EDST.
**CRC still amends too.** CRC doesn't know `/U` (it answers `MESSAGE TOO LONG`), so when you press Enter the app
holds that Enter back, erases the trailing EDST modifiers from CRC's MCA, and presses Enter for you. CRC gets
`QZ 350 DAL123` and amends its data block, and the EDST sends the uplink. One command does both.

#### `/U` commands that work

Type the callsign. The CRC function key works in place of typing the verb (it puts the verb on CRC's line for you).

| Command | CRC key | What the pilot receives | Example |
|---|---|---|---|
| `QZ <alt> <callsign> /U` | F5 | Assigned altitude: `CLIMB TO AND MAINTAIN` / `DESCEND TO AND MAINTAIN` / `MAINTAIN` | `QZ 350 DAL123 /U` → `CLIMB TO AND MAINTAIN FL350` |
| `QQ <alt> <callsign> /U` | F8 | Interim altitude (same CPDLC message as QZ) | `QQ 240 DAL123 /U` → `DESCEND TO AND MAINTAIN FL240` |
| `QU <fix> <callsign> /U` | F6 | `PROCEED DIRECT TO <fix>` (first fix only) | `QU LUNNI DAL123 /U` → `PROCEED DIRECT TO LUNNI` |
| `QS <hdg> <callsign> /U` | type it | `FLY HEADING <hdg>` | `QS 270 DAL123 /U` → `FLY HEADING 270` |
| `QS LT <hdg> <callsign> /U` / `QS RT <hdg> …` | type it | `TURN LEFT HEADING` / `TURN RIGHT HEADING` | `QS LT 090 DAL123 /U` |
| `QS PH <callsign> /U` | type it | `FLY PRESENT HEADING` | `QS PH DAL123 /U` |
| `QS /<kt> <callsign> /U` | type it | `MAINTAIN <kt>KT` (80–400 kt) | `QS /280 DAL123 /U` → `MAINTAIN 280KT` |

**Altitudes:** `350`, `FL350` or `35000`, from FL010 to FL600. Below FL180 the pilot gets feet
(`QZ 80 DAL123 /U` → `CLIMB AND MAINTAIN 8,000ft`). `QQ` also takes ERAM's interim prefixes (`QQ R110 …`).
VFR, OTP and block altitudes (`240B260`) are not sent.

**Modifiers:** add them before or after `/U`, for example `QZ 350 DAL123 /PD /U`.

| Modifier | Adds to the message | Applies to |
|---|---|---|
| `/PD` | `AT PILOTS DISCRETION` | Altitude, direct |
| `/IMM` | `IMMEDIATELY` | All |
| `/EXP` | `EXPEDITE` | All |
| `/TFC` | `DUE TO TRAFFIC` | All |
| `/WX` | `DUE TO WEATHER` | All |
| `/RES` | `DUE TO RESTRICTED AIRSPACE` | All |

Put `/U` and the modifiers **at the end of the line**. That's how the app knows what to erase before CRC gets it.
Modifiers in the middle still reach the EDST, but CRC gets them too and answers `MESSAGE TOO LONG`.

**What has to be true:** you're signed in and verified in the EDST, and the aircraft is logged on to CPDLC. Otherwise
the EDST rejects the command and nothing is sent. The top bar shows why in red: `NOT AUTH` (not signed in or verified),
`NO CPDLC` (aircraft not logged on), `NO TRACK` (callsign not found), `ILL ALT` / `ILL HDG` / `ILL SPD` / `NO FIX`
(the value couldn't be read).

Only `QZ` has a no-`/U` form (the [altitude amendment](#altitude-amendment-nothing-is-sent) above). `QQ`, `QU` and `QS`
without `/U` are rejected by the EDST with `REQUIRE /U`, and CRC handles them as normal.

### Handoffs: one-click CPDLC frequency change

When you start a handoff in CRC by typing the sector and the callsign (`11 AAL004`), CRC handles it as usual and the
app also tells the EDST. If AAL004 is on CPDLC, its EDST row shows **`▶|` with an orange bar and the receiving
sector**, for example `AAL004 ▶| (11)`. **Click `▶|`** to send the CPDLC frequency change (`CONTACT <center> <freq>`).
The marker then becomes the EDST's normal `▶` "handoff pending WILCO".

- **Finding the frequency:** the EDST looks up the receiving sector's controller in vNAS's public controller feed
  (the ERAM sector of each position) for your ARTCC, for example "MINNEAPOLIS CENTER 133.400". It falls back to an
  online `…_11_CTR` callsign. The top bar shows what it found.
- **No controller online for that sector:** the marker is grey, and clicking it opens the EDST's normal HANDOFF menu
  so you can choose.
- **Right-click** the marker to dismiss it. It also clears by itself when the aircraft leaves CPDLC, after 15 minutes,
  or when you send a handoff some other way.
- **Not covered:** handoffs finished by clicking the aircraft in CRC. A CRC CID (`11 413`) works once the app has
  learned it from a QF (see [CRC CIDs](#crc-cids-learned-from-qf)). `240 AAL004` is an altitude, not a handoff.
- **Requires** the EDST page with host API version 2 (vatflow, November 2026 or later).

### CRC CIDs: learned from QF

Use the CRC CID in place of the callsign (`QZ 240 151 /U`, `11 151`) once the app has seen that aircraft's **QF
readout**. Run QF in CRC as usual: middle-click the data block, or type `QF <callsign>` / `QF <CID>`. CRC prints
`151 SKW5646(32) E75L/L …` in its Response Area. The app reads that line and learns **151 = SKW5646** (owning sector
`32`), and the top bar shows `CID 151 = SKW5646 (32)`. From then on, 151 in a mirrored command is passed to the EDST as
SKW5646. CRC keeps its own line with the CID.

- **How it's read:** CRC draws its ERAM display with OpenGL, so there is no text to query. Right after a QF the app
  takes a picture of CRC's ERAM window and runs Windows' built-in text recognition (OCR) on it. The picture is
  processed in memory and never saved or sent; only the parsed readout line is logged.
- **Checked before it's trusted:** the readout's callsign must be a real flight in the VATSIM data with a matching type
  or beacon code. A single misread character (OCR can read W as H) is corrected only when exactly one real flight fits.
  On a test server (flights not on VATSIM) pairs are kept unverified; the EDST still checks the callsign exists.
- **Unknown CID:** a `/U` uplink with a CID the app hasn't learned sends nothing, and the top bar says
  `CID 151 NOT KNOWN - QF IT IN CRC FIRST`. Other lines with unknown CIDs (such as routine handoffs) are skipped quietly.
- **Freshness:** a new QF replaces older pairs for that CID or callsign. Pairs expire after 4 hours.

### What isn't mirrored

- **Lines you edited in CRC with cursor keys, Home/End, Delete or a Ctrl shortcut (such as Ctrl+R recall).** The app
  can't be sure what CRC's MCA then holds, so it passes nothing on and doesn't touch CRC's line. The top bar shows
  `LINE EDITED IN CRC - NOT MIRRORED, RETYPE IT`. Backspace is fine.
- **Commands you finish by clicking an aircraft in CRC.** The app only sees typed text, so it never gets the callsign.
  Type the callsign.
- **CRC CIDs the app hasn't seen a QF for.** QF the aircraft once (see above), or type the callsign.
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
| `Url` | `https://vatflow.io/vusalink/edst/` | EDST page to load. A URL on the command line overrides it |
| `RefreshSeconds` | `15` | Only used with older EDST pages that predate the host API (they refreshed every 30 s). Current pages set their own rate |

## How the app talks to the EDST

The EDST page publishes `window.vUSALinkEDST`:

| Member | |
|---|---|
| `version` | `2`. Bumped only if the API itself changes. Version 2 added ERAM handoffs |
| `command(text, {source})` | Runs an ERAM command the same way the EDST MCA does. Returns `{ok, action: 'uplink' \| 'amend' \| 'handoff' \| 'ignored', flid, error}` (`handoff` also has `sector`, `facility`, `freq`). With `source: 'crc'` the callsign must be typed (no fallback to the selected row), and commands the EDST doesn't handle come back `ignored` without touching the MCA |
| `status()` | `{signedIn, canControl, testMode, listMode, refreshMs}` |

Everything else in the page is internal and free to change. If the loaded page has no host API (an older EDST),
the app falls back to its previous method for one release.

## Limits

- Needs internet, because the EDST is served from vatflow.io. If it can't load, the top bar shows OFFLINE and retries every 15 s.
- Traffic is as fresh as the VATSIM data feed (about 15 s; the EDST refreshes every 15 s). CRC gets its traffic
  from vNAS over an encrypted connection and offers no local feed, so the app can't read CRC's traffic.

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
| `VUSALINK_CRC_PROCESS=<name>` | Treat another process as CRC (for testing mirroring without CRC) |
| `VUSALINK_QFREAD=1` | Read CRC's Response Area once at start-up, as after a QF |

`build.ps1 -Out <folder>` builds into another folder, for example while the app in `bin\` is running.

The log is `bin\vUSALinkApp.log`.

## License

[MIT](LICENSE). The WebView2 DLLs in `lib\` are under Microsoft's license (`lib\WebView2-LICENSE.txt`).
