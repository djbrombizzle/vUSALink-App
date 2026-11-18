using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace VusaLinkApp
{
    // Decides what a command typed into CRC's MCA means for the EDST.
    //  - Uplink: QZ / QQ / QU / QS with the /U modifier and a typed aircraft -> EDST MCA (sends CPDLC).
    //  - Amend:  plain "QZ <alt> <FLID>" (no /U) -> EDST assigned altitude is updated locally, nothing is sent,
    //            so the controller can uplink it from the EDST altitude menu.
    // Everything else (data block work, clicked targets, dot-aliases) is ignored.
    static class CrcForward
    {
        static readonly string[] UplinkVerbs = { "QZ", "QQ", "QU", "QS" };
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
