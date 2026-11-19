using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace VusaLinkApp
{
    // vUSALink-Lite: the CRC side of vUSALink App without the EDST window. Runs in the system tray and feeds
    // the EDST page open in a CRC browser tab (or any browser) over a local link (see LinkServer).
    static class LiteProgram
    {
        public const string Version = "0.4.0";

        [STAThread]
        static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            bool first;
            using (var single = new Mutex(true, @"Local\vUSALink.Lite", out first))
            {
                if (!first)
                {
                    Forms.MessageBox.Show("vUSALink-Lite is already running (see the tray icon).", "vUSALink-Lite");
                    return;
                }
                var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) => { Log.Info("Unhandled: " + e.Exception); e.Handled = true; };
                var lite = new LiteMain();
                app.Startup += (s, e) => lite.Start();
                app.Exit += (s, e) => lite.Stop();
                app.Run();
            }
        }
    }

    class LiteMain
    {
        const string EdstUrl = "https://vatflow.io/vusalink/edst/?crc=1";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "vUSALink-Lite";

        readonly CrcSync sync = new CrcSync();
        readonly CidBook cids = new CidBook();
        readonly LinkServer link = new LinkServer(LiteProgram.Version);
        readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
        readonly DispatcherTimer statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        readonly DispatcherTimer cidTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        Forms.NotifyIcon tray;
        Forms.ToolStripMenuItem miCrc, miEdst, miLast, miMirror, miStartup;
        bool mirrorOwned, mirrorOn = true;
        int readGen;
        string lastText = "";

        public void Start()
        {
            Log.Info("vUSALink-Lite " + LiteProgram.Version + " starting");
            BuildTray();

            link.CurrentCids = () => cids.ForEdst();
            link.ConnectionsChanged += () => ui.BeginInvoke(new Action(UpdateStatus));
            if (!link.Start())
                Notify("Can't open the local link on port " + LinkServer.Port + " (" + link.StartError + "). Is another copy running?", true);

            cids.Changed += () => link.SendCids(cids.ForEdst());
            cidTimer.Tick += (s, e) => { if (cids.Count > 0) Task.Run(() => cids.Maintain()); };
            cidTimer.Start();
            Task.Run(() => cids.Maintain());

            // /U is trimmed off and UC lines are cleared from CRC's line only while an EDST page is connected,
            // so a command is never silently lost when nothing can send it.
            sync.TrimForCrc = raw => link.HasEdst ? CrcForward.CrcTrimLength(raw, link.ApiVersion >= 2) : 0;
            sync.Diagnose = (raw, erase) => Log.Info(CrcForward.Gate(raw) != null || CrcForward.Handoff(raw) != null || raw.TrimEnd().EndsWith("/U")
                ? "CRC Enter: [" + raw + "] erase=" + erase
                : "CRC Enter: non-EDST line (" + raw.Trim().Length + " chars)");
            sync.CommandEntered += cmd => ui.BeginInvoke(new Action(() => OnCrcCommand(cmd)));
            sync.CommandSkipped += cmd => ui.BeginInvoke(new Action(() =>
            {
                if (CrcForward.Gate(cmd) != null && CrcForward.IsUplinkLine(cmd))
                    Notify("Line edited in CRC (cursor keys / Delete / Ctrl): not sent. Retype it.", true);
            }));
            sync.ReadoutLikely += () => ReadQfSoon(++readGen, 400, true);
            var crcProc = Environment.GetEnvironmentVariable("VUSALINK_CRC_PROCESS");
            if (!string.IsNullOrEmpty(crcProc)) sync.ProcessName = CrcScreen.ProcessName = crcProc;

            mirrorOwned = CrcMirrorLock.TryAcquire();
            if (mirrorOwned) sync.Start();
            else Notify("vUSALink App is already watching CRC, so Lite won't. Close the app to use Lite.", true);

            statusTimer.Tick += (s, e) => UpdateStatus();
            statusTimer.Start();
            UpdateStatus();
            SelfTest();
        }

        // Debug: VUSALINK_LITE_SELFTEST="line1|line2|..." runs each line through exactly what happens on Enter in
        // CRC (the CRC-line decision, then the EDST command) without pressing any keys, logs the results and exits.
        // VUSALINK_LITE_SELFTEST_WAIT = seconds to wait first (default 10) so a test page can connect.
        async void SelfTest()
        {
            var lines = Environment.GetEnvironmentVariable("VUSALINK_LITE_SELFTEST");
            if (string.IsNullOrEmpty(lines)) return;
            int wait;
            if (!int.TryParse(Environment.GetEnvironmentVariable("VUSALINK_LITE_SELFTEST_WAIT"), out wait)) wait = 10;
            await Task.Delay(wait * 1000);
            Log.Info("SELFTEST start: EDST connected=" + link.HasEdst + " api=" + link.ApiVersion + " pages=" + link.PageCount);
            foreach (var line in lines.Split('|'))
            {
                int erase = sync.TrimForCrc(line);
                Log.Info("SELFTEST [" + line + "] CRC line: " +
                    (erase > 0 ? "trim " + erase + " chars, then Enter" : erase < 0 ? "cleared, no Enter" : "left alone"));
                OnCrcCommand(line);
                await Task.Delay(1500);
            }
            await Task.Delay(1500);
            Log.Info("SELFTEST done");
            System.Windows.Application.Current.Shutdown();
        }

        public void Stop()
        {
            sync.Dispose();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
        }

        // ---------------------------------------------------------------- CRC -> EDST

        async void OnCrcCommand(string raw)
        {
            if (!mirrorOn) return;
            bool relevant = CrcForward.Gate(raw) != null || CrcForward.Handoff(raw) != null;
            if (!relevant) return;
            if (!link.HasEdst)
            {
                Log.Info("No EDST connected: " + raw.Trim());
                if (CrcForward.IsUplinkLine(raw)) Notify("EDST not connected: open the EDST in a CRC tab. Nothing was sent.", true);
                return;
            }
            string cid, unknownCid;
            var resolved = CrcForward.ResolveCid(raw, c => { var e = cids.Find(c); return e != null ? e.Callsign : null; },
                out cid, out unknownCid);
            if (resolved == null)
            {
                Log.Info("CID " + unknownCid + " not known: " + raw.Trim());
                if (CrcForward.IsUplinkLine(raw)) Notify("CID " + unknownCid + " not known: QF it in CRC first. Nothing was sent.", true);
                return;
            }
            int api = link.ApiVersion;
            var cmd = CrcForward.Gate(resolved) ?? (api >= 2 ? CrcForward.Handoff(resolved) : null);
            if (cmd == null || (CrcForward.IsUc(cmd) && api < 2)) return;
            if (cid != null) Log.Info("CID " + cid + " -> " + cmd);
            SetLast("→ " + cmd);
            var r = await link.SendCommand(cmd);
            if (r == null) { Log.Info("No answer from EDST: " + cmd); Notify("The EDST didn't answer: " + cmd, true); return; }
            var action = Json.Str(r, "action") ?? "";
            bool ok = Json.Bool(r, "ok") == true;
            var error = Json.Str(r, "error") ?? "";
            Log.Info("EDST result " + cmd + " ok=" + ok + " action=" + action + " " + error);
            if (action == "ignored") return;
            if (action == "handoff")
            {
                var fac = Json.Str(r, "facility");
                SetLast("▶ " + Json.Str(r, "flid") + " (" + Json.Str(r, "sector") + ")" + (string.IsNullOrEmpty(fac) ? "" : " " + Json.Str(r, "freq")));
                return;
            }
            if (ok) SetLast((action == "amend" ? "AMENDED " : "✓ ") + cmd);
            else
            {
                SetLast("✗ " + cmd + " " + error);
                if (CrcForward.IsUplinkLine(cmd)) Notify(cmd + "\n" + error.ToUpperInvariant() + ": not sent", true);
            }
        }

        // ---------------------------------------------------------------- CRC QF readouts -> CIDs

        async void ReadQfSoon(int gen, int delayMs, bool retry)
        {
            await Task.Delay(delayMs);
            if (gen != readGen || !mirrorOn) return;
            List<Tuple<QfReadout, CidBook.Entry, string>> results;
            try
            {
                results = await Task.Run(() =>
                {
                    var outp = new List<Tuple<QfReadout, CidBook.Entry, string>>();
                    foreach (var bmp in CrcScreen.CaptureEramWindows())
                        using (bmp)
                            foreach (var rd in QfReadout.Parse(CrcScreen.Ocr(bmp).GetAwaiter().GetResult()))
                            {
                                string why;
                                outp.Add(Tuple.Create(rd, cids.Learn(rd, out why), why));
                            }
                    return outp;
                });
            }
            catch (Exception ex) { Log.Info("QF read failed: " + ex.Message); return; }
            foreach (var r in results)
                Log.Info(r.Item2 != null
                    ? "QF read: " + r.Item1 + " -> CID " + r.Item1.Cid + " = " + r.Item2.Callsign + (r.Item2.Verified ? "" : " (not on VATSIM, unverified)")
                    : "QF read rejected: " + r.Item1 + " (" + r.Item3 + ")");
            var top = results.LastOrDefault(r => r.Item2 != null);
            if (top == null) { if (retry) ReadQfSoon(gen, 900, false); return; }
            SetLast("CID " + top.Item1.Cid + " = " + top.Item2.Callsign + " (" + top.Item2.Sector + ")");
        }

        // ---------------------------------------------------------------- tray

        void BuildTray()
        {
            var menu = new Forms.ContextMenuStrip();
            var title = new Forms.ToolStripMenuItem("vUSALink-Lite " + LiteProgram.Version) { Enabled = false };
            miCrc = new Forms.ToolStripMenuItem("CRC: -") { Enabled = false };
            miEdst = new Forms.ToolStripMenuItem("EDST: -") { Enabled = false };
            miLast = new Forms.ToolStripMenuItem("Last: -") { Enabled = false };
            miMirror = new Forms.ToolStripMenuItem("CRC mirroring") { Checked = true, CheckOnClick = true };
            miMirror.CheckedChanged += (s, e) => { mirrorOn = miMirror.Checked; sync.Enabled = mirrorOn; UpdateStatus(); };
            miStartup = new Forms.ToolStripMenuItem("Start with Windows") { Checked = StartsWithWindows(), CheckOnClick = true };
            miStartup.CheckedChanged += (s, e) => SetStartWithWindows(miStartup.Checked);
            var open = new Forms.ToolStripMenuItem("Open EDST in browser", null, (s, e) => OpenUrl(EdstUrl));
            var logItem = new Forms.ToolStripMenuItem("Show log", null, (s, e) => { try { Process.Start(new ProcessStartInfo(Log.File_) { UseShellExecute = true }); } catch { } });
            var exit = new Forms.ToolStripMenuItem("Exit", null, (s, e) => System.Windows.Application.Current.Shutdown());
            menu.Items.AddRange(new Forms.ToolStripItem[]
            {
                title, new Forms.ToolStripSeparator(), miCrc, miEdst, miLast, new Forms.ToolStripSeparator(),
                miMirror, miStartup, open, logItem, new Forms.ToolStripSeparator(), exit
            });
            tray = new Forms.NotifyIcon { Icon = MakeIcon(), Visible = true, ContextMenuStrip = menu, Text = "vUSALink-Lite" };
            tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) ShowMenu(); };
        }

        void ShowMenu()
        {
            // Left-click opens the same menu as right-click.
            var mi = typeof(Forms.NotifyIcon).GetMethod("ShowContextMenu",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (mi != null) mi.Invoke(tray, null);
        }

        void UpdateStatus()
        {
            bool crc = Process.GetProcessesByName(sync.ProcessName).Length > 0;
            int pages = link.PageCount;
            bool edst = link.HasEdst;
            miCrc.Text = "CRC: " + (crc ? "running" : "not running") + (!mirrorOwned ? " (watched by vUSALink App)" : !mirrorOn ? " (mirroring off)" : "");
            miEdst.Text = "EDST: " + (edst ? "connected" + (pages > 1 ? " (" + pages + " pages)" : "") + ", host API v" + link.ApiVersion
                : link.StartError != null ? "link unavailable" : "not connected (open the EDST in a CRC tab)");
            miLast.Text = "Last: " + (lastText == "" ? "-" : lastText) + (cids.Count > 0 ? "   · " + cids.Count + " CID(s) learned" : "");
            var tip = "vUSALink-Lite · CRC " + (crc ? "●" : "○") + " · EDST " + (edst ? "●" : "○");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        void SetLast(string s)
        {
            lastText = s.Length > 70 ? s.Substring(0, 70) : s;
            UpdateStatus();
        }

        void Notify(string text, bool warn)
        {
            Log.Info("Notify: " + text);
            ui.BeginInvoke(new Action(() =>
            {
                if (tray == null) return;
                tray.BalloonTipTitle = "vUSALink-Lite";
                tray.BalloonTipText = text;
                tray.BalloonTipIcon = warn ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info;
                tray.ShowBalloonTip(5000);
            }));
        }

        static bool StartsWithWindows()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null;
        }

        static void SetStartWithWindows(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                    else k.DeleteValue(RunName, false);
                }
                Log.Info("Start with Windows: " + on);
            }
            catch (Exception ex) { Log.Info("Start with Windows failed: " + ex.Message); }
        }

        static void OpenUrl(string u)
        {
            try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
        }

        // Teal "L" on black, like the app's "E".
        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var pen = new Pen(Color.FromArgb(0x2e, 0x9c, 0x9c), 3))
                using (var font = new Font("Consolas", 20, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var br = new SolidBrush(Color.FromArgb(0x2e, 0x9c, 0x9c)))
                {
                    g.Clear(Color.Black);
                    g.DrawRectangle(pen, 1, 1, 29, 29);
                    var sz = g.MeasureString("L", font);
                    g.DrawString("L", font, br, 16 - sz.Width / 2, 16 - sz.Height / 2);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
