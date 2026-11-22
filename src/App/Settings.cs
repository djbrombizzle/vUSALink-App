using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace VusaLinkApp
{
    // Window/app preferences in %LOCALAPPDATA%\vUSALinkApp\settings.ini (key=value).
    // The EDST page keeps its own settings (sign-in, layout, filters) in the WebView2 profile next to it.
    class Settings
    {
        public static readonly string Dir = AppPaths.Dir;
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public const string DefaultUrl = "https://vatflow.io/vusalink/edst/";

        public string Url = DefaultUrl;
        public bool CrcBar;           // load the page with ?crc=1 (EDST's compact CRC companion layout)
        public bool Mirror = true;    // forward "/U" commands typed in CRC to the EDST MCA
        public bool Topmost;
        public bool Echo;             // type EDST uplinks into CRC as ERAM commands (CRC ECHO), off by default
        public double Zoom = 1.0;
        public double Left = double.NaN, Top = double.NaN, Width = 1400, Height = 860;
        public bool Maximized;

        public static Settings Load()
        {
            var s = new Settings();
            MigrateOldName();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var d = File.ReadAllLines(FilePath)
                    .Select(l => l.Split(new[] { '=' }, 2)).Where(p => p.Length == 2)
                    .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);
                s.Url = Str(d, "Url", s.Url);
                s.CrcBar = Bool(d, "CrcBar", s.CrcBar);
                s.Mirror = Bool(d, "Mirror", s.Mirror);
                s.Topmost = Bool(d, "Topmost", s.Topmost);
                s.Echo = Bool(d, "CrcEcho", s.Echo);
                s.Zoom = Num(d, "Zoom", s.Zoom);
                s.Left = Num(d, "Left", s.Left);
                s.Top = Num(d, "Top", s.Top);
                s.Width = Num(d, "Width", s.Width);
                s.Height = Num(d, "Height", s.Height);
                s.Maximized = Bool(d, "Maximized", s.Maximized);
            }
            catch (Exception ex) { Log.Info("Settings load failed: " + ex.Message); }
            return s;
        }

        // Early builds were called "EDST Companion"; carry their settings and EDST sign-in over once.
        static void MigrateOldName()
        {
            try
            {
                var old = Path.Combine(Path.GetDirectoryName(Dir), "EDSTCompanion");
                if (Directory.Exists(old) && !Directory.Exists(Dir))
                {
                    Directory.Move(old, Dir);
                    Log.Info("Moved settings from " + old);
                }
            }
            catch (Exception ex) { Log.Info("Settings migration skipped: " + ex.Message); }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var c = CultureInfo.InvariantCulture;
                File.WriteAllLines(FilePath, new[]
                {
                    "Url=" + Url,
                    "CrcBar=" + CrcBar,
                    "Mirror=" + Mirror,
                    "Topmost=" + Topmost,
                    "CrcEcho=" + Echo,
                    "Zoom=" + Zoom.ToString(c),
                    "Left=" + Left.ToString(c),
                    "Top=" + Top.ToString(c),
                    "Width=" + Width.ToString(c),
                    "Height=" + Height.ToString(c),
                    "Maximized=" + Maximized
                });
            }
            catch (Exception ex) { Log.Info("Settings save failed: " + ex.Message); }
        }

        static string Str(Dictionary<string, string> d, string k, string def)
        {
            string v;
            return d.TryGetValue(k, out v) && v != "" ? v : def;
        }

        static bool Bool(Dictionary<string, string> d, string k, bool def)
        {
            string v; bool b;
            return d.TryGetValue(k, out v) && bool.TryParse(v, out b) ? b : def;
        }

        static double Num(Dictionary<string, string> d, string k, double def)
        {
            string v; double n;
            return d.TryGetValue(k, out v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out n) ? n : def;
        }
    }
}
