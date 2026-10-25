// A folder that a data logger keeps writing CSV files into. New files and new lines at the end of a file are read
// every two seconds. Where each file was read up to is kept in the database, so after a restart HOODATA goes on
// where it stopped: the lines written meanwhile are read then, with their own times.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Hoodata
{
    public sealed class FolderSource : LoopSource
    {
        CsvLayout layout;
        Dictionary<string, ChannelHint> hints;
        readonly Dictionary<string, long> offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public long Lines;

        public FolderSource(Engine engine, SourceInfo info) : base(engine, info) { }

        string MetaKey { get { return "folder:" + info.Id; } }

        void LoadOffsets()
        {
            offsets.Clear();
            object o;
            if (Json.TryParse(engine.Store.GetMeta(MetaKey) ?? "{}", out o) && o is Dictionary<string, object>)
                foreach (var kv in (Dictionary<string, object>)o) if (kv.Value is double) offsets[kv.Key] = (long)(double)kv.Value;
        }

        void SaveOffsets()
        {
            var w = new JsonWriter().Obj();
            foreach (var kv in offsets) w.Prop(kv.Key, kv.Value);
            engine.Store.SetMeta(MetaKey, w.EndObj().ToString());
        }

        protected override void Run()
        {
            string dir = Str("path", "");
            string pattern = Str("pattern", "*.csv");
            if (dir.Length == 0 || !Directory.Exists(dir)) throw new DirectoryNotFoundException("The folder does not exist: " + dir);
            LoadOffsets();
            Connected();
            while (!Stopping)
            {
                var files = new List<FileInfo>(new DirectoryInfo(dir).GetFiles(pattern));
                files.Sort((a, b) => a.LastWriteTimeUtc != b.LastWriteTimeUtc ? a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc) : string.CompareOrdinal(a.Name, b.Name));
                bool changed = false;
                foreach (FileInfo f in files)
                {
                    if (Stopping) break;
                    if (layout == null)
                    {
                        // The first file tells the format; the others are taken to be alike.
                        layout = CsvAnalyzer.Analyze(f.FullName);
                        hints = new Dictionary<string, ChannelHint>();
                        foreach (CsvColumn c in layout.Columns)
                            if (IsValue(c)) hints[c.Name] = new ChannelHint { Name = c.Name, Unit = c.Unit };
                    }
                    long offset;
                    offsets.TryGetValue(f.Name, out offset);
                    if (f.Length < offset) offset = 0;   // replaced or cut: read it again
                    if (f.Length == offset) continue;
                    offset = ReadFrom(f.FullName, offset);
                    offsets[f.Name] = offset;
                    changed = true;
                }
                if (changed) SaveOffsets();
                if (Wait(2000)) break;
            }
        }

        bool IsValue(CsvColumn c) { return c.Kind == "number" && c.Index != layout.TimeColumn && c.Index != layout.ClockColumn; }

        // Reads the complete lines after `offset`; returns where the next read starts.
        long ReadFrom(string path, long offset)
        {
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                long n = Math.Min(fs.Length - offset, 64L * 1024 * 1024);
                bytes = new byte[n];
                int got = 0;
                while (got < n)
                {
                    int k = fs.Read(bytes, got, (int)(n - got));
                    if (k <= 0) break;
                    got += k;
                }
                if (got < n) Array.Resize(ref bytes, got);
            }
            int end = Array.LastIndexOf(bytes, (byte)'\n');
            if (end < 0) return offset;   // no complete line yet
            int start = 0;
            if (offset == 0 && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) start = 3;
            string text = layout.Encoding.GetString(bytes, start, end + 1 - start);
            var csv = new CsvReader(new StringReader(text), layout.Delimiter);
            var readings = new List<Reading>();
            int skip = offset == 0 ? layout.Skip : 0;
            string[] rec;
            long index = 0;
            while ((rec = csv.Next()) != null)
            {
                if (index++ < skip) continue;
                if (rec.Length > layout.Columns.Count) continue;
                rec = CsvLayout.Pad(rec, layout.Columns.Count);
                long t;
                if (!CsvAnalyzer.TryRowTime(layout, rec, TimeZoneInfo.Local, out t)) continue;
                foreach (CsvColumn c in layout.Columns)
                {
                    if (!IsValue(c)) continue;
                    double v;
                    string u;
                    if (Values.TryNumber(rec[c.Index], layout.DecimalComma, out v, out u)) readings.Add(new Reading(c.Name, t, v));
                }
                Lines++;
                if (readings.Count >= 5000) { Send(readings, hints); readings = new List<Reading>(); }
            }
            Send(readings, hints);
            return offset + end + 1;
        }
    }
}
