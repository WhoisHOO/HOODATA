// CSV import: finds the encoding, the delimiter, the header row, the time column and the number columns, then feeds
// the rows to the engine in time order as one "file" source.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Hoodata
{
    public sealed class CsvReader
    {
        readonly TextReader r;
        readonly char d;
        public long Records;

        public CsvReader(TextReader reader, char delimiter) { r = reader; d = delimiter; }

        // The next record, skipping blank lines; null at the end.
        public string[] Next()
        {
            while (true)
            {
                var fields = new List<string>();
                var f = new StringBuilder();
                bool quoted = false, any = false, fieldStarted = false;
                while (true)
                {
                    int c = r.Read();
                    if (c < 0)
                    {
                        if (!any) return null;
                        fields.Add(f.ToString());
                        break;
                    }
                    any = true;
                    if (quoted)
                    {
                        if (c == '"')
                        {
                            if (r.Peek() == '"') { r.Read(); f.Append('"'); }
                            else quoted = false;
                        }
                        else f.Append((char)c);
                        continue;
                    }
                    if (c == '"' && !fieldStarted) { quoted = true; fieldStarted = true; continue; }
                    if (c == d) { fields.Add(f.ToString()); f.Length = 0; fieldStarted = false; continue; }
                    if (c == '\r') { if (r.Peek() == '\n') r.Read(); fields.Add(f.ToString()); break; }
                    if (c == '\n') { fields.Add(f.ToString()); break; }
                    f.Append((char)c);
                    fieldStarted = true;
                }
                Records++;
                if (fields.Count == 1 && fields[0].Trim().Length == 0) continue;
                return fields.ToArray();
            }
        }
    }

    public static class Values
    {
        static readonly Regex Leading = new Regex(@"^\s*([-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)\s*([^\d\s]{0,6})\s*$", RegexOptions.CultureInvariant);
        static readonly HashSet<string> Missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "", "nan", "n/a", "na", "-", "--", "null", "none", "#n/a", "#value!", "#div/0!", "err", "error", "inf", "-inf" };

        // A number, with a unit after it allowed ("23.5 C", "45%"); on/off and true/false as 1/0.
        public static bool TryNumber(string s, bool decimalComma, out double v, out string unit)
        {
            unit = null;
            v = double.NaN;
            if (s == null) return false;
            s = s.Trim();
            if (Missing.Contains(s)) return false;
            string low = s.ToLowerInvariant();
            if (low == "true" || low == "on" || low == "yes") { v = 1; return true; }
            if (low == "false" || low == "off" || low == "no") { v = 0; return true; }
            if (decimalComma) s = s.Replace(".", "").Replace(',', '.');
            else if (s.IndexOf(',') > 0 && Regex.IsMatch(s, @"^[-+]?\d{1,3}(,\d{3})+(\.\d+)?$")) s = s.Replace(",", "");
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return !double.IsNaN(v) && !double.IsInfinity(v);
            Match m = Leading.Match(s);
            if (!m.Success) return false;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return false;
            if (m.Groups[2].Value.Length > 0) unit = m.Groups[2].Value;
            return true;
        }

        static readonly Regex UnitInHeader = new Regex(@"^(.*?)\s*[\(\[]\s*([^\)\]]{1,12})\s*[\)\]]\s*$");

        // "Temperature (C)" or "Temp[%]" -> name and unit.
        public static void SplitHeader(string header, out string name, out string unit)
        {
            Match m = UnitInHeader.Match(header ?? "");
            if (m.Success && m.Groups[1].Value.Length > 0) { name = m.Groups[1].Value; unit = m.Groups[2].Value; }
            else { name = (header ?? "").Trim(); unit = ""; }
        }
    }

    public static class TimeParser
    {
        public const string EpochSeconds = "epoch_s", EpochMillis = "epoch_ms";

        // Tried in this order; the first that reads nearly all samples wins.
        public static readonly string[] DateTimeFormats = {
            "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "yyyy-MM-dd HH:mm:ss.FFFFFFFK",
            "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mmK",
            "yyyy-M-d H:mm:ss.FFFFFFF", "yyyy-M-d H:mm",
            "yyyy/M/d H:mm:ss.FFFFFFF", "yyyy/M/d H:mm", "yyyy.M.d H:mm:ss.FFFFFFF", "yyyy.M.d H:mm",
            "yyyy-M-d tt h:mm:ss", "yyyy-M-d tt h:mm", "yyyy/M/d tt h:mm:ss", "yyyy/M/d tt h:mm",
            "yyyy. M. d. tt h:mm:ss", "yyyy. M. d. tt h:mm", "yyyy. M. d. H:mm:ss", "yyyy. M. d. H:mm",
            "M/d/yyyy h:mm:ss tt", "M/d/yyyy h:mm tt", "M/d/yyyy H:mm:ss.FFFFFFF", "M/d/yyyy H:mm",
            "d/M/yyyy H:mm:ss.FFFFFFF", "d/M/yyyy H:mm", "d.M.yyyy H:mm:ss.FFFFFFF", "d.M.yyyy H:mm",
            "yyyyMMddHHmmss", "yyyyMMdd HHmmss", "yyyyMMddHHmm" };

        public static readonly string[] DateFormats = {
            "yyyy-M-d", "yyyy/M/d", "yyyy.M.d", "yyyy. M. d.", "yyyy. M. d", "M/d/yyyy", "d/M/yyyy", "d.M.yyyy", "yyyyMMdd" };

        public static readonly string[] ClockFormats = { "H:mm:ss.FFFFFFF", "H:mm", "tt h:mm:ss", "tt h:mm", "h:mm:ss tt", "h:mm tt" };

        public static string Normalize(string s)
        {
            if (s == null) return "";
            // Korean AM and PM, as Excel on a Korean Windows writes them.
            s = s.Trim().Replace("\uC624\uC804", "AM").Replace("\uC624\uD6C4", "PM");
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s;
        }

        // Day-first and month-first formats both read 3/4/2026; the PC's own date order breaks the tie.
        static bool PreferDayFirst()
        {
            string p = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
            return p.IndexOf('d') >= 0 && p.IndexOf('d') < p.IndexOf('M');
        }

        public static string Detect(IList<string> samples, string[] formats)
        {
            var values = new List<string>();
            foreach (string s in samples) { string n = Normalize(s); if (n.Length > 0) values.Add(n); }
            if (values.Count == 0) return null;
            string best = null;
            double bestRatio = 0;
            bool dayFirst = PreferDayFirst();
            foreach (string f in formats)
            {
                int ok = 0;
                foreach (string v in values)
                {
                    DateTime dt;
                    if (DateTime.TryParseExact(v, f, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt)) ok++;
                }
                double ratio = ok / (double)values.Count;
                bool better = ratio > bestRatio + 1e-9;
                // A tie between month-first and day-first: follow the PC.
                if (!better && Math.Abs(ratio - bestRatio) < 1e-9 && best != null && dayFirst && f.StartsWith("d") && best.StartsWith("M/d")) better = true;
                if (better) { best = f; bestRatio = ratio; }
            }
            return bestRatio >= 0.9 ? best : null;
        }

        public static string DetectEpoch(IList<string> samples)
        {
            int s = 0, ms = 0, n = 0;
            foreach (string x in samples)
            {
                double v;
                if (!double.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                n++;
                if (v >= 946684800 && v < 4102444800) s++;              // 2000 to 2100 in seconds
                else if (v >= 946684800000 && v < 4102444800000) ms++;  // and in milliseconds
            }
            if (n == 0) return null;
            if (s >= 0.95 * n) return EpochSeconds;
            if (ms >= 0.95 * n) return EpochMillis;
            return null;
        }

        static DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static bool TryParse(string s, string format, TimeZoneInfo zone, out long ms)
        {
            ms = 0;
            if (format == EpochSeconds || format == EpochMillis)
            {
                double v;
                if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return false;
                ms = (long)Math.Round(format == EpochSeconds ? v * 1000 : v);
                return true;
            }
            DateTime dt;
            if (!DateTime.TryParseExact(Normalize(s), format, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt)) return false;
            return ToMs(dt, zone, out ms);
        }

        public static bool ToMs(DateTime dt, TimeZoneInfo zone, out long ms)
        {
            ms = 0;
            try
            {
                DateTime utc;
                if (dt.Kind == DateTimeKind.Utc) utc = dt;
                else if (dt.Kind == DateTimeKind.Local) utc = dt.ToUniversalTime();
                else if (zone.IsInvalidTime(dt)) return false;   // a local time skipped by a clock change
                else utc = TimeZoneInfo.ConvertTimeToUtc(dt, zone);
                ms = (long)(utc - Epoch).TotalMilliseconds;
                return true;
            }
            catch (ArgumentException) { return false; }
        }
    }

    public sealed class CsvColumn
    {
        public int Index;
        public string Header, Name, Unit, Kind;   // kind: time, date, clock, epoch, number, text
        public string Format;
        public double NumberRatio;
        public readonly List<string> Samples = new List<string>();
    }

    public sealed class CsvLayout
    {
        public string EncodingName;
        public Encoding Encoding;
        public char Delimiter;
        public bool DecimalComma;
        public int HeaderRecord = -1;    // records to skip before the data, including the header
        public int Skip;
        public readonly List<CsvColumn> Columns = new List<CsvColumn>();
        public int TimeColumn = -1, ClockColumn = -1;
        public string TimeFormat;
        public bool Descending;
        public readonly List<string[]> Preview = new List<string[]>();
        public long Size;

        public void Write(JsonWriter w)
        {
            w.Obj().Prop("encoding", EncodingName).Prop("delimiter", Delimiter.ToString()).Prop("decimal_comma", DecimalComma)
                .Prop("has_header", HeaderRecord >= 0).Prop("time_column", TimeColumn).Prop("clock_column", ClockColumn)
                .Key("time_format").Str(TimeFormat).Prop("descending", Descending).Prop("size", Size);
            w.Key("columns").Arr();
            foreach (CsvColumn c in Columns)
            {
                w.Obj().Prop("index", c.Index).Prop("header", c.Header).Prop("name", c.Name).Prop("unit", c.Unit).Prop("kind", c.Kind);
                w.Key("samples").Arr();
                for (int i = 0; i < Math.Min(3, c.Samples.Count); i++) w.Str(c.Samples[i]);
                w.EndArr().EndObj();
            }
            w.EndArr();
            w.Key("preview").Arr();
            foreach (string[] row in Preview) { w.Arr(); foreach (string f in row) w.Str(f); w.EndArr(); }
            w.EndArr().EndObj();
        }
    }

    public static class CsvAnalyzer
    {
        static readonly string[] TimeWords = { "time", "date", "timestamp", "datetime", "ts", "epoch", 
            // Korean: time, date and time, date, time of day, measured, recorded
            "\uC2DC\uAC04", "\uC77C\uC2DC", "\uB0A0\uC9DC", "\uC2DC\uAC01", "\uCE21\uC815", "\uAE30\uB85D" };

        public static Encoding DetectEncoding(byte[] head, int n, out string name)
        {
            if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) { name = "utf-8"; return new UTF8Encoding(false); }
            if (n >= 2 && head[0] == 0xFF && head[1] == 0xFE) { name = "utf-16le"; return Encoding.Unicode; }
            if (n >= 2 && head[0] == 0xFE && head[1] == 0xFF) { name = "utf-16be"; return Encoding.BigEndianUnicode; }
            // Strict UTF-8 over the start; a character cut at the end of the sample does not count against it.
            int len = n;
            int back = 0;
            while (back < 3 && len > 0 && (head[len - 1] & 0xC0) == 0x80) { len--; back++; }
            if (len > 0 && head[len - 1] >= 0xC0) len--;
            try
            {
                new UTF8Encoding(false, true).GetString(head, 0, len);
                name = "utf-8";
                return new UTF8Encoding(false);
            }
            catch (DecoderFallbackException) { }
            // Excel on a Korean Windows saves CSV as CP949.
            name = "cp949";
            return Encoding.GetEncoding(949);
        }

        static char DetectDelimiter(string text)
        {
            char[] candidates = { ',', '\t', ';', '|' };
            string[] lines = text.Split('\n');
            char best = ',';
            int bestScore = -1;
            foreach (char c in candidates)
            {
                var counts = new Dictionary<int, int>();
                int seen = 0;
                for (int i = 0; i < lines.Length - 1 && seen < 40; i++)
                {
                    if (lines[i].Trim().Length == 0) continue;
                    seen++;
                    int k = 0;
                    bool q = false;
                    foreach (char ch in lines[i]) { if (ch == '"') q = !q; else if (ch == c && !q) k++; }
                    if (k == 0) continue;
                    int cur;
                    counts.TryGetValue(k, out cur);
                    counts[k] = cur + 1;
                }
                int score = 0;
                foreach (var kv in counts) if (kv.Value > score) score = kv.Value;
                if (score > bestScore) { best = c; bestScore = score; }
            }
            return best;
        }

        static bool LooksLikeData(string field)
        {
            double v;
            string u;
            if (Values.TryNumber(field, false, out v, out u)) return true;
            string n = TimeParser.Normalize(field);
            foreach (string f in TimeParser.DateTimeFormats)
            {
                DateTime dt;
                if (DateTime.TryParseExact(n, f, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt)) return true;
            }
            return false;
        }

        public static CsvLayout Analyze(string path)
        {
            var layout = new CsvLayout { Size = new FileInfo(path).Length };
            byte[] head = new byte[256 * 1024];
            int n;
            using (var fs = File.OpenRead(path)) n = fs.Read(head, 0, head.Length);
            string encName;
            layout.Encoding = DetectEncoding(head, n, out encName);
            layout.EncodingName = encName;
            string text = layout.Encoding.GetString(head, 0, n);
            layout.Delimiter = DetectDelimiter(text);

            var records = new List<string[]>();
            using (var sr = new StreamReader(path, layout.Encoding, true))
            {
                var csv = new CsvReader(sr, layout.Delimiter);
                string[] rec;
                while (records.Count < 600 && (rec = csv.Next()) != null) records.Add(rec);
            }
            if (records.Count == 0) throw new HttpError(400, "empty_file");

            // The usual field count; rows before the first one with it are a preamble.
            var counts = new Dictionary<int, int>();
            foreach (string[] r in records) { int c; counts.TryGetValue(r.Length, out c); counts[r.Length] = c + 1; }
            int width = 0, most = 0;
            foreach (var kv in counts) if (kv.Value > most || (kv.Value == most && kv.Key > width)) { width = kv.Key; most = kv.Value; }
            int first = 0;
            while (first < records.Count && records[first].Length != width) first++;

            string[] top = records[first];
            int dataLike = 0;
            foreach (string f in top) if (LooksLikeData(f)) dataLike++;
            bool header = dataLike * 2 < top.Length || (first + 1 < records.Count && dataLike < top.Length && Count(records[first + 1]) > dataLike);
            layout.HeaderRecord = header ? first : -1;
            layout.Skip = header ? first + 1 : first;

            var data = new List<string[]>();
            for (int i = layout.Skip; i < records.Count; i++) if (records[i].Length == width) data.Add(records[i]);
            for (int i = 0; i < Math.Min(8, data.Count); i++) layout.Preview.Add(data[i]);

            // A ';' file whose numbers use a decimal comma.
            if (layout.Delimiter == ';')
            {
                int commas = 0, total = 0;
                foreach (string[] r in data) foreach (string f in r) { total++; if (Regex.IsMatch(f.Trim(), @"^[-+]?\d+,\d+$")) commas++; }
                layout.DecimalComma = commas > 0 && commas * 10 >= total;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < width; c++)
            {
                var col = new CsvColumn { Index = c };
                col.Header = header ? top[c].Trim() : "col" + (c + 1);
                if (col.Header.Length == 0) col.Header = "col" + (c + 1);
                string name, unit;
                Values.SplitHeader(col.Header, out name, out unit);
                string unique = name;
                for (int k = 2; names.Contains(unique); k++) unique = name + " (" + k + ")";
                names.Add(unique);
                col.Name = unique;
                col.Unit = unit;
                int nonEmpty = 0, numbers = 0;
                foreach (string[] r in data)
                {
                    string f = r[c].Trim();
                    if (f.Length == 0) continue;
                    nonEmpty++;
                    if (col.Samples.Count < 200) col.Samples.Add(f);
                    double v;
                    string u;
                    if (Values.TryNumber(f, layout.DecimalComma, out v, out u))
                    {
                        numbers++;
                        if (u != null && col.Unit.Length == 0) col.Unit = u;
                    }
                }
                col.NumberRatio = nonEmpty == 0 ? 0 : numbers / (double)nonEmpty;
                col.Kind = "text";
                if ((col.Format = TimeParser.Detect(col.Samples, TimeParser.DateTimeFormats)) != null) col.Kind = "time";
                else if ((col.Format = TimeParser.Detect(col.Samples, TimeParser.DateFormats)) != null) col.Kind = "date";
                else if ((col.Format = TimeParser.Detect(col.Samples, TimeParser.ClockFormats)) != null) col.Kind = "clock";
                else if (col.NumberRatio >= 0.8)
                {
                    col.Kind = "number";
                    string epoch = IsTimeWord(col.Name) ? TimeParser.DetectEpoch(col.Samples) : null;
                    if (epoch != null) { col.Kind = "epoch"; col.Format = epoch; }
                }
                layout.Columns.Add(col);
            }
            PickTime(layout);
            if (layout.TimeColumn >= 0 && data.Count >= 2)
            {
                long a, b;
                if (TryRowTime(layout, data[0], TimeZoneInfo.Local, out a) && TryRowTime(layout, data[data.Count - 1], TimeZoneInfo.Local, out b))
                    layout.Descending = b < a;
            }
            return layout;
        }

        static int Count(string[] rec)
        {
            int k = 0;
            foreach (string f in rec) if (LooksLikeData(f)) k++;
            return k;
        }

        static bool IsTimeWord(string name)
        {
            string low = name.ToLowerInvariant();
            foreach (string w in TimeWords) if (low == w || low.Contains(w)) return true;
            return false;
        }

        static void PickTime(CsvLayout layout)
        {
            CsvColumn best = null;
            foreach (CsvColumn c in layout.Columns)
                if (c.Kind == "time" || c.Kind == "epoch")
                    if (best == null || (IsTimeWord(c.Name) && !IsTimeWord(best.Name))) best = c;
            if (best != null) { layout.TimeColumn = best.Index; layout.TimeFormat = best.Format; return; }
            CsvColumn date = layout.Columns.Find(c => c.Kind == "date");
            CsvColumn clock = layout.Columns.Find(c => c.Kind == "clock");
            if (date != null && clock != null)
            {
                layout.TimeColumn = date.Index;
                layout.ClockColumn = clock.Index;
                layout.TimeFormat = date.Format + " " + clock.Format;
            }
            else if (date != null) { layout.TimeColumn = date.Index; layout.TimeFormat = date.Format; }
        }

        public static bool TryRowTime(CsvLayout layout, string[] row, TimeZoneInfo zone, out long ms)
        {
            ms = 0;
            if (layout.TimeColumn < 0 || layout.TimeColumn >= row.Length) return false;
            string s = row[layout.TimeColumn];
            if (layout.ClockColumn >= 0)
            {
                if (layout.ClockColumn >= row.Length) return false;
                s = s.Trim() + " " + TimeParser.Normalize(row[layout.ClockColumn]);
            }
            return TimeParser.TryParse(s, layout.TimeFormat, zone, out ms);
        }
    }

    public sealed class ImportJob
    {
        public readonly string Id = Guid.NewGuid().ToString("N").Substring(0, 12);
        public string FileName, FilePath, Name, State = "ready", Error;
        public CsvLayout Layout;
        public double Progress;
        public long Rows, ValueCount, Skipped, OutOfOrder;
        public int SourceId;
        public bool Finished;
        public long FinishedAt;
        public List<int> Columns;   // value columns to import
        public TimeZoneInfo Zone = TimeZoneInfo.Local;
        public double IntervalSeconds = 1;   // when the file has no time column
        public long StartMs;
        volatile bool cancel;
        public bool DeleteFileWhenDone = true;

        public static ImportJob Prepare(string path, string fileName)
        {
            var job = new ImportJob { FilePath = path, FileName = fileName };
            job.Layout = CsvAnalyzer.Analyze(path);
            job.Name = Path.GetFileNameWithoutExtension(fileName);
            job.Columns = new List<int>();
            foreach (CsvColumn c in job.Layout.Columns)
                if (c.Kind == "number" && c.Index != job.Layout.TimeColumn && c.Index != job.Layout.ClockColumn) job.Columns.Add(c.Index);
            job.StartMs = Clock.Now();
            return job;
        }

        public void Cancel() { cancel = true; }

        public void Write(JsonWriter w)
        {
            w.Obj().Prop("id", Id).Prop("file", FileName).Prop("name", Name).Prop("state", State).Key("error").Str(Error)
                .Prop("progress", Progress).Prop("rows", Rows).Prop("values", ValueCount).Prop("skipped", Skipped)
                .Prop("out_of_order", OutOfOrder).Prop("source", SourceId).EndObj();
        }

        public void Start(Engine engine)
        {
            State = "running";
            var t = new Thread(() => Run(engine)) { IsBackground = true, Name = "HOODATA import" };
            t.Start();
        }

        public void Run(Engine engine)
        {
            try
            {
                if (Columns == null || Columns.Count == 0) throw new InvalidOperationException("no_columns");
                var cfg = new JsonWriter().Obj().Prop("file", FileName).Prop("encoding", Layout.EncodingName)
                    .Prop("delimiter", Layout.Delimiter.ToString()).Prop("time_format", Layout.TimeFormat ?? "")
                    .Prop("zone", Zone.Id).EndObj().ToString();
                SourceInfo src = engine.AddSource("file", Name, cfg, true);
                SourceId = src.Id;
                var hints = new Dictionary<string, ChannelHint>();
                foreach (int c in Columns)
                {
                    CsvColumn col = Layout.Columns[c];
                    hints[col.Name] = new ChannelHint { Name = col.Name, Unit = col.Unit };
                }
                if (Layout.Descending) ReadAllReversed(engine, hints);
                else ReadStreaming(engine, hints);
                engine.Submit(SourceId, null, null, true);
                engine.WaitForQueue();
                State = cancel ? "cancelled" : "done";
                Progress = 1;
            }
            catch (Exception e)
            {
                State = "error";
                Error = e.Message;
            }
            finally
            {
                Finished = true;
                FinishedAt = Clock.Now();
                if (DeleteFileWhenDone) try { File.Delete(FilePath); } catch { }
            }
        }

        IEnumerable<string[]> DataRows(Action<double> progress)
        {
            using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Layout.Encoding, true))
            {
                var csv = new CsvReader(sr, Layout.Delimiter);
                string[] rec;
                int width = Layout.Columns.Count;
                long index = 0;
                while ((rec = csv.Next()) != null)
                {
                    if (cancel) yield break;
                    if (index++ < Layout.Skip) continue;
                    if (rec.Length != width) { Skipped++; continue; }
                    if ((index & 1023) == 0 && progress != null && fs.Length > 0) progress((double)fs.Position / fs.Length);
                    yield return rec;
                }
            }
        }

        bool RowTime(string[] rec, long rowIndex, out long t)
        {
            if (Layout.TimeColumn < 0) { t = StartMs + (long)(rowIndex * IntervalSeconds * 1000); return true; }
            return CsvAnalyzer.TryRowTime(Layout, rec, Zone, out t);
        }

        void AddValues(string[] rec, long t, List<Reading> batch)
        {
            foreach (int c in Columns)
            {
                double v;
                string u;
                if (Values.TryNumber(rec[c], Layout.DecimalComma, out v, out u))
                {
                    batch.Add(new Reading(Layout.Columns[c].Name, t, v));
                    ValueCount++;
                }
            }
        }

        void ReadStreaming(Engine engine, Dictionary<string, ChannelHint> hints)
        {
            var batch = new List<Reading>();
            long last = long.MinValue, row = 0;
            foreach (string[] rec in DataRows(p => Progress = 0.98 * p))
            {
                long t;
                if (!RowTime(rec, row++, out t)) { Skipped++; continue; }
                if (t < last) OutOfOrder++;
                last = Math.Max(last, t);
                Rows++;
                AddValues(rec, t, batch);
                if (batch.Count >= 5000) { engine.Submit(SourceId, batch, hints); batch = new List<Reading>(); }
            }
            if (batch.Count > 0) engine.Submit(SourceId, batch, hints);
        }

        // Newest first, as some loggers export: read it all, then send it oldest first.
        void ReadAllReversed(Engine engine, Dictionary<string, ChannelHint> hints)
        {
            var rows = new List<KeyValuePair<long, string[]>>();
            long row = 0;
            foreach (string[] rec in DataRows(p => Progress = 0.5 * p))
            {
                long t;
                if (!RowTime(rec, row++, out t)) { Skipped++; continue; }
                rows.Add(new KeyValuePair<long, string[]>(t, rec));
                if (rows.Count > 5000000) throw new InvalidOperationException("too_many_rows");
            }
            rows.Reverse();
            var batch = new List<Reading>();
            long last = long.MinValue;
            for (int i = 0; i < rows.Count; i++)
            {
                if (cancel) break;
                if (rows[i].Key < last) OutOfOrder++;
                last = Math.Max(last, rows[i].Key);
                Rows++;
                AddValues(rows[i].Value, rows[i].Key, batch);
                if (batch.Count >= 5000) { engine.Submit(SourceId, batch, hints); batch = new List<Reading>(); Progress = 0.5 + 0.48 * i / rows.Count; }
            }
            if (batch.Count > 0) engine.Submit(SourceId, batch, hints);
        }
    }
}
