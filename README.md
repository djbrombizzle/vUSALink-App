# vUSALink App and vUSALink-Lite

A Windows desktop app for the **vUSAlink EDST** that runs next to **CRC**. **vUSALink-Lite** is the same CRC link
without the window, for controllers who keep the EDST in a CRC browser tab (see [Two ways to use it](#two-ways-to-use-it)). It shows the EDST from VATFLOW
(`https://vatflow.io/vusalink/edst/`) in its own window and connects it to CRC. You can amend an altitude or send a
CPDLC uplink by typing the command in CRC, without typing it again in the EDST.

> 📖 **New to the vUSAlink EDST?** The **[vUSAlink EDST Manual](MANUAL.md)** covers the whole system: how CPDLC
> reaches pilots through KUSA, signing in and verifying, every column, menu and MCA command, and working from CRC.

The EDST is loaded live, so every update to VATFLOW's EDST shows up in the app without a new download.
CRC itself is never changed: the app doesn't read or write CRC's files or connect to vNAS.

**The web EDST is the standard; this app is another way to use it.** All EDST behaviour lives in the web page:
what a command means, amendments, menus and the refresh rate. The app adds only what a browser can't do:
it passes on what you type in CRC, keeps the window on top, remembers your sign-in and opens pop-ups as windows.
It talks to the page through one small, stable host API (see [How the app talks to the EDST](#how-the-app-talks-to-the-edst)).

## Two ways to use it

| | **vUSALink App** | **vUSALink-Lite** |
|---|---|---|
| Where the EDST is | In the app's own window | In a **CRC browser tab** (or any browser) |
| What you download | `vUSALink-App-vX.Y.Z.zip` (exe + 3 DLLs) | `vUSALink-Lite-vX.Y.Z.zip` (one small exe) |
| Runs as | A window | An icon in the system tray, no window |
| CRC mirroring, `/U` trim, UC, handoffs, CIDs from QF | Yes | Yes, the same code |
| CRC ECHO (EDST uplinks typed into CRC) | Yes | Not yet |

Use one or the other. If both are running, only the first one started watches CRC, so a command is never handled twice.

## vUSALink-Lite (EDST in a CRC tab)

1. Download `vUSALink-Lite-vX.Y.Z.zip` from [Releases](../../releases), unzip it anywhere and run `vUSALinkLite.exe`.
   A teal **L** appears in the system tray. Right-click it (or left-click) for the menu, where you can tick
   **Start with Windows** so it's always there.
2. Open the EDST in a CRC browser tab as usual (`https://vatflow.io/vusalink/edst/?crc=1`) and sign in.
3. The EDST toolbar shows **LINK** (teal) when it's connected to Lite. Commands you type in CRC now reach the EDST
   exactly as they do with the app. Results show in the EDST's MCA. If an uplink typed in CRC isn't sent, Lite also
   shows a Windows notification.

How it connects: Lite listens on `localhost` port 47613 on your PC only (never the network), and only accepts the
vatflow EDST pages. Lite only trims `/U` or clears a UC line in CRC while an EDST page is connected, so a command is
never silently lost when nothing can send it. Without Lite, the EDST in a CRC tab works exactly as before.

The tray menu shows whether CRC is running and the EDST is connected, plus the last command and the CIDs learned.
It also has **CRC mirroring** on/off, **Start with Windows**, **Open EDST in browser**, **Show log** and **Exit**.
The log is `vUSALinkLite.log` next to the exe.

## Download (vUSALink App)

1. Download the latest `vUSALink-App-vX.Y.Z.zip` from [Releases](../../releases).
2. Unzip it anywhere (for example `Documents\vUSALink App`). Keep the `.exe` and the three `.dll` files together.
3. Run `vUSALinkApp.exe` and sign in with VATSIM from the EDST, as on the website.

Needs Windows 10 or 11 with the Microsoft Edge WebView2 Runtime, which Windows 11 already includes. If it's missing,
the app tells you where to get it.

> **"Windows protected your PC" / antivirus warning:** the app isn't code-signed, and it uses a keyboard hook to see
> what you type into CRC. Windows SmartScreen and some antivirus programs flag that combination. Click
> **More info → Run anyway**, or build it yourself from this source. The hook only reads keys while a CRC window is in
> the foreground. Keystrokes are never stored or sent anywhere, and only the commands below are passed to the EDST.

### Updates

From v0.3.4 the app checks GitHub for a newer release when it starts, and every 6 hours while it runs. If there is one,
an amber **UPDATE vX.Y.Z** button appears at the left of the top bar. Click it: the app downloads the new zip,
swaps its files and restarts, in a few seconds. vUSALink-Lite does the same from its tray menu (**Update to vX.Y.Z**)
and shows a notification when an update is out. Nothing installs without your click, so it never restarts in the
middle of a session on its own.

- Settings and learned CIDs (`%LOCALAPPDATA%USALinkApp`) are kept.
- If anything goes wrong, the old files are put back and the app keeps running the version you had. The app then
  offers the download page.
- The app has to be able to write to its own folder. Under `Program Files` it can't, so update by hand there, or
  move the app to a folder you own.
- No internet, or GitHub unreachable: no button, the app starts as usual.
- Versions before v0.3.4 can't update themselves. Download v0.3.4 or later by hand once.

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
| `QS /M.<nn> <callsign> /U` | type it | `MAINTAIN M.<nn>` (`/M80` works too) | `QS /M.80 DAL123 /U` → `MAINTAIN M.80` |

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

### Crossing restrictions: `UC`

ERAM's crossing restriction uplink. Type it in CRC's MCA like any command. `UC` is itself the uplink, so no `/U` is
needed. CRC has no `UC` command, so when you press Enter **the app clears the line from CRC's MCA** (CRC never sees it
and shows no error) and the EDST sends it.

| Command | What the pilot receives | ACL |
|---|---|---|
| `UC VUH A100 AAL002` | `CROSS VUH AT AND MAINTAIN 10,000ft`, plus the destination altimeter below FL180 | Coded under the altitude |
| `UC FAY A240 AWE228` | `CROSS FAY AT AND MAINTAIN FL240` | Coded under the altitude |
| `UC VUH S250 AAL002` | `CROSS VUH AT 250KT` | Coded under the speed |
| `UC APRIL M.80 NASA242` | `CROSS APRIL AT M.80` | Coded under the speed |
| `UC STV A100 AAL002/UAL262/AWE121` | One uplink to each aircraft | Each row |

- **Position:** a fix, navaid or fix-radial-distance (`AUS030015`).
- **Reasons:** `/TFC`, `/WX` and `/RES` add `DUE TO …` (`UC FAY A240 /TFC AWE228`).
- **CIDs** work in place of callsigns once learned from a QF, including in a `/`-separated list.
- **Same checks** as other uplinks: signed in and verified, aircraft on CPDLC. Otherwise the EDST rejects it and nothing
  is sent.
- **Requires** the EDST page with host API version 2. With an older page the app leaves UC lines alone in CRC.

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
  takes a picture of CRC's ERAM window and runs Windows' built-in text recognition (OCR) on it. It keeps looking for
  up to 3.5 s until the new readout appears (CRC draws it a moment after the QF), and if the readout's first line is
  garbled it reads that spot again enlarged. The picture is processed in memory and never saved or sent; only the
  parsed readout line is logged.
- **Checked before it's trusted:** the readout's callsign must be a real flight in the VATSIM data with a matching type
  or beacon code. A single misread character (OCR can read W as H) is corrected only when exactly one real flight fits.
  On a test server (flights not on VATSIM) pairs are kept unverified; the EDST still checks the callsign exists.
- **Unknown CID:** a `/U` uplink with a CID the app hasn't learned sends nothing, and the top bar says
  `CID 151 NOT KNOWN - QF IT IN CRC FIRST`. Other lines with unknown CIDs (such as routine handoffs) are skipped quietly.
- **Shown in the EDST:** learned CIDs appear in the Aircraft List left of the callsign (`266 UAL1123`). When another
  sector owns the track, its number follows the callsign (`266 UAL1123 (01)`). Aircraft on your frequency or owned
  by your own sector show no number. The owner comes from the last QF, so QF again to refresh it.
- **How long a pair lasts:** a CRC CID belongs to one pilot connection under one callsign, so the app keeps a pair
  for as long as that connection is on VATSIM, even across app restarts. It is saved in
  `%LOCALAPPDATA%\vUSALinkApp\cids.json`. A pair is dropped when the pilot reconnects (VATSIM logon time changes) or has
  been gone from VATSIM for 3 minutes; the app checks every 30 seconds. QF the aircraft again after a reconnect. On a
  test server (flights not on VATSIM) pairs are kept for 12 hours. A newer QF always replaces older pairs for that
  CID or callsign.

### What isn't mirrored

- **Lines you edited in CRC with cursor keys, Home/End, Delete or a Ctrl shortcut (such as Ctrl+R recall).** The app
  can't be sure what CRC's MCA then holds, so it passes nothing on and doesn't touch CRC's line. The top bar shows
  `LINE EDITED IN CRC - NOT MIRRORED, RETYPE IT`. Backspace is fine.
- **Commands you finish by clicking an aircraft in CRC.** The app only sees typed text, so it never gets the callsign.
  Type the callsign.
- **CRC CIDs the app hasn't seen a QF for.** QF the aircraft once (see above), or type the callsign.
- **CRC dot-aliases, data block work and everything else.** Ignored.

## CRC ECHO: EDST uplinks typed into CRC

The other direction. With **CRC ECHO** on (top bar, off by default), an uplink you send from the EDST is also
typed into CRC as the matching ERAM command, so CRC's data block matches what the pilot was told. For example,
DAL123 direct LUNNI from the route menu types `QU LUNNI DAL123` into CRC's MCA and presses Enter.

| Sent from the EDST | Typed into CRC |
|---|---|
| Altitude (ALT menu or `QZ … /U`), block altitude | `QZ 350 DAL123`, `QZ 240B260 DAL123` |
| Temporary altitude (ALT menu with **TEMP**, or `QQ … /U`) | `QQ 110 DAL123` |
| Code (CODE menu) | `QB 3456 DAL123` |
| Heading (HDG menu, LT/RT too, or `QS … /U`) | `QS 270 DAL123` |
| Speed in knots (SPD menu or `QS /… /U`) | `QS /280 DAL123` |
| Direct (route menu, `WHEN ABLE` too, or `QU … /U`) | `QU LUNNI DAL123` |
| Route (route menu, edited route) | `AM DAL123 RTE KBOS.SSOXS7.SSOXS.Q167.ZIZZI..KNUKK` |

The commands follow CRC's ERAM command reference (docs.virtualnas.net/crc/eram). A route typed with spaces is
converted to ERAM's dotted form (`.` next to an airway or procedure, `..` between fixes).

**AMEND, for any aircraft (CPDLC or not).** Each EDST menu (ALT, CODE, HDG, SPD, route) has an **AMEND** button.
Pick a value and press AMEND: the EDST updates its own row and the app types the command above into CRC, with
no CPDLC uplink. That's how to amend an aircraft that isn't on CPDLC. AMEND always types into CRC, even with
CRC ECHO off. With nothing selected, AMEND removes the last queued uplink as before. In a plain browser or a
CRC tab (no app), AMEND updates the EDST only.

- **Only after the uplink went out.** A rejected or failed uplink types nothing.
- **Not echoed:** commands that came from CRC (they're already there), Mach speeds and "or greater/less" speeds
  and present heading (CRC has no typed form for them; they're menu-only in CRC), conditional directs (AT fix /
  AT altitude), crossing restrictions (CRC has no UC), departure clearances and weather deviations.
- **How:** CRC has no way for other programs to give it commands, and it only takes keys while its window has
  focus. So the app briefly brings CRC's ERAM window to the front, types the line, and gives focus back: a
  blink of about 0.2 s.
- **Which window:** the CRC ERAM window you used last (or the ERAM window that owns the CRC popup you used last).
  If that isn't known, the ERAM window that was in front most recently. With several ERAM windows open, click in
  the one you work in once so the app knows.
- **When it won't type** (the top bar says `NOT SENT TO CRC` and why; the uplink itself still went out):
  - you typed something into CRC's MCA and haven't entered it (part-way through a command). If the app only
    isn't sure what the line holds (after a click or a key it can't follow, such as an arrow key), it presses
    Esc in CRC first to clear the line, then types
  - no CRC window is showing an ERAM display (it never types into the Messages or Controllers windows, or a CRC
    window showing a browser tab)
  - Windows didn't hand focus to CRC within half a second
  - CRC runs as administrator, or vUSALink-Lite is the one watching CRC
- **It changes CRC for real.** A typed `QZ`/`QU` amends the flight plan on vNAS, where other controllers see it,
  exactly as if you had typed it.
- Needs the EDST page with host API version 3 (vatflow, November 2026 or later).

## Top bar

| Item | Effect |
|---|---|
| `CRC ●/○` | Whether CRC is running |
| CRC MIRROR | Turn CRC mirroring on or off (on by default) |
| CRC ECHO | Type uplinks sent from the EDST into CRC (off by default, see [CRC ECHO](#crc-echo-edst-uplinks-typed-into-crc)) |
| CRC BAR | Switch the EDST to its compact CRC layout (`?crc=1`) |
| ON TOP | Keep the app (and its pop-ups) above CRC |
| − / + | Zoom (Ctrl+wheel works too) |
| RELOAD / BROWSER | Reload the latest EDST from vatflow, skipping cached copies (F5 is a normal reload) / open the current page in your browser |

Keys: **F11** full screen, **F12** developer tools.

Settings (window position, zoom, toggles) are in `%LOCALAPPDATA%\vUSALinkApp\settings.ini`:

| Setting | Default | |
|---|---|---|
| `Url` | `https://vatflow.io/vusalink/edst/` | EDST page to load. A URL on the command line overrides it |

## How the app talks to the EDST

The EDST page publishes `window.vUSALinkEDST`:

| Member | |
|---|---|
| `version` | `3`. Bumped only if the API itself changes. Version 2 added ERAM handoffs, UC and `setCids`; version 3 added `setHost` (CRC echo) |
| `command(text, {source})` | Runs an ERAM command the same way the EDST MCA does. Returns `{ok, action: 'uplink' \| 'amend' \| 'handoff' \| 'ignored', flid, error}` (`handoff` also has `sector`, `facility`, `freq`). With `source: 'crc'` the callsign must be typed (no fallback to the selected row), and commands the EDST doesn't handle come back `ignored` without touching the MCA |
| `setCids(map)` | v2. `{CALLSIGN: {cid, sector}}` learned from CRC QF readouts. Replaces the page's table; the page shows the CID left of the callsign and `(sector)` when another sector owns the track |
| `setHost({crcEcho})` | v3. With `crcEcho: true` the page posts `{t: 'crcEcho', text: 'QU LUNNI DAL123'}` to the app (WebView2 `postMessage`) after each uplink sent from the EDST that has an ERAM equivalent. Commands that came in through `command()` are never echoed |
| `status()` | `{signedIn, canControl, testMode, listMode, refreshMs, mySectors}` |

Everything else in the page is internal and free to change. If the loaded page has no host API, the app passes
nothing on and leaves CRC's line alone (the top bar shows `EDST HOST API NOT FOUND`). vUSALink-Lite talks to the
page through the same host API, over its local link.

## Limits

- **CRC and the app must run at the same level.** If CRC was started as administrator and the app wasn't, Windows
  hides what you type and click in CRC from the app: nothing is mirrored and QF isn't read. The top bar then shows
  `CRC ● RUNS AS ADMIN` (Lite shows a notification). Start CRC normally from the Start menu, or run the app as
  administrator too.
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
Release with the zips. Before tagging, set `AppVersion.Current` in `src/Shared/Updater.cs` to the same version
(CI refuses a tag that doesn't match, since the update check compares the two).

Debug switches (environment variables):

| Variable | Effect |
|---|---|
| `VUSALINK_SNAPSHOT=<file.png>` | Save a picture of the page once it loads, then exit |
| `VUSALINK_INJECT=<command>` | Send a command through the CRC path first (`{CS}` = first aircraft on the list) |
| `VUSALINK_PRE` / `VUSALINK_POST` | Page scripts to run before / after it |
| `VUSALINK_PROBE=<seconds>` | Log when the page refreshes traffic |
| `VUSALINK_CRC_PROCESS=<name>` | Treat another process as CRC (for testing mirroring without CRC) |
| `VUSALINK_QFREAD=1` | Read CRC's Response Area once at start-up, as after a QF |
| `VUSALINK_FAKE_VERSION=<x.y.z>` | Pretend to be that version (to test updating: it offers the latest release) |
| `VUSALINK_UPDATE_SELFTEST=1` | vUSALink-Lite only: install an available update straight away, without a click |
| `VUSALINK_QFDUMP=<folder>` | Save every capture of CRC taken after a QF, and the text OCR read from it, to that folder. A `qfdump` folder next to the exe does the same. Captures show all of CRC's windows, so delete them afterwards |

`build.ps1 -Out <folder>` builds into another folder, for example while the app in `bin\` is running.

The log is `bin\vUSALinkApp.log`.

## License

[MIT](LICENSE). The WebView2 DLLs in `lib\` are under Microsoft's license (`lib\WebView2-LICENSE.txt`).
