// A small JSON reader and writer. Objects come back as Dictionary<string, object>, arrays as List<object>, numbers as
// double. Written for this project so the engine needs nothing outside .NET Framework 4.8. C# 5 (Add-Type's csc).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Hoodata
{
    public static class Json
    {
        public static object Parse(string text)
        {
            var p = new JsonParser(text);
            p.SkipSpace();
            object value = p.ReadValue(0);
            p.SkipSpace();
            if (!p.AtEnd) throw new FormatException("Unexpected text after the JSON value at " + p.Position);
            return value;
        }

        public static bool TryParse(string text, out object value)
        {
            try { value = Parse(text); return true; }
            catch (FormatException) { value = null; return false; }
        }

        public static string Quote(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            AppendQuoted(sb, s);
            return sb.ToString();
        }

        public static void AppendQuoted(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    // < and > too, so a value can never close a <script> block it is placed in.
                    case '<': sb.Append("\\u003c"); break;
                    case '>': sb.Append("\\u003e"); break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static string Number(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        // Helpers for reading parsed objects.
        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }

        public static object Get(Dictionary<string, object> o, string key)
        {
            object v;
            return o != null && o.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(Dictionary<string, object> o, string key, string fallback)
        {
            object v = Get(o, key);
            if (v == null) return fallback;
            if (v is string) return (string)v;
            if (v is double) return Number((double)v);
            if (v is bool) return (bool)v ? "true" : "false";
            return fallback;
        }

        public static double Num(Dictionary<string, object> o, string key, double fallback)
        {
            object v = Get(o, key);
            if (v is double) return (double)v;
            double d;
            if (v is string && double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return fallback;
        }

        public static bool Bool(Dictionary<string, object> o, string key, bool fallback)
        {
            object v = Get(o, key);
            if (v is bool) return (bool)v;
            if (v is double) return (double)v != 0;
            return fallback;
        }
    }

    sealed class JsonParser
    {
        readonly string s;
        int i;
        const int MaxDepth = 64;

        public JsonParser(string text) { s = text ?? ""; }
        public bool AtEnd { get { return i >= s.Length; } }
        public int Position { get { return i; } }

        public void SkipSpace()
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r' || s[i] == (char)0xFEFF)) i++;
        }

        FormatException Error(string what) { return new FormatException(what + " at " + i); }

        public object ReadValue(int depth)
        {
            if (depth > MaxDepth) throw Error("JSON nested too deeply");
            if (i >= s.Length) throw Error("Unexpected end of JSON");
            char c = s[i];
            if (c == '{') return ReadObject(depth);
            if (c == '[') return ReadArray(depth);
            if (c == '"') return ReadString();
            if (c == 't' && Match("true")) return true;
            if (c == 'f' && Match("false")) return false;
            if (c == 'n' && Match("null")) return null;
            if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
            throw Error("Unexpected character '" + c + "'");
        }

        bool Match(string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        Dictionary<string, object> ReadObject(int depth)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            i++;
            SkipSpace();
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (true)
            {
                SkipSpace();
                if (i >= s.Length || s[i] != '"') throw Error("Expected a property name");
                string key = ReadString();
                SkipSpace();
                if (i >= s.Length || s[i] != ':') throw Error("Expected ':'");
                i++;
                SkipSpace();
                o[key] = ReadValue(depth + 1);
                SkipSpace();
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return o; }
                throw Error("Expected ',' or '}'");
            }
        }

        List<object> ReadArray(int depth)
        {
            var a = new List<object>();
            i++;
            SkipSpace();
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                SkipSpace();
                a.Add(ReadValue(depth + 1));
                SkipSpace();
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return a; }
                throw Error("Expected ',' or ']'");
            }
        }

        string ReadString()
        {
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw Error("Unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw Error("Unterminated escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw Error("Bad \\u escape");
                        int code;
                        if (!int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            throw Error("Bad \\u escape");
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: throw Error("Bad escape");
                }
            }
        }

        double ReadNumber()
        {
            int start = i;
            if (s[i] == '-') i++;
            while (i < s.Length && ((s[i] >= '0' && s[i] <= '9') || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) i++;
            double d;
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw Error("Bad number");
            return d;
        }
    }

    // Appends JSON to a StringBuilder, keeping track of commas.
    public sealed class JsonWriter
    {
        readonly StringBuilder sb = new StringBuilder();
        readonly Stack<bool> first = new Stack<bool>();
        bool afterKey;

        public override string ToString() { return sb.ToString(); }

        void Separate()
        {
            if (afterKey) { afterKey = false; return; }
            if (first.Count == 0) return;
            if (first.Peek()) { first.Pop(); first.Push(false); }
            else sb.Append(',');
        }

        public JsonWriter Obj() { Separate(); sb.Append('{'); first.Push(true); return this; }
        public JsonWriter EndObj() { first.Pop(); sb.Append('}'); return this; }
        public JsonWriter Arr() { Separate(); sb.Append('['); first.Push(true); return this; }
        public JsonWriter EndArr() { first.Pop(); sb.Append(']'); return this; }

        public JsonWriter Key(string name)
        {
            Separate();
            Json.AppendQuoted(sb, name);
            sb.Append(':');
            afterKey = true;
            return this;
        }

        public JsonWriter Str(string v)
        {
            Separate();
            if (v == null) sb.Append("null"); else Json.AppendQuoted(sb, v);
            return this;
        }

        public JsonWriter Num(double v) { Separate(); sb.Append(Json.Number(v)); return this; }
        public JsonWriter Int(long v) { Separate(); sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
        public JsonWriter Bool(bool v) { Separate(); sb.Append(v ? "true" : "false"); return this; }
        public JsonWriter Null() { Separate(); sb.Append("null"); return this; }

        public JsonWriter NumOrNull(double? v) { return v.HasValue ? Num(v.Value) : Null(); }
        public JsonWriter IntOrNull(long? v) { return v.HasValue ? Int(v.Value) : Null(); }

        // Already-encoded JSON, e.g. a stored detail object.
        public JsonWriter Raw(string json) { Separate(); sb.Append(string.IsNullOrEmpty(json) ? "null" : json); return this; }

        public JsonWriter Prop(string name, string v) { return Key(name).Str(v); }
        public JsonWriter Prop(string name, double v) { return Key(name).Num(v); }
        public JsonWriter Prop(string name, long v) { return Key(name).Int(v); }
        public JsonWriter Prop(string name, int v) { return Key(name).Int(v); }
        public JsonWriter Prop(string name, bool v) { return Key(name).Bool(v); }
    }
}
