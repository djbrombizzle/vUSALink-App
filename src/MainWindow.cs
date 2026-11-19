using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace VusaLinkApp
{
    static class Theme
    {
        // Colours from the vUSAlink EDST page (:root in /vusalink/edst/).
        public static readonly Brush Bg = Hex("#000000"), Fg = Hex("#d2d2cd"), Dim = Hex("#8a9088"), Line = Hex("#333333"),
            Teal = Hex("#2e9c9c"), TealInk = Hex("#06201f"), Green = Hex("#39d24b"), Amber = Hex("#e0913f"),
            Yellow = Hex("#dcd63f"), Red = Hex("#e0483b");
        public static readonly FontFamily Mono = new FontFamily("Consolas, Lucida Console, Courier New");

        static Brush Hex(string h)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
            b.Freeze();
            return b;
        }
    }

    class MainWindow : Window
    {
        readonly Settings cfg = Settings.Load();
        readonly string urlOverride;
        readonly WebView2 web = new WebView2();
        readonly TextBlock status = new TextBlock(), crcState = new TextBlock(), last = new TextBlock();
        readonly CrcSync sync = new CrcSync();
        readonly CidBook cids = new CidBook();
        int readGen;
        string lastLearned = "";
        DateTime lastLearnedAt = DateTime.MinValue;
        readonly DispatcherTimer crcTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        Border btnMirror, btnCrcBar, btnTop;
        CoreWebView2Environment env;
        bool ready, retryPending;
        int apiVersion;              // vUSALinkEDST host API version on the loaded page (0 = older page)
        int forwarded;

        public MainWindow(string url)
        {
            urlOverride = url;
            Title = "vUSALink App";
            Background = Theme.Bg;
            Width = cfg.Width; Height = cfg.Height;
            Topmost = cfg.Topmost;
            if (!double.IsNaN(cfg.Left) && !double.IsNaN(cfg.Top) && OnScreen(cfg.Left, cfg.Top, cfg.Width))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = cfg.Left; Top = cfg.Top;
            }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (cfg.Maximized) WindowState = WindowState.Maximized;
            Icon = MakeIcon();

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var strip = BuildStrip();
            Grid.SetRow(strip, 0);
            Grid.SetRow(web, 1);
            web.DefaultBackgroundColor = System.Drawing.Color.Black;
            grid.Children.Add(strip);
            grid.Children.Add(web);
            Content = grid;

            Loaded += async (s, e) => await InitWeb();
            Closing += (s, e) => SaveWindow();
            Closed += (s, e) => { sync.Dispose(); crcTimer.Stop(); };
            PreviewKeyDown += OnKey;

            sync.Enabled = cfg.Mirror;
            // /U is trimmed off before CRC gets the line; a UC line (CRC has no UC) is cleared from CRC entirely,
            // but only when the loaded EDST takes UC (host API v2), so a line never just disappears.
            sync.TrimForCrc = raw => CrcForward.CrcTrimLength(raw, ready && apiVersion >= 2 && cfg.Mirror);
            // Log what happens on Enter in CRC. Only EDST commands are written out; anything else (chat etc.)
            // is logged as its length only.
            sync.Diagnose = (raw, erase) => Log.Info(CrcForward.Gate(raw) != null || CrcForward.Handoff(raw) != null
                    || raw.TrimEnd().EndsWith("/U")
                ? "CRC Enter: [" + raw + "] erase=" + erase
                : "CRC Enter: non-EDST line (" + raw.Trim().Length + " chars)");
            var crcProc = Environment.GetEnvironmentVariable("VUSALINK_CRC_PROCESS");
            if (!string.IsNullOrEmpty(crcProc)) sync.ProcessName = CrcScreen.ProcessName = crcProc;
            sync.ReadoutLikely += () => ReadQfSoon(++readGen, 400, true);
            cids.Changed += () => Dispatcher.BeginInvoke(new Action(PushCids));
            sync.CommandSkipped += cmd => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (CrcForward.Gate(cmd) == null) return;
                Log.Info("Edited line not mirrored: " + cmd);
                last.Text = "✗ LINE EDITED IN CRC - NOT MIRRORED, RETYPE IT";
                last.Foreground = Theme.Amber;
                last.ToolTip = "Cursor keys, Delete or a Ctrl shortcut were used, so the app can't be sure what CRC's MCA held";
            }));
            sync.CommandEntered += cmd => Dispatcher.BeginInvoke(new Action(() => OnCrcCommand(cmd)));
            sync.Start();
            crcTimer.Tick += (s, e) => UpdateCrcState();
            crcTimer.Start();
            UpdateCrcState();
            // A learned CID lasts for that pilot connection; every 30 s drop pairs whose connection ended or changed.
            cidTimer.Tick += (s, e) => { if (cids.Count > 0) Task.Run(() => cids.Maintain()); };
            cidTimer.Start();
            Task.Run(() => cids.Maintain());
        }

        readonly DispatcherTimer cidTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };

        // ---------------------------------------------------------------- strip (top bar)

        FrameworkElement BuildStrip()
        {
            var dock = new DockPanel { LastChildFill = true, Height = 22, Background = Theme.Bg };
            var border = new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = dock };

            var title = Label("vUSALINK APP", Theme.Teal);
            title.FontWeight = FontWeights.Bold;
            title.Margin = new Thickness(8, 0, 12, 0);
            DockPanel.SetDock(title, Dock.Left);
            dock.Children.Add(title);

            var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 4, 0) };
            DockPanel.SetDock(right, Dock.Right);
            btnMirror = Button("CRC MIRROR", "Forward QZ / QQ / QU / QS commands with /U typed in CRC to the EDST MCA", ToggleMirror);
            btnCrcBar = Button("CRC BAR", "Switch the EDST page to its compact CRC layout (?crc=1)", ToggleCrcBar);
            btnTop = Button("ON TOP", "Keep this window above CRC", ToggleTop);
            right.Children.Add(btnMirror);
            right.Children.Add(btnCrcBar);
            right.Children.Add(btnTop);
            right.Children.Add(Button("−", "Zoom out (Ctrl+-)", () => SetZoom(web.ZoomFactor - 0.1)));
            right.Children.Add(Button("+", "Zoom in (Ctrl+=)", () => SetZoom(web.ZoomFactor + 0.1)));
            right.Children.Add(Button("RELOAD", "Reload EDST (F5)", Navigate));
            right.Children.Add(Button("BROWSER", "Open this EDST page in your web browser", OpenInBrowser));
            dock.Children.Add(right);

            crcState.FontFamily = last.FontFamily = status.FontFamily = Theme.Mono;
            crcState.FontSize = last.FontSize = status.FontSize = 12;
            crcState.VerticalAlignment = last.VerticalAlignment = status.VerticalAlignment = VerticalAlignment.Center;
            crcState.Margin = new Thickness(0, 0, 12, 0);
            last.Margin = new Thickness(0, 0, 12, 0);
            last.Foreground = Theme.Dim;
            status.Foreground = Theme.Dim;
            status.TextTrimming = TextTrimming.CharacterEllipsis;
            var mid = new StackPanel { Orientation = Orientation.Horizontal };
            mid.Children.Add(crcState);
            mid.Children.Add(last);
            mid.Children.Add(status);
            dock.Children.Add(mid);

            RefreshButtons();
            return border;
        }

        static TextBlock Label(string text, Brush fg)
        {
            return new TextBlock
            {
                Text = text, Foreground = fg, FontFamily = Theme.Mono, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        static Border Button(string text, string tip, Action click)
        {
            var b = new Border
            {
                Child = Label(text, Theme.Fg),
                BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(3, 2, 0, 2),
                Background = Theme.Bg, Cursor = Cursors.Hand, ToolTip = tip
            };
            b.MouseEnter += (s, e) => b.BorderBrush = Theme.Teal;
            b.MouseLeave += (s, e) => b.BorderBrush = Theme.Line;
            b.MouseLeftButtonUp += (s, e) => click();
            return b;
        }

        static void SetOn(Border b, bool on)
        {
            b.Background = on ? Theme.Teal : Theme.Bg;
            ((TextBlock)b.Child).Foreground = on ? Theme.TealInk : Theme.Fg;
        }

        void RefreshButtons()
        {
            SetOn(btnMirror, cfg.Mirror);
            SetOn(btnCrcBar, cfg.CrcBar);
            SetOn(btnTop, cfg.Topmost);
        }

        void SetStatus(string text, Brush color)
        {
            status.Text = text;
            status.Foreground = color ?? Theme.Dim;
        }

        void UpdateCrcState()
        {
            bool running = Process.GetProcessesByName("CRC").Length > 0;
            crcState.Text = running ? "CRC ●" : "CRC ○";
            crcState.Foreground = running ? Theme.Green : Theme.Dim;
            crcState.ToolTip = (running
                ? (cfg.Mirror ? "CRC is running - /U uplink commands typed in CRC are forwarded" : "CRC is running - mirroring is off")
                : "CRC is not running") + " · " + cids.Count + " CID(s) learned from QF";
        }

        // ---------------------------------------------------------------- WebView2

        string StartUrl()
        {
            var u = urlOverride ?? cfg.Url;
            if (!u.Contains("://")) u = "https://" + u;
            var ub = new UriBuilder(u);
            var q = (ub.Query ?? "").TrimStart('?');
            var parts = q.Split('&').Where(p => p != "" && !p.StartsWith("crc=")).ToList();
            if (cfg.CrcBar) parts.Add("crc=1");
            ub.Query = string.Join("&", parts);
            return ub.Uri.ToString();
        }

        async Task InitWeb()
        {
            try
            {
                var data = Path.Combine(Settings.Dir, "WebView2");
                Directory.CreateDirectory(data);
                env = await CoreWebView2Environment.CreateAsync(null, data,
                    new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
                await web.EnsureCoreWebView2Async(env);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                MessageBox.Show("The Microsoft Edge WebView2 Runtime is required.\n\nInstall it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and start vUSALink App again.",
                    "vUSALink App");
                Close();
                return;
            }
            var core = web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.DocumentTitleChanged += (s, e) =>
                Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "vUSALink App" : core.DocumentTitle + " - vUSALink App";
            core.NewWindowRequested += OnNewWindow;
            core.WebMessageReceived += OnWebMessage;
            core.NavigationStarting += (s, e) => SetStatus("LOADING " + Short(e.Uri), Theme.Dim);
            core.NavigationCompleted += OnNavigated;
            web.ZoomFactor = Math.Max(0.5, Math.Min(2.5, cfg.Zoom));
            ready = true;
            Navigate();
        }

        void Navigate()
        {
            if (!ready) return;
            var u = StartUrl();
            Log.Info("Navigate " + u);
            web.CoreWebView2.Navigate(u);
        }

        async void OnNavigated(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var core = web.CoreWebView2;
            if (e.IsSuccess)
            {
                SetStatus(Short(core.Source), Theme.Dim);
                apiVersion = await DetectHostApi();
                if (apiVersion >= 2) PushCids();
                if (apiVersion < 1)
                {
                    // Older EDST page without the host API: patch it the old way (remove after one release).
                    await LegacyApplyRefreshRate();
                    await LegacyInstallAltMenuHook();
                }
                var snap = Environment.GetEnvironmentVariable("VUSALINK_SNAPSHOT");
                if (!string.IsNullOrEmpty(snap)) await Snapshot(snap);
                return;
            }
            if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
            Log.Info("Navigation failed: " + e.WebErrorStatus);
            SetStatus("OFFLINE (" + e.WebErrorStatus + ") - retrying in 15 s", Theme.Red);
            if (retryPending) return;
            retryPending = true;
            await Task.Delay(15000);
            retryPending = false;
            Navigate();
        }

        // The web EDST is the standard; this app is a shell around it. The page publishes a small, stable host API
        // (window.vUSALinkEDST: version, command(), status()) and owns all EDST behaviour: what a command means,
        // amendments, the altitude menu, the refresh rate. The app only passes on what's typed in CRC.
        async Task<int> DetectHostApi()
        {
            try
            {
                var r = await web.CoreWebView2.ExecuteScriptAsync(
                    "(window.vUSALinkEDST&&typeof vUSALinkEDST.command==='function'&&vUSALinkEDST.version)||0");
                int v;
                int.TryParse(r, out v);
                Log.Info("EDST host API version " + v);
                return v;
            }
            catch (Exception ex) { Log.Info("Host API check failed: " + ex.Message); return 0; }
        }

        // ---- Legacy: EDST pages from before the host API (vatflow before Nov 2026). Remove after one release. ----

        // Re-schedules the page's own refresh at the VATSIM feed cadence (the old page used 30 s).
        async Task LegacyApplyRefreshRate()
        {
            if (cfg.RefreshSeconds <= 0) return;
            int sec = Math.Max(5, cfg.RefreshSeconds);
            var js =
                "(function(ms){if(typeof scheduleVatsim!=='function'||typeof refreshVatsim!=='function')return 'no-edst';" +
                "scheduleVatsim=function(){if(vatsimTimer)clearInterval(vatsimTimer);" +
                "vatsimTimer=setInterval(function(){refreshVatsim(!!listMode());},listMode()?4000:ms);};" +
                "if(vatsimTimer)scheduleVatsim();return 'ok';})(" + (sec * 1000) + ")";
            try
            {
                var r = await web.CoreWebView2.ExecuteScriptAsync(js);
                Log.Info("Refresh rate " + sec + " s: " + r);
            }
            catch (Exception ex) { Log.Info("Refresh rate inject failed: " + ex.Message); }
        }

        // When the EDST altitude menu opens for an aircraft whose altitude was amended from CRC, pre-select that
        // altitude (the EDST's own stageAlt) so UPLINK is one click. One-shot per amendment.
        async Task LegacyInstallAltMenuHook()
        {
            var js =
                "(function(){if(window.__edstcAltHook||typeof openAltMenu!=='function'||typeof stageAlt!=='function')return 'skip';" +
                "window.__edstcAltHook=1;var orig=openAltMenu;" +
                "openAltMenu=function(i,x,y){orig(i,x,y);try{var c=altTarget&&altTarget.cs,m=window.__crcAsg;" +
                "if(c&&m&&m[c]!=null){if(assigned[c]===m[c])stageAlt(m[c]);delete m[c];}}catch(e){}};return 'ok';})()";
            try { Log.Info("Alt menu hook: " + await web.CoreWebView2.ExecuteScriptAsync(js)); }
            catch (Exception ex) { Log.Info("Alt menu hook failed: " + ex.Message); }
        }

        // Debug: VUSALINK_SNAPSHOT=<file.png> captures the page once loaded, then exits.
        // VUSALINK_INJECT=<command> runs a command through the CRC forwarding path first.
        async Task Snapshot(string file)
        {
            await Task.Delay(8000);
            // VUSALINK_QFREAD=1: read CRC's Response Area once (as after a QF) before anything else.
            if (Environment.GetEnvironmentVariable("VUSALINK_QFREAD") == "1")
            {
                var sw = Stopwatch.StartNew();
                ReadQfSoon(++readGen, 0, false);
                await Task.Delay(3000);
                Log.Info("QF read test done (" + sw.ElapsedMilliseconds + " ms incl. wait), top bar: " + last.Text);
            }
            // VUSALINK_PRE / VUSALINK_POST: page scripts run first / after the injected command.
            var pre = Environment.GetEnvironmentVariable("VUSALINK_PRE");
            if (!string.IsNullOrEmpty(pre)) { Log.Info("Pre: " + await web.CoreWebView2.ExecuteScriptAsync(pre)); await Task.Delay(4000); }
            // VUSALINK_PROBE=<seconds>: log when the page actually refreshes traffic during that window
            // (also keeps the app open that long, e.g. while a stand-in CRC types).
            int probe;
            if (int.TryParse(Environment.GetEnvironmentVariable("VUSALINK_PROBE"), out probe) && probe > 0)
            {
                await web.CoreWebView2.ExecuteScriptAsync(
                    "window.__t0=Date.now();window.__hits=[];var __o=refreshVatsim;" +
                    "refreshVatsim=function(f){window.__hits.push(Math.round((Date.now()-window.__t0)/1000));return __o(f);};");
                await Task.Delay(probe * 1000);
                Log.Info("Probe refreshVatsim at s: " + await web.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.__hits)"));
            }
            // {CS} in VUSALINK_INJECT becomes the first aircraft on the EDST list.
            var inject = Environment.GetEnvironmentVariable("VUSALINK_INJECT");
            if (!string.IsNullOrEmpty(inject))
            {
                if (inject.Contains("{CS}"))
                {
                    var cs = Json.Parse(await web.CoreWebView2.ExecuteScriptAsync("(displayList()[0]||{}).cs||''")) as string;
                    inject = inject.Replace("{CS}", cs ?? "");
                }
                OnCrcCommand(inject);
                await Task.Delay(3000);
            }
            var post = Environment.GetEnvironmentVariable("VUSALINK_POST");
            if (!string.IsNullOrEmpty(post)) { Log.Info("Post: " + await web.CoreWebView2.ExecuteScriptAsync(post)); await Task.Delay(1500); }
            using (var fs = File.Create(file))
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
            Log.Info("Snapshot written " + file + " status=" + status.Text + " last=" + last.Text);
            Close();
        }

        static bool InApp(string uri)
        {
            Uri u;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out u)) return false;
            var h = u.Host.ToLowerInvariant();
            return h == "vatflow.io" || h.EndsWith(".vatflow.io") || h == "vatflow.net" || h.EndsWith(".vatflow.net")
                || h.EndsWith("vatsim.net") || u.Scheme == "about";
        }

        // vatflow pages (CPDLC map, list windows, sign-in) open in companion windows that share the
        // sign-in; anything else goes to the default browser.
        async void OnNewWindow(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            if (!InApp(e.Uri))
            {
                e.Handled = true;
                OpenExternal(e.Uri);
                return;
            }
            var deferral = e.GetDeferral();
            try
            {
                var pop = new PopupWindow(env, Topmost);
                pop.Owner = null;
                var feat = e.WindowFeatures;
                if (feat != null && feat.HasSize) { pop.Width = Math.Max(300, feat.Width); pop.Height = Math.Max(200, feat.Height + 30); }
                if (feat != null && feat.HasPosition) { pop.WindowStartupLocation = WindowStartupLocation.Manual; pop.Left = feat.Left; pop.Top = feat.Top; }
                pop.Show();
                await pop.Ready;
                e.NewWindow = pop.Web.CoreWebView2;
                e.Handled = true;
            }
            catch (Exception ex) { Log.Info("Popup failed: " + ex.Message); }
            finally { deferral.Complete(); }
        }

        static void OpenExternal(string uri)
        {
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Info("Open external failed: " + ex.Message); }
        }

        void OpenInBrowser()
        {
            OpenExternal(ready && web.CoreWebView2.Source.StartsWith("http") ? web.CoreWebView2.Source : StartUrl());
        }

        static string Short(string u)
        {
            Uri x;
            return Uri.TryCreate(u, UriKind.Absolute, out x) ? x.Host + x.AbsolutePath : u;
        }

        // ---------------------------------------------------------------- CRC -> EDST

        void OnCrcCommand(string raw)
        {
            if (apiVersion >= 1) HostCommand(raw);
            else LegacyCommand(raw);
        }

        // Hands a CRC command to the EDST's host API. The page decides what it means (/U uplink, plain QZ
        // amendment, reject) and shows its own MCA feedback; commands it doesn't handle come back "ignored".
        void HostCommand(string raw)
        {
            // A CRC CID in the FLID position is swapped for the callsign learned from a QF readout.
            string cid, unknownCid;
            var resolved = CrcForward.ResolveCid(raw, c => { var e = cids.Find(c); return e != null ? e.Callsign : null; },
                out cid, out unknownCid);
            if (resolved == null)
            {
                Log.Info("CID " + unknownCid + " not known: " + raw.Trim());
                if (CrcForward.IsUplinkLine(raw))
                {
                    last.Text = "✗ CID " + unknownCid + " NOT KNOWN - QF IT IN CRC FIRST (NOTHING SENT)";
                    last.Foreground = Theme.Red;
                    last.ToolTip = "The app learns CID = callsign from QF readouts in CRC's Response Area (typed QF or middle-click on a data block)";
                }
                return;
            }
            // v2 pages also take ERAM handoffs ("11 AAL004") and mark CPDLC aircraft for a one-click CONTACT.
            var cmd = CrcForward.Gate(resolved) ?? (apiVersion >= 2 ? CrcForward.Handoff(resolved) : null);
            if (cmd == null || !ready) return;
            if (CrcForward.IsUc(cmd) && apiVersion < 2) return;   // older EDST pages don't know UC
            if (cid != null) Log.Info("CID " + cid + " -> " + cmd);
            var js =
                "(function(c){var post=function(o){o.t='host';o.cmd=c;window.chrome.webview.postMessage(JSON.stringify(o));};" +
                "if(!window.vUSALinkEDST){post({ok:false,error:'EDST NOT READY'});return;}" +
                "vUSALinkEDST.command(c,{source:'crc'}).then(function(r){r=r||{};" +
                "post({ok:!!r.ok,action:r.action||'',error:r.error||'',flid:r.flid||'',sector:r.sector||'',facility:r.facility||'',freq:r.freq||''});})" +
                ".catch(function(){post({ok:false,error:'SEND FAIL'});});})(" + Json.Write(cmd) + ");";
            web.CoreWebView2.ExecuteScriptAsync(js);
        }

        // ---------------------------------------------------------------- CRC QF readouts -> CIDs

        // After a QF (typed, or middle-click on a data block) CRC prints the flight plan in its Response Area.
        // Read it shortly after; if nothing new is there yet, look once more.
        async void ReadQfSoon(int gen, int delayMs, bool retry)
        {
            await Task.Delay(delayMs);
            if (gen != readGen) return;   // a newer QF superseded this one
            List<Tuple<QfReadout, CidBook.Entry, string>> results;
            try { results = await Task.Run(() => ReadQf()); }
            catch (Exception ex) { Log.Info("QF read failed: " + ex.Message); return; }
            var learned = results.Where(r => r.Item2 != null).ToList();
            foreach (var r in results)
                Log.Info(r.Item2 != null
                    ? "QF read: " + r.Item1 + " -> CID " + r.Item1.Cid + " = " + r.Item2.Callsign + (r.Item2.Verified ? "" : " (not on VATSIM, unverified)")
                    : "QF read rejected: " + r.Item1 + " (" + r.Item3 + ")");
            var top = learned.LastOrDefault();
            if (top == null)
            {
                if (retry) ReadQfSoon(gen, 900, false);
                return;
            }
            var key = top.Item1.Cid + "=" + top.Item2.Callsign;
            if (key == lastLearned && (DateTime.UtcNow - lastLearnedAt).TotalSeconds < 20 && retry) { ReadQfSoon(gen, 900, false); }
            lastLearned = key; lastLearnedAt = DateTime.UtcNow;
            last.Text = "CID " + top.Item1.Cid + " = " + top.Item2.Callsign + " (" + top.Item2.Sector + ")";
            last.Foreground = Theme.Teal;
            last.ToolTip = "Learned from CRC's QF readout: you can now use " + top.Item1.Cid + " in place of " + top.Item2.Callsign +
                ". (" + top.Item2.Sector + ") is the sector that owns the track." + (top.Item2.Verified ? "" : " Not found on VATSIM, so not cross-checked.");
        }

        // Shows learned CIDs in the EDST: CID left of the callsign, and "(sector)" after it when another
        // sector owns the track (host API v2 setCids; the page decides what to show).
        async void PushCids()
        {
            if (!ready || apiVersion < 2) return;
            try
            {
                var r = await web.CoreWebView2.ExecuteScriptAsync(
                    "(function(m){return window.vUSALinkEDST&&vUSALinkEDST.setCids?vUSALinkEDST.setCids(m):-1;})(" +
                    Json.Write(cids.ForEdst()) + ")");
                Log.Info("CIDs sent to EDST: " + r);
            }
            catch (Exception ex) { Log.Info("CID push failed: " + ex.Message); }
        }

        List<Tuple<QfReadout, CidBook.Entry, string>> ReadQf()
        {
            var outp = new List<Tuple<QfReadout, CidBook.Entry, string>>();
            foreach (var bmp in CrcScreen.CaptureEramWindows())
                using (bmp)
                {
                    var lines = CrcScreen.Ocr(bmp).GetAwaiter().GetResult();
                    foreach (var r in QfReadout.Parse(lines))
                    {
                        string why;
                        var e = cids.Learn(r, out why);
                        outp.Add(Tuple.Create(r, e, why));
                    }
                }
            return outp;
        }

        void LegacyCommand(string raw)
        {
            int fl; string id;
            if (CrcForward.Amend(raw, out fl, out id)) { AmendAltitude(fl, id); return; }
            var cmd = CrcForward.Uplink(raw);
            if (cmd == null) return;
            Log.Info("CRC command forwarded: " + cmd);
            if (!ready)
            {
                ShowResult(cmd, false, "EDST NOT READY");
                return;
            }
            last.Text = "→ " + cmd;
            last.Foreground = Theme.Yellow;
            // mcaExecute is the EDST page's own MCA entry point: same parsing, auth, NO TRACK / NO CPDLC
            // checks and feedback as typing the command into the EDST MCA.
            var js =
                "(function(c){var post=function(o){o.t='mca';o.cmd=c;window.chrome.webview.postMessage(JSON.stringify(o));};" +
                "if(typeof mcaExecute!=='function'){post({ok:false,error:'EDST NOT READY'});return;}" +
                "mcaExecute(c).then(function(r){post({ok:!!(r&&r.ok),error:(r&&r.error)||'',flid:(r&&r.flid)||''});})" +
                ".catch(function(){post({ok:false,error:'SEND FAIL'});});})(" + Json.Write(cmd) + ");";
            web.CoreWebView2.ExecuteScriptAsync(js);
        }

        // Plain "QZ <alt> <FLID or CID>" in CRC: set the EDST assigned altitude locally (saved like any EDST
        // amendment, nothing sent). The next time the altitude menu is opened for that aircraft the new altitude
        // is already selected, so one click on UPLINK sends it.
        void AmendAltitude(int fl, string id)
        {
            var cmd = "QZ " + fl.ToString("000") + " " + id;
            Log.Info("CRC amendment: " + cmd);
            if (!ready) { ShowResult(cmd, false, "EDST NOT READY", true); return; }
            var js =
                "(function(fl,id){var post=function(o){o.t='amend';o.cmd='QZ '+('00'+fl).slice(-3)+' '+id;window.chrome.webview.postMessage(JSON.stringify(o));};" +
                "if(typeof assigned!=='object'||typeof renderBoard!=='function'||typeof mcaFindAircraft!=='function'){post({ok:false,error:'EDST NOT READY'});return;}" +
                "var a=mcaFindAircraft(id);" +
                "if(!a){var L=(typeof displayList==='function'?displayList():[]).concat(liveFlights||[]);" +
                "a=L.find(function(x){return x&&String(x.cid||'').toUpperCase()===id;})||null;}" +
                "if(!a){post({ok:false,error:id.length<=3?'CID NOT KNOWN TO EDST - USE CALLSIGN':'NO TRACK'});return;}" +
                "assigned[a.cs]=fl;window.__crcAsg=window.__crcAsg||{};window.__crcAsg[a.cs]=fl;" +
                "try{persist();}catch(e){}renderBoard();post({ok:true,flid:a.cs});" +
                "})(" + fl + "," + Json.Write(id) + ");";
            web.CoreWebView2.ExecuteScriptAsync(js);
        }

        void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var o = Json.Parse(e.TryGetWebMessageAsString());
                var t = Json.Str(o, "t");
                if (t != "mca" && t != "amend" && t != "host") return;
                var cmd = Json.Str(o, "cmd");
                var action = Json.Str(o, "action") ?? "";
                if (action == "ignored") { Log.Info("Ignored by EDST: " + cmd); return; }
                if (action == "handoff")
                {
                    var fac = Json.Str(o, "facility");
                    last.Text = "▶ " + Json.Str(o, "flid") + " HANDOFF (" + Json.Str(o, "sector") + ")" +
                        (string.IsNullOrEmpty(fac) ? " - CLICK ▶ IN EDST TO PICK FREQ" : " - CLICK ▶ IN EDST: CONTACT " + fac + " " + Json.Str(o, "freq"));
                    last.Foreground = Theme.Amber;
                    last.ToolTip = "On CPDLC: the EDST row shows ▶| (sector). Click it to send the frequency change.";
                    Log.Info("Handoff marked " + cmd + " " + fac + " " + Json.Str(o, "freq"));
                    return;
                }
                bool amend = t == "amend" || action == "amend";
                var flid = Json.Str(o, "flid");
                if (amend && !string.IsNullOrEmpty(flid) && !cmd.EndsWith(" " + flid)) cmd += " (" + flid + ")";
                ShowResult(cmd, Json.Bool(o, "ok") == true, Json.Str(o, "error"), amend);
            }
            catch (Exception ex) { Log.Info("Web message: " + ex.Message); }
        }

        void ShowResult(string cmd, bool ok, string error, bool amend = false)
        {
            if (ok && !amend) forwarded++;
            last.Text = (ok ? "✓ " : "✗ ") + cmd +
                (ok ? (amend ? "  AMENDED - UPLINK FROM ALT MENU" : "") : "  " + (error ?? "").ToUpperInvariant());
            last.Foreground = ok ? (amend ? Theme.Yellow : Theme.Green) : Theme.Red;
            last.ToolTip = amend
                ? (ok ? "Assigned altitude updated in EDST only - nothing sent to the pilot yet" : "EDST not updated")
                : ok ? forwarded + " CRC command(s) uplinked this session" : "Not sent - see the EDST MCA for details";
            Log.Info((amend ? "Amend result " : "Forward result ") + cmd + " ok=" + ok + " " + error);
        }

        // ---------------------------------------------------------------- toggles / keys

        void ToggleMirror()
        {
            cfg.Mirror = !cfg.Mirror;
            sync.Enabled = cfg.Mirror;
            RefreshButtons(); UpdateCrcState(); cfg.Save();
        }

        void ToggleCrcBar()
        {
            cfg.CrcBar = !cfg.CrcBar;
            RefreshButtons(); cfg.Save();
            Navigate();
        }

        void ToggleTop()
        {
            cfg.Topmost = !cfg.Topmost;
            Topmost = cfg.Topmost;
            foreach (var w in Application.Current.Windows.OfType<PopupWindow>()) w.Topmost = cfg.Topmost;
            RefreshButtons(); cfg.Save();
        }

        void SetZoom(double z)
        {
            web.ZoomFactor = Math.Round(Math.Max(0.5, Math.Min(2.5, z)), 2);
            SetStatus("ZOOM " + Math.Round(web.ZoomFactor * 100) + "%", Theme.Dim);
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11)
            {
                if (WindowStyle == WindowStyle.None) { WindowStyle = WindowStyle.SingleBorderWindow; WindowState = WindowState.Normal; }
                else { WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; }
                e.Handled = true;
            }
            else if (e.Key == Key.F12 && ready) { web.CoreWebView2.OpenDevToolsWindow(); e.Handled = true; }
        }

        void SaveWindow()
        {
            cfg.Maximized = WindowState == WindowState.Maximized;
            var r = cfg.Maximized ? RestoreBounds : new Rect(Left, Top, ActualWidth, ActualHeight);
            if (!r.IsEmpty && r.Width > 200) { cfg.Left = r.Left; cfg.Top = r.Top; cfg.Width = r.Width; cfg.Height = r.Height; }
            if (ready) cfg.Zoom = web.ZoomFactor;
            cfg.Save();
        }

        static bool OnScreen(double left, double top, double width)
        {
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
            double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
            return left + width - 80 > vl && left + 80 < vr && top >= vt - 10 && top + 40 < vb;
        }

        // A small teal "E" so the window is recognisable on the taskbar next to CRC.
        public static ImageSource MakeIcon()
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, new Pen(Theme.Teal, 2), new Rect(1, 1, 30, 30));
                var ft = new FormattedText("E", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(Theme.Mono, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 24, Theme.Teal, 1.0);
                dc.DrawText(ft, new Point(16 - ft.Width / 2, 16 - ft.Height / 2));
            }
            var bmp = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }
    }

    // Secondary window for pages the EDST opens with window.open / target=_blank (list windows, CPDLC map).
    class PopupWindow : Window
    {
        public readonly WebView2 Web = new WebView2();
        public readonly Task Ready;

        public PopupWindow(CoreWebView2Environment env, bool topmost)
        {
            Title = "vUSALink App";
            Background = Theme.Bg;
            Width = 1000; Height = 700;
            Topmost = topmost;
            Icon = MainWindow.MakeIcon();
            Web.DefaultBackgroundColor = System.Drawing.Color.Black;
            Content = Web;
            Ready = Web.EnsureCoreWebView2Async(env);
            Ready.ContinueWith(t =>
            {
                if (t.IsFaulted) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var core = Web.CoreWebView2;
                    core.DocumentTitleChanged += (s, e) => Title = core.DocumentTitle + " - vUSALink App";
                    core.WindowCloseRequested += (s, e) => Close();
                }));
            });
        }
    }
}
