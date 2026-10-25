// The dashboard's API and files. Only this PC can connect (the server listens on 127.0.0.1), and only a browser that
// was given the key: the HOODATA window opens /open?key=..., which sets it as a SameSite=Strict cookie.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Hoodata
{
    public sealed class Api
    {
        readonly Engine e;
        const string Cookie = "hoodata_key";
        static readonly Dictionary<string, string> Files = new Dictionary<string, string> {
            { "/", "index.html" }, { "/index.html", "index.html" }, { "/app.js", "app.js" }, { "/plot.js", "plot.js" },
            { "/style.css", "style.css" }, { "/favicon.svg", "favicon.svg" } };
        static readonly Dictionary<string, string> Types = new Dictionary<string, string> {
            { ".html", "text/html; charset=utf-8" }, { ".js", "text/javascript; charset=utf-8" }, { ".css", "text/css; charset=utf-8" },
            { ".svg", "image/svg+xml" }, { ".json", "application/json; charset=utf-8" } };
        const string Csp = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'; " +
            "frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

        public Api(Engine engine) { e = engine; }

        static bool SameKey(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public HttpResponse Handle(HttpRequest req)
        {
            // Another site can point a name at 127.0.0.1 (DNS rebinding); its requests carry that name in Host.
            string host = req.Header("Host") ?? "";
            if (host != "127.0.0.1:" + e.Port && host != "localhost:" + e.Port) return HttpResponse.Error(421, "wrong_host");

            if (req.Path == "/open")
            {
                if (!SameKey(req.Arg("key", null), e.Key)) return HttpResponse.Error(403, "wrong_key");
                HttpResponse r = HttpResponse.Redirect("/");
                r.Headers["Set-Cookie"] = Cookie + "=" + e.Key + "; Path=/; HttpOnly; SameSite=Strict; Max-Age=31536000";
                return r;
            }

            string file;
            if (req.Method == "GET" && Files.TryGetValue(req.Path, out file)) return Static(Path.Combine(e.WebDir, file));
            if (req.Method == "GET" && req.Path == "/strings.json") return Static(Path.Combine(e.AppDir, "strings.json"));

            if (!req.Path.StartsWith("/api/")) return HttpResponse.Error(404, "not_found");
            if (!SameKey(req.Cookie(Cookie), e.Key)) return HttpResponse.Error(401, "no_key");
            if (req.Method != "GET")
            {
                // The page sends this header; a form or a plain cross-site request cannot.
                if (req.Header("X-HOODATA") != "1") return HttpResponse.Error(403, "no_header");
                string origin = req.Header("Origin");
                if (origin != null && origin != "http://127.0.0.1:" + e.Port && origin != "http://localhost:" + e.Port)
                    return HttpResponse.Error(403, "wrong_origin");
            }
            return Route(req);
        }

        HttpResponse Static(string path)
        {
            if (!File.Exists(path)) return HttpResponse.Error(404, "not_found");
            string type;
            if (!Types.TryGetValue(Path.GetExtension(path).ToLowerInvariant(), out type)) type = "application/octet-stream";
            var r = new HttpResponse { ContentType = type, Body = File.ReadAllBytes(path) };
            r.Headers["Cache-Control"] = "no-cache";
            if (type.StartsWith("text/html")) r.Headers["Content-Security-Policy"] = Csp;
            return r;
        }

        static string[] Parts(string path) { return path.Substring(5).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries); }

        HttpResponse Route(HttpRequest req)
        {
            string[] p = Parts(req.Path);
            string m = req.Method;
            if (p.Length == 0) return HttpResponse.Error(404, "not_found");
            switch (p[0])
            {
                case "info": return Info();
                case "live": return Live(req.ArgLong("since", 0));
                case "channels":
                    if (p.Length == 1 && m == "GET") return ChannelList();
                    if (p.Length == 2 && m == "POST") return UpdateChannel(int.Parse(p[1]), req.BodyJson());
                    break;
                case "series": return Series(req);
                case "extent": return Extent((int)req.ArgLong("ch", -1));
                case "events":
                    if (p.Length == 1 && m == "GET") return Events(req, false);
                    if (p.Length == 3 && p[2] == "ack" && m == "POST")
                    {
                        e.Store.SetEventAck(long.Parse(p[1]), Json.Bool(req.BodyJson(), "ack", true));
                        return HttpResponse.Json("{\"ok\":true}");
                    }
                    break;
                case "events.csv": return Events(req, true);
                case "stats": return Stats(req, false);
                case "stats.csv": return Stats(req, true);
                case "sources":
                    if (p.Length == 1 && m == "GET") return SourceList();
                    if (p.Length == 1 && m == "POST") return AddSource(req.BodyJson());
                    if (p.Length == 2 && m == "POST") return UpdateSource(int.Parse(p[1]), req.BodyJson());
                    if (p.Length == 2 && m == "DELETE") { e.DeleteSource(int.Parse(p[1])); return HttpResponse.Json("{\"ok\":true}"); }
                    break;
                case "import":
                    if (p.Length == 1 && m == "POST") return Upload(req);
                    if (p.Length == 3 && p[2] == "start" && m == "POST") return StartImport(p[1], req.BodyJson());
                    if (p.Length == 2 && m == "DELETE") return CancelImport(p[1]);
                    break;
                case "jobs": return Jobs();
                case "serial-ports": return SerialPorts();
                case "network": return NetworkInfo();
                case "patterns":
                    if (p.Length == 1 && m == "GET") return Patterns();
                    if (p.Length == 2 && p[1] == "run" && m == "POST") return RunPatterns(req.BodyJson());
                    break;
                case "settings":
                    if (m == "POST") return Settings(req.BodyJson());
                    break;
            }
            return HttpResponse.Error(404, "not_found");
        }

        // ---- info and live state ----

        HttpResponse Info()
        {
            var w = new JsonWriter().Obj().Prop("version", Engine.Version).Prop("lang", e.Lang)
                .Prop("sensitivity", e.Settings.Sensitivity).Prop("retention_days", e.RetentionDays).Prop("port", e.Port)
                .Prop("data_dir", e.DataDir).Prop("sqlite", SqliteDb.Version).Prop("started", e.StartedAt).EndObj();
            return HttpResponse.Json(w);
        }

        static void WriteChannel(JsonWriter w, Channel ch, long now)
        {
            ChannelInfo c = ch.Info;
            w.Prop("id", c.Id).Prop("source", c.Source).Prop("key", c.Key).Prop("name", c.Name).Prop("unit", c.Unit)
                .Key("lo").NumOrNull(c.Lo).Key("hi").NumOrNull(c.Hi).Prop("hidden", c.Hidden).Prop("live", ch.Live)
                .Prop("status", ch.Status(now)).Prop("count", ch.Det.Count);
            if (ch.Det.LastT != long.MinValue) w.Prop("t", ch.Det.LastT).Prop("v", ch.Det.LastV);
            w.Key("noise").Num(ch.Det.Noise).Key("interval").Num(ch.Det.TypicalInterval);
            w.Key("open").Arr();
            foreach (EventRec ev in ch.Det.OpenEvents()) w.Str(ev.Kind);
            w.EndArr();
        }

        HttpResponse Live(long since)
        {
            long now = Clock.Now();
            var w = new JsonWriter().Obj().Prop("seq", e.Seq).Prop("now", now);
            w.Key("channels").Arr();
            lock (e.Gate)
                foreach (Channel ch in e.Channels())
                {
                    w.Obj();
                    WriteChannel(w, ch, now);
                    w.Key("points");
                    ch.WritePoints(w, since);
                    w.EndObj();
                }
            w.EndArr();
            w.Key("events").Arr();
            foreach (EventChange c in e.ChangesSince(since))
            {
                w.Obj().Prop("seq", c.Seq).Prop("op", c.Op);
                w.Key("event");
                WriteEvent(w, c.E);
                w.EndObj();
            }
            w.EndArr();
            WriteSources(w.Key("sources"));
            WriteJobs(w.Key("jobs"));
            return HttpResponse.Json(w.EndObj());
        }

        HttpResponse ChannelList()
        {
            long now = Clock.Now();
            var w = new JsonWriter().Arr();
            lock (e.Gate) foreach (Channel ch in e.Channels()) { w.Obj(); WriteChannel(w, ch, now); w.EndObj(); }
            return HttpResponse.Json(w.EndArr());
        }

        HttpResponse UpdateChannel(int id, Dictionary<string, object> body)
        {
            Channel ch = e.FindChannel(id);
            if (ch == null) return HttpResponse.Error(404, "no_channel");
            ChannelInfo c = ch.Info;
            var n = new ChannelInfo
            {
                Id = c.Id, Source = c.Source, Key = c.Key, Created = c.Created,
                Name = Json.Str(body, "name", c.Name).Trim(),
                Unit = Json.Str(body, "unit", c.Unit).Trim(),
                Lo = body.ContainsKey("lo") ? OptNum(body["lo"]) : c.Lo,
                Hi = body.ContainsKey("hi") ? OptNum(body["hi"]) : c.Hi,
                Hidden = Json.Bool(body, "hidden", c.Hidden)
            };
            if (n.Name.Length == 0) n.Name = c.Key;
            if (n.Lo.HasValue && n.Hi.HasValue && n.Lo.Value > n.Hi.Value) return HttpResponse.Error(400, "lo_above_hi");
            e.UpdateChannel(n);
            return HttpResponse.Json("{\"ok\":true}");
        }

        static double? OptNum(object o)
        {
            if (o is double) return (double)o;
            double d;
            if (o is string && double.TryParse((string)o, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        // ---- series for a graph ----

        static readonly long[] Buckets = { 1000, 2000, 5000, 10000, 15000, 30000, 60000, 120000, 300000, 600000, 900000,
            1800000, 3600000, 7200000, 10800000, 21600000, 43200000, 86400000, 604800000 };

        HttpResponse Series(HttpRequest req)
        {
            int ch = (int)req.ArgLong("ch", -1);
            long to = req.ArgLong("to", Clock.Now()), from = req.ArgLong("from", to - 3600000);
            int points = (int)Math.Max(50, Math.Min(5000, req.ArgLong("n", 1200)));
            if (to <= from) return HttpResponse.Error(400, "bad_range");
            var w = new JsonWriter().Obj().Prop("ch", ch).Prop("from", from).Prop("to", to);
            using (var rd = e.Store.Read())
            {
                long raw;
                using (var st = rd.Db.Prepare("SELECT count(*) FROM sample WHERE ch = ?1 AND t >= ?2 AND t <= ?3"))
                {
                    st.Bind(1, ch).Bind(2, from).Bind(3, to);
                    st.Step();
                    raw = st.GetLong(0);
                }
                long minutes;
                using (var st = rd.Db.Prepare("SELECT count(*) FROM minute WHERE ch = ?1 AND t >= ?2 AND t <= ?3"))
                {
                    st.Bind(1, ch).Bind(2, from - 59999).Bind(3, to);
                    st.Step();
                    minutes = st.GetLong(0);
                }
                long span = to - from, bucket = 0;
                bool noise = req.Arg("field", "") == "noise";
                if (noise) raw = long.MaxValue;   // noise exists only per minute
                if (raw > points)
                {
                    bucket = Buckets[Buckets.Length - 1];
                    foreach (long b in Buckets) if (span / b <= points) { bucket = b; break; }
                }
                // Raw values are gone after the retention period; then the minutes are all there is.
                bool useMinutes = noise || ((bucket >= 60000 || raw == 0) && minutes > 0);
                if (useMinutes && bucket < 60000) bucket = 60000;
                w.Prop("bucket", bucket);
                var t = new List<long>();
                var v = new List<double>();
                var lo = new List<double>();
                var hi = new List<double>();
                string sql;
                if (bucket == 0) sql = "SELECT t, v, v, v FROM sample WHERE ch = ?1 AND t >= ?2 AND t <= ?3 ORDER BY t";
                else if (!useMinutes)
                    sql = "SELECT (t / ?4) * ?4, avg(v), min(v), max(v) FROM sample WHERE ch = ?1 AND t >= ?2 AND t <= ?3 GROUP BY t / ?4 ORDER BY 1";
                else if (noise)
                    sql = "SELECT (t / ?4) * ?4, avg(noise), min(noise), max(noise) FROM minute WHERE ch = ?1 AND t >= ?2 AND t <= ?3 AND noise IS NOT NULL GROUP BY t / ?4 ORDER BY 1";
                else
                    sql = "SELECT (t / ?4) * ?4, sum(mean * n) / sum(n), min(mn), max(mx) FROM minute WHERE ch = ?1 AND t >= ?2 AND t <= ?3 GROUP BY t / ?4 ORDER BY 1";
                using (var st = rd.Db.Prepare(sql))
                {
                    st.Bind(1, ch).Bind(2, useMinutes ? from - (from % 60000) : from).Bind(3, to);
                    if (bucket > 0) st.Bind(4, bucket);
                    while (st.Step())
                    {
                        t.Add(st.GetLong(0)); v.Add(st.GetDouble(1)); lo.Add(st.GetDouble(2)); hi.Add(st.GetDouble(3));
                    }
                }
                w.Key("t").Arr(); foreach (long x in t) w.Int(x); w.EndArr();
                w.Key("v").Arr(); foreach (double x in v) w.Num(x); w.EndArr();
                if (bucket > 0)
                {
                    w.Key("lo").Arr(); foreach (double x in lo) w.Num(x); w.EndArr();
                    w.Key("hi").Arr(); foreach (double x in hi) w.Num(x); w.EndArr();
                }
                w.Key("events").Arr();
                using (var st = rd.Db.Prepare("SELECT " + EventColumns + " FROM event WHERE ch = ?1 AND t0 <= ?3 AND (t1 IS NULL OR t1 >= ?2) ORDER BY t0 LIMIT 2000"))
                {
                    st.Bind(1, ch).Bind(2, from).Bind(3, to);
                    while (st.Step()) WriteEvent(w, ReadEvent(st));
                }
                w.EndArr();
            }
            return HttpResponse.Json(w.EndObj());
        }

        // The time span a channel has data for, from its one-minute summaries.
        HttpResponse Extent(int ch)
        {
            using (var rd = e.Store.Read())
            using (var st = rd.Db.Prepare("SELECT min(t), max(t) FROM minute WHERE ch = ?1"))
            {
                st.Bind(1, ch);
                st.Step();
                var w = new JsonWriter().Obj().Prop("ch", ch).Key("first").IntOrNull(st.GetLongOrNull(0))
                    .Key("last").IntOrNull(st.IsNull(1) ? (long?)null : st.GetLong(1) + 59999);
                return HttpResponse.Json(w.EndObj());
            }
        }

        // ---- anomalies ----

        const string EventColumns = "id, ch, kind, t0, t1, value, expected, score, detail, ack";

        static EventRec ReadEvent(SqliteStmt st)
        {
            return new EventRec
            {
                Id = st.GetLong(0), Ch = (int)st.GetLong(1), Kind = st.GetText(2), T0 = st.GetLong(3), T1 = st.GetLongOrNull(4),
                Value = st.GetDoubleOrNull(5), Expected = st.GetDoubleOrNull(6), Score = st.GetDoubleOrNull(7),
                Detail = st.GetText(8), Ack = st.GetLong(9) != 0
            };
        }

        static void WriteEvent(JsonWriter w, EventRec ev)
        {
            w.Obj().Prop("id", ev.Id).Prop("ch", ev.Ch).Prop("kind", ev.Kind).Prop("t0", ev.T0).Key("t1").IntOrNull(ev.T1)
                .Key("value").NumOrNull(ev.Value).Key("expected").NumOrNull(ev.Expected).Key("score").NumOrNull(ev.Score)
                .Key("detail").Raw(ev.Detail).Prop("ack", ev.Ack).EndObj();
        }

        HttpResponse Events(HttpRequest req, bool csv)
        {
            long to = req.ArgLong("to", long.MaxValue), from = req.ArgLong("from", 0);
            int ch = (int)req.ArgLong("ch", -1);
            string kind = req.Arg("kind", "");
            bool hideAck = req.Arg("hide_ack", "") == "1";
            int limit = (int)Math.Max(1, Math.Min(csv ? 1000000 : 5000, req.ArgLong("limit", csv ? 1000000 : 500)));
            int offset = (int)Math.Max(0, req.ArgLong("offset", 0));
            string sql = "SELECT " + EventColumns + " FROM event WHERE t0 >= ?1 AND t0 <= ?2" +
                (ch >= 0 ? " AND ch = ?3" : "") + (kind.Length > 0 ? " AND kind = ?4" : "") + (hideAck ? " AND ack = 0" : "") +
                " ORDER BY t0 DESC LIMIT ?5 OFFSET ?6";
            var list = new List<EventRec>();
            long total;
            using (var rd = e.Store.Read())
            {
                using (var st = rd.Db.Prepare(sql))
                {
                    st.Bind(1, from).Bind(2, to).Bind(5, limit).Bind(6, offset);
                    if (ch >= 0) st.Bind(3, ch);
                    if (kind.Length > 0) st.Bind(4, kind);
                    while (st.Step()) list.Add(ReadEvent(st));
                }
                string count = "SELECT count(*) FROM event WHERE t0 >= ?1 AND t0 <= ?2" + (ch >= 0 ? " AND ch = ?3" : "") +
                    (kind.Length > 0 ? " AND kind = ?4" : "") + (hideAck ? " AND ack = 0" : "");
                using (var st = rd.Db.Prepare(count))
                {
                    st.Bind(1, from).Bind(2, to);
                    if (ch >= 0) st.Bind(3, ch);
                    if (kind.Length > 0) st.Bind(4, kind);
                    st.Step();
                    total = st.GetLong(0);
                }
                if (!csv)
                {
                    // Counts by kind for the same filter, all kinds.
                    string byKind = "SELECT kind, count(*) FROM event WHERE t0 >= ?1 AND t0 <= ?2" + (ch >= 0 ? " AND ch = ?3" : "") +
                        (hideAck ? " AND ack = 0" : "") + " GROUP BY kind";
                    var w = new JsonWriter().Obj().Prop("total", total).Key("by_kind").Obj();
                    using (var st = rd.Db.Prepare(byKind))
                    {
                        st.Bind(1, from).Bind(2, to);
                        if (ch >= 0) st.Bind(3, ch);
                        while (st.Step()) w.Prop(st.GetText(0), st.GetLong(1));
                    }
                    w.EndObj().Key("events").Arr();
                    foreach (EventRec ev in list) WriteEvent(w, ev);
                    return HttpResponse.Json(w.EndArr().EndObj());
                }
            }
            var names = ChannelNames();
            var t = Texts(req.Arg("lang", e.Lang));
            var sb = new StringBuilder();
            CsvRow(sb, T(t, "CsvStart"), T(t, "CsvEnd"), T(t, "CsvChannel"), T(t, "CsvKind"), T(t, "CsvValue"),
                T(t, "CsvExpected"), T(t, "CsvScore"), T(t, "CsvAck"));
            foreach (EventRec ev in list)
            {
                string name;
                names.TryGetValue(ev.Ch, out name);
                CsvRow(sb, LocalTime(ev.T0), ev.T1.HasValue ? LocalTime(ev.T1.Value) : "", name ?? ev.Ch.ToString(),
                    T(t, "Kind_" + ev.Kind), Num(ev.Value), Num(ev.Expected), Num(ev.Score), ev.Ack ? "1" : "0");
            }
            return CsvResponse(sb, "hoodata-events.csv");
        }

        // ---- statistics for a period ----

        HttpResponse Stats(HttpRequest req, bool csv)
        {
            long to = req.ArgLong("to", Clock.Now()), from = req.ArgLong("from", to - 86400000);
            var rows = new Dictionary<int, double[]>();   // n, mean, sd, min, max, noise, first, last
            var kinds = new Dictionary<int, Dictionary<string, long>>();
            var gaps = new Dictionary<int, long>();
            using (var rd = e.Store.Read())
            {
                using (var st = rd.Db.Prepare(
                    "SELECT ch, sum(n), sum(n * mean), sum(n * mean * mean), sum((n - 1) * sd * sd), min(mn), max(mx), avg(noise), min(t), max(t) " +
                    "FROM minute WHERE t >= ?1 AND t <= ?2 GROUP BY ch"))
                {
                    st.Bind(1, from - (from % 60000)).Bind(2, to);
                    while (st.Step())
                    {
                        double n = st.GetDouble(1), s1 = st.GetDouble(2), s2 = st.GetDouble(3), within = st.GetDouble(4);
                        double mean = s1 / n;
                        double between = s2 - n * mean * mean;
                        double sd = n > 1 ? Math.Sqrt(Math.Max(0, (within + between) / (n - 1))) : 0;
                        rows[(int)st.GetLong(0)] = new[] { n, mean, sd, st.GetDouble(5), st.GetDouble(6),
                            st.IsNull(7) ? double.NaN : st.GetDouble(7), st.GetDouble(8), st.GetDouble(9) + 59999 };
                    }
                }
                using (var st = rd.Db.Prepare("SELECT ch, kind, count(*) FROM event WHERE t0 >= ?1 AND t0 <= ?2 GROUP BY ch, kind"))
                {
                    st.Bind(1, from).Bind(2, to);
                    while (st.Step())
                    {
                        int c = (int)st.GetLong(0);
                        Dictionary<string, long> k;
                        if (!kinds.TryGetValue(c, out k)) kinds[c] = k = new Dictionary<string, long>();
                        k[st.GetText(1)] = st.GetLong(2);
                    }
                }
                // Time without data: the gaps, cut to the period.
                using (var st = rd.Db.Prepare("SELECT ch, sum(min(coalesce(t1, ?3), ?2) - max(t0, ?1)) FROM event WHERE kind = 'gap' AND t0 <= ?2 AND coalesce(t1, ?3) >= ?1 GROUP BY ch"))
                {
                    st.Bind(1, from).Bind(2, to).Bind(3, Clock.Now());
                    while (st.Step()) gaps[(int)st.GetLong(0)] = Math.Max(0, st.GetLong(1));
                }
            }
            var channels = e.Channels();
            if (!csv)
            {
                var w = new JsonWriter().Obj().Prop("from", from).Prop("to", to).Key("channels").Arr();
                foreach (Channel ch in channels)
                {
                    double[] r;
                    if (!rows.TryGetValue(ch.Info.Id, out r)) continue;
                    w.Obj().Prop("ch", ch.Info.Id).Prop("n", (long)r[0]).Prop("mean", r[1]).Prop("sd", r[2]).Prop("min", r[3])
                        .Prop("max", r[4]).Key("noise").Num(r[5]).Prop("first", (long)r[6]).Prop("last", (long)r[7]);
                    long g;
                    w.Prop("gap_ms", gaps.TryGetValue(ch.Info.Id, out g) ? g : 0);
                    w.Key("events").Obj();
                    Dictionary<string, long> k;
                    if (kinds.TryGetValue(ch.Info.Id, out k)) foreach (var kv in k) w.Prop(kv.Key, kv.Value);
                    w.EndObj().EndObj();
                }
                return HttpResponse.Json(w.EndArr().EndObj());
            }
            var t = Texts(req.Arg("lang", e.Lang));
            var sb = new StringBuilder();
            var head = new List<string> { T(t, "CsvChannel"), T(t, "CsvUnit"), T(t, "StatCount"), T(t, "StatMean"), T(t, "StatSd"),
                T(t, "StatMin"), T(t, "StatMax"), T(t, "StatNoise"), T(t, "StatFirst"), T(t, "StatLast"), T(t, "StatGap") };
            foreach (string k in Kinds.All) head.Add(T(t, "Kind_" + k));
            CsvRow(sb, head.ToArray());
            foreach (Channel ch in channels)
            {
                double[] r;
                if (!rows.TryGetValue(ch.Info.Id, out r)) continue;
                long g;
                gaps.TryGetValue(ch.Info.Id, out g);
                var cells = new List<string> { ch.Info.Name, ch.Info.Unit, ((long)r[0]).ToString(CultureInfo.InvariantCulture),
                    Num(r[1]), Num(r[2]), Num(r[3]), Num(r[4]), Num(r[5]), LocalTime((long)r[6]), LocalTime((long)r[7]),
                    (g / 1000.0).ToString("0", CultureInfo.InvariantCulture) };
                Dictionary<string, long> k;
                kinds.TryGetValue(ch.Info.Id, out k);
                foreach (string kind in Kinds.All)
                {
                    long c = 0;
                    if (k != null) k.TryGetValue(kind, out c);
                    cells.Add(c.ToString(CultureInfo.InvariantCulture));
                }
                CsvRow(sb, cells.ToArray());
            }
            return CsvResponse(sb, "hoodata-statistics.csv");
        }

        // ---- sources ----

        void WriteSources(JsonWriter w)
        {
            w.Arr();
            foreach (SourceRuntime s in e.Sources())
            {
                // The encrypted password never goes back to the page; only whether there is one.
                object cfg;
                Json.TryParse(s.Info.Config ?? "{}", out cfg);
                var c = Json.Obj(cfg) ?? new Dictionary<string, object>();
                c["has_password"] = c.Remove("password_enc");
                w.Obj().Prop("id", s.Info.Id).Prop("kind", s.Info.Kind).Prop("name", s.Info.Name).Key("config").Raw(Serialize(c))
                    .Prop("enabled", s.Info.Enabled).Prop("created", s.Info.Created)
                    .Prop("state", s.Impl != null ? s.Impl.State : "stopped").Key("error").Str(s.Impl != null ? s.Impl.Error : null).EndObj();
            }
            w.EndArr();
        }

        HttpResponse SourceList()
        {
            var w = new JsonWriter();
            WriteSources(w);
            return HttpResponse.Json(w);
        }

        static readonly HashSet<string> AddableKinds = new HashSet<string> { "pc", "http", "mqtt", "modbus", "serial", "folder" };

        // Config as the page sends it, made ready to keep: a password is encrypted, an HTTP source gets a key.
        static Dictionary<string, object> PrepareConfig(string kind, Dictionary<string, object> cfg, Dictionary<string, object> old)
        {
            cfg = cfg ?? new Dictionary<string, object>();
            if (kind == "mqtt")
            {
                string pw = Json.Str(cfg, "password", "");
                cfg.Remove("password");
                if (pw.Length > 0) cfg["password_enc"] = Secrets.Protect(pw);
                else if (Json.Bool(cfg, "clear_password", false)) cfg.Remove("password_enc");
                else if (old != null && old.ContainsKey("password_enc")) cfg["password_enc"] = old["password_enc"];
                cfg.Remove("clear_password");
            }
            if (kind == "http")
            {
                string key = Json.Str(cfg, "key", "");
                if (key.Length < 16) key = old != null ? Json.Str(old, "key", "") : "";
                if (key.Length < 16 || Json.Bool(cfg, "new_key", false)) key = Secrets.NewKey(16);
                cfg["key"] = key;
                cfg.Remove("new_key");
                double port = Json.Num(cfg, "port", 47110);
                if (port < 1024 || port > 65535) throw new HttpError(400, "bad_port");
            }
            if (kind == "folder" && !Directory.Exists(Json.Str(cfg, "path", ""))) throw new HttpError(400, "no_folder");
            return cfg;
        }

        HttpResponse AddSource(Dictionary<string, object> body)
        {
            string kind = Json.Str(body, "kind", "");
            if (!AddableKinds.Contains(kind)) return HttpResponse.Error(400, "bad_kind");
            var cfg = PrepareConfig(kind, Json.Obj(Json.Get(body, "config")), null);
            string name = Json.Str(body, "name", kind).Trim();
            SourceInfo s = e.AddSource(kind, name.Length > 0 ? name : kind, Serialize(cfg), Json.Bool(body, "enabled", true));
            return HttpResponse.Json(new JsonWriter().Obj().Prop("id", s.Id).EndObj());
        }

        HttpResponse UpdateSource(int id, Dictionary<string, object> body)
        {
            SourceRuntime src = e.Sources().Find(x => x.Info.Id == id);
            if (src == null) return HttpResponse.Error(404, "no_source");
            var cfgIn = Json.Obj(Json.Get(body, "config"));
            string config = null;
            if (cfgIn != null)
            {
                object old;
                Json.TryParse(src.Info.Config ?? "{}", out old);
                // Settings not sent stay as they were (an interval change keeps the address).
                var merged = new Dictionary<string, object>(Json.Obj(old) ?? new Dictionary<string, object>());
                foreach (var kv in cfgIn) merged[kv.Key] = kv.Value;
                config = Serialize(PrepareConfig(src.Info.Kind, merged, Json.Obj(old)));
            }
            string name = Json.Get(body, "name") is string ? ((string)body["name"]).Trim() : null;
            if (name != null && name.Length == 0) name = null;
            e.UpdateSource(id, name, config, body.ContainsKey("enabled") ? (bool?)Json.Bool(body, "enabled", true) : null);
            return HttpResponse.Json("{\"ok\":true}");
        }

        HttpResponse SerialPorts()
        {
            var w = new JsonWriter().Arr();
            foreach (string p in SerialSource.Ports()) w.Str(p);
            return HttpResponse.Json(w.EndArr());
        }

        HttpResponse NetworkInfo()
        {
            var w = new JsonWriter().Obj().Key("addresses").Arr();
            foreach (string a in Network.Addresses()) w.Str(a);
            w.EndArr().Prop("host", Environment.MachineName);
            return HttpResponse.Json(w.EndObj());
        }

        // ---- imports ----

        HttpResponse Upload(HttpRequest req)
        {
            string name = Path.GetFileName(req.Arg("name", "data.csv"));
            if (name.Length == 0) name = "data.csv";
            string path = Path.Combine(e.UploadDir, Guid.NewGuid().ToString("N") + ".upload");
            try
            {
                using (var fs = File.Create(path)) req.CopyBodyTo(fs, null);
                if (name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    // Excel: the first worksheet becomes a CSV, imported like one.
                    string csv = path + ".csv";
                    try { Xlsx.ToCsv(path, csv); }
                    finally { TryDelete(path); }
                    path = csv;
                }
                ImportJob job = ImportJob.Prepare(path, name);
                e.AddJob(job);
                var w = new JsonWriter().Obj().Prop("id", job.Id).Prop("name", job.Name).Key("columns").Arr();
                foreach (int c in job.Columns) w.Int(c);
                w.EndArr().Key("layout");
                job.Layout.Write(w);
                return HttpResponse.Json(w.EndObj());
            }
            catch (HttpError) { TryDelete(path); throw; }
            catch (Exception ex) { TryDelete(path); throw new HttpError(400, "unreadable: " + ex.Message); }
        }

        static void TryDelete(string path) { try { File.Delete(path); } catch { } }

        ImportJob FindJob(string id)
        {
            foreach (ImportJob j in e.JobList()) if (j.Id == id) return j;
            throw new HttpError(404, "no_job");
        }

        HttpResponse StartImport(string id, Dictionary<string, object> body)
        {
            ImportJob job = FindJob(id);
            if (job.State != "ready") return HttpResponse.Error(409, "already_started");
            string name = Json.Str(body, "name", job.Name).Trim();
            if (name.Length > 0) job.Name = name;
            var cols = Json.Get(body, "columns") as List<object>;
            if (cols != null)
            {
                job.Columns = new List<int>();
                foreach (object o in cols)
                    if (o is double && (int)(double)o >= 0 && (int)(double)o < job.Layout.Columns.Count) job.Columns.Add((int)(double)o);
            }
            if (body.ContainsKey("time_column"))
            {
                int tc = (int)Json.Num(body, "time_column", -1);
                if (tc != job.Layout.TimeColumn)
                {
                    job.Layout.ClockColumn = -1;
                    job.Layout.TimeColumn = tc;
                    job.Layout.TimeFormat = tc >= 0 && tc < job.Layout.Columns.Count ? job.Layout.Columns[tc].Format : null;
                    if (tc >= 0 && job.Layout.TimeFormat == null) return HttpResponse.Error(400, "not_a_time_column");
                }
            }
            string zone = Json.Str(body, "zone", "local");
            job.Zone = zone == "utc" ? TimeZoneInfo.Utc : TimeZoneInfo.Local;
            job.IntervalSeconds = Math.Max(0.001, Json.Num(body, "interval", job.IntervalSeconds));
            job.StartMs = (long)Json.Num(body, "start", job.StartMs);
            job.Columns.Remove(job.Layout.TimeColumn);
            job.Columns.Remove(job.Layout.ClockColumn);
            if (job.Columns.Count == 0) return HttpResponse.Error(400, "no_columns");
            job.Start(e);
            return HttpResponse.Json("{\"ok\":true}");
        }

        HttpResponse CancelImport(string id)
        {
            ImportJob job = FindJob(id);
            job.Cancel();
            if (job.State == "ready")
            {
                job.State = "cancelled";
                job.Finished = true;
                job.FinishedAt = Clock.Now();
                TryDelete(job.FilePath);
            }
            return HttpResponse.Json("{\"ok\":true}");
        }

        void WriteJobs(JsonWriter w)
        {
            w.Arr();
            foreach (ImportJob j in e.JobList()) j.Write(w);
            w.EndArr();
        }

        HttpResponse Jobs()
        {
            var w = new JsonWriter();
            WriteJobs(w);
            return HttpResponse.Json(w);
        }

        // ---- patterns ----

        HttpResponse Patterns()
        {
            string json = e.Store.GetMeta("patterns");
            var w = new JsonWriter().Obj().Prop("running", e.PatternsRunning).Key("error").Str(e.PatternsError).Key("result").Raw(json);
            return HttpResponse.Json(w.EndObj());
        }

        HttpResponse RunPatterns(Dictionary<string, object> body)
        {
            long to = (long)Json.Num(body, "to", Clock.Now());
            long from = (long)Json.Num(body, "from", to - 30L * 86400000);
            // Cut the period to where there is data, so "all" does not start in 1970.
            using (var rd = e.Store.Read())
            using (var st = rd.Db.Prepare("SELECT min(t), max(t) FROM minute"))
            {
                st.Step();
                if (!st.IsNull(0))
                {
                    from = Math.Max(from, st.GetLong(0));
                    to = Math.Min(to, st.GetLong(1) + 60000);
                }
            }
            if (to <= from) return HttpResponse.Error(400, "bad_range");
            bool started = e.StartPatterns(from, to);
            return HttpResponse.Json(new JsonWriter().Obj().Prop("started", started).EndObj());
        }

        // ---- settings ----

        HttpResponse Settings(Dictionary<string, object> body)
        {
            string s = Json.Str(body, "sensitivity", null);
            if (s != null)
            {
                if (s != "low" && s != "normal" && s != "high") return HttpResponse.Error(400, "bad_sensitivity");
                e.SetSensitivity(s);
            }
            if (body.ContainsKey("retention_days"))
            {
                int d = (int)Json.Num(body, "retention_days", e.RetentionDays);
                if (d < 1 || d > 3650) return HttpResponse.Error(400, "bad_retention");
                e.RetentionDays = d;
                e.Store.SetMeta("retention_days", d.ToString(CultureInfo.InvariantCulture));
            }
            string lang = Json.Str(body, "lang", null);
            if (lang == "ko" || lang == "en")
            {
                e.Lang = lang;
                e.Store.SetMeta("lang", lang);
            }
            return Info();
        }

        // ---- helpers ----

        static string Serialize(object o)
        {
            var w = new JsonWriter();
            WriteValue(w, o);
            return w.ToString();
        }

        static void WriteValue(JsonWriter w, object o)
        {
            if (o == null) w.Null();
            else if (o is string) w.Str((string)o);
            else if (o is double) w.Num((double)o);
            else if (o is bool) w.Bool((bool)o);
            else if (o is Dictionary<string, object>)
            {
                w.Obj();
                foreach (var kv in (Dictionary<string, object>)o) { w.Key(kv.Key); WriteValue(w, kv.Value); }
                w.EndObj();
            }
            else if (o is List<object>)
            {
                w.Arr();
                foreach (object x in (List<object>)o) WriteValue(w, x);
                w.EndArr();
            }
            else w.Null();
        }

        Dictionary<int, string> ChannelNames()
        {
            var d = new Dictionary<int, string>();
            foreach (Channel ch in e.Channels()) d[ch.Info.Id] = ch.Info.Name;
            return d;
        }

        Dictionary<string, object> Texts(string lang) { return e.Texts(lang); }

        static string T(Dictionary<string, object> t, string key) { return Json.Str(t, key, key); }

        static string LocalTime(long ms) { return Clock.ToLocal(ms).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }

        static string Num(double? v)
        {
            if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return "";
            return Math.Round(v.Value, 6).ToString("R", CultureInfo.InvariantCulture);
        }

        static void CsvRow(StringBuilder sb, params string[] cells)
        {
            for (int i = 0; i < cells.Length; i++)
            {
                if (i > 0) sb.Append(',');
                string c = cells[i] ?? "";
                // A leading = + - @ would run as a formula in Excel.
                if (c.Length > 0 && "=+-@".IndexOf(c[0]) >= 0 && !IsNumber(c)) c = "'" + c;
                if (c.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) c = "\"" + c.Replace("\"", "\"\"") + "\"";
                sb.Append(c);
            }
            sb.Append("\r\n");
        }

        static bool IsNumber(string s)
        {
            double d;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        static HttpResponse CsvResponse(StringBuilder sb, string fileName)
        {
            // With a byte order mark, so Excel reads it as UTF-8.
            byte[] text = Encoding.UTF8.GetBytes(sb.ToString());
            var body = new byte[text.Length + 3];
            body[0] = 0xEF; body[1] = 0xBB; body[2] = 0xBF;
            Buffer.BlockCopy(text, 0, body, 3, text.Length);
            var r = new HttpResponse { ContentType = "text/csv; charset=utf-8", Body = body };
            r.Headers["Content-Disposition"] = "attachment; filename=\"" + fileName + "\"";
            return r;
        }
    }
}
