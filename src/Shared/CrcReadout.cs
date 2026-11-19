using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace VusaLinkApp
{
    // Reads ERAM QF readouts from CRC's Response Area so CRC CIDs can be used in mirrored commands.
    //
    // CRC draws its ERAM display with OpenGL, so there's no text to query: the app captures CRC's ERAM window
    // (PrintWindow works even when it's covered) and runs Windows' built-in OCR on it. Only the window is read,
    // only right after a QF (typed, or a middle-click on a data block), nothing is saved or sent anywhere.
    //
    // A QF readout looks like:   1926
    //                            151 SKW5646(32) E75L/L
    //                            6566 450 350 KORD..EARND ...
    // i.e. <CID> <ACID>(<owning sector>) <type/equip>, then <beacon> <speed> <altitude> <route>.
    static class CrcScreen
    {
        public static string ProcessName = "CRC";

        public static List<Bitmap> CaptureEramWindows()
        {
            var list = new List<Bitmap>();
            var old = SetThreadDpiAwarenessContext((IntPtr)(-4));   // per-monitor v2: real pixel sizes
            try
            {
                foreach (var h in FindEramWindows())
                {
                    RECT r;
                    if (!GetWindowRect(h, out r)) continue;
                    int w = r.Right - r.Left, ht = r.Bottom - r.Top;
                    if (w < 400 || ht < 300) continue;   // tool windows (voice switch, messages ...) aren't scopes
                    var bmp = new Bitmap(w, ht, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        var hdc = g.GetHdc();
                        try { PrintWindow(h, hdc, 2); }   // PW_RENDERFULLCONTENT
                        finally { g.ReleaseHdc(hdc); }
                    }
                    list.Add(bmp);
                }
            }
            finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
            return list;
        }

        static List<IntPtr> FindEramWindows()
        {
            var pids = new HashSet<uint>(Process.GetProcessesByName(ProcessName).Select(p => (uint)p.Id));
            var found = new List<IntPtr>();
            if (pids.Count == 0) return found;
            EnumWindows((h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains(pid) || !IsWindowVisible(h)) return true;
                // Any real-sized CRC window: CRC titles a display window "CRC : 1 : ERAM : ZDC", but just "CRC : 1"
                // when a browser tab (e.g. the EDST) is the selected tab, so the title can't be relied on.
                // Small tool windows are skipped by size in CaptureEramWindows.
                found.Add(h);
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // Captures CRC's windows, reads them and returns the QF readouts found. 'info' says what was seen, for the log.
        public static List<QfReadout> ReadReadouts(out string info)
        {
            var all = new List<QfReadout>();
            int wins = 0, lines = 0;
            foreach (var bmp in CaptureEramWindows())
                using (bmp)
                {
                    wins++;
                    var l = Ocr(bmp).GetAwaiter().GetResult();
                    lines += l.Length;
                    all.AddRange(QfReadout.Parse(l));
                }
            info = wins + " CRC window(s) captured, " + lines + " text line(s), " + all.Count + " QF readout(s)" +
                (wins == 0 ? " (no CRC window found)" : "");
            return all;
        }

        // Windows OCR on light-on-black ERAM text: invert to dark-on-light first.
        public static async Task<string[]> Ocr(Bitmap src)
        {
            var eng = OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            if (eng == null) throw new InvalidOperationException("Windows OCR is not available (no OCR language installed)");
            byte[] png;
            using (var inv = new Bitmap(src.Width, src.Height))
            {
                using (var g = Graphics.FromImage(inv))
                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(new ColorMatrix(new[]
                    {
                        new float[] { -0.33f, -0.33f, -0.33f, 0, 0 }, new float[] { -0.33f, -0.33f, -0.33f, 0, 0 },
                        new float[] { -0.33f, -0.33f, -0.33f, 0, 0 }, new float[] { 0, 0, 0, 1, 0 }, new float[] { 1, 1, 1, 0, 1 }
                    }));
                    g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
                using (var ms = new MemoryStream()) { inv.Save(ms, ImageFormat.Png); png = ms.ToArray(); }
            }
            var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var wr = new Windows.Storage.Streams.DataWriter(ras.GetOutputStreamAt(0));
            wr.WriteBytes(png);
            await AsTask<uint>(wr.StoreAsync());
            var dec = await AsTask(BitmapDecoder.CreateAsync(ras));
            var sbm = await AsTask(dec.GetSoftwareBitmapAsync());
            var res = await AsTask(eng.RecognizeAsync(sbm));
            return res.Lines.Select(l => l.Text).ToArray();
        }

        // Awaits a WinRT async operation without the System.Runtime.WindowsRuntime extensions (which need the
        // SDK's union Windows.winmd; this app builds against the per-namespace files in System32\WinMetadata).
        static Task<T> AsTask<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            var tcs = new TaskCompletionSource<T>();
            op.Completed = (o, status) =>
            {
                if (status == Windows.Foundation.AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
                else tcs.TrySetException(o.ErrorCode ?? new Exception("WinRT " + status));
            };
            return tcs.Task;
        }

        delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    }

    class QfReadout
    {
        public string Cid, Callsign, Sector, Type, Beacon, Altitude;
        public override string ToString()
        {
            return Cid + " " + Callsign + "(" + Sector + ") " + Type + (Beacon != null ? " " + Beacon : "") + (Altitude != null ? " " + Altitude : "");
        }

        // "151 SKW5646(32) E75L/L" (+ next line "6566 450 350 ..."). OCR reads ERAM's slashed zero as Ø/ø.
        static readonly Regex Line1 = new Regex(@"^([0-9A-Z]{3})\s+([A-Z][A-Z0-9]{1,7})\s*\(\s*([0-9A-Z]{1,3})\s*\)\s*([A-Z0-9]{2,4}(?:/[A-Z0-9])?)?");
        static readonly Regex Line2 = new Regex(@"^([0-7]{4})\s+(\d{2,4})\s+(\d{2,3}[A-Z0-9]*)\b");

        public static string Clean(string s)
        {
            return (s ?? "").ToUpperInvariant().Replace('Ø', '0').Replace('ø', '0').Replace("( ", "(").Replace(" )", ")").Trim();
        }

        public static List<QfReadout> Parse(string[] lines)
        {
            var list = new List<QfReadout>();
            for (int i = 0; i < lines.Length; i++)
            {
                var m = Line1.Match(Clean(lines[i]));
                if (!m.Success || !m.Groups[1].Value.Any(char.IsDigit)) continue;
                var r = new QfReadout
                {
                    Cid = m.Groups[1].Value, Callsign = m.Groups[2].Value,
                    Sector = m.Groups[3].Value, Type = m.Groups[4].Value
                };
                if (i + 1 < lines.Length)
                {
                    var m2 = Line2.Match(Clean(lines[i + 1]));
                    if (m2.Success) { r.Beacon = m2.Groups[1].Value; r.Altitude = m2.Groups[3].Value; }
                }
                list.Add(r);
            }
            return list;
        }
    }

    // CID -> callsign pairs learned from QF readouts. Each readout is checked against the VATSIM data feed
    // before it is trusted, because one misread character could otherwise pair a CID with the wrong aircraft.
    //
    // A CRC CID belongs to one pilot connection under one callsign: it stays the same for that connection and
    // changes when the pilot reconnects. So a pair is kept (and saved to disk, surviving an app restart) only
    // while that same connection is on VATSIM: it is dropped as soon as the callsign's VATSIM logon time changes
    // (a reconnect) or the callsign has been gone for 3 minutes (disconnected). Flights that aren't on VATSIM
    // (test servers) can't be tracked that way and are kept for 12 hours. A newer QF replaces older pairs.
    class CidBook
    {
        public class Entry { public string Callsign, Sector, Logon; public bool Verified; public DateTime At, LastSeen; }

        readonly Dictionary<string, Entry> byCid = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        readonly object sync = new object();
        Dictionary<string, Pilot> feed = new Dictionary<string, Pilot>(StringComparer.OrdinalIgnoreCase);
        DateTime feedAt = DateTime.MinValue;
        static readonly TimeSpan GoneFor = TimeSpan.FromMinutes(3), UnverifiedTtl = TimeSpan.FromHours(12);
        static string FilePath { get { return Path.Combine(AppPaths.Dir, "cids.json"); } }

        class Pilot { public string Type, Squawk, AssignedSquawk, Logon; }

        public CidBook() { Load(); }

        public event Action Changed;   // a pair was learned or dropped (may fire off the UI thread)

        public int Count { get { lock (sync) return byCid.Count; } }

        // callsign -> {cid, sector}, the shape the EDST host API's setCids() takes.
        public Dictionary<string, object> ForEdst()
        {
            lock (sync)
                return byCid.ToDictionary(kv => kv.Value.Callsign, kv => (object)new Dictionary<string, object>
                {
                    { "cid", kv.Key }, { "sector", kv.Value.Sector ?? "" }
                }, StringComparer.OrdinalIgnoreCase);
        }

        public Entry Find(string cid)
        {
            lock (sync)
            {
                Entry e;
                return cid != null && byCid.TryGetValue(cid, out e) ? e : null;
            }
        }

        // Called every 30 s off the UI thread: drops pairs whose pilot connection has ended or been replaced.
        public void Maintain()
        {
            RefreshFeed();
            bool changed = false, dropped = false;
            lock (sync)
            {
                var now = DateTime.UtcNow;
                foreach (var kv in byCid.ToList())
                {
                    var e = kv.Value;
                    Pilot p;
                    string why = null;
                    if (feed.Count > 0 && feed.TryGetValue(e.Callsign, out p))
                    {
                        if (e.Logon != null && p.Logon != null && p.Logon != e.Logon) why = "pilot reconnected";
                        else { e.LastSeen = now; changed = true; }
                    }
                    else if (e.Verified ? now - e.LastSeen > GoneFor : now - e.At > UnverifiedTtl) why = "pilot disconnected";
                    if (why != null) { byCid.Remove(kv.Key); changed = dropped = true; Log.Info("CID " + kv.Key + " = " + e.Callsign + " dropped (" + why + ")"); }
                }
            }
            if (changed) Save();
            if (dropped && Changed != null) Changed();
        }

        void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var arr = Json.Parse(File.ReadAllText(FilePath)) as List<object>;
                if (arr == null) return;
                lock (sync)
                    foreach (var o in arr)
                    {
                        var cid = Json.Str(o, "cid"); var cs = Json.Str(o, "callsign");
                        if (string.IsNullOrEmpty(cid) || string.IsNullOrEmpty(cs)) continue;
                        byCid[cid] = new Entry
                        {
                            Callsign = cs, Sector = Json.Str(o, "sector") ?? "", Logon = Json.Str(o, "logon"),
                            Verified = Json.Bool(o, "verified") == true,
                            At = ParseTime(Json.Str(o, "at")), LastSeen = ParseTime(Json.Str(o, "lastSeen"))
                        };
                    }
                Log.Info("CID book: " + byCid.Count + " pair(s) loaded");
            }
            catch (Exception ex) { Log.Info("CID book load failed: " + ex.Message); }
        }

        void Save()
        {
            try
            {
                List<object> arr;
                lock (sync)
                    arr = byCid.Select(kv => (object)new Dictionary<string, object>
                    {
                        { "cid", kv.Key }, { "callsign", kv.Value.Callsign }, { "sector", kv.Value.Sector },
                        { "logon", kv.Value.Logon },
                        { "verified", kv.Value.Verified },
                        { "at", kv.Value.At.ToString("o", CultureInfo.InvariantCulture) },
                        { "lastSeen", kv.Value.LastSeen.ToString("o", CultureInfo.InvariantCulture) }
                    }).ToList();
                Directory.CreateDirectory(AppPaths.Dir);
                File.WriteAllText(FilePath, Json.Write(arr));
            }
            catch (Exception ex) { Log.Info("CID book save failed: " + ex.Message); }
        }

        static DateTime ParseTime(string s)
        {
            DateTime t;
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)
                ? t.ToUniversalTime() : DateTime.UtcNow;
        }

        // Returns a description of what was learned, or null if the readout was rejected (reason in 'why').
        public Entry Learn(QfReadout r, out string why)
        {
            why = null;
            RefreshFeed();
            Dictionary<string, Pilot> f;
            lock (sync) f = feed;
            var cs = r.Callsign;
            bool verified = false;
            Pilot p;
            if (f.Count > 0 && f.TryGetValue(cs, out p))
            {
                if (!Agrees(r, p)) { why = "type/beacon don't match VATSIM data for " + cs; return null; }
                verified = true;
            }
            else if (f.Count > 0)
            {
                // OCR may have misread one character (W -> H ...): accept a unique one-character fix that also
                // agrees on type or beacon.
                var fixes = f.Where(kv => kv.Key.Length == cs.Length && Diff(kv.Key, cs) == 1 && Agrees(r, kv.Value))
                    .Select(kv => kv.Key).ToList();
                if (fixes.Count == 1) { cs = fixes[0]; verified = true; }
                else if (fixes.Count > 1) { why = "ambiguous callsign " + cs; return null; }
                // Not on VATSIM at all (e.g. a test server): keep it unverified. The EDST still checks the callsign.
            }
            Pilot conn;
            var e = new Entry
            {
                Callsign = cs, Sector = r.Sector, Verified = verified, At = DateTime.UtcNow, LastSeen = DateTime.UtcNow,
                Logon = verified && f.TryGetValue(cs, out conn) ? conn.Logon : null   // this pilot connection
            };
            lock (sync)
            {
                // One CID per callsign: a new QF readout replaces older pairs for either side.
                foreach (var k in byCid.Where(kv => kv.Value.Callsign == cs && kv.Key != r.Cid).Select(kv => kv.Key).ToList())
                    byCid.Remove(k);
                byCid[r.Cid] = e;
            }
            Save();
            if (Changed != null) Changed();
            return e;
        }

        static bool Agrees(QfReadout r, Pilot p)
        {
            var rt = (r.Type ?? "").Split('/')[0];
            bool typeOk = rt.Length >= 3 && !string.IsNullOrEmpty(p.Type) &&
                          (p.Type.StartsWith(rt, StringComparison.OrdinalIgnoreCase) || rt.StartsWith(p.Type, StringComparison.OrdinalIgnoreCase));
            bool beaconOk = r.Beacon != null && (r.Beacon == p.AssignedSquawk || r.Beacon == p.Squawk);
            return typeOk || beaconOk;
        }

        static int Diff(string a, string b)
        {
            int d = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) d++;
            return d;
        }

        void RefreshFeed()
        {
            if (DateTime.UtcNow - feedAt < TimeSpan.FromSeconds(15)) return;
            feedAt = DateTime.UtcNow;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://data.vatsim.net/v3/vatsim-data.json");
                req.UserAgent = "vUSALinkApp (CRC companion)";
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                req.Timeout = 15000;
                string body;
                using (var resp = req.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) body = rd.ReadToEnd();
                var d = new Dictionary<string, Pilot>(StringComparer.OrdinalIgnoreCase);
                var pilots = Json.Arr(Json.Parse(body), "pilots");
                if (pilots != null)
                    foreach (var o in pilots)
                    {
                        var cs = Json.Str(o, "callsign");
                        if (string.IsNullOrEmpty(cs)) continue;
                        var fp = Json.Obj(o, "flight_plan");
                        d[cs] = new Pilot
                        {
                            Logon = Json.Str(o, "logon_time"),
                            Squawk = Json.Str(o, "transponder"),
                            Type = fp != null ? Json.Str(fp, "aircraft_short") : null,
                            AssignedSquawk = fp != null ? Json.Str(fp, "assigned_transponder") : null
                        };
                    }
                lock (sync) feed = d;
            }
            catch (Exception ex) { Log.Info("CID book: VATSIM feed failed: " + ex.Message); }
        }
    }
}
