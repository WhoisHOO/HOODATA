// Pattern analysis: in which situations does an anomaly happen more often than usual, and which anomalies tend to
// follow which. Time is cut into slots (5 minutes or more); each slot gets situations (time of day, weekday, other
// channels high or low, a switch on or off) and, per channel and kind, whether an anomaly started in it. Rates are
// compared with Fisher's exact test, corrected for the number of tests (Benjamini-Hochberg). See docs/METHODS.md.
using System;
using System.Collections.Generic;

namespace Hoodata
{
    public sealed class Condition
    {
        public string Type;      // hour, weekday, daytype, high, low, value
        public int Ch = -1;
        public int Hour;         // hour: start of a 4-hour bin
        public int Day;          // weekday: 0 = Sunday
        public bool Weekend;     // daytype
        public double Value;     // high/low: the threshold; value: the value
        public string Group { get { return Type == "hour" ? "hour" : Type == "weekday" || Type == "daytype" ? "day" : "ch" + Ch; } }

        public void Write(JsonWriter w)
        {
            w.Obj().Prop("type", Type);
            if (Ch >= 0) w.Prop("ch", Ch);
            if (Type == "hour") w.Prop("from", Hour).Prop("to", Hour + 4);
            if (Type == "weekday") w.Prop("day", Day);
            if (Type == "daytype") w.Prop("weekend", Weekend);
            if (Type == "high" || Type == "low" || Type == "value") w.Prop("value", Value);
            w.EndObj();
        }
    }

    public sealed class Rule
    {
        public int Ch;
        public string Kind;
        public List<Condition> When = new List<Condition>();
        public int Slots, Hits, Events, Observed;   // slots in the situation, of them with the anomaly; all with it; all
        public double Lift, P, Q;
    }

    public sealed class Sequence
    {
        public int ChA, ChB;
        public string KindA, KindB;
        public long Window;
        public int Hits, CountB;
        public double Expected, Lift, P, Q;
    }

    public sealed class PatternResult
    {
        public long From, To, Created, SlotMs;
        public int Slots, Targets, Tests;
        public readonly List<Rule> Rules = new List<Rule>();
        public readonly List<Sequence> Sequences = new List<Sequence>();
        public readonly long[,] Heat = new long[7, 24];    // anomaly starts by weekday and hour
        public string Note;

        public string ToJson()
        {
            var w = new JsonWriter().Obj().Prop("from", From).Prop("to", To).Prop("created", Created).Prop("slot_ms", SlotMs)
                .Prop("slots", Slots).Prop("targets", Targets).Prop("tests", Tests).Key("note").Str(Note);
            w.Key("rules").Arr();
            foreach (Rule r in Rules)
            {
                w.Obj().Prop("ch", r.Ch).Prop("kind", r.Kind).Prop("slots", r.Slots).Prop("hits", r.Hits).Prop("events", r.Events)
                    .Prop("observed", r.Observed).Prop("lift", r.Lift).Prop("p", r.P).Prop("q", r.Q).Key("when").Arr();
                foreach (Condition c in r.When) c.Write(w);
                w.EndArr().EndObj();
            }
            w.EndArr().Key("sequences").Arr();
            foreach (Sequence s in Sequences)
                w.Obj().Prop("ch_a", s.ChA).Prop("kind_a", s.KindA).Prop("ch_b", s.ChB).Prop("kind_b", s.KindB)
                    .Prop("window", s.Window).Prop("hits", s.Hits).Prop("count_b", s.CountB).Prop("expected", s.Expected)
                    .Prop("lift", s.Lift).Prop("p", s.P).Prop("q", s.Q).EndObj();
            w.EndArr().Key("heat").Arr();
            for (int d = 0; d < 7; d++) { w.Arr(); for (int h = 0; h < 24; h++) w.Int(Heat[d, h]); w.EndArr(); }
            return w.EndArr().EndObj().ToString();
        }
    }

    // A set of slots as bits.
    public sealed class Bits
    {
        public readonly ulong[] W;
        public Bits(int n) { W = new ulong[(n + 63) / 64]; }
        public void Set(int i) { W[i >> 6] |= 1UL << (i & 63); }
        public bool Get(int i) { return (W[i >> 6] & (1UL << (i & 63))) != 0; }

        static int Pop(ulong x)
        {
            x -= (x >> 1) & 0x5555555555555555UL;
            x = (x & 0x3333333333333333UL) + ((x >> 2) & 0x3333333333333333UL);
            x = (x + (x >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((x * 0x0101010101010101UL) >> 56);
        }

        public int Count() { int c = 0; foreach (ulong x in W) c += Pop(x); return c; }

        public static int CountAnd(Bits a, Bits b) { int c = 0; for (int i = 0; i < a.W.Length; i++) c += Pop(a.W[i] & b.W[i]); return c; }
        public static int CountAnd(Bits a, Bits b, Bits c) { int n = 0; for (int i = 0; i < a.W.Length; i++) n += Pop(a.W[i] & b.W[i] & c.W[i]); return n; }
        public static int CountAnd(Bits a, Bits b, Bits c, Bits d) { int n = 0; for (int i = 0; i < a.W.Length; i++) n += Pop(a.W[i] & b.W[i] & c.W[i] & d.W[i]); return n; }
    }

    public static class StatTests
    {
        // Lanczos approximation of ln Gamma.
        static readonly double[] G = { 676.5203681218851, -1259.1392167224028, 771.32342877765313, -176.61502916214059,
            12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7 };

        public static double LogGamma(double x)
        {
            if (x < 0.5) return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1 - x);
            x -= 1;
            double a = 0.99999999999980993, t = x + 7.5;
            for (int i = 0; i < 8; i++) a += G[i] / (x + i + 1);
            return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
        }

        static double LogChoose(double n, double k) { return LogGamma(n + 1) - LogGamma(k + 1) - LogGamma(n - k + 1); }

        // One-sided Fisher exact test: P(X >= k) when `draws` slots are picked from `total`, of which `marked` are marked.
        public static double HyperUpper(int k, int total, int marked, int draws)
        {
            int hi = Math.Min(marked, draws);
            if (k <= Math.Max(0, draws - (total - marked))) return 1;
            double logDen = LogChoose(total, draws), sum = 0;
            for (int x = k; x <= hi; x++)
            {
                double term = Math.Exp(LogChoose(marked, x) + LogChoose(total - marked, draws - x) - logDen);
                sum += term;
                if (term < sum * 1e-15) break;
            }
            return Math.Min(1, sum);
        }

        // P(X >= k) for X ~ Binomial(n, p).
        public static double BinomUpper(int k, int n, double p)
        {
            if (k <= 0) return 1;
            if (p <= 0) return 0;
            if (p >= 1) return 1;
            double sum = 0, lp = Math.Log(p), lq = Math.Log(1 - p);
            for (int x = k; x <= n; x++)
            {
                double term = Math.Exp(LogChoose(n, x) + x * lp + (n - x) * lq);
                sum += term;
                if (x > n * p && term < sum * 1e-15) break;
            }
            return Math.Min(1, sum);
        }

        // Benjamini-Hochberg adjusted p-values (q-values), in the order given.
        public static double[] Adjust(IList<double> p)
        {
            int m = p.Count;
            var idx = new int[m];
            var ps = new double[m];
            for (int i = 0; i < m; i++) { idx[i] = i; ps[i] = p[i]; }
            Array.Sort(ps, idx);
            var q = new double[m];
            double min = 1;
            for (int i = m - 1; i >= 0; i--)
            {
                min = Math.Min(min, ps[i] * m / (i + 1));
                q[idx[i]] = min;
            }
            return q;
        }
    }

    public static class PatternAnalyzer
    {
        public const int MinEvents = 5, MinHits = 3;
        public const double MinLift = 2, MaxQ = 0.05;
        static readonly long[] Windows = { 5 * 60000L, 30 * 60000L, 2 * 3600000L };

        sealed class Target
        {
            public int Ch;
            public string Kind;
            public Bits Hit;
            public List<long> Starts = new List<long>();
        }

        sealed class Feature
        {
            public Condition C;
            public Bits B;
        }

        // A channel's own level says nothing new about most of its anomalies: a level change or an unusual level
        // defines it, and a spike moves the slot's mean itself. Stuck values, gaps and noise do not.
        static bool OwnLevelAllowed(string kind) { return kind == Kinds.Noisy || kind == Kinds.Stuck || kind == Kinds.Gap; }

        // A switch (a pump on or off, a mode) has a few values, never anything in between, and keeps one of them
        // through most minutes. A measurement read several times a minute has minutes with a single value only by
        // chance or at one resting value (0 while a machine is off), and its other minutes reach many values.
        static bool IsSwitch(HashSet<double> values, HashSet<double> extremes, int[] minutes)
        {
            return values.Count >= 1 && values.Count <= 4 && extremes.Count <= 4 && minutes[1] >= 0.5 * minutes[0];
        }

        public static PatternResult Run(Store store, ICollection<int> channelIds, long from, long to)
        {
            var res = new PatternResult { From = from, To = to, Created = Clock.Now() };
            var ids = new HashSet<int>(channelIds);

            // Only the time with data counts: an imported file from January and this week's live values must not
            // turn the months between them into slots. Slots are 5 minutes, or longer when there would be more
            // than 20000 of them.
            long blocks5;
            using (var rd = store.Read())
            using (var st = rd.Db.Prepare("SELECT count(DISTINCT t / 300000) FROM minute WHERE t >= ?1 AND t < ?2"))
            {
                st.Bind(1, from).Bind(2, to);
                st.Step();
                blocks5 = st.GetLong(0);
            }
            long slot = 300000L * Math.Max(1, (blocks5 + 19999) / 20000);
            var index = new Dictionary<long, int>();
            var slotStart = new List<long>();
            using (var rd = store.Read())
            using (var st = rd.Db.Prepare("SELECT DISTINCT (t - ?1) / ?3 FROM minute WHERE t >= ?1 AND t < ?2 ORDER BY 1"))
            {
                st.Bind(1, from).Bind(2, to).Bind(3, slot);
                while (st.Step()) { long key = st.GetLong(0); index[key] = slotStart.Count; slotStart.Add(from + key * slot); }
            }
            int n = slotStart.Count;
            res.SlotMs = slot;
            res.Slots = n;
            if (n == 0) { res.Note = "no_data"; return res; }

            // Slot means and which slots each channel has data in, from the one-minute summaries.
            var sum = new Dictionary<int, double[]>();
            var cnt = new Dictionary<int, int[]>();
            var constant = new Dictionary<int, bool[]>();   // all minutes in the slot had one value
            var distinct = new Dictionary<int, HashSet<double>>();
            var minutes = new Dictionary<int, int[]>();   // minutes with data, and those with a single value
            var extremes = new Dictionary<int, HashSet<double>>();   // every minute's lowest and highest value
            // Switches (channels with a few values, such as a pump on or off) also give events: the moments they
            // change to each value, which can come before an anomaly.
            var switches = new Dictionary<int, List<KeyValuePair<double, long>>>();
            using (var rd = store.Read())
            using (var st = rd.Db.Prepare("SELECT ch, t, n, mean, mn, mx FROM minute WHERE t >= ?1 AND t < ?2 ORDER BY ch, t"))
            {
                st.Bind(1, from).Bind(2, to);
                int lastCh = -1;
                double lastMean = double.NaN;
                while (st.Step())
                {
                    int ch = (int)st.GetLong(0);
                    if (!ids.Contains(ch)) continue;
                    long tm = st.GetLong(1);
                    int i;
                    if (!index.TryGetValue((tm - from) / slot, out i)) continue;
                    {
                        double mean0 = st.GetDouble(3);
                        if (ch != lastCh) { lastCh = ch; lastMean = double.NaN; }
                        else if (Math.Abs(mean0 - lastMean) > 1e-9 && st.GetDouble(4) == st.GetDouble(5))
                        {
                            List<KeyValuePair<double, long>> sw;
                            if (!switches.TryGetValue(ch, out sw)) switches[ch] = sw = new List<KeyValuePair<double, long>>();
                            if (sw.Count < 20000) sw.Add(new KeyValuePair<double, long>(Math.Round(mean0, 6), tm));
                        }
                        lastMean = mean0;
                    }
                    double[] s;
                    if (!sum.TryGetValue(ch, out s))
                    {
                        sum[ch] = s = new double[n];
                        cnt[ch] = new int[n];
                        var c = new bool[n];
                        for (int k = 0; k < n; k++) c[k] = true;
                        constant[ch] = c;
                        distinct[ch] = new HashSet<double>();
                        minutes[ch] = new int[2];
                        extremes[ch] = new HashSet<double>();
                    }
                    double mean = st.GetDouble(3), mn = st.GetDouble(4), mx = st.GetDouble(5);
                    int m = (int)st.GetLong(2);
                    if (cnt[ch][i] > 0 && Math.Abs(sum[ch][i] / cnt[ch][i] - mean) > 1e-9) constant[ch][i] = false;
                    if (mn != mx) constant[ch][i] = false;
                    s[i] += mean * m;
                    cnt[ch][i] += m;
                    // Only minutes with one value count: a minute in which a switch changed has a mean in between
                    // (0.42), which would make a switch read faster than once a minute look like a measurement.
                    if (mn == mx && distinct[ch].Count <= 5) distinct[ch].Add(Math.Round(mean, 6));
                    minutes[ch][0]++;
                    if (mn == mx) minutes[ch][1]++;
                    if (extremes[ch].Count <= 5) { extremes[ch].Add(Math.Round(mn, 6)); extremes[ch].Add(Math.Round(mx, 6)); }
                }
            }

            var observed = new Dictionary<int, Bits>();
            foreach (var kv in cnt)
            {
                var b = new Bits(n);
                for (int i = 0; i < n; i++) if (kv.Value[i] > 0) b.Set(i);
                observed[kv.Key] = b;
            }

            // Targets: channel and kind with enough anomalies.
            var targets = new Dictionary<string, Target>();
            using (var rd = store.Read())
            using (var st = rd.Db.Prepare("SELECT ch, kind, t0 FROM event WHERE t0 >= ?1 AND t0 < ?2 ORDER BY t0"))
            {
                st.Bind(1, from).Bind(2, to);
                while (st.Step())
                {
                    int ch = (int)st.GetLong(0);
                    if (!ids.Contains(ch)) continue;
                    string kind = st.GetText(1);
                    long t0 = st.GetLong(2);
                    DateTime local = Clock.ToLocal(t0);
                    res.Heat[(int)local.DayOfWeek, local.Hour]++;
                    string key = ch + "/" + kind;
                    Target tg;
                    if (!targets.TryGetValue(key, out tg)) targets[key] = tg = new Target { Ch = ch, Kind = kind, Hit = new Bits(n) };
                    int i;
                    if (!index.TryGetValue((t0 - from) / slot, out i)) continue;
                    tg.Hit.Set(i);
                    if (tg.Starts.Count < 20000) tg.Starts.Add(t0);
                    // The slot the anomaly starts in counts as observed even without data (a gap starts where data ends).
                    Bits ob;
                    if (!observed.TryGetValue(ch, out ob)) observed[ch] = ob = new Bits(n);
                    ob.Set(i);
                }
            }

            // Situations.
            var features = new List<Feature>();
            var hourBins = new Bits[6];
            var weekdays = new Bits[7];
            Bits weekend = new Bits(n), weekdayType = new Bits(n);
            for (int k = 0; k < 6; k++) hourBins[k] = new Bits(n);
            for (int k = 0; k < 7; k++) weekdays[k] = new Bits(n);
            for (int i = 0; i < n; i++)
            {
                DateTime local = Clock.ToLocal(slotStart[i] + slot / 2);
                hourBins[local.Hour / 4].Set(i);
                weekdays[(int)local.DayOfWeek].Set(i);
                if (local.DayOfWeek == DayOfWeek.Saturday || local.DayOfWeek == DayOfWeek.Sunday) weekend.Set(i); else weekdayType.Set(i);
            }
            for (int k = 0; k < 6; k++) features.Add(new Feature { C = new Condition { Type = "hour", Hour = 4 * k }, B = hourBins[k] });
            if ((long)n * slot >= 14 * 86400000L)
            {
                features.Add(new Feature { C = new Condition { Type = "daytype", Weekend = true }, B = weekend });
                features.Add(new Feature { C = new Condition { Type = "daytype", Weekend = false }, B = weekdayType });
                for (int k = 0; k < 7; k++) features.Add(new Feature { C = new Condition { Type = "weekday", Day = k }, B = weekdays[k] });
            }
            foreach (var kv in sum)
            {
                int ch = kv.Key;
                int[] c = cnt[ch];
                bool discrete = IsSwitch(distinct[ch], extremes[ch], minutes[ch]);
                if (discrete)
                {
                    foreach (double v in distinct[ch])
                    {
                        var b = new Bits(n);
                        for (int i = 0; i < n; i++) if (c[i] > 0 && constant[ch][i] && Math.Abs(kv.Value[i] / c[i] - v) < 1e-6) b.Set(i);
                        features.Add(new Feature { C = new Condition { Type = "value", Ch = ch, Value = v }, B = b });
                    }
                    continue;
                }
                var means = new List<double>();
                for (int i = 0; i < n; i++) if (c[i] > 0) means.Add(kv.Value[i] / c[i]);
                if (means.Count < 20) continue;
                var arr = means.ToArray();
                double lo = Robust.Quantile(arr, 0.2), hi = Robust.Quantile(arr, 0.8);
                if (!(hi > lo)) continue;
                Bits bl = new Bits(n), bh = new Bits(n);
                for (int i = 0; i < n; i++)
                {
                    if (c[i] == 0) continue;
                    double m = kv.Value[i] / c[i];
                    if (m <= lo) bl.Set(i);
                    if (m >= hi) bh.Set(i);
                }
                features.Add(new Feature { C = new Condition { Type = "low", Ch = ch, Value = lo }, B = bl });
                features.Add(new Feature { C = new Condition { Type = "high", Ch = ch, Value = hi }, B = bh });
            }

            // Single situations, then pairs of the strongest ones.
            var candidates = new List<Rule>();
            var used = new List<Target>();
            foreach (Target tg in targets.Values)
            {
                Bits ob = observed[tg.Ch];
                int total = ob.Count(), events = Bits.CountAnd(tg.Hit, ob);
                if (events < MinEvents || total < 20) continue;
                used.Add(tg);
                var singles = new List<KeyValuePair<Feature, Rule>>();
                foreach (Feature f in features)
                {
                    if (f.C.Ch == tg.Ch && !OwnLevelAllowed(tg.Kind)) continue;
                    int slots = Bits.CountAnd(f.B, ob);
                    if (slots == 0 || slots == total) continue;
                    int hits = Bits.CountAnd(f.B, ob, tg.Hit);
                    Rule r = MakeRule(tg, total, events, slots, hits);
                    r.When.Add(f.C);
                    res.Tests++;
                    candidates.Add(r);
                    singles.Add(new KeyValuePair<Feature, Rule>(f, r));
                }
                singles.Sort((a, b) => a.Value.P.CompareTo(b.Value.P));
                int top = Math.Min(8, singles.Count);
                for (int a = 0; a < top; a++)
                    for (int b = a + 1; b < top; b++)
                    {
                        Feature fa = singles[a].Key, fb = singles[b].Key;
                        if (fa.C.Group == fb.C.Group) continue;
                        int slots = Bits.CountAnd(fa.B, fb.B, ob);
                        if (slots == 0) continue;
                        int hits = Bits.CountAnd(fa.B, fb.B, ob, tg.Hit);
                        Rule r = MakeRule(tg, total, events, slots, hits);
                        r.When.Add(fa.C);
                        r.When.Add(fb.C);
                        res.Tests++;
                        // A pair is kept only when it says more than either part alone: within each part's slots, the
                        // other part must still make the anomaly clearly more likely. Otherwise a few chance hits
                        // in a small subset (a weekday added to a real condition) would look like a sharper rule.
                        Rule ra = singles[a].Value, rb = singles[b].Value;
                        if (r.Lift < 1.2 * Math.Max(ra.Lift, rb.Lift)) continue;
                        double pa = StatTests.HyperUpper(hits, ra.Slots, ra.Hits, slots);
                        double pb = StatTests.HyperUpper(hits, rb.Slots, rb.Hits, slots);
                        if (pa > 0.01 || pb > 0.01) continue;
                        candidates.Add(r);
                    }
            }
            res.Targets = used.Count;

            // What comes before: anomalies, and switches changing to a value ("to:1").
            var triggers = new List<Target>(used);
            foreach (var kv in switches)
            {
                if (!distinct.ContainsKey(kv.Key) || !IsSwitch(distinct[kv.Key], extremes[kv.Key], minutes[kv.Key])) continue;
                var byValue = new Dictionary<double, Target>();
                foreach (var change in kv.Value)
                {
                    Target tg;
                    if (!byValue.TryGetValue(change.Key, out tg))
                        byValue[change.Key] = tg = new Target { Ch = kv.Key, Kind = "to:" + Json.Number(change.Key) };
                    tg.Starts.Add(change.Value);
                }
                foreach (Target tg in byValue.Values) if (tg.Starts.Count >= MinEvents) triggers.Add(tg);
            }

            // Which anomaly tends to follow which, within a window.
            var seqs = new List<Sequence>();
            long observedTime = n * slot;
            foreach (Target a in triggers)
                foreach (Target b in used)
                {
                    if (a == b) continue;
                    Sequence best = null;
                    foreach (long win in Windows)
                    {
                        double cover = Covered(a.Starts, win, from, to) / (double)observedTime;
                        if (cover <= 0 || cover >= 0.5) continue;
                        int hits = Follows(a.Starts, b.Starts, win);
                        double expected = b.Starts.Count * cover;
                        var s = new Sequence
                        {
                            ChA = a.Ch, KindA = a.Kind, ChB = b.Ch, KindB = b.Kind, Window = win, Hits = hits, CountB = b.Starts.Count,
                            Expected = expected, Lift = expected > 0 ? hits / expected : 0, P = StatTests.BinomUpper(hits, b.Starts.Count, cover)
                        };
                        res.Tests++;
                        if (best == null || s.P < best.P) best = s;
                    }
                    if (best != null) seqs.Add(best);
                }

            // One correction over everything tested.
            var all = new List<double>();
            foreach (Rule r in candidates) all.Add(r.P);
            foreach (Sequence s in seqs) all.Add(s.P);
            double extra = Math.Max(0, res.Tests - all.Count);   // tests dropped above still count
            var padded = new List<double>(all);
            for (int i = 0; i < extra; i++) padded.Add(1);
            double[] q = StatTests.Adjust(padded);
            for (int i = 0; i < candidates.Count; i++) candidates[i].Q = q[i];
            for (int i = 0; i < seqs.Count; i++) seqs[i].Q = q[candidates.Count + i];

            foreach (Rule r in candidates)
                if (r.Q <= MaxQ && r.Lift >= MinLift && r.Hits >= MinHits) res.Rules.Add(r);
            // A sequence must also account for a fair share of B (a fifth), or two things that both happen at
            // dawn would look like one leading to the other.
            foreach (Sequence s in seqs)
                if (s.Q <= MaxQ && s.Lift >= MinLift && s.Hits >= MinHits && s.Hits >= 0.2 * s.CountB) res.Sequences.Add(s);
            res.Rules.Sort((x, y) => y.Lift * Math.Sqrt(y.Hits) == x.Lift * Math.Sqrt(x.Hits) ? 0 : y.Lift * Math.Sqrt(y.Hits) > x.Lift * Math.Sqrt(x.Hits) ? 1 : -1);
            res.Sequences.Sort((x, y) => x.P.CompareTo(y.P));
            if (res.Rules.Count > 200) res.Rules.RemoveRange(200, res.Rules.Count - 200);
            if (res.Sequences.Count > 100) res.Sequences.RemoveRange(100, res.Sequences.Count - 100);
            if (used.Count == 0) res.Note = "few_events";
            return res;
        }

        static Rule MakeRule(Target tg, int total, int events, int slots, int hits)
        {
            double rate = hits / (double)slots, baseRate = events / (double)total;
            return new Rule
            {
                Ch = tg.Ch, Kind = tg.Kind, Slots = slots, Hits = hits, Events = events, Observed = total,
                Lift = baseRate > 0 ? rate / baseRate : 0,
                P = StatTests.HyperUpper(hits, total, events, slots)
            };
        }

        // Total length of the union of (t, t + win], cut to the period.
        static long Covered(List<long> starts, long win, long from, long to)
        {
            long total = 0, curA = long.MinValue, curB = long.MinValue;
            foreach (long t in starts)
            {
                long a = Math.Max(t, from), b = Math.Min(t + win, to);
                if (b <= a) continue;
                if (a > curB) { if (curB > curA) total += curB - curA; curA = a; curB = b; }
                else if (b > curB) curB = b;
            }
            if (curB > curA) total += curB - curA;
            return total;
        }

        // How many of B's starts come within (0, win] after one of A's. Both lists are in time order.
        static int Follows(List<long> a, List<long> b, long win)
        {
            int hits = 0, j = 0;
            foreach (long tb in b)
            {
                while (j < a.Count && a[j] < tb - win) j++;
                // a[j] >= tb - win: is there one before tb?
                int k = j;
                while (k < a.Count && a[k] < tb) { if (tb - a[k] <= win) { hits++; break; } k++; }
            }
            return hits;
        }
    }
}
