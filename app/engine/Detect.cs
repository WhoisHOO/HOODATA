// Anomaly detection, one Detector per channel, fed values in time order. The methods and their sources are described
// in docs/METHODS.md. Everything here is plain arithmetic on recent values, so each finding can say why it was made.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hoodata
{
    public static class Kinds
    {
        public const string Gap = "gap", Spike = "spike", Step = "step", Stuck = "stuck", Range = "range",
            Noisy = "noisy", Unusual = "unusual";
        public static readonly string[] All = { Gap, Spike, Step, Stuck, Range, Noisy, Unusual };

        // Open anomalies of these kinds make a channel's status "alert"; the others make it "warn".
        public static bool IsAlert(string kind) { return kind == Gap || kind == Stuck || kind == Range || kind == Step; }
    }

    public sealed class DetectSettings
    {
        public string Sensitivity = "normal";
        public double SpikeK = 6;        // spike or step: this many robust standard deviations from the recent median
        public double GapFactor = 5;     // gap: this many times the usual interval without data
        public double StuckFactor = 3;   // stuck: this many times the longest usual run of equal values
        public double NoisyFactor = 3;   // noisy: the noise this many times its usual level
        public double UnusualK = 4;      // unusual: this many robust standard deviations from the usual for the hour
        public long MinGapMs = 10000;

        public static DetectSettings For(string sensitivity)
        {
            var s = new DetectSettings();
            switch (sensitivity)
            {
                case "low":
                    s.Sensitivity = "low"; s.SpikeK = 8; s.GapFactor = 10; s.StuckFactor = 5; s.NoisyFactor = 4; s.UnusualK = 5;
                    break;
                case "high":
                    s.Sensitivity = "high"; s.SpikeK = 5; s.GapFactor = 3; s.StuckFactor = 2; s.NoisyFactor = 2.5; s.UnusualK = 3.5;
                    break;
            }
            return s;
        }
    }

    // Receives the anomalies a detector finds.
    public interface IEventSink
    {
        EventRec Open(string kind, long t0, double? value, double? expected, double? score, string detail);
        void Changed(EventRec e);
        void Close(EventRec e, long t1);
    }

    public sealed class Ring
    {
        readonly double[] a;
        int start, count;
        public Ring(int capacity) { a = new double[capacity]; }
        public int Count { get { return count; } }
        public int Capacity { get { return a.Length; } }
        public void Clear() { start = 0; count = 0; }

        public void Add(double v)
        {
            if (count < a.Length) { a[(start + count) % a.Length] = v; count++; }
            else { a[start] = v; start = (start + 1) % a.Length; }
        }

        public double this[int i] { get { return a[(start + i) % a.Length]; } }
        public double Last { get { return a[(start + count - 1) % a.Length]; } }

        public double[] ToArray()
        {
            var r = new double[count];
            for (int i = 0; i < count; i++) r[i] = a[(start + i) % a.Length];
            return r;
        }
    }

    public static class Robust
    {
        public const double MadToSd = 1.4826;

        // Sorts the array in place.
        public static double Median(double[] x)
        {
            if (x.Length == 0) return double.NaN;
            Array.Sort(x);
            int m = x.Length / 2;
            return x.Length % 2 == 1 ? x[m] : 0.5 * (x[m - 1] + x[m]);
        }

        // Median and median absolute deviation. The array is reused.
        public static void MedianMad(double[] x, out double median, out double mad)
        {
            median = Median(x);
            for (int i = 0; i < x.Length; i++) x[i] = Math.Abs(x[i] - median);
            mad = Median(x);
        }

        public static double Quantile(double[] x, double p)
        {
            if (x.Length == 0) return double.NaN;
            Array.Sort(x);
            double pos = p * (x.Length - 1);
            int lo = (int)Math.Floor(pos);
            int hi = Math.Min(lo + 1, x.Length - 1);
            return x[lo] + (x[hi] - x[lo]) * (pos - lo);
        }

        // Standard deviation of the noise from successive differences: for independent noise of deviation s,
        // d = x[t] - x[t-1] has deviation s * sqrt(2); its median is the local trend and is taken out.
        public static double NoiseFromDiffs(double[] d)
        {
            if (d.Length < 8) return double.NaN;
            double med, mad;
            var copy = (double[])d.Clone();
            MedianMad(copy, out med, out mad);
            double s = MadToSd * mad / Math.Sqrt(2);
            if (s > 0) return s;
            // Mostly equal steps (a coarse sensor): fall back to the mean absolute deviation.
            double sum = 0;
            foreach (double v in d) sum += Math.Abs(v - med);
            return sum / d.Length * 1.2533 / Math.Sqrt(2);
        }
    }

    public sealed class Detector
    {
        const int Window = 24, MinWindow = 16, MaxSpikeLength = 5, DiffWindow = 64, ShortDiffs = 24;
        const int Blocks = 14 * 12;     // five-minute means per hour of day: two weeks
        const long BlockMs = 5 * 60000;

        readonly IEventSink sink;
        DetectSettings cfg;
        public double? Lo, Hi;
        public bool Quiet;              // replaying stored values: update the state but report nothing
        public long ResumeFloor = long.MinValue;  // gaps that start before this (HOODATA was not running) are not gaps

        long n, prevT = long.MinValue;
        double prevV;
        readonly Ring win = new Ring(Window), diffs = new Ring(DiffWindow), intervals = new Ring(64);
        double step = double.PositiveInfinity;   // the smallest change seen: the sensor's resolution
        double noise = double.NaN;               // cached noise estimate
        double interval = double.NaN;            // cached usual interval between values
        int sinceNoise;

        // spike / step
        readonly List<double> pendV = new List<double>();
        readonly List<long> pendT = new List<long>();
        double pendMed, pendScale;
        EventRec lastSpike;
        int lastSpikeCount;
        bool consecutiveNow;

        // stuck
        long runStart = long.MinValue, runEnd;
        int changes;
        readonly Ring runs = new Ring(200);

        // noisy: past noise estimates with the level they were taken at, so that a machine that is noisier when it
        // runs is compared with its own running noise
        readonly Ring noiseHist = new Ring(1024), noiseLevel = new Ring(1024);
        int noisyOver, noisyUnder, sinceNoiseHist;
        long lastNoiseHistT;
        double usualNoise = double.NaN, usualAt = double.NaN;
        double level = double.NaN;   // the recent median

        // unusual: the usual level for each hour, kept apart for weekdays and weekends
        long blockStart = long.MinValue;
        double blockSum;
        int blockCount, unusualOver, unusualUnder;
        readonly Ring[] profile = new Ring[48];
        readonly Ring[] profileDay = new Ring[48];

        EventRec gapEv, stuckEv, rangeEv, noisyEv, unusualEv;

        public Detector(IEventSink sink, DetectSettings cfg)
        {
            this.sink = sink;
            this.cfg = cfg;
            for (int h = 0; h < profile.Length; h++) { profile[h] = new Ring(Blocks); profileDay[h] = new Ring(Blocks); }
        }

        public DetectSettings Settings { get { return cfg; } set { cfg = value; } }
        public long Count { get { return n; } }
        public long LastT { get { return prevT; } }
        public double LastV { get { return prevV; } }
        public bool Learning { get { return win.Count < MinWindow; } }
        public double Noise { get { return noise; } }

        // The 90th percentile of the recent intervals, so a source that sends unevenly is not taken for one with gaps.
        public double TypicalInterval { get { return interval; } }

        public double GapThreshold
        {
            get
            {
                double ti = TypicalInterval;
                return double.IsNaN(ti) ? double.NaN : Math.Max(cfg.MinGapMs, cfg.GapFactor * ti);
            }
        }

        public bool HasOpen(string kind)
        {
            switch (kind)
            {
                case Kinds.Gap: return gapEv != null;
                case Kinds.Stuck: return stuckEv != null;
                case Kinds.Range: return rangeEv != null;
                case Kinds.Noisy: return noisyEv != null;
                case Kinds.Unusual: return unusualEv != null;
            }
            return false;
        }

        public IEnumerable<EventRec> OpenEvents()
        {
            foreach (EventRec e in new[] { gapEv, stuckEv, rangeEv, noisyEv, unusualEv }) if (e != null) yield return e;
        }

        EventRec Open(string kind, long t0, double? value, double? expected, double? score, string detail)
        {
            return Quiet ? null : sink.Open(kind, t0, value, expected, score, detail);
        }

        void Close(ref EventRec e, long t1)
        {
            if (e != null) sink.Close(e, t1);
            e = null;
        }

        static string D(double v) { return Json.Number(Math.Round(v, 6)); }

        // ---- live check: called about once a second for channels whose source is running ----

        public void Tick(long now)
        {
            if (gapEv != null || prevT == long.MinValue || prevT < ResumeFloor) return;
            double th = GapThreshold;
            if (double.IsNaN(th) || now - prevT <= th) return;
            gapEv = Open(Kinds.Gap, prevT, null, null, (now - prevT) / TypicalInterval,
                "{\"interval\":" + D(TypicalInterval) + "}");
        }

        // ---- one value ----

        public void Feed(long t, double v)
        {
            if (prevT != long.MinValue && t <= prevT) return;   // out of order or repeated: stored, not judged
            n++;
            double th = GapThreshold;
            bool consecutive = true;
            // The first value after HOODATA was not running: the time between says nothing about the source.
            bool resumed = prevT != long.MinValue && prevT < ResumeFloor && t >= ResumeFloor;
            if (resumed) consecutive = false;
            if (prevT != long.MinValue && !resumed)
            {
                long dt = t - prevT;
                if (!double.IsNaN(th) && dt > th)
                {
                    consecutive = false;
                    if (gapEv != null) Close(ref gapEv, t);
                    else if (prevT >= ResumeFloor)
                    {
                        EventRec g = Open(Kinds.Gap, prevT, null, null, dt / TypicalInterval,
                            "{\"interval\":" + D(TypicalInterval) + "}");
                        Close(ref g, t);
                    }
                }
                else if (gapEv != null) Close(ref gapEv, t);
                intervals.Add(dt);
                if (consecutive)
                {
                    double d = v - prevV;
                    diffs.Add(d);
                    double ad = Math.Abs(d);
                    if (ad > 1e-9 * Math.Max(1, Math.Abs(v)) && ad < step) step = ad;
                }
            }

            if (++sinceNoise >= 8 || double.IsNaN(noise) || double.IsNaN(interval))
            {
                sinceNoise = 0;
                noise = Robust.NoiseFromDiffs(diffs.ToArray());
                if (intervals.Count >= 8) interval = Robust.Quantile(intervals.ToArray(), 0.9);
            }

            CheckRange(t, v);
            consecutiveNow = consecutive && prevT != long.MinValue;
            CheckSpike(t, v);
            CheckStuck(t, v, consecutive);
            CheckNoisy(t);
            CheckUnusual(t, v);

            prevT = t;
            prevV = v;
        }

        // ---- range ----

        void CheckRange(long t, double v)
        {
            bool below = Lo.HasValue && v < Lo.Value, above = Hi.HasValue && v > Hi.Value;
            if (below || above)
            {
                if (rangeEv == null)
                {
                    rangeEv = Open(Kinds.Range, t, v, below ? Lo : Hi, null,
                        "{\"lo\":" + (Lo.HasValue ? D(Lo.Value) : "null") + ",\"hi\":" + (Hi.HasValue ? D(Hi.Value) : "null") + "}");
                }
                else if (Math.Abs(v - rangeEv.Expected.GetValueOrDefault(v)) > Math.Abs(rangeEv.Value.GetValueOrDefault(v) - rangeEv.Expected.GetValueOrDefault(v)))
                {
                    rangeEv.Value = v;
                    sink.Changed(rangeEv);
                }
            }
            else if (rangeEv != null) Close(ref rangeEv, t);
        }

        // ---- spike and step ----

        double Scale(double mad)
        {
            double s = Robust.MadToSd * mad;
            if (!double.IsNaN(noise) && noise > s) s = noise;
            if (!double.IsInfinity(step) && step > s) s = step;
            return s;
        }

        void CheckSpike(long t, double v)
        {
            if (pendV.Count > 0)
            {
                double dev = v - pendMed;
                if (Math.Abs(dev) <= 0.5 * cfg.SpikeK * pendScale)
                {
                    ReportSpike();
                    win.Add(v);
                    return;
                }
                pendV.Add(v);
                pendT.Add(t);
                if (pendV.Count > MaxSpikeLength) ReportStep();
                return;
            }

            if (win.Count >= MinWindow)
            {
                double med, mad;
                Robust.MedianMad(win.ToArray(), out med, out mad);
                level = med;
                double scale = Scale(mad);
                // Spikes and steps are sudden: the value must also jump from the one just before. A slow swing or a
                // morning warm-up drifts away from the median without ever jumping.
                double jump = Math.Max(double.IsNaN(noise) ? 0 : noise * Math.Sqrt(2), double.IsInfinity(step) ? 0 : step);
                bool sudden = !consecutiveNow || Math.Abs(v - prevV) > 0.5 * cfg.SpikeK * jump;
                if (scale > 0 && sudden && Math.Abs(v - med) > cfg.SpikeK * scale)
                {
                    pendMed = med;
                    pendScale = scale;
                    pendV.Add(v);
                    pendT.Add(t);
                    return;
                }
            }
            win.Add(v);
        }

        void ReportSpike()
        {
            int far = 0;
            for (int i = 1; i < pendV.Count; i++)
                if (Math.Abs(pendV[i] - pendMed) > Math.Abs(pendV[far] - pendMed)) far = i;
            double value = pendV[far], score = Math.Abs(value - pendMed) / pendScale;
            long t0 = pendT[0], t1 = pendT[pendT.Count - 1];
            pendV.Clear();
            pendT.Clear();
            if (Quiet) return;

            // Spikes close together are one event, so a bad stretch is one line in the record.
            double ti = TypicalInterval;
            long merge = (long)Math.Max(60000, double.IsNaN(ti) ? 0 : 10 * ti);
            if (lastSpike != null && lastSpike.T1.HasValue && t0 - lastSpike.T1.Value <= merge)
            {
                lastSpike.T1 = t1;
                double prevScore = lastSpike.Score.GetValueOrDefault();
                if (score > prevScore)
                {
                    lastSpike.Score = score;
                    lastSpike.Value = value;
                    lastSpike.Expected = pendMed;
                }
                lastSpikeCount++;
                lastSpike.Detail = SpikeDetail(lastSpikeCount, pendScale);
                sink.Changed(lastSpike);
                return;
            }
            EventRec e = sink.Open(Kinds.Spike, t0, value, pendMed, score, SpikeDetail(1, pendScale));
            sink.Close(e, t1);
            lastSpike = e;
            lastSpikeCount = 1;
        }

        static string SpikeDetail(int count, double scale)
        {
            return "{\"count\":" + count + ",\"scale\":" + D(scale) + "}";
        }

        void ReportStep()
        {
            var vals = pendV.ToArray();
            double level = Robust.Median((double[])vals.Clone());
            double shift = level - pendMed;
            if (!Quiet)
            {
                EventRec e = sink.Open(Kinds.Step, pendT[0], level, pendMed, Math.Abs(shift) / pendScale,
                    "{\"before\":" + D(pendMed) + ",\"after\":" + D(level) + ",\"scale\":" + D(pendScale) + "}");
                sink.Close(e, pendT[pendT.Count - 1]);
            }
            // A new level can bring a new spread (a machine that runs is noisier than one that stands), so the
            // spread is learned again from the new level's values.
            win.Clear();
            foreach (double p in pendV) win.Add(p);
            diffs.Clear();
            for (int i = 1; i < pendV.Count; i++) diffs.Add(pendV[i] - pendV[i - 1]);
            noise = double.NaN;
            pendV.Clear();
            pendT.Clear();
        }

        // ---- stuck ----

        void CheckStuck(long t, double v, bool consecutive)
        {
            bool same = runStart != long.MinValue && consecutive && Math.Abs(v - prevV) <= 1e-12 * Math.Max(1, Math.Abs(v));
            if (same)
            {
                runEnd = t;
                if (stuckEv == null && changes >= 50 && runs.Count >= 20)
                {
                    double ti = TypicalInterval;
                    double usual = Robust.Quantile(runs.ToArray(), 0.99);
                    double th = Math.Max(cfg.StuckFactor * usual, Math.Max(60000, double.IsNaN(ti) ? 0 : 10 * ti));
                    if (t - runStart > th)
                        stuckEv = Open(Kinds.Stuck, runStart, v, null, (t - runStart) / Math.Max(usual, 1),
                            "{\"usual\":" + D(usual) + "}");
                }
                return;
            }
            if (runStart != long.MinValue)
            {
                if (stuckEv != null) Close(ref stuckEv, t);
                else
                {
                    double ti = TypicalInterval;
                    runs.Add(runEnd - runStart + (double.IsNaN(ti) ? 0 : ti));
                }
                if (consecutive) changes++;
            }
            runStart = t;
            runEnd = t;
        }

        // ---- noisy ----

        void CheckNoisy(long t)
        {
            if (diffs.Count < ShortDiffs) return;
            // One past estimate every 5 minutes (and 16 values at least): 1024 of them reach back about 3.5 days,
            // far enough to remember yesterday's running hours across a night.
            sinceNoiseHist++;
            if (sinceNoiseHist >= 16 && t - lastNoiseHistT >= 300000)
            {
                sinceNoiseHist = 0;
                lastNoiseHistT = t;
                if (!double.IsNaN(noise) && !double.IsNaN(level) && noisyEv == null) { noiseHist.Add(noise); noiseLevel.Add(level); usualAt = double.NaN; }
            }
            if (noiseHist.Count < 32 || (n & 3) != 0 || double.IsNaN(level)) return;
            var recent = new double[ShortDiffs];
            for (int i = 0; i < ShortDiffs; i++) recent[i] = diffs[diffs.Count - ShortDiffs + i];
            double now = Robust.NoiseFromDiffs(recent);
            if (double.IsNaN(usualAt) || (n & 31) == 0 || Math.Abs(level - usualAt) > 5 * Math.Max(now, double.IsNaN(usualNoise) ? 0 : usualNoise))
            {
                usualNoise = UsualNoise(level);
                usualAt = level;
            }
            double usual = usualNoise;
            if (!(usual > 0) || double.IsNaN(now)) return;
            double ratio = now / usual;
            if (noisyEv == null)
            {
                noisyOver = ratio > cfg.NoisyFactor ? noisyOver + 1 : 0;
                if (noisyOver >= 3)
                {
                    noisyEv = Open(Kinds.Noisy, t, now, usual, ratio, "{}");
                    noisyUnder = 0;
                }
            }
            else
            {
                if (ratio > noisyEv.Score.GetValueOrDefault())
                {
                    noisyEv.Value = now;
                    noisyEv.Score = ratio;
                    sink.Changed(noisyEv);
                }
                noisyUnder = ratio < 1.5 ? noisyUnder + 1 : 0;
                if (noisyUnder >= 3) { Close(ref noisyEv, t); noisyOver = 0; }
            }
        }

        // The median noise of up to 32 past estimates taken near this level. Estimates from a level far away (a machine
        // that was off) do not count; with fewer than 16 near ones there is nothing to compare with yet.
        double UsualNoise(double at)
        {
            int m = noiseHist.Count;
            var dist = new double[m];
            var idx = new int[m];
            for (int i = 0; i < m; i++) { dist[i] = Math.Abs(noiseLevel[i] - at); idx[i] = i; }
            Array.Sort(dist, idx);
            var near = new List<double>();
            double res = double.IsInfinity(step) ? 0 : step;
            for (int i = 0; i < m && near.Count < 32; i++)
            {
                double nz = noiseHist[idx[i]];
                if (dist[i] > Math.Max(10 * nz, Math.Max(0.05 * Math.Abs(at), 5 * res))) break;
                near.Add(nz);
            }
            return near.Count >= 16 ? Robust.Median(near.ToArray()) : double.NaN;
        }

        // ---- unusual for the hour of day ----

        void CheckUnusual(long t, double v)
        {
            long b = t - (t % BlockMs);
            if (blockStart == long.MinValue) blockStart = b;
            if (b != blockStart)
            {
                if (blockCount > 0) CloseBlock(blockStart + BlockMs - 1, blockSum / blockCount);
                blockStart = b;
                blockSum = 0;
                blockCount = 0;
            }
            blockSum += v;
            blockCount++;
        }

        public static int LocalHour(long t) { return Clock.ToLocal(t).Hour; }
        static double LocalDay(long t) { return Math.Floor((Clock.ToLocal(t) - new DateTime(2000, 1, 1)).TotalDays); }

        // Weekdays and weekends keep separate profiles: offices, shops and machines run on a weekly rhythm.
        static int ProfileIndex(long t)
        {
            DateTime local = Clock.ToLocal(t);
            bool weekend = local.DayOfWeek == DayOfWeek.Saturday || local.DayOfWeek == DayOfWeek.Sunday;
            return (weekend ? 24 : 0) + local.Hour;
        }

        // Also used to rebuild the profile from stored summaries when HOODATA starts.
        public void AddToProfile(long t, double mean)
        {
            int i = ProfileIndex(t);
            profile[i].Add(mean);
            profileDay[i].Add(LocalDay(t));
        }

        void CloseBlock(long t, double mean)
        {
            int idx = ProfileIndex(t), h = idx % 24;
            Ring p = profile[idx], pd = profileDay[idx];
            double today = LocalDay(t);
            // The usual range comes from earlier days only, and needs five of them: with fewer, the spread from one
            // day to the next is not known yet.
            var earlier = new List<double>();
            var dayMeans = new Dictionary<double, double[]>();
            for (int i = 0; i < p.Count; i++)
            {
                if (pd[i] == today) continue;
                earlier.Add(p[i]);
                double[] acc;
                if (!dayMeans.TryGetValue(pd[i], out acc)) dayMeans[pd[i]] = acc = new double[2];
                acc[0] += p[i];
                acc[1]++;
            }
            if (dayMeans.Count >= 5 && earlier.Count >= 24)
            {
                double med, mad;
                Robust.MedianMad(earlier.ToArray(), out med, out mad);
                double s = Robust.MadToSd * mad;
                // The spread between days, from each day's mean for this hour.
                var means = new double[dayMeans.Count];
                int k = 0;
                foreach (double[] acc in dayMeans.Values) means[k++] = acc[0] / acc[1];
                double dm, dmad;
                Robust.MedianMad(means, out dm, out dmad);
                if (Robust.MadToSd * dmad > s) s = Robust.MadToSd * dmad;
                if (!double.IsInfinity(step) && step > s) s = step;
                if (s > 0)
                {
                    double z = Math.Abs(mean - med) / s;
                    if (unusualEv == null)
                    {
                        unusualOver = z > cfg.UnusualK ? unusualOver + 1 : 0;
                        if (unusualOver >= 3)
                        {
                            unusualEv = Open(Kinds.Unusual, t - 3 * BlockMs + 1, mean, med, z,
                                "{\"lo\":" + D(med - cfg.UnusualK * s) + ",\"hi\":" + D(med + cfg.UnusualK * s) + ",\"hour\":" + h + "}");
                            unusualUnder = 0;
                        }
                    }
                    else
                    {
                        if (z > unusualEv.Score.GetValueOrDefault())
                        {
                            unusualEv.Value = mean;
                            unusualEv.Score = z;
                            sink.Changed(unusualEv);
                        }
                        unusualUnder = z < cfg.UnusualK - 1 ? unusualUnder + 1 : 0;
                        if (unusualUnder >= 3) { Close(ref unusualEv, t); unusualOver = 0; }
                    }
                }
            }
            AddToProfile(t, mean);
        }

        // When a source stops, its channels stop being judged; anything still open ends at the last value.
        public void Suspend()
        {
            long t = prevT == long.MinValue ? Clock.Now() : prevT;
            Close(ref gapEv, Math.Max(t, gapEv != null ? gapEv.T0 : t));
            Close(ref stuckEv, t);
            Close(ref rangeEv, t);
            Close(ref noisyEv, t);
            Close(ref unusualEv, t);
        }
    }
}
