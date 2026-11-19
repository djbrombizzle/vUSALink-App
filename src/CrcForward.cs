using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace VusaLinkApp
{
    // Filters what is typed into CRC before it reaches the EDST page.
    //
    // Gate(): with the EDST host API (current pages) the page decides what a command means. The app only
    // passes on lines that start with an EDST verb. The keyboard hook sees everything typed while CRC is
    // focused (chat too), so this is a privacy gate, not EDST logic.
    //
    // Uplink() / Amend(): legacy rules for EDST pages from before the host API. Remove after one release.
    static class CrcForward
    {
        static readonly string[] UplinkVerbs = { "QZ", "QQ", "QU", "QS" };

        public static string Gate(string raw)
        {
            string text;
            var toks = Tokens(raw, out text);
            return toks.Length >= 2 && Array.IndexOf(UplinkVerbs, toks[0]) >= 0 ? text : null;
        }

        // ERAM handoff "<sector> <FLID>" (e.g. "11 AAL004"), same rule as the EDST page (host API v2).
        // Not "240 AAL004" (an implied altitude) and not a CRC CID ("11 413").
        static readonly Regex HandoffLine = new Regex(@"^(\d{1,2}|[A-Z]\d{1,2}|[A-Z]) ([A-Z][A-Z0-9]{1,7})$");

        public static string Handoff(string raw)
        {
            string text;
            Tokens(raw, out text);
            var m = HandoffLine.Match(text);
            return m.Success && m.Groups[2].Value.Any(char.IsDigit) ? text : null;
        }

        // EDST-only modifiers CRC doesn't understand ("QZ 240 DAL123 /U" -> MESSAGE TOO LONG in CRC).
        static readonly string[] EdstMods = { "U", "PD", "IMM", "EXP", "TFC", "RES", "WX", "OK" };
        static readonly Regex TrailingMods = new Regex(@"(\s+/[A-Za-z/]+)+\s*$");

        // How many characters to erase from the end of CRC's MCA line before CRC gets it: the trailing EDST
        // modifiers, when they include /U. "QZ 240 DAL123 /PD /U" -> 8, leaving "QZ 240 DAL123" for CRC.
        public static int CrcTrimLength(string raw)
        {
            if (Gate(raw) == null) return 0;
            var m = TrailingMods.Match(raw ?? "");
            if (!m.Success || m.Index == 0) return 0;
            var mods = m.Value.Split(new[] { ' ', '\t', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.ToUpperInvariant()).ToList();
            if (!mods.Contains("U") || mods.Any(x => Array.IndexOf(EdstMods, x) < 0)) return 0;
            return m.Length;
        }
        static readonly Regex Callsign = new Regex(@"^[A-Z][A-Z0-9]{1,7}$");
        static readonly Regex EramCid = new Regex(@"^[A-Z0-9]{3}$");

        static string[] Tokens(string raw, out string text)
        {
            text = Regex.Replace((raw ?? "").ToUpperInvariant(), @"\s+", " ").Trim();
            return text.Split(' ');
        }

        static bool HasUplink(string[] toks)
        {
            return toks.Skip(1).Any(t => t.StartsWith("/") && t.Split('/').Contains("U"));
        }

        public static string Uplink(string raw)
        {
            string text;
            var toks = Tokens(raw, out text);
            if (text.StartsWith(".")) return null;             // CRC alias - its target was clicked, not typed
            if (toks.Length < 3 || Array.IndexOf(UplinkVerbs, toks[0]) < 0) return null;
            if (!HasUplink(toks)) return null;
            if (!toks.Skip(1).Any(IsCallsign)) return null;
            return text;
        }

        // "QZ 240 DAL123", "QZ FL240 DAL123", "QZ 24000 4A2" -> (240, "DAL123" / "4A2").
        public static bool Amend(string raw, out int fl, out string id)
        {
            fl = 0; id = null;
            string text;
            var toks = Tokens(raw, out text);
            if (toks.Length != 3 || toks[0] != "QZ" || HasUplink(toks)) return false;
            for (int i = 1; i <= 2; i++)
            {
                int alt = ParseAlt(toks[i]);
                var other = toks[3 - i];
                if (alt > 0 && (IsCallsign(other) || EramCid.IsMatch(other)))
                {
                    fl = alt; id = other;
                    return true;
                }
            }
            return false;
        }

        static int ParseAlt(string t)
        {
            if (t.StartsWith("FL")) t = t.Substring(2);
            int n;
            if (!Regex.IsMatch(t, @"^\d{2,5}$") || !int.TryParse(t, out n)) return 0;
            if (t.Length >= 4) n = (int)Math.Round(n / 100.0);
            return n >= 10 && n <= 600 ? n : 0;
        }

        // Same rule as EDST's parser: letter first, contains a digit, not an altitude-like token.
        static bool IsCallsign(string t)
        {
            foreach (var part in t.Split('/'))
            {
                if (!Callsign.IsMatch(part) || !part.Any(char.IsDigit)) continue;
                if (Regex.IsMatch(part, @"^[RLP]\d{2,3}$") || Regex.IsMatch(part, @"^FL\d{2,3}$")) continue;
                return true;
            }
            return false;
        }
    }
}
