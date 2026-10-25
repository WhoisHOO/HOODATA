// The engine: sources hand it readings, one worker thread sorts them into channels, runs the detectors, keeps the
// one-minute summaries and writes everything in batches. The dashboard talks to it through Api.cs.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Hoodata
{
    public struct Reading
    {
        public string Key;
        public long T;
        public double V;
        public Reading(string key, long t, double v) { Key = key; T = t; V = v; }
    }

    // What a source knows about a channel before the first value arrives.
    public sealed class ChannelHint
    {
        public string Name, Unit;
        public double? Lo, Hi;
    }

    public interface ISource
    {
        void Start();
        void Stop();
        string State { get; }   // running, stopped, error, idle
        string Error { get; }
        bool Live { get; }       // its channels are checked for gaps against the clock
    }

    public sealed class SourceRuntime
    {
        public SourceInfo Info;
        public ISource Impl;
        public long StartedAt;
        public string Warning;   // e.g. too_many_channels
        public long FutureAt;    // when a value dated in the future last came
    }

    sealed class Batch
    {
        public int Source;
        public List<Reading> Items;
        public Dictionary<string, ChannelHint> Hints;
        public ManualResetEventSlim Done;
    }

    public sealed class EventChange
    {
        public long Seq;
        public string Op;   // open, change, close
        public EventRec E;
    }

    public sealed class Channel : IEventSink
    {
        public ChannelInfo Info;
        public Detector Det;
        public bool Live;
        readonly Engine engine;
        const int RingSize = 720;
        readonly long[] rs = new long[RingSize], rt = new long[RingSize];
        readonly double[] rv = new double[RingSize];
        int rStart, rCount;
        long mStart = long.MinValue;
        int mN;
        double mMin, mMax, mMean, mM2;
        public long LastSeq, LastAlertT, LastWarnT, OutOfOrder;   // 0: never

        public Channel(Engine engine, ChannelInfo info)
        {
            this.engine = engine;
            Info = info;
            Det = new Detector(this, engine.Settings) { Lo = info.Lo, Hi = info.Hi };
        }

        // How far ahead of this PC's clock a running source's value may be dated. One value dated years ahead (a typo
        // in a logger's line, a device whose clock is wrong) would otherwise make every later value look old.
        public const long FutureMarginMs = 10 * 60000;

        public void Add(long t, double v, long now)
        {
            if (Det.LastT != long.MinValue && t <= Det.LastT)
            {
                OutOfOrder++;
                engine.AddPending(new Sample(Info.Id, t, v));
                return;
            }
            if (Live) Det.Arrived(now, t);
            Det.Feed(t, v);
            AddToMinute(t, v);
            Remember(t, v);
            engine.AddPending(new Sample(Info.Id, t, v));
        }

        // Values replayed from the database when HOODATA starts: they teach the detector and fill the graph.
        public void Replay(long t, double v)
        {
            Det.Quiet = true;
            Det.Feed(t, v);
            Det.Quiet = false;
            Remember(t, v);
        }

        void Remember(long t, double v)
        {
            long s = engine.NextSeq();
            int i;
            if (rCount < RingSize) { i = (rStart + rCount) % RingSize; rCount++; }
            else { i = rStart; rStart = (rStart + 1) % RingSize; }
            rs[i] = s; rt[i] = t; rv[i] = v;
            LastSeq = s;
        }

        public void WritePoints(JsonWriter w, long since)
        {
            w.Arr();
            for (int k = 0; k < rCount; k++)
            {
                int i = (rStart + k) % RingSize;
                if (rs[i] <= since) continue;
                w.Arr().Int(rt[i]).Num(rv[i]).EndArr();
            }
            w.EndArr();
        }

        void AddToMinute(long t, double v)
        {
            long m = t - ((t % 60000) + 60000) % 60000;
            if (m != mStart) { FlushMinute(); mStart = m; }
            mN++;
            if (mN == 1) { mMin = mMax = mMean = v; mM2 = 0; return; }
            if (v < mMin) mMin = v;
            if (v > mMax) mMax = v;
            double d = v - mMean;
            mMean += d / mN;
            mM2 += d * (v - mMean);
        }

        public void FlushMinute()
        {
            if (mN == 0) return;
            double noise = Det.Noise;
            double sd = mN > 1 ? Math.Sqrt(mM2 / (mN - 1)) : 0;
            // Values near the ends of the double range can overflow the sums; such a minute is not summarised.
            if (!IsFinite(mMean) || !IsFinite(mMin) || !IsFinite(mMax)) { mN = 0; return; }
            engine.AddPending(new MinuteRow
            {
                Ch = Info.Id, T = mStart, N = mN, Min = mMin, Max = mMax, Mean = mMean,
                Sd = IsFinite(sd) ? sd : 0,
                Noise = IsFinite(noise) ? noise : (double?)null
            });
            mN = 0;
        }

        static bool IsFinite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        public string Status(long now)
        {
            bool alert = false, warn = false;
            foreach (EventRec e in Det.OpenEvents()) { if (Kinds.IsAlert(e.Kind)) alert = true; else warn = true; }
            if (alert || (Live && now - LastAlertT < 600000)) return "alert";
            if (warn || (Live && now - LastWarnT < 600000)) return "warn";
            if (Det.Learning) return "learning";
            return "ok";
        }

        public EventRec Open(string kind, long t0, double? value, double? expected, double? score, string detail)
        {
            var e = new EventRec { Ch = Info.Id, Kind = kind, T0 = t0, Value = value, Expected = expected, Score = score, Detail = detail ?? "{}" };
            engine.Store.InsertEvent(e);
            if (Kinds.IsAlert(kind)) LastAlertT = Clock.Now(); else LastWarnT = Clock.Now();
            engine.LogEvent("open", e);
            return e;
        }

        // A change is a new detection too (another spike merged in, an episode reopened or growing), so it keeps the
        // status held like an opening does.
        public void Changed(EventRec e)
        {
            engine.Store.UpdateEvent(e);
            if (Kinds.IsAlert(e.Kind)) LastAlertT = Clock.Now(); else LastWarnT = Clock.Now();
            engine.LogEvent("change", e);
        }

        public void Close(EventRec e, long t1)
        {
            e.T1 = Math.Max(t1, e.T0);
            engine.Store.UpdateEvent(e);
            engine.LogEvent("close", e);
        }
    }

    public sealed class Engine : IDisposable
    {
        public const string Version = "0.1.0";
        public readonly string AppDir, DataDir, WebDir;
        public Store Store;
        public DetectSettings Settings = DetectSettings.For("normal");
        public string Lang = "en";
        public int RetentionDays = 30;
        public string Key;
        public int Port;
        public Action<string> Log;

        internal readonly object Gate = new object();
        readonly Dictionary<int, Channel> channels = new Dictionary<int, Channel>();
        readonly Dictionary<string, Channel> byKey = new Dictionary<string, Channel>(StringComparer.Ordinal);
        readonly Dictionary<int, SourceRuntime> sources = new Dictionary<int, SourceRuntime>();
        readonly BlockingCollection<Batch> queue = new BlockingCollection<Batch>(256);
        readonly List<Sample> pendingSamples = new List<Sample>();
        readonly List<MinuteRow> pendingMinutes = new List<MinuteRow>();
        readonly List<EventChange> changes = new List<EventChange>();
        public readonly List<ImportJob> Jobs = new List<ImportJob>();
        Thread worker;
        volatile bool stopping;
        long seq;
        HttpServer http;
        Api api;
        public long StartedAt;
        public long Received, Invalid, Future, Dropped;

        // The browser's cookie: a token made at each start (not the lasting key), under a name of its own for each data
        // folder, so that two HOODATAs on one PC do not overwrite each other's.
        public string Session, CookieName;

        // Set while values cannot be written (a full disk, a locked file); shown on the dashboard and in the window.
        public volatile string StoreError;
        int flushFailures;

        // One message cannot make unlimited channels: a source gets at most this many.
        public const int MaxChannelsPerSource = 1000;
        readonly Dictionary<int, int> channelsPerSource = new Dictionary<int, int>();

        // Adding, changing and removing sources happen one at a time, so a stopped source is never restarted twice.
        readonly object lifecycle = new object();

        public Engine(string appDir, string dataDir)
        {
            AppDir = appDir;
            DataDir = dataDir;
            WebDir = System.IO.Path.Combine(appDir, "web");
        }

        public string LogPath;
        readonly object logLock = new object();

        // Written by the engine itself: PowerShell script blocks cannot run on the engine's threads.
        public void Say(string s)
        {
            if (Log != null) Log(s);
            if (LogPath == null) return;
            lock (logLock)
                try
                {
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 4 * 1024 * 1024) File.Copy(LogPath, LogPath + ".old", true);
                    if (fi.Exists && fi.Length > 4 * 1024 * 1024) File.Delete(LogPath);
                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }

        public long EventsSince(long t)
        {
            using (var rd = Store.Read())
            using (var st = rd.Db.Prepare("SELECT count(*) FROM event WHERE t0 >= ?1"))
            {
                st.Bind(1, t);
                st.Step();
                return st.GetLong(0);
            }
        }

        public int RunningSources
        {
            get
            {
                int n = 0;
                lock (Gate) foreach (SourceRuntime s in sources.Values) if (s.Impl != null && s.Impl.Live && s.Impl.State == "running") n++;
                return n;
            }
        }

        public int ChannelCount { get { lock (Gate) return channels.Count; } }

        static string Hash8(string s)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)), 0, 4).Replace("-", "").ToLowerInvariant();
        }

        public long NextSeq() { return Interlocked.Increment(ref seq); }
        public long Seq { get { return Interlocked.Read(ref seq); } }

        // ---- start and stop ----

        public void Start(int port)
        {
            StartedAt = Clock.Now();
            Store = new Store(System.IO.Path.Combine(DataDir, "hoodata.db"));
            Store.CloseOpenEvents();
            Key = Store.GetMeta("key");
            if (Key == null)
            {
                var b = new byte[32];
                using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
                Key = BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
                Store.SetMeta("key", Key);
            }
            string sens = Store.GetMeta("sensitivity");
            if (sens != null) Settings = DetectSettings.For(sens);
            string ret = Store.GetMeta("retention_days");
            int r;
            if (ret != null && int.TryParse(ret, out r) && r > 0) RetentionDays = r;
            string lang = Store.GetMeta("lang");
            if (lang != null) Lang = lang;
            Session = Secrets.NewKey(24);
            CookieName = "hoodata_" + Hash8(DataDir.ToLowerInvariant());
            // Copies of uploads that were never imported (a closed preview, a stopped HOODATA).
            try { foreach (string f in Directory.GetFiles(UploadDir)) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }

            var list = Store.LoadSources();
            if (list.Count == 0 && Store.GetMeta("first_run") == null)
            {
                // Something to look at straight away: this PC. It can be switched off or removed.
                list.Add(Store.AddSource("pc", Text("PcSource"), "{\"interval\":5}", true));
                Store.SetMeta("first_run", Clock.Now().ToString());
            }
            lock (Gate)
            {
                foreach (SourceInfo s in list) sources[s.Id] = new SourceRuntime { Info = s };
                foreach (ChannelInfo c in Store.LoadChannels())
                {
                    if (!sources.ContainsKey(c.Source)) continue;
                    var ch = new Channel(this, c);
                    channels[c.Id] = ch;
                    byKey[c.Source + "\u0001" + c.Key] = ch;
                    int n;
                    channelsPerSource.TryGetValue(c.Source, out n);
                    channelsPerSource[c.Source] = n + 1;
                    WarmUp(ch);
                }
            }

            worker = new Thread(Work) { IsBackground = true, Name = "HOODATA worker" };
            worker.Start();

            api = new Api(this);
            http = StartHttp(port);
            Port = http.Port;
            lock (Gate) foreach (SourceRuntime s in sources.Values) if (s.Info.Enabled) StartSource(s);
            Say("HOODATA " + Version + " on http://127.0.0.1:" + Port + "/, SQLite " + SqliteDb.Version + ", data in " + DataDir);
        }

        HttpServer StartHttp(int port)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                int p = port == 0 ? 0 : port + attempt;
                var server = new HttpServer(IPAddress.Loopback, p, api.Handle) { Log = Say };
                try { server.Start(); return server; }
                catch (System.Net.Sockets.SocketException) { if (port == 0) throw; }
            }
            throw new InvalidOperationException("No free port from " + port + " to " + (port + 19) + ".");
        }

        void WarmUp(Channel ch)
        {
            using (var rd = Store.Read())
            {
                var t = new List<long>();
                var v = new List<double>();
                using (var st = rd.Db.Prepare("SELECT t, v FROM sample WHERE ch = ?1 ORDER BY t DESC LIMIT 300"))
                {
                    st.Bind(1, ch.Info.Id);
                    while (st.Step()) { t.Add(st.GetLong(0)); v.Add(st.GetDouble(1)); }
                }
                t.Reverse();
                v.Reverse();
                long first = t.Count > 0 ? t[0] : long.MaxValue;
                // The usual level for each hour of the day. Five weeks back, so that a weekend hour has the five earlier
                // weekend days it needs.
                using (var st = rd.Db.Prepare("SELECT t / 300000, sum(mean * n) / sum(n) FROM minute WHERE ch = ?1 AND t >= ?2 AND t < ?3 GROUP BY t / 300000 ORDER BY 1"))
                {
                    st.Bind(1, ch.Info.Id).Bind(2, Clock.Now() - 35L * 86400000).Bind(3, first);
                    while (st.Step()) ch.Det.AddToProfile(st.GetLong(0) * 300000 + 299999, st.GetDouble(1));
                }
                // The usual noise at each level, from the noise each minute summary kept (about the last 3.5 days).
                using (var st = rd.Db.Prepare("SELECT t, noise, mean FROM minute WHERE ch = ?1 AND t >= ?2 AND t < ?3 AND noise IS NOT NULL ORDER BY t"))
                {
                    st.Bind(1, ch.Info.Id).Bind(2, Clock.Now() - 84L * 3600000).Bind(3, first);
                    while (st.Step()) ch.Det.AddNoiseEstimate(st.GetLong(0), st.GetDouble(1), st.GetDouble(2));
                }
                for (int i = 0; i < t.Count; i++) ch.Replay(t[i], v[i]);
            }
        }

        public void Stop()
        {
            if (stopping) return;
            lock (lifecycle) StopAll();
        }

        void StopAll()
        {
            if (http != null) http.Stop();   // the dashboard goes first: nothing is changed while stopping
            List<SourceRuntime> all;
            lock (Gate) all = new List<SourceRuntime>(sources.Values);
            foreach (SourceRuntime s in all) StopSource(s);
            lock (Gate) foreach (ImportJob j in Jobs) j.Cancel();
            stopping = true;
            if (worker != null) worker.Join(15000);
            lock (Gate)
            {
                foreach (Channel ch in channels.Values) { ch.FlushMinute(); ch.Det.Suspend(); }
            }
            Flush();
            if (Store != null) Store.Dispose();
            Say("HOODATA stopped");
        }

        public void Dispose() { Stop(); }

        // ---- sources ----

        public ISource CreateSource(SourceInfo info)
        {
            switch (info.Kind)
            {
                case "pc": return new PcSource(this, info);
                case "file": return new FileSource();
                case "http": return new HttpIngestSource(this, info);
                case "mqtt": return new MqttSource(this, info);
                case "modbus": return new ModbusTcpSource(this, info);
                case "serial": return new SerialSource(this, info);
                case "folder": return new FolderSource(this, info);
                default: return new UnknownSource(info.Kind);
            }
        }

        void StartSource(SourceRuntime s)
        {
            try
            {
                s.Impl = CreateSource(s.Info);
                s.StartedAt = Clock.Now();
                foreach (Channel ch in ChannelsOf(s.Info.Id))
                {
                    ch.Live = s.Impl.Live;
                    ch.Det.ResumeFloor = ResumeFloor(s);
                    ch.Det.WatchFrom = s.StartedAt;
                }
                s.Impl.Start();
            }
            catch (Exception e) { Say("source " + s.Info.Id + ": " + e.Message); }
        }

        // Values from before a source started cannot show its gaps (HOODATA was not running then), except for a
        // folder: its files hold the lines written meanwhile, with their own times.
        static long ResumeFloor(SourceRuntime s) { return s.Impl is FolderSource ? long.MinValue : s.StartedAt; }

        void StopSource(SourceRuntime s)
        {
            if (s.Impl == null) return;
            try { s.Impl.Stop(); } catch (Exception e) { Say("source " + s.Info.Id + ": " + e.Message); }
            Submit(s.Info.Id, null, null, true);   // after its last readings, close what is open
        }

        List<Channel> ChannelsOf(int source)
        {
            var list = new List<Channel>();
            foreach (Channel ch in channels.Values) if (ch.Info.Source == source) list.Add(ch);
            return list;
        }

        public SourceInfo AddSource(string kind, string name, string config, bool enabled)
        {
            lock (lifecycle)
            {
                if (stopping) throw new HttpError(503, "stopping");
                SourceInfo info = Store.AddSource(kind, name, config, enabled);
                var s = new SourceRuntime { Info = info };
                lock (Gate)
                {
                    sources[info.Id] = s;
                    if (enabled) StartSource(s);
                }
                return info;
            }
        }

        public void UpdateSource(int id, string name, string config, bool? enabled)
        {
            lock (lifecycle)
            {
                if (stopping) throw new HttpError(503, "stopping");
                SourceRuntime s;
                lock (Gate) if (!sources.TryGetValue(id, out s)) throw new HttpError(404, "no_source");
                StopSource(s);
                WaitForQueue();   // the stop is processed before the restart
                lock (Gate)
                {
                    if (name != null) s.Info.Name = name;
                    // New settings can mean a new rhythm (a poll every 20 s instead of every second).
                    if (config != null && config != s.Info.Config)
                        foreach (Channel ch in ChannelsOf(id)) ch.Det.ResetRhythm();
                    if (config != null) s.Info.Config = config;
                    if (enabled.HasValue) s.Info.Enabled = enabled.Value;
                    Store.UpdateSource(s.Info);
                    s.Impl = null;
                    s.Warning = null;
                    if (s.Info.Enabled) StartSource(s);
                }
            }
        }

        public void DeleteSource(int id)
        {
            lock (lifecycle)
            {
                SourceRuntime s;
                lock (Gate) if (!sources.TryGetValue(id, out s)) throw new HttpError(404, "no_source");
                StopSource(s);
                WaitForQueue();
                lock (Gate)
                {
                    foreach (Channel ch in ChannelsOf(id)) { channels.Remove(ch.Info.Id); byKey.Remove(id + "\u0001" + ch.Info.Key); }
                    sources.Remove(id);
                    channelsPerSource.Remove(id);
                }
                Flush();
                Store.DeleteSource(id);
            }
        }

        public List<SourceRuntime> Sources()
        {
            lock (Gate) return new List<SourceRuntime>(sources.Values);
        }

        public List<Channel> Channels()
        {
            lock (Gate) return new List<Channel>(channels.Values);
        }

        public Channel FindChannel(int id)
        {
            Channel ch;
            lock (Gate) return channels.TryGetValue(id, out ch) ? ch : null;
        }

        // ---- readings ----

        // Called by sources, from any thread. Blocks while the queue is full, which paces a big import.
        public void Submit(int source, List<Reading> items, Dictionary<string, ChannelHint> hints, bool suspend)
        {
            if (stopping) return;
            var b = new Batch { Source = source, Items = items, Hints = hints };
            if (suspend) b.Items = null;
            try { queue.Add(b); } catch (InvalidOperationException) { }
        }

        public void Submit(int source, List<Reading> items, Dictionary<string, ChannelHint> hints)
        {
            Submit(source, items, hints, false);
        }

        // Waits until everything submitted so far is processed and written.
        public void WaitForQueue()
        {
            if (stopping || worker == null) return;
            var done = new ManualResetEventSlim(false);
            try { queue.Add(new Batch { Source = -1, Done = done }); } catch (InvalidOperationException) { return; }
            done.Wait(60000);
        }

        void Work()
        {
            long lastFlush = 0, lastTick = 0, lastPrune = 0;
            while (!stopping || queue.Count > 0)
            {
                Batch b;
                try
                {
                    if (queue.TryTake(out b, 200)) Process(b);
                    long now = Clock.Now();
                    if (now - lastTick >= 1000) { Tick(now); lastTick = now; }
                    if (now - lastFlush >= 500 || pendingSamples.Count >= 20000) { Flush(); lastFlush = now; }
                    if (now - lastPrune >= 3600000) { Prune(now); lastPrune = now; }
                }
                catch (Exception e) { Say("worker: " + e); Thread.Sleep(500); }
            }
        }

        void Process(Batch b)
        {
            if (b.Source == -1)
            {
                Flush();
                b.Done.Set();
                return;
            }
            lock (Gate)
            {
                SourceRuntime src;
                if (!sources.TryGetValue(b.Source, out src)) return;
                if (b.Items == null)
                {
                    foreach (Channel ch in ChannelsOf(b.Source)) { ch.FlushMinute(); ch.Det.Suspend(); ch.Live = false; }
                    return;
                }
                long now = Clock.Now();
                int future = 0, taken = 0;
                foreach (Reading r in b.Items)
                {
                    if (double.IsNaN(r.V) || double.IsInfinity(r.V) || r.Key == null) { Invalid++; continue; }
                    Channel ch = Resolve(src, r.Key, b.Hints);
                    if (ch == null) { Dropped++; continue; }
                    if (ch.Live && r.T > now + Channel.FutureMarginMs) { Future++; future++; continue; }
                    Received++;
                    taken++;
                    ch.Add(r.T, r.V, now);
                }
                // Said on the source until ten minutes after the last such value (one source can take values from
                // several devices, and only one of them may have its clock wrong).
                if (future > 0) { src.Warning = "future_times"; src.FutureAt = now; }
                else if (src.Warning == "future_times" && now - src.FutureAt > 600000) src.Warning = null;
            }
            if (b.Done != null) b.Done.Set();
        }

        Channel Resolve(SourceRuntime src, string key, Dictionary<string, ChannelHint> hints)
        {
            Channel ch;
            string k = src.Info.Id + "\u0001" + key;
            if (byKey.TryGetValue(k, out ch)) return ch;
            int count;
            channelsPerSource.TryGetValue(src.Info.Id, out count);
            if (count >= MaxChannelsPerSource)
            {
                src.Warning = "too_many_channels";
                return null;
            }
            channelsPerSource[src.Info.Id] = count + 1;
            ChannelHint hint = null;
            if (hints != null) hints.TryGetValue(key, out hint);
            ChannelInfo info = Store.AddChannel(src.Info.Id, key, hint != null && hint.Name != null ? hint.Name : key, hint != null ? hint.Unit : "");
            if (hint != null && (hint.Lo.HasValue || hint.Hi.HasValue))
            {
                info.Lo = hint.Lo;
                info.Hi = hint.Hi;
                Store.UpdateChannel(info);
            }
            ch = new Channel(this, info);
            ch.Live = src.Impl != null && src.Impl.Live;
            ch.Det.ResumeFloor = ch.Live ? ResumeFloor(src) : long.MinValue;
            if (ch.Live) ch.Det.WatchFrom = src.StartedAt;
            channels[info.Id] = ch;
            byKey[k] = ch;
            return ch;
        }

        void Tick(long now)
        {
            lock (Gate) foreach (Channel ch in channels.Values) if (ch.Live) ch.Det.Tick(now);
        }

        internal void AddPending(Sample s) { pendingSamples.Add(s); }
        internal void AddPending(MinuteRow m) { pendingMinutes.Add(m); }

        readonly object flushLock = new object();

        // A batch that cannot be written (a full disk, a locked file) is kept and tried again, so nothing is lost when
        // the cause goes away; while it lasts, StoreError says so. Waiting values are capped at about a million. A
        // batch the database refuses as such (a constraint, a wrong type) would fail again every time: it is dropped.
        void Flush()
        {
            lock (flushLock)
            {
                List<Sample> s;
                List<MinuteRow> m;
                lock (Gate)
                {
                    if (pendingSamples.Count == 0 && pendingMinutes.Count == 0) return;
                    s = new List<Sample>(pendingSamples);
                    m = new List<MinuteRow>(pendingMinutes);
                    pendingSamples.Clear();
                    pendingMinutes.Clear();
                }
                try
                {
                    Store.Write(s, m);
                    if (StoreError != null) Say("writing works again after " + flushFailures + " failures");
                    StoreError = null;
                    flushFailures = 0;
                }
                catch (SqliteException e)
                {
                    int code = e.Code & 0xFF;
                    if (code == 18 || code == 19 || code == 20 || code == 25)   // TOOBIG, CONSTRAINT, MISMATCH, RANGE
                    {
                        Say("dropped " + s.Count + " values and " + m.Count + " minutes that cannot be written: " + e.Message);
                        lock (Gate) Dropped += s.Count;
                        return;
                    }
                    if (flushFailures++ == 0) Say("cannot write: " + e.Message);
                    StoreError = e.Message;
                    lock (Gate)
                    {
                        pendingSamples.InsertRange(0, s);
                        pendingMinutes.InsertRange(0, m);
                        const int Cap = 1000000;
                        if (pendingSamples.Count > Cap)
                        {
                            Dropped += pendingSamples.Count - Cap;
                            pendingSamples.RemoveRange(0, pendingSamples.Count - Cap);
                        }
                    }
                }
            }
        }

        // Raw values from running sources are kept for RetentionDays; imported files keep theirs until removed.
        void Prune(long now)
        {
            var ids = new List<int>();
            lock (Gate)
                foreach (Channel ch in channels.Values)
                {
                    SourceRuntime s;
                    if (sources.TryGetValue(ch.Info.Source, out s) && s.Info.Kind != "file") ids.Add(ch.Info.Id);
                }
            if (ids.Count > 0) Store.PruneSamples(ids, now - RetentionDays * 86400000L);
        }

        // ---- anomalies ----

        internal void LogEvent(string op, EventRec e)
        {
            var copy = new EventRec
            {
                Id = e.Id, Ch = e.Ch, Kind = e.Kind, T0 = e.T0, T1 = e.T1, Value = e.Value, Expected = e.Expected,
                Score = e.Score, Detail = e.Detail, Ack = e.Ack
            };
            lock (changes)
            {
                changes.Add(new EventChange { Seq = NextSeq(), Op = op, E = copy });
                if (changes.Count > 2000) changes.RemoveRange(0, 1000);
            }
        }

        public List<EventChange> ChangesSince(long since)
        {
            var list = new List<EventChange>();
            lock (changes) foreach (EventChange c in changes) if (c.Seq > since) list.Add(c);
            return list;
        }

        public void SetSensitivity(string s)
        {
            DetectSettings cfg = DetectSettings.For(s);
            lock (Gate)
            {
                Settings = cfg;
                foreach (Channel ch in channels.Values) ch.Det.Settings = cfg;
            }
            Store.SetMeta("sensitivity", cfg.Sensitivity);
        }

        public void UpdateChannel(ChannelInfo info)
        {
            lock (Gate)
            {
                Channel ch;
                if (channels.TryGetValue(info.Id, out ch))
                {
                    ch.Info = info;
                    ch.Det.Lo = info.Lo;
                    ch.Det.Hi = info.Hi;
                }
                Store.UpdateChannel(info);
            }
        }

        // ---- patterns ----

        public volatile bool PatternsRunning;
        public string PatternsError;

        // Runs the pattern analysis in the background; the result is kept in the database.
        public bool StartPatterns(long from, long to)
        {
            lock (Gate)
            {
                if (PatternsRunning) return false;
                PatternsRunning = true;
            }
            var ids = new List<int>();
            foreach (Channel ch in Channels()) if (!ch.Info.Hidden) ids.Add(ch.Info.Id);
            var t = new Thread(() =>
            {
                try
                {
                    Flush();
                    PatternResult r = PatternAnalyzer.Run(Store, ids, from, to);
                    Store.SetMeta("patterns", r.ToJson());
                    PatternsError = null;
                }
                catch (Exception e) { PatternsError = e.Message; Say("patterns: " + e); }
                finally { PatternsRunning = false; }
            }) { IsBackground = true, Name = "HOODATA patterns" };
            t.Start();
            return true;
        }

        // ---- imports ----

        // A new upload replaces a preview that was left without importing or cancelling, and old ones expire.
        public void AddJob(ImportJob job)
        {
            lock (Gate)
            {
                foreach (ImportJob j in Jobs)
                    if (j.State == "ready") j.Abandon();
                Jobs.RemoveAll(j => j.Finished && Clock.Now() - j.FinishedAt > 3600000);
                Jobs.Add(job);
            }
        }

        public List<ImportJob> JobList()
        {
            lock (Gate) return new List<ImportJob>(Jobs);
        }

        // ---- texts, from strings.json (this file stays ASCII: Add-Type's compiler reads it in the ANSI code page) ----

        Dictionary<string, object> strings;

        public Dictionary<string, object> Texts(string lang)
        {
            if (strings == null)
            {
                object all = null;
                string path = System.IO.Path.Combine(AppDir, "strings.json");
                if (File.Exists(path)) Json.TryParse(File.ReadAllText(path, Encoding.UTF8), out all);
                strings = Json.Obj(all) ?? new Dictionary<string, object>();
            }
            return Json.Obj(Json.Get(strings, lang == "ko" ? "ko" : "en")) ?? new Dictionary<string, object>();
        }

        public string Text(string key) { return Json.Str(Texts(Lang), key, key); }

        public string UploadDir
        {
            get
            {
                string d = System.IO.Path.Combine(DataDir, "uploads");
                Directory.CreateDirectory(d);
                return d;
            }
        }
    }

    // A source whose readings were imported once: nothing runs.
    public sealed class FileSource : ISource
    {
        public void Start() { }
        public void Stop() { }
        public string State { get { return "idle"; } }
        public string Error { get { return null; } }
        public bool Live { get { return false; } }
    }

    // A kind this version does not know, e.g. from a newer HOODATA: kept, not run.
    public sealed class UnknownSource : ISource
    {
        readonly string kind;
        public UnknownSource(string kind) { this.kind = kind; }
        public void Start() { }
        public void Stop() { }
        public string State { get { return "error"; } }
        public string Error { get { return "unknown kind: " + kind; } }
        public bool Live { get { return false; } }
    }
}
