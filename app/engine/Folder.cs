// A folder that a data logger keeps writing CSV files into. New files and new lines at the end of a file are read
// every two seconds. Where each file was read up to is kept in the database, so after a restart HOODATA goes on
// where it stopped: the lines written meanwhile are read then, with their own times.
// Each file's format is read from that file (loggers add columns; one file per sensor is common). A file is
// recognised by a fingerprint of its first bytes and of the bytes before where it was read up to, so a file replaced
// under the same name is read from its start. A file is opened only when its size or time changed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Hoodata
{
    public sealed class FolderSource : LoopSource
    {
        sealed class FileState
        {
            public long Offset;
            public string Print;   // of the file up to Offset; null when saved by an earlier version
            // Not saved: what the file looked like when last read this run, what the folder listing last said of it,
            // and why it could not be read.
            public long SeenLength = -1, ListedLength = -1;
            public DateTime SeenWrite, ListedWrite;
            public bool Waiting;
            public int Failures;
        }

        readonly Dictionary<string, FileState> files = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, CsvLayout> layouts = new Dictionary<string, CsvLayout>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, ChannelHint> hints = new Dictionary<string, ChannelHint>();
        public long Lines;

        public FolderSource(Engine engine, SourceInfo info) : base(engine, info) { }

        string MetaKey { get { return "folder:" + info.Id; } }

        void LoadState()
        {
            files.Clear();
            object o;
            if (!Json.TryParse(engine.Store.GetMeta(MetaKey) ?? "{}", out o) || !(o is Dictionary<string, object>)) return;
            foreach (var kv in (Dictionary<string, object>)o)
            {
                var st = new FileState();
                if (kv.Value is double) st.Offset = (long)(double)kv.Value;   // saved by an earlier version: no fingerprint
                else
                {
                    var d = Json.Obj(kv.Value);
                    st.Offset = (long)Json.Num(d, "o", 0);
                    if (d.ContainsKey("p")) st.Print = Json.Str(d, "p", null);
                }
                files[kv.Key] = st;
            }
        }

        void SaveState()
        {
            var w = new JsonWriter().Obj();
            foreach (var kv in files)
            {
                w.Key(kv.Key).Obj().Prop("o", kv.Value.Offset);
                w.Key("p").Str(kv.Value.Print).EndObj();
            }
            engine.Store.SetMeta(MetaKey, w.EndObj().ToString());
        }

        // The first bytes and the last ones before `upTo`: a header alone is the same in every file of a logger, and
        // the lines just read are not.
        static string Fingerprint(string path, long upTo)
        {
            int head = (int)Math.Min(256, upTo), tail = (int)Math.Min(256, upTo);
            var b = new byte[head + tail];
            using (var fs = CsvAnalyzer.OpenShared(path))
            {
                if (fs.Length < upTo) return null;
                if (!ReadFully(fs, b, 0, head)) return null;
                fs.Seek(upTo - tail, SeekOrigin.Begin);
                if (!ReadFully(fs, b, head, tail)) return null;
            }
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(b)).Replace("-", "");
        }

        // Whether a line ends just before `at` ("\n", or "\n" in UTF-16 either way round).
        static bool EndsLine(string path, long at)
        {
            var b = new byte[2];
            int k = (int)Math.Min(2, at);
            using (var fs = CsvAnalyzer.OpenShared(path))
            {
                fs.Seek(at - k, SeekOrigin.Begin);
                if (!ReadFully(fs, b, 0, k)) return false;
            }
            return b[k - 1] == 0x0A || (k == 2 && b[0] == 0x0A && b[1] == 0);
        }

        static bool ReadFully(Stream s, byte[] b, int at, int count)
        {
            while (count > 0)
            {
                int k = s.Read(b, at, count);
                if (k <= 0) return false;
                at += k;
                count -= k;
            }
            return true;
        }

        static bool IsValue(CsvLayout l, CsvColumn c) { return c.Kind == "number" && c.Index != l.TimeColumn && c.Index != l.ClockColumn; }

        static bool Usable(CsvLayout l)
        {
            if (l.TimeColumn < 0) return false;
            foreach (CsvColumn c in l.Columns) if (IsValue(l, c)) return true;
            return false;
        }

        protected override void Run()
        {
            string dir = Str("path", "");
            string pattern = Str("pattern", "*.csv");
            if (dir.Length == 0 || !Directory.Exists(dir)) throw new DirectoryNotFoundException("The folder does not exist: " + dir);
            LoadState();
            Connected();
            while (!Stopping)
            {
                var list = new List<FileInfo>(new DirectoryInfo(dir).GetFiles(pattern));
                // While a logger keeps a file open, the folder listing gives the size and time of an earlier write;
                // the file itself gives the current ones. It is asked only of files that may be changing: changed in
                // the listing, not read to their listed end, or among the newest.
                list.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = 0; i < list.Count; i++)
                {
                    FileInfo f = list[i];
                    FileState st;
                    if (!files.TryGetValue(f.Name, out st)) files[f.Name] = st = new FileState();
                    bool maybe = i < 8 || f.Length != st.ListedLength || f.LastWriteTimeUtc != st.ListedWrite || f.Length != st.Offset;
                    st.ListedLength = f.Length;
                    st.ListedWrite = f.LastWriteTimeUtc;
                    if (maybe) try { f.Refresh(); } catch (IOException) { }
                }
                list.Sort((a, b) => a.LastWriteTimeUtc != b.LastWriteTimeUtc ? a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc) : string.CompareOrdinal(a.Name, b.Name));
                bool changed = false;
                string waiting = null, unreadable = null;
                foreach (FileInfo f in list)
                {
                    if (Stopping) break;
                    FileState st;
                    if (!files.TryGetValue(f.Name, out st)) files[f.Name] = st = new FileState();
                    try
                    {
                        if (!Poll(f, st, ref changed)) waiting = f.Name;
                        st.Failures = 0;
                    }
                    catch (IOException e)
                    {
                        // Being written or moved this moment: next round. Said when it lasts (locked by a program).
                        if (++st.Failures >= 5) unreadable = f.Name + ": " + e.Message;
                    }
                    catch (UnauthorizedAccessException e) { if (++st.Failures >= 5) unreadable = f.Name + ": " + e.Message; }
                    catch (HttpError) { waiting = f.Name; }   // empty, or not a table (yet)
                }
                if (changed) SaveState();
                // A file whose format cannot be read yet (only a header so far) is waited for, and said so.
                error = unreadable != null ? "cannot read " + unreadable
                    : waiting != null ? "waiting for " + waiting + ": no time column or values yet" : null;
                if (Wait(2000)) break;
            }
        }

        const long MaxRead = 64L * 1024 * 1024;   // per file and round

        // Reads what is new in one file. False when the file's format cannot be read yet.
        bool Poll(FileInfo f, FileState st, ref bool changed)
        {
            long length = f.Length;
            DateTime written = f.LastWriteTimeUtc;
            if (length == st.SeenLength && written == st.SeenWrite) return !st.Waiting;
            // A file being written can be listed with an older size: shorter only counts when the file says so.
            if (length < st.Offset) using (var fs = CsvAnalyzer.OpenShared(f.FullName)) length = fs.Length;
            // Saved by an earlier version, without a fingerprint: trusted only where a line ends.
            if (st.Print == null && st.Offset > 0 && length >= st.Offset && !EndsLine(f.FullName, st.Offset))
            {
                st.Offset = 0;
                layouts.Remove(f.Name);
                changed = true;
            }
            bool replaced = length < st.Offset ||
                (st.Offset > 0 && st.Print != null && Fingerprint(f.FullName, st.Offset) != st.Print);
            if (replaced)
            {
                st.Offset = 0;
                st.Print = null;
                layouts.Remove(f.Name);
                changed = true;
            }
            bool more = false;
            st.Waiting = false;
            if (length != st.Offset)
            {
                CsvLayout layout;
                if (!layouts.TryGetValue(f.Name, out layout))
                {
                    layout = CsvAnalyzer.Analyze(f.FullName);
                    if (!Usable(layout)) layout = null;
                    else
                    {
                        layouts[f.Name] = layout;
                        foreach (CsvColumn c in layout.Columns)
                            if (IsValue(layout, c) && !hints.ContainsKey(c.Name)) hints[c.Name] = new ChannelHint { Name = c.Name, Unit = c.Unit };
                    }
                }
                if (layout == null) st.Waiting = true;
                else
                {
                    long next = ReadFrom(f.FullName, layout, st.Offset);
                    more = length - st.Offset > MaxRead;
                    if (next != st.Offset)
                    {
                        st.Offset = next;
                        st.Print = Fingerprint(f.FullName, next);
                        changed = true;
                    }
                }
            }
            else if (st.Offset > 0 && st.Print == null)
            {
                st.Print = Fingerprint(f.FullName, st.Offset);   // saved by an earlier version
                changed = true;
            }
            // Looked at again only when it changes; a file read in parts goes on next round.
            if (!more) { st.SeenLength = length; st.SeenWrite = written; }
            return !st.Waiting;
        }

        // Where the last complete line ends in `bytes`, read from `offset`; -1 if there is none yet. In UTF-16 a line
        // end is two bytes, at an even position from the start of the file.
        static int LastLineEnd(byte[] bytes, Encoding enc, long offset)
        {
            if (enc.CodePage == 1200 || enc.CodePage == 1201)
            {
                bool le = enc.CodePage == 1200;
                for (int i = bytes.Length - 2; i >= 0; i--)
                    if ((offset + i) % 2 == 0 && (le ? bytes[i] == 0x0A && bytes[i + 1] == 0 : bytes[i] == 0 && bytes[i + 1] == 0x0A))
                        return i + 1;
                return -1;
            }
            return Array.LastIndexOf(bytes, (byte)'\n');
        }

        // Reads the complete lines after `offset`; returns where the next read starts.
        long ReadFrom(string path, CsvLayout layout, long offset)
        {
            byte[] bytes;
            using (var fs = CsvAnalyzer.OpenShared(path))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                long n = Math.Min(fs.Length - offset, MaxRead);
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
            int end = LastLineEnd(bytes, layout.Encoding, offset);
            if (end < 0) return offset;   // no complete line yet
            int start = 0;
            if (offset == 0)
            {
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) start = 3;
                else if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF))) start = 2;
            }
            string text = layout.Encoding.GetString(bytes, start, end + 1 - start);
            var csv = new CsvReader(new StringReader(text), layout.Delimiter);
            var readings = new List<Reading>();
            int skip = offset == 0 ? layout.Skip : 0;
            int width = layout.Columns.Count;
            string[] rec;
            long index = 0;
            while ((rec = csv.Next()) != null)
            {
                if (index++ < skip) continue;
                if (rec.Length > width) continue;
                rec = CsvLayout.Pad(rec, width);
                long t;
                if (!CsvAnalyzer.TryRowTime(layout, rec, TimeZoneInfo.Local, out t)) continue;
                foreach (CsvColumn c in layout.Columns)
                {
                    if (!IsValue(layout, c)) continue;
                    double v;
                    string u;
                    if (Values.TryNumber(rec[c.Index], c.DecimalComma, out v, out u)) readings.Add(new Reading(c.Name, t, v));
                }
                Lines++;
                if (readings.Count >= 5000) { Send(readings, hints); readings = new List<Reading>(); }
            }
            Send(readings, hints);
            return offset + end + 1;
        }
    }
}
