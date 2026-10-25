// What devices send, read into named numbers. One parser for every input (HTTP, MQTT, serial lines), so a device
// can switch from one to another without changing what it prints. Recognised, one message or one line at a time:
//   {"temp": 23.5, "env": {"hum": 61}, "ts": 1792900000}   JSON; nested names joined with '.'; ts/time/timestamp
//   temp=23.5,hum=61   or   temp=23.5 hum=61              name=value pairs
//   temp:23.5,hum:61   or   temp:23.5 hum:61              the Arduino Serial Plotter's format
//   air,room=lab temp=23.5,hum=61 1792900000000000000     InfluxDB line protocol (tags join the name)
//   23.5,61   or   23.5 61   or   23.5                    numbers alone: value1, value2, ... (or just the name given)
//   time,temp,hum + rows                                  a small table: a header line, then rows (time column optional)
//   {"temp":1}\n{"temp":2}                                 one JSON object per line
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Hoodata
{
    public sealed class ParsedValues
    {
        public readonly List<KeyValuePair<string, double>> Values = new List<KeyValuePair<string, double>>();
        public long? Time;   // from the message, when it carried one
    }

    public static class Payload
    {
        static readonly HashSet<string> TimeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ts", "time", "timestamp", "t", "datetime" };
        static readonly Regex Pair = new Regex(@"^\s*([^\W\d][\w.\-/]*)\s*[=:]\s*(\S+?)\s*$", RegexOptions.CultureInvariant);
        static readonly Regex Influx = new Regex(@"^([^\W\d][\w\-]*)((?:,[\w\-]+=[^\s,]+)*)\s+([\w\-]+=[^\s]+(?:,[\w\-]+=[^\s]+)*)(?:\s+(\d{9,19}))?\s*$", RegexOptions.CultureInvariant);

        // Each line of `text` is a message; a JSON document may span lines. `name` is used for a bare number.
        public static List<ParsedValues> Parse(string text, string name)
        {
            var result = new List<ParsedValues>();
            if (text == null) return result;
            if (name != null && name.Length == 0) name = null;
            string trimmed = text.Trim();
            if (trimmed.Length == 0) return result;
            if (trimmed[0] == '{' || trimmed[0] == '[')
            {
                object doc;
                if (Json.TryParse(trimmed, out doc))
                {
                    if (doc is List<object>) { foreach (object o in (List<object>)doc) AddJson(result, o, name); }
                    else AddJson(result, doc, name);
                    return result;
                }
            }
            string[] lines = trimmed.Split('\n');
            if (ParseTable(lines, result)) return result;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                object doc;
                if (line.StartsWith("{") && Json.TryParse(line, out doc)) { AddJson(result, doc, name); continue; }
                ParsedValues pv = ParseLine(line, name);
                if (pv != null && pv.Values.Count > 0) result.Add(pv);
            }
            return result;
        }

        static readonly Regex HeaderName = new Regex(@"^[^\W\d][\w .\-/()\[\]%]*$", RegexOptions.CultureInvariant);

        // A header line of names followed by rows with as many fields (what a spreadsheet exports): each row is one
        // message, its time from a time column when there is one.
        static bool ParseTable(string[] lines, List<ParsedValues> result)
        {
            if (lines.Length < 2) return false;
            string first = lines[0].Trim();
            char d = first.IndexOf('\t') >= 0 ? '\t' : first.IndexOf(';') >= 0 ? ';' : ',';
            string[] head = first.Split(d);
            if (head.Length < 2) return false;
            foreach (string h in head) if (!HeaderName.IsMatch(h.Trim())) return false;
            var rows = new List<string[]>();
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                string[] f = line.Split(d);
                if (f.Length != head.Length) return false;
                rows.Add(f);
            }
            if (rows.Count == 0) return false;
            // The time column is named so and holds times in every row; a 't' with 23.5 in it is a temperature.
            int timeCol = -1;
            for (int c = 0; c < head.Length && timeCol < 0; c++)
            {
                string n = head[c].Trim();
                if (!TimeNames.Contains(n) && !n.Equals("date", StringComparison.OrdinalIgnoreCase)) continue;
                bool times = true;
                foreach (string[] f in rows) { long t; if (!TryTime(f[c].Trim(), out t)) { times = false; break; } }
                if (times) timeCol = c;
            }
            foreach (string[] f in rows)
            {
                var pv = new ParsedValues();
                long t;
                if (timeCol >= 0 && TryTime(f[timeCol].Trim(), out t)) pv.Time = t;
                for (int c = 0; c < head.Length; c++)
                {
                    if (c == timeCol) continue;
                    double v;
                    string unit;
                    if (Values.TryNumber(f[c], d != ',' && f[c].IndexOf('.') < 0, out v, out unit)) pv.Values.Add(new KeyValuePair<string, double>(head[c].Trim(), v));
                }
                if (pv.Values.Count > 0) result.Add(pv);
            }
            return true;
        }

        static void AddJson(List<ParsedValues> result, object doc, string name)
        {
            var pv = new ParsedValues();
            if (doc is double) pv.Values.Add(new KeyValuePair<string, double>(name ?? "value", (double)doc));
            else if (doc is Dictionary<string, object>) Flatten(pv, (Dictionary<string, object>)doc, null);
            if (pv.Values.Count > 0) result.Add(pv);
        }

        static void Flatten(ParsedValues pv, Dictionary<string, object> o, string prefix)
        {
            foreach (var kv in o)
            {
                string key = prefix == null ? kv.Key : prefix + "." + kv.Key;
                if (prefix == null && TimeNames.Contains(kv.Key))
                {
                    long t;
                    if (TryTime(kv.Value, out t)) { pv.Time = t; continue; }
                }
                if (kv.Value is double) pv.Values.Add(new KeyValuePair<string, double>(key, (double)kv.Value));
                else if (kv.Value is bool) pv.Values.Add(new KeyValuePair<string, double>(key, (bool)kv.Value ? 1 : 0));
                else if (kv.Value is string)
                {
                    double v;
                    string unit;
                    if (Values.TryNumber((string)kv.Value, false, out v, out unit)) pv.Values.Add(new KeyValuePair<string, double>(key, v));
                }
                else if (kv.Value is Dictionary<string, object>) Flatten(pv, (Dictionary<string, object>)kv.Value, key);
            }
        }

        // Seconds, milliseconds or nanoseconds since 1970, or an ISO 8601 text. A text without a zone (Z or +09:00) is
        // local time, as ISO 8601 says and as the CSV import reads it.
        public static bool TryTime(object o, out long ms)
        {
            ms = 0;
            double d;
            if (o is double) d = (double)o;
            else if (o is string && double.TryParse((string)o, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { }
            else if (o is string)
            {
                DateTime dt;
                if (!DateTime.TryParse((string)o, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal, out dt)) return false;
                ms = Clock.FromUtc(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                return true;
            }
            else return false;
            if (d > 9.46e8 && d < 4.1e9) ms = (long)Math.Round(d * 1000);
            else if (d > 9.46e11 && d < 4.1e12) ms = (long)Math.Round(d);
            else if (d > 9.46e17 && d < 4.1e18) ms = (long)Math.Round(d / 1e6);
            else return false;
            return true;
        }

        static ParsedValues ParseLine(string line, string name)
        {
            if (line.Length == 0 || line[0] == '#') return null;
            var pv = new ParsedValues();

            Match im = Influx.Match(line);
            if (im.Success && im.Groups[3].Value.Contains("="))
            {
                string prefix = im.Groups[1].Value;
                foreach (string tag in im.Groups[2].Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    prefix += "." + tag.Substring(tag.IndexOf('=') + 1);
                foreach (string field in im.Groups[3].Value.Split(','))
                {
                    int eq = field.IndexOf('=');
                    string v = field.Substring(eq + 1).TrimEnd('i', 'u');
                    double d;
                    if (v == "t" || v == "true" || v == "T") d = 1;
                    else if (v == "f" || v == "false" || v == "F") d = 0;
                    else if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) continue;
                    pv.Values.Add(new KeyValuePair<string, double>(prefix + "." + field.Substring(0, eq), d));
                }
                if (im.Groups[4].Success)
                {
                    long t;
                    if (TryTime(im.Groups[4].Value, out t)) pv.Time = t;
                }
                if (pv.Values.Count > 0) return pv;
                pv = new ParsedValues();
            }

            // Pairs, separated by commas, semicolons, tabs or spaces. "Temperature: 23.5" and "temp = 23.5" (what
            // many Arduino sketches print) are pairs too: the spaces around the separator go first.
            if (line.IndexOf(':') > 0 || line.IndexOf('=') > 0) line = Regex.Replace(line, @"\s*([=:])\s*", "$1");
            string[] parts = line.Split(new[] { ',', ';', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            bool allPairs = parts.Length > 0;
            foreach (string part in parts) if (!Pair.IsMatch(part)) { allPairs = false; break; }
            if (allPairs)
            {
                foreach (string part in parts)
                {
                    Match m = Pair.Match(part);
                    string key = m.Groups[1].Value, val = m.Groups[2].Value;
                    if (TimeNames.Contains(key))
                    {
                        long t;
                        if (TryTime(val, out t)) { pv.Time = t; continue; }
                    }
                    double d;
                    string unit;
                    if (Values.TryNumber(val, false, out d, out unit)) pv.Values.Add(new KeyValuePair<string, double>(key, d));
                }
                return pv;
            }

            // Numbers alone.
            var nums = new List<double>();
            foreach (string part in parts)
            {
                double d;
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return null;
                nums.Add(d);
            }
            if (nums.Count == 1) pv.Values.Add(new KeyValuePair<string, double>(name ?? "value", nums[0]));
            else for (int i = 0; i < nums.Count; i++) pv.Values.Add(new KeyValuePair<string, double>((name != null ? name + "." : "value") + (i + 1), nums[i]));
            return pv;
        }

        // Readings for the engine: the message's time if it had one, else `now`.
        // Rows without a time are dated now. When several such rows in one message give the same name, only the last
        // of them is taken: they would all have the same time, and the latest reading is the one that counts. (One
        // value per line, 'temp=23.5' then 'hum=61', keeps both.)
        public static List<Reading> ToReadings(List<ParsedValues> parsed, string prefix, long now)
        {
            var list = new List<Reading>();
            var undated = new HashSet<string>();
            for (int i = parsed.Count - 1; i >= 0; i--)
            {
                ParsedValues pv = parsed[i];
                long t = pv.Time.HasValue ? pv.Time.Value : now;
                for (int k = pv.Values.Count - 1; k >= 0; k--)
                {
                    var kv = pv.Values[k];
                    // A bare number sent to a named place (an MQTT topic, /ingest/<device>) takes that name.
                    string key = prefix == null || prefix.Length == 0 ? kv.Key : kv.Key == "value" ? prefix : prefix + "/" + kv.Key;
                    if (key.Length > 200) key = key.Substring(0, 200);
                    if (!pv.Time.HasValue && !undated.Add(key)) continue;
                    list.Add(new Reading(key, t, kv.Value));
                }
            }
            list.Reverse();
            return list;
        }
    }
}
