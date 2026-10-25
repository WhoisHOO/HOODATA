// The database: connections (data sources), channels, values, one-minute summaries and anomalies.
// One connection writes, under WriteLock; readers get their own read-only connections (WAL lets them run alongside).
using System;
using System.Collections.Generic;
using System.IO;

namespace Hoodata
{
    public sealed class SourceInfo
    {
        public int Id;
        public string Kind;
        public string Name;
        public string Config;
        public bool Enabled;
        public long Created;
    }

    public sealed class ChannelInfo
    {
        public int Id;
        public int Source;
        public string Key;
        public string Name;
        public string Unit;
        public double? Lo;
        public double? Hi;
        public bool Hidden;
        public long Created;
    }

    public struct Sample
    {
        public int Ch;
        public long T;
        public double V;
        public Sample(int ch, long t, double v) { Ch = ch; T = t; V = v; }
    }

    public sealed class MinuteRow
    {
        public int Ch;
        public long T;
        public int N;
        public double Min, Max, Mean, Sd;
        public double? Noise;
    }

    public sealed class EventRec
    {
        public long Id;
        public int Ch;
        public string Kind;
        public long T0;
        public long? T1;
        public double? Value;
        public double? Expected;
        public double? Score;
        public string Detail = "{}";
        public bool Ack;
    }

    public sealed class Store : IDisposable
    {
        public const int SchemaVersion = 1;
        public readonly object WriteLock = new object();
        readonly string path;
        readonly SqliteDb w;
        readonly Stack<SqliteDb> readers = new Stack<SqliteDb>();
        SqliteStmt insSample, insMinute;

        public Store(string path)
        {
            this.path = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            w = new SqliteDb(path, false);
            w.Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
            long version = 0;
            w.Exec("CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT)");
            string v = GetMeta("schema");
            if (v != null) long.TryParse(v, out version);
            if (version > SchemaVersion)
                throw new InvalidOperationException("The data folder was written by a newer HOODATA (schema " + version + ").");
            w.Exec(@"
CREATE TABLE IF NOT EXISTS source(id INTEGER PRIMARY KEY, kind TEXT NOT NULL, name TEXT NOT NULL,
  config TEXT NOT NULL DEFAULT '{}', enabled INTEGER NOT NULL DEFAULT 1, created INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS channel(id INTEGER PRIMARY KEY, source INTEGER NOT NULL, key TEXT NOT NULL,
  name TEXT NOT NULL, unit TEXT NOT NULL DEFAULT '', lo REAL, hi REAL, hidden INTEGER NOT NULL DEFAULT 0,
  created INTEGER NOT NULL, UNIQUE(source, key));
CREATE TABLE IF NOT EXISTS sample(ch INTEGER NOT NULL, t INTEGER NOT NULL, v REAL NOT NULL,
  PRIMARY KEY(ch, t)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS minute(ch INTEGER NOT NULL, t INTEGER NOT NULL, n INTEGER NOT NULL,
  mn REAL NOT NULL, mx REAL NOT NULL, mean REAL NOT NULL, sd REAL NOT NULL, noise REAL,
  PRIMARY KEY(ch, t)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS event(id INTEGER PRIMARY KEY, ch INTEGER NOT NULL, kind TEXT NOT NULL,
  t0 INTEGER NOT NULL, t1 INTEGER, value REAL, expected REAL, score REAL, detail TEXT NOT NULL DEFAULT '{}',
  ack INTEGER NOT NULL DEFAULT 0);
CREATE INDEX IF NOT EXISTS event_t0 ON event(t0);
CREATE INDEX IF NOT EXISTS event_ch ON event(ch, t0);
");
            SetMeta("schema", SchemaVersion.ToString());
            insSample = w.Prepare("INSERT OR REPLACE INTO sample(ch, t, v) VALUES(?1, ?2, ?3)");
            insMinute = w.Prepare("INSERT OR REPLACE INTO minute(ch, t, n, mn, mx, mean, sd, noise) VALUES(?1,?2,?3,?4,?5,?6,?7,?8)");
        }

        public string DbPath { get { return path; } }

        // ---- readers ----

        public sealed class Reader : IDisposable
        {
            readonly Store store;
            public readonly SqliteDb Db;
            internal Reader(Store store, SqliteDb db) { this.store = store; Db = db; }
            public void Dispose() { store.Return(Db); }
        }

        public Reader Read()
        {
            SqliteDb db = null;
            lock (readers) { if (readers.Count > 0) db = readers.Pop(); }
            if (db == null) db = new SqliteDb(path, true);
            return new Reader(this, db);
        }

        void Return(SqliteDb db)
        {
            lock (readers)
            {
                if (readers.Count < 8) { readers.Push(db); return; }
            }
            db.Dispose();
        }

        // ---- meta ----

        public string GetMeta(string key)
        {
            lock (WriteLock)
                using (var st = w.Prepare("SELECT v FROM meta WHERE k = ?1"))
                {
                    st.Bind(1, key);
                    return st.Step() ? st.GetText(0) : null;
                }
        }

        public void SetMeta(string key, string value)
        {
            lock (WriteLock)
                using (var st = w.Prepare("INSERT OR REPLACE INTO meta(k, v) VALUES(?1, ?2)"))
                    st.Bind(1, key).Bind(2, value).Run();
        }

        // ---- sources ----

        public List<SourceInfo> LoadSources()
        {
            var list = new List<SourceInfo>();
            lock (WriteLock)
                using (var st = w.Prepare("SELECT id, kind, name, config, enabled, created FROM source ORDER BY id"))
                    while (st.Step())
                        list.Add(new SourceInfo
                        {
                            Id = (int)st.GetLong(0),
                            Kind = st.GetText(1),
                            Name = st.GetText(2),
                            Config = st.GetText(3),
                            Enabled = st.GetLong(4) != 0,
                            Created = st.GetLong(5)
                        });
            return list;
        }

        public SourceInfo AddSource(string kind, string name, string config, bool enabled)
        {
            var s = new SourceInfo { Kind = kind, Name = name, Config = config ?? "{}", Enabled = enabled, Created = Clock.Now() };
            lock (WriteLock)
            {
                using (var st = w.Prepare("INSERT INTO source(kind, name, config, enabled, created) VALUES(?1,?2,?3,?4,?5)"))
                    st.Bind(1, kind).Bind(2, name).Bind(3, s.Config).Bind(4, enabled ? 1 : 0).Bind(5, s.Created).Run();
                s.Id = (int)w.LastInsertRowId;
            }
            return s;
        }

        public void UpdateSource(SourceInfo s)
        {
            lock (WriteLock)
                using (var st = w.Prepare("UPDATE source SET name = ?2, config = ?3, enabled = ?4 WHERE id = ?1"))
                    st.Bind(1, s.Id).Bind(2, s.Name).Bind(3, s.Config).Bind(4, s.Enabled ? 1 : 0).Run();
        }

        // Removes the source with everything it collected.
        public void DeleteSource(int id)
        {
            lock (WriteLock)
            {
                w.Exec("BEGIN");
                try
                {
                    foreach (string sql in new[] {
                        "DELETE FROM sample WHERE ch IN (SELECT id FROM channel WHERE source = ?1)",
                        "DELETE FROM minute WHERE ch IN (SELECT id FROM channel WHERE source = ?1)",
                        "DELETE FROM event WHERE ch IN (SELECT id FROM channel WHERE source = ?1)",
                        "DELETE FROM channel WHERE source = ?1",
                        "DELETE FROM source WHERE id = ?1" })
                        using (var st = w.Prepare(sql)) st.Bind(1, id).Run();
                    w.Exec("COMMIT");
                }
                catch { w.Exec("ROLLBACK"); throw; }
            }
        }

        // ---- channels ----

        static ChannelInfo ReadChannel(SqliteStmt st)
        {
            return new ChannelInfo
            {
                Id = (int)st.GetLong(0),
                Source = (int)st.GetLong(1),
                Key = st.GetText(2),
                Name = st.GetText(3),
                Unit = st.GetText(4),
                Lo = st.GetDoubleOrNull(5),
                Hi = st.GetDoubleOrNull(6),
                Hidden = st.GetLong(7) != 0,
                Created = st.GetLong(8)
            };
        }

        public List<ChannelInfo> LoadChannels()
        {
            var list = new List<ChannelInfo>();
            lock (WriteLock)
                using (var st = w.Prepare("SELECT id, source, key, name, unit, lo, hi, hidden, created FROM channel ORDER BY id"))
                    while (st.Step()) list.Add(ReadChannel(st));
            return list;
        }

        public ChannelInfo AddChannel(int source, string key, string name, string unit)
        {
            var c = new ChannelInfo { Source = source, Key = key, Name = name, Unit = unit ?? "", Created = Clock.Now() };
            lock (WriteLock)
            {
                using (var st = w.Prepare("INSERT INTO channel(source, key, name, unit, created) VALUES(?1,?2,?3,?4,?5)"))
                    st.Bind(1, source).Bind(2, key).Bind(3, name).Bind(4, c.Unit).Bind(5, c.Created).Run();
                c.Id = (int)w.LastInsertRowId;
            }
            return c;
        }

        public void UpdateChannel(ChannelInfo c)
        {
            lock (WriteLock)
                using (var st = w.Prepare("UPDATE channel SET name = ?2, unit = ?3, lo = ?4, hi = ?5, hidden = ?6 WHERE id = ?1"))
                    st.Bind(1, c.Id).Bind(2, c.Name).Bind(3, c.Unit).Bind(4, c.Lo).Bind(5, c.Hi).Bind(6, c.Hidden ? 1 : 0).Run();
        }

        // ---- values ----

        // Writes one batch in a single transaction.
        public void Write(List<Sample> samples, List<MinuteRow> minutes)
        {
            if (samples.Count == 0 && minutes.Count == 0) return;
            lock (WriteLock)
            {
                w.Exec("BEGIN");
                try
                {
                    foreach (Sample s in samples)
                    {
                        insSample.Bind(1, s.Ch).Bind(2, s.T).Bind(3, s.V);
                        insSample.Run();
                    }
                    foreach (MinuteRow m in minutes)
                    {
                        insMinute.Bind(1, m.Ch).Bind(2, m.T).Bind(3, m.N).Bind(4, m.Min).Bind(5, m.Max)
                            .Bind(6, m.Mean).Bind(7, m.Sd).Bind(8, m.Noise);
                        insMinute.Run();
                    }
                    w.Exec("COMMIT");
                }
                catch { w.Exec("ROLLBACK"); throw; }
            }
        }

        // Raw values older than the cutoff go; the one-minute summaries and the anomalies stay.
        public int PruneSamples(List<int> channels, long before)
        {
            int total = 0;
            lock (WriteLock)
                using (var st = w.Prepare("DELETE FROM sample WHERE ch = ?1 AND t < ?2"))
                    foreach (int ch in channels)
                    {
                        st.Bind(1, ch).Bind(2, before).Run();
                        total += w.Changes;
                    }
            return total;
        }

        // ---- anomalies ----

        public void InsertEvent(EventRec e)
        {
            lock (WriteLock)
            {
                using (var st = w.Prepare("INSERT INTO event(ch, kind, t0, t1, value, expected, score, detail, ack) VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9)"))
                    st.Bind(1, e.Ch).Bind(2, e.Kind).Bind(3, e.T0).Bind(4, e.T1).Bind(5, e.Value).Bind(6, e.Expected)
                        .Bind(7, e.Score).Bind(8, e.Detail ?? "{}").Bind(9, e.Ack ? 1 : 0).Run();
                e.Id = w.LastInsertRowId;
            }
        }

        public void UpdateEvent(EventRec e)
        {
            lock (WriteLock)
                using (var st = w.Prepare("UPDATE event SET t1 = ?2, value = ?3, expected = ?4, score = ?5, detail = ?6, ack = ?7 WHERE id = ?1"))
                    st.Bind(1, e.Id).Bind(2, e.T1).Bind(3, e.Value).Bind(4, e.Expected).Bind(5, e.Score)
                        .Bind(6, e.Detail ?? "{}").Bind(7, e.Ack ? 1 : 0).Run();
        }

        public void SetEventAck(long id, bool ack)
        {
            lock (WriteLock)
                using (var st = w.Prepare("UPDATE event SET ack = ?2 WHERE id = ?1"))
                    st.Bind(1, id).Bind(2, ack ? 1 : 0).Run();
        }

        // Anomalies left open by a previous run end where their channel's data ended.
        public void CloseOpenEvents()
        {
            lock (WriteLock)
                w.Exec("UPDATE event SET t1 = max(t0, coalesce((SELECT max(t) FROM minute WHERE minute.ch = event.ch) + 59999, t0)) WHERE t1 IS NULL");
        }

        public void Dispose()
        {
            lock (WriteLock)
            {
                if (insSample != null) { insSample.Dispose(); insSample = null; }
                if (insMinute != null) { insMinute.Dispose(); insMinute = null; }
                w.Dispose();
            }
            lock (readers) while (readers.Count > 0) readers.Pop().Dispose();
        }
    }

    public static class Clock
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static long Now() { return (long)(DateTime.UtcNow - Epoch).TotalMilliseconds; }
        public static long FromUtc(DateTime utc) { return (long)(utc - Epoch).TotalMilliseconds; }
        public static DateTime ToUtc(long ms) { return Epoch.AddMilliseconds(ms); }
        public static DateTime ToLocal(long ms) { return Epoch.AddMilliseconds(ms).ToLocalTime(); }
    }
}
