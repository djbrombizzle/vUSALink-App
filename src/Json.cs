using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VusaLinkApp
{
    // Minimal, fast JSON reader. Objects -> Dictionary<string,object>, arrays -> List<object>,
    // numbers -> double, plus string/bool/null.
    static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            if (s.Length > 0 && s[0] == '﻿') i = 1;
            return Value(s, ref i);
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && s[i] <= ' ') i++;
        }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            char c = s[i];
            if (c == '{')
            {
                i++;
                var d = new Dictionary<string, object>();
                Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    string k = ReadString(s, ref i);
                    Ws(s, ref i);
                    i++; // ':'
                    d[k] = Value(s, ref i);
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    i++; // '}'
                    return d;
                }
            }
            if (c == '[')
            {
                i++;
                var l = new List<object>();
                Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    i++; // ']'
                    return l;
                }
            }
            if (c == '"') return ReadString(s, ref i);
            if (c == 't') { i += 4; return true; }
            if (c == 'f') { i += 5; return false; }
            if (c == 'n') { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string ReadString(string s, ref int i)
        {
            i++;
            StringBuilder sb = null;
            int st = i;
            while (true)
            {
                char c = s[i];
                if (c == '"')
                {
                    string r = sb == null ? s.Substring(st, i - st) : sb.Append(s, st, i - st).ToString();
                    i++;
                    return r;
                }
                if (c == '\\')
                {
                    if (sb == null) sb = new StringBuilder();
                    sb.Append(s, st, i - st);
                    char e = s[i + 1];
                    i += 2;
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                        default: sb.Append(e); break;
                    }
                    st = i;
                    continue;
                }
                i++;
            }
        }

        // ---- accessors ----

        public static object Get(object o, string k)
        {
            var d = o as Dictionary<string, object>;
            object v;
            if (d != null && d.TryGetValue(k, out v)) return v;
            return null;
        }

        public static Dictionary<string, object> Obj(object o, string k)
        {
            return Get(o, k) as Dictionary<string, object>;
        }

        public static List<object> Arr(object o, string k)
        {
            return Get(o, k) as List<object>;
        }

        public static string Str(object o, string k)
        {
            var v = Get(o, k);
            return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double Num(object o, string k, double def)
        {
            var v = Get(o, k);
            if (v is double) return (double)v;
            if (v is string)
            {
                double r;
                if (double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
            }
            return def;
        }

        public static bool? Bool(object o, string k)
        {
            var v = Get(o, k);
            if (v is bool) return (bool)v;
            return null;
        }

        // ---- writer (for companion settings) ----

        public static string Write(object o)
        {
            var sb = new StringBuilder();
            WriteValue(sb, o, 0);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object o, int indent)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            if (o is string) { WriteString(sb, (string)o); return; }
            if (o is double || o is int || o is long || o is float)
            {
                sb.Append(Convert.ToDouble(o).ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            var d = o as IDictionary<string, object>;
            if (d != null)
            {
                sb.Append("{\n");
                int n = 0;
                foreach (var kv in d)
                {
                    sb.Append(' ', indent + 2);
                    WriteString(sb, kv.Key);
                    sb.Append(": ");
                    WriteValue(sb, kv.Value, indent + 2);
                    if (++n < d.Count) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(' ', indent).Append('}');
                return;
            }
            var l = o as System.Collections.IEnumerable;
            if (l != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var x in l)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    WriteValue(sb, x, indent + 2);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, o.ToString());
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
        }
    }
}
