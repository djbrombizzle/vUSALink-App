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
using System.Threading;
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
            int wins = 0, lines = 0, zooms = 0;
            // Debug: VUSALINK_QFDUMP=<folder>, or a "qfdump" folder next to the exe, saves every capture and its
            // OCR text there.
            var dump = Environment.GetEnvironmentVariable("VUSALINK_QFDUMP");
            var local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qfdump");
            if (string.IsNullOrEmpty(dump) && Directory.Exists(local)) dump = local;
            var stamp = DateTime.Now.ToString("HHmmss_fff");
            foreach (var bmp in CaptureEramWindows())
                using (bmp)
                {
                    wins++;
                    List<OcrText> l;
                    int z;
                    all.AddRange(ReadBitmap(bmp, out l, out z));
                    lines += l.Count;
                    zooms += z;
                    if (!string.IsNullOrEmpty(dump))
                        try
                        {
                            Directory.CreateDirectory(dump);
                            bmp.Save(Path.Combine(dump, stamp + "_w" + wins + ".png"), ImageFormat.Png);
                            File.WriteAllLines(Path.Combine(dump, stamp + "_w" + wins + ".txt"), l.Select(x => x.Text));
                        }
                        catch { }
                }
            info = wins + " CRC window(s) captured, " + lines + " text line(s), " + all.Count + " QF readout(s)" +
                (zooms > 0 ? " (" + zooms + " zoomed look(s))" : "") + (wins == 0 ? " (no CRC window found)" : "");
            return all;
        }

        public class OcrText { public string Text; public RectangleF Box; }

        // Reads one window. At screen size OCR sometimes mangles the readout's first line ("493 N677PL*(12)" comes
        // back as "493 12)") while the second line (beacon, speed, altitude) reads fine. So wherever a second line is
        // found without a readout above it, that spot is cut out, enlarged and read again.
        public static List<QfReadout> ReadBitmap(Bitmap bmp, out List<OcrText> lines, out int zooms)
        {
            zooms = 0;
            lines = Ocr(bmp, 1).GetAwaiter().GetResult();
            var texts = lines.Select(x => x.Text).ToArray();
            var found = QfReadout.Parse(texts);
            for (int i = 0; i < lines.Count && zooms < 3; i++)
            {
                if (!QfReadout.IsSecondLine(texts[i])) continue;
                if (i > 0 && QfReadout.Parse(new[] { texts[i - 1], texts[i] }).Count > 0) continue;   // read fine
                var b = lines[i].Box;
                float h = Math.Max(8, b.Height);
                var crop = Rectangle.Round(RectangleF.FromLTRB(b.Left - 2 * h, b.Top - 2.2f * h,
                    Math.Max(b.Right, b.Left + 32 * h), b.Bottom + 0.6f * h));
                crop.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
                if (crop.Width < 20 || crop.Height < 10) continue;
                zooms++;
                using (var part = bmp.Clone(crop, bmp.PixelFormat))
                    // OCR is touchy about text size (N677PL reads at 2x and 4x, not at 3x): try each until one works.
                    foreach (var scale in new[] { 2, 3, 4 })
                    {
                        var more = QfReadout.Parse(Ocr(part, scale).GetAwaiter().GetResult().Select(x => x.Text).ToArray());
                        foreach (var r in more)
                            if (!found.Any(f => f.Key == r.Key)) found.Add(r);
                        if (more.Count > 0) break;
                    }
            }
            return found;
        }

        // Windows OCR on light-on-black ERAM text: invert to dark-on-light first (and enlarge by 'scale').
        // Returns each line with its box in 'src' pixels.
        public static async Task<List<OcrText>> Ocr(Bitmap src, int scale)
        {
            var eng = OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            if (eng == null) throw new InvalidOperationException("Windows OCR is not available (no OCR language installed)");
            byte[] png;
            using (var inv = new Bitmap(src.Width * scale, src.Height * scale))
            {
                using (var g = Graphics.FromImage(inv))
                using (var ia = new ImageAttributes())
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    ia.SetColorMatrix(new ColorMatrix(new[]
                    {
                        new float[] { -0.33f, -0.33f, -0.33f, 0, 0 }, new float[] { -0.33f, -0.33f, -0.33f, 0, 0 },
                        new float[] { -0.33f, -0.33f, -0.33f, 0, 0 }, new float[] { 0, 0, 0, 1, 0 }, new float[] { 1, 1, 1, 0, 1 }
                    }));
                    g.DrawImage(src, new Rectangle(0, 0, inv.Width, inv.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
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
            var outp = new List<OcrText>();
            foreach (var l in res.Lines)
            {
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = 0, y1 = 0;
                foreach (var w in l.Words)
                {
                    var r = w.BoundingRect;
                    x0 = Math.Min(x0, (float)r.X); y0 = Math.Min(y0, (float)r.Y);
                    x1 = Math.Max(x1, (float)(r.X + r.Width)); y1 = Math.Max(y1, (float)(r.Y + r.Height));
                }
                outp.Add(new OcrText
                {
                    Text = l.Text,
                    Box = x1 > 0 ? RectangleF.FromLTRB(x0 / scale, y0 / scale, x1 / scale, y1 / scale) : RectangleF.Empty
                });
            }
            return outp;
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

        public string Key { get { return Cid + " " + Callsign; } }

        // "151 SKW5646(32) E75L/L" (+ next line "6566 450 350 ..."). OCR reads ERAM's slashed zero as Ø/ø.
        // Not anchored to the start of the line: OCR sometimes joins other text on the same row in front of it.
        // The callsign may come back split by a space ("DALI 146" for DAL1146: ERAM's narrow 1 leaves a gap), and
        // may be followed by ERAM's "*" marker ("AAL343*(12)", which OCR may also read as X; see CidBook.LooksLike).
        static readonly Regex Line1 = new Regex(@"(?:^|\s)([0-9A-Z]{3})\s+([A-Z][A-Z0-9]{0,7}(?:\s[A-Z0-9]{1,7})?)\s*\*?\s*\(\s*([0-9A-Z]{1,3})\s*\)\s*([A-Z0-9]{2,4}(?:/[A-Z0-9])?)?");
        static readonly Regex Line2 = new Regex(@"(?:^|\s)([0-7]{4})\s+(\d{2,4})\s+(\d{2,3}[A-Z0-9]*)\b");
        static readonly Regex AirlineFlight = new Regex(@"^([A-Z]{3})([0-9IO]*[0-9][0-9IO]*)$");

        public static string Clean(string s)
        {
            return (s ?? "").ToUpperInvariant().Replace('Ø', '0').Replace('ø', '0').Replace("( ", "(").Replace(" )", ")").Trim();
        }

        // The readout's second line: "<beacon> <speed> <altitude> <route>".
        public static bool IsSecondLine(string line) { return Line2.IsMatch(Clean(line)); }

        // Airline flight number: I and O there are OCR's 1 and 0 (DALI146 -> DAL1146, AALI0I0 -> AAL1010).
        public static string FixFlightNumber(string cs)
        {
            var af = AirlineFlight.Match(cs ?? "");
            return af.Success ? af.Groups[1].Value + af.Groups[2].Value.Replace('I', '1').Replace('O', '0') : cs;
        }

        public static List<QfReadout> Parse(string[] lines)
        {
            var list = new List<QfReadout>();
            for (int i = 0; i < lines.Length; i++)
            {
                var m = Line1.Match(Clean(lines[i]));
                if (!m.Success || !m.Groups[1].Value.Any(char.IsDigit)) continue;
                var cs = m.Groups[2].Value.Replace(" ", "");
                if (cs.Length < 2 || cs.Length > 8) continue;
                cs = FixFlightNumber(cs);
                var r = new QfReadout
                {
                    Cid = m.Groups[1].Value, Callsign = cs,
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

        // ERAMv2 (djbrombizzle/ERAMv2) reads CRC's QF readouts too and writes what it learns here; we write cids.json,
        // which ERAMv2 reads. So a CID either program reads reaches both. [{cid, callsign, sector, at}]
        static string SharedPath { get { return Path.Combine(AppPaths.Dir, "eramv2-cids.json"); } }
        DateTime sharedStamp = DateTime.MinValue;
        readonly System.Threading.Timer sharedTimer;

        public CidBook()
        {
            Load();
            sharedTimer = new System.Threading.Timer(_ => ImportShared(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        }

        // Takes the pairs ERAMv2 learned since the last look. Each must be a live VATSIM flight (exact callsign: ERAMv2
        // only keeps readouts that match one) and newer than what we have for that callsign.
        void ImportShared()
        {
            try
            {
                var stamp = File.Exists(SharedPath) ? File.GetLastWriteTimeUtc(SharedPath) : DateTime.MinValue;
                if (stamp == DateTime.MinValue || stamp == sharedStamp) return;
                var arr = Json.Parse(File.ReadAllText(SharedPath)) as List<object>;
                if (arr == null) return;
                RefreshFeed();
                Dictionary<string, Pilot> f;
                lock (sync) f = feed;
                if (f.Count == 0) return;              // can't verify now: try again next tick
                sharedStamp = stamp;
                var learned = new List<string>();
                lock (sync)
                    foreach (var o in arr)
                    {
                        var cid = (Json.Str(o, "cid") ?? "").Trim().ToUpperInvariant();
                        var cs = (Json.Str(o, "callsign") ?? "").Trim().ToUpperInvariant();
                        var at = ParseTime(Json.Str(o, "at"));
                        Pilot p;
                        if (cid.Length != 3 || cs.Length == 0 || !f.TryGetValue(cs, out p)) continue;
                        if (DateTime.UtcNow - at > UnverifiedTtl) continue;
                        Entry cur;
                        if (byCid.TryGetValue(cid, out cur) && cur.Callsign == cs && cur.At >= at) continue;
                        if (byCid.Values.Any(e => e.Callsign == cs && e.At >= at)) continue;   // we read a newer QF ourselves
                        foreach (var k in byCid.Where(kv => kv.Value.Callsign == cs && kv.Key != cid).Select(kv => kv.Key).ToList())
                            byCid.Remove(k);
                        byCid[cid] = new Entry
                        {
                            Callsign = cs, Sector = (Json.Str(o, "sector") ?? "").Trim().ToUpperInvariant(), Logon = p.Logon,
                            Verified = true, At = at, LastSeen = DateTime.UtcNow
                        };
                        learned.Add("CID " + cid + " = " + cs);
                    }
                if (learned.Count == 0) return;
                Log.Info("From ERAMv2: " + string.Join(", ", learned));
                Save();
                if (Changed != null) Changed();
            }
            catch (Exception) { /* ERAMv2 is writing it: next tick */ }
        }

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
                        var cid = Json.Str(o, "cid"); var cs = QfReadout.FixFlightNumber(Json.Str(o, "callsign"));
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
        // On success 'why' says how the callsign was corrected, if it was.
        public Entry Learn(QfReadout r, out string why)
        {
            why = null;
            RefreshFeed();
            Dictionary<string, Pilot> f;
            lock (sync) f = feed;
            var cs = r.Callsign;
            bool verified = false;
            if (f.Count > 0)
            {
                string pick, reject;
                pick = Pick(r, f.Select(kv => new KeyValuePair<string, Pilot>(kv.Key, kv.Value)), out reject);
                if (pick == null && reject != null) { why = reject; return null; }
                if (pick != null)
                {
                    if (pick != cs) why = "OCR " + cs + " -> " + pick;
                    cs = pick; verified = true;
                }
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

        // Which VATSIM flight a readout is: the callsign as read, or one OCR could have misread as it (look-alike
        // characters such as AALI0I0 for AAL1010, one other wrong character, or a stray character after it). The
        // flight must agree on type or beacon. If several do (a fleet of same-type flights one digit apart, e.g.
        // FDX18 / FDX19 / FDX12, all B77L), the readout's beacon decides when it matches one of them, then the
        // closest callsign: as read, then look-alikes only, then one other wrong character. Still a tie: rejected.
        // Returns null with 'reject' null when nothing on VATSIM looks like it (e.g. a test server).
        static string Pick(QfReadout r, IEnumerable<KeyValuePair<string, Pilot>> feed, out string reject)
        {
            reject = null;
            var read = r.Callsign;
            var cands = new List<KeyValuePair<string, int>>();     // callsign, distance
            bool exactSeen = false;
            foreach (var kv in feed)
            {
                int d = Distance(read, kv.Key);
                if (d < 0) continue;
                if (d == 0) exactSeen = true;
                if (Agrees(r, kv.Value)) cands.Add(new KeyValuePair<string, int>(kv.Key, d));
            }
            var pilots = feed.ToDictionary(kv => kv.Key, kv => kv.Value);
            if (cands.Count == 0)
            {
                // The callsign is exactly a VATSIM flight and the OCR got no type or beacon off the readout (it often
                // reads only "947 KAY6E(32)"): nothing contradicts it, so take it.
                if (exactSeen && string.IsNullOrEmpty((r.Type ?? "").Trim()) && r.Beacon == null) return read;
                if (exactSeen) reject = "type/beacon don't match VATSIM data for " + read;
                return null;
            }
            if (r.Beacon != null)
            {
                var byBeacon = cands.Where(c => BeaconAgrees(r, pilots[c.Key])).ToList();
                if (byBeacon.Count > 0) cands = byBeacon;
            }
            int best = cands.Min(c => c.Value);
            var top = cands.Where(c => c.Value == best).Select(c => c.Key).ToList();
            if (top.Count == 1) return top[0];
            reject = "ambiguous callsign " + read + " (" + string.Join("/", top) + ")";
            return null;
        }

        static bool BeaconAgrees(QfReadout r, Pilot p)
        {
            return r.Beacon != null && (r.Beacon == p.AssignedSquawk || r.Beacon == p.Squawk);
        }

        static bool Agrees(QfReadout r, Pilot p)
        {
            var rt = (r.Type ?? "").Split('/')[0];
            bool typeOk = rt.Length >= 3 && !string.IsNullOrEmpty(p.Type) &&
                          (p.Type.StartsWith(rt, StringComparison.OrdinalIgnoreCase) || rt.StartsWith(p.Type, StringComparison.OrdinalIgnoreCase));
            bool beaconOk = r.Beacon != null && (r.Beacon == p.AssignedSquawk || r.Beacon == p.Squawk);
            return typeOk || beaconOk;
        }

        // Characters Windows OCR mixes up in ERAM's font.
        static char Fold(char c)
        {
            switch (c)
            {
                case 'I': case 'L': case '|': return '1';
                case 'O': case 'Q': case 'D': return '0';
                case 'S': return '5';
                case 'B': return '8';
                case 'Z': return '2';
                case 'G': return '6';
            }
            return c;
        }

        // 'read' (OCR) could be 'real' (VATSIM callsign): same length with look-alikes free and at most one other
        // difference, or the same plus one stray trailing character.
        public static bool LooksLike(string read, string real)
        {
            return read != real && Distance(read, real) >= 0;
        }

        // How far an OCR read is from a real callsign: 0 = identical, 1 = look-alike characters only (F0X18 for
        // FDX18), +1 for a stray trailing character, 3 = one other wrong character too. -1 = not alike.
        public static int Distance(string read, string real)
        {
            if (read == null || real == null) return -1;
            if (read == real) return 0;
            int stray = 0;
            if (read.Length == real.Length + 1) { read = read.Substring(0, real.Length); stray = 1; }
            if (read.Length != real.Length) return -1;
            int folded = 0, other = 0;
            for (int i = 0; i < read.Length; i++)
            {
                if (read[i] == real[i]) continue;
                if (Fold(read[i]) == Fold(real[i])) folded = 1;
                else if (++other > 1) return -1;
            }
            return other * 3 + folded + stray;
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

    // Reads CRC's Response Area after a QF (typed, or a middle-click on a data block) and learns the CID.
    //
    // CRC draws the readout a moment after the QF, and until then the previous QF's readout is still on screen.
    // So the reader keeps looking (about every half second, for up to 3.5 s) until a readout appears that wasn't
    // there on the last look, and learns that one. If the same aircraft is QF'd again, nothing new appears; the
    // readout on screen is learned again when time runs out. A newer QF stops the older one's looking.
    class QfReader
    {
        static readonly TimeSpan Window = TimeSpan.FromMilliseconds(3500);
        readonly CidBook cids;
        int gen;
        HashSet<string> lastKeys = new HashSet<string>();   // readouts seen on the last look

        public Func<bool> Enabled = () => true;
        public event Action<QfReadout, CidBook.Entry> Learned;   // on the thread that called Trigger

        public QfReader(CidBook cids) { this.cids = cids; }

        public async void Trigger()
        {
            int g = Interlocked.Increment(ref gen);
            var start = DateTime.UtcNow;
            await Task.Delay(250);
            for (int look = 1; g == gen && Enabled(); look++)
            {
                bool last = DateTime.UtcNow - start >= Window;
                Tuple<QfReadout, CidBook.Entry> got;
                try { got = await Task.Run(() => Look(look, last)); }
                catch (Exception ex) { Log.Info("QF read failed: " + ex.Message); return; }
                if (g != gen) return;
                if (got != null) { if (Learned != null) Learned(got.Item1, got.Item2); return; }
                if (last) { Log.Info("QF read: no new readout after " + look + " look(s)"); return; }
                await Task.Delay(150);
            }
        }

        // One look. Returns the readout to report, or null to keep looking. On the last look a readout that was
        // already on screen counts too (the same aircraft QF'd again).
        Tuple<QfReadout, CidBook.Entry> Look(int look, bool last)
        {
            string info;
            var readouts = CrcScreen.ReadReadouts(out info);
            var keys = new HashSet<string>(readouts.Select(r => r.Key));
            var fresh = readouts.Where(r => !lastKeys.Contains(r.Key)).ToList();
            Log.Info("QF read (look " + look + "): " + info + (readouts.Count > 0 ? ", " + fresh.Count + " new" : ""));
            if (readouts.Count > 0) lastKeys = keys;
            var use = fresh.Count > 0 ? fresh : last ? readouts : fresh;
            Tuple<QfReadout, CidBook.Entry> top = null;
            foreach (var r in use)
            {
                string why;
                var e = cids.Learn(r, out why);
                if (e != null)
                {
                    Log.Info("QF read: " + r + " -> CID " + r.Cid + " = " + e.Callsign +
                        (why != null ? " (" + why + ")" : "") + (e.Verified ? "" : " (not on VATSIM, unverified)"));
                    top = Tuple.Create(r, e);
                }
                else
                {
                    Log.Info("QF read rejected: " + r + " (" + why + ")");
                    // A misread readout may read better on the next look.
                    lastKeys.Remove(r.Key);
                }
            }
            return top;
        }
    }
}
