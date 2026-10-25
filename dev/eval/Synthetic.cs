// Synthetic data with anomalies put in at known times, to measure how many the detectors find (recall) and how many
// of their findings are real (precision). Deterministic: the same seed gives the same data. Not part of the app;
// dev\eval\Evaluate-Detectors.ps1 and the tests compile it together with the engine.
using System;
using System.Collections.Generic;

namespace Hoodata.Eval
{
    public sealed class Truth
    {
        public string Kind;
        public long T0, T1;
        public string Note;
    }

    public sealed class Scenario
    {
        public string Name;
        public long IntervalMs;
        public readonly List<long> T = new List<long>();
        public readonly List<double> V = new List<double>();
        public readonly List<Truth> Truths = new List<Truth>();
        public double? Lo, Hi;
        public double Days { get { return T.Count < 2 ? 0 : (T[T.Count - 1] - T[0]) / 86400000.0; } }
    }

    public sealed class Collector : IEventSink
    {
        public readonly List<EventRec> Events = new List<EventRec>();
        long next = 1;

        public EventRec Open(string kind, long t0, double? value, double? expected, double? score, string detail)
        {
            var e = new EventRec { Id = next++, Kind = kind, T0 = t0, Value = value, Expected = expected, Score = score, Detail = detail };
            Events.Add(e);
            return e;
        }

        public void Changed(EventRec e) { }
        public void Close(EventRec e, long t1) { e.T1 = Math.Max(t1, e.T0); }
    }

    public sealed class ScoreRow
    {
        public string Scenario, Kind;
        public int Truths, Found, Detections, FalsePositives;
        public double Days;
    }

    public static class Synthetic
    {
        // Monday 5 January 2026, midnight on this PC's clock, so the hour-of-day logic sees real local hours.
        static long Start() { return Clock.FromUtc(new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Local).ToUniversalTime()); }

        sealed class Gauss
        {
            readonly Random r;
            public Gauss(int seed) { r = new Random(seed); }
            public double U() { return r.NextDouble(); }
            public double N()
            {
                double u1 = 1 - r.NextDouble(), u2 = r.NextDouble();
                return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            }
            public int Int(int lo, int hi) { return r.Next(lo, hi); }
        }

        static double Quantize(double v, double q) { return q > 0 ? Math.Round(v / q) * q : v; }

        // Random sample positions, at least minApart from each other and from the ends.
        static List<int> Positions(Gauss g, int n, int count, int start, int minApart)
        {
            var list = new List<int>();
            for (int tries = 0; list.Count < count && tries < 100000; tries++)
            {
                int p = g.Int(start, n - minApart);
                bool ok = true;
                foreach (int q in list) if (Math.Abs(q - p) < minApart) { ok = false; break; }
                if (ok) list.Add(p);
            }
            list.Sort();
            return list;
        }

        // A room or greenhouse temperature: a daily cycle, a slow wander, noise, a 0.1-degree sensor.
        public static Scenario Temperature(int seed, bool clean)
        {
            var g = new Gauss(seed);
            var s = new Scenario { Name = clean ? "temperature-clean" : "temperature", IntervalMs = 60000 };
            int n = 14 * 1440;
            long t0 = Start();
            var level = new double[n];
            double walk = 0;
            for (int i = 0; i < n; i++)
            {
                double hour = (i % 1440) / 60.0;
                walk = 0.995 * walk + 0.03 * g.N();
                level[i] = 20 + 5 * Math.Sin(2 * Math.PI * (hour - 9) / 24) + walk;
            }
            var noise = new double[n];
            for (int i = 0; i < n; i++) noise[i] = 0.08;
            var keep = new bool[n];
            for (int i = 0; i < n; i++) keep[i] = true;
            var forced = new double?[n];

            if (!clean)
            {
                var pos = Positions(g, n, 24, 1440, 600);
                int k = 0;
                // 8 spikes of one or two samples, 2 to 8 degrees.
                for (int j = 0; j < 8; j++, k++)
                {
                    int p = pos[k], len = g.Int(1, 3);
                    double d = (g.U() < 0.5 ? -1 : 1) * (2 + 6 * g.U());
                    for (int m = 0; m < len; m++) forced[p + m] = level[p + m] + d;
                    s.Truths.Add(new Truth { Kind = Kinds.Spike, T0 = t0 + p * 60000L, T1 = t0 + (p + len - 1) * 60000L });
                }
                // 2 level shifts of 3 degrees for 6 hours (a heater switched on, a sensor moved): a step up and one down.
                for (int j = 0; j < 2; j++, k++)
                {
                    int p = pos[k];
                    for (int m = 0; m < 360; m++) level[p + m] += 3;
                    s.Truths.Add(new Truth { Kind = Kinds.Step, T0 = t0 + p * 60000L, T1 = t0 + p * 60000L });
                    s.Truths.Add(new Truth { Kind = Kinds.Step, T0 = t0 + (p + 360) * 60000L, T1 = t0 + (p + 360) * 60000L });
                }
                // 2 stuck stretches of 4 hours.
                for (int j = 0; j < 2; j++, k++)
                {
                    int p = pos[k];
                    double v = Quantize(level[p], 0.1);
                    for (int m = 0; m < 240; m++) forced[p + m] = v;
                    s.Truths.Add(new Truth { Kind = Kinds.Stuck, T0 = t0 + p * 60000L, T1 = t0 + (p + 239) * 60000L });
                }
                // 2 gaps of 2 hours.
                for (int j = 0; j < 2; j++, k++)
                {
                    int p = pos[k];
                    for (int m = 0; m < 120; m++) keep[p + m] = false;
                    s.Truths.Add(new Truth { Kind = Kinds.Gap, T0 = t0 + (p - 1) * 60000L, T1 = t0 + (p + 120) * 60000L });
                }
                // 2 noisy stretches of 2 hours, noise 8 times as large.
                for (int j = 0; j < 2; j++, k++)
                {
                    int p = pos[k];
                    for (int m = 0; m < 120; m++) noise[p + m] = 0.64;
                    s.Truths.Add(new Truth { Kind = Kinds.Noisy, T0 = t0 + p * 60000L, T1 = t0 + (p + 119) * 60000L });
                }
                // An unusual night on day 9: 3 degrees warmer from 01:00 to 05:00, eased in and out over 40 minutes so
                // that it is not a sudden step.
                {
                    int p = 9 * 1440 + 60;
                    for (int m = 0; m < 240; m++)
                    {
                        double f = Math.Min(1, Math.Min(m, 239 - m) / 40.0);
                        level[p + m] += 3 * f;
                    }
                    s.Truths.Add(new Truth { Kind = Kinds.Unusual, T0 = t0 + (p + 20) * 60000L, T1 = t0 + (p + 220) * 60000L });
                }
            }
            for (int i = 0; i < n; i++)
            {
                if (!keep[i]) continue;
                double v = forced[i].HasValue ? forced[i].Value : Quantize(level[i] + noise[i] * g.N(), 0.1);
                s.T.Add(t0 + i * 60000L);
                s.V.Add(Math.Round(v, 1));
            }
            return s;
        }

        // A pressure reading every second: a slow wander and fine noise.
        public static Scenario Pressure(int seed, bool clean)
        {
            var g = new Gauss(seed);
            var s = new Scenario { Name = clean ? "pressure-clean" : "pressure", IntervalMs = 1000 };
            int n = 86400;
            long t0 = Start();
            var level = new double[n];
            double walk = 0;
            for (int i = 0; i < n; i++)
            {
                walk = 0.9995 * walk + 0.002 * g.N();
                level[i] = 101.3 + walk + 0.2 * Math.Sin(2 * Math.PI * i / 86400.0);
            }
            var noise = new double[n];
            for (int i = 0; i < n; i++) noise[i] = 0.02;
            var keep = new bool[n];
            for (int i = 0; i < n; i++) keep[i] = true;
            var forced = new double?[n];
            if (!clean)
            {
                var pos = Positions(g, n, 18, 3600, 2000);
                int k = 0;
                for (int j = 0; j < 10; j++, k++)
                {
                    int p = pos[k], len = g.Int(1, 4);
                    double d = (g.U() < 0.5 ? -1 : 1) * (0.25 + 0.75 * g.U());
                    for (int m = 0; m < len; m++) forced[p + m] = level[p + m] + d;
                    s.Truths.Add(new Truth { Kind = Kinds.Spike, T0 = t0 + p * 1000L, T1 = t0 + (p + len - 1) * 1000L });
                }
                for (int j = 0; j < 3; j++, k++)
                {
                    int p = pos[k];
                    double d = (g.U() < 0.5 ? -1 : 1) * 0.5;
                    for (int m = p; m < n; m++) level[m] += d;
                    s.Truths.Add(new Truth { Kind = Kinds.Step, T0 = t0 + p * 1000L, T1 = t0 + p * 1000L });
                }
                for (int j = 0; j < 2; j++, k++)
                {
                    int p = pos[k];
                    for (int m = 0; m < 300; m++) keep[p + m] = false;
                    s.Truths.Add(new Truth { Kind = Kinds.Gap, T0 = t0 + (p - 1) * 1000L, T1 = t0 + (p + 300) * 1000L });
                }
                {
                    int p = pos[k++];
                    for (int m = 0; m < 600; m++) noise[p + m] = 0.1;
                    s.Truths.Add(new Truth { Kind = Kinds.Noisy, T0 = t0 + p * 1000L, T1 = t0 + (p + 599) * 1000L });
                }
                {
                    int p = pos[k++];
                    double v = Math.Round(level[p], 3);
                    for (int m = 0; m < 600; m++) forced[p + m] = v;
                    s.Truths.Add(new Truth { Kind = Kinds.Stuck, T0 = t0 + p * 1000L, T1 = t0 + (p + 599) * 1000L });
                }
            }
            for (int i = 0; i < n; i++)
            {
                if (!keep[i]) continue;
                double v = forced[i].HasValue ? forced[i].Value : level[i] + noise[i] * g.N();
                s.T.Add(t0 + i * 1000L);
                s.V.Add(Math.Round(v, 3));
            }
            return s;
        }

        // A machine's current every 10 seconds: on from 08:00 to 18:00 on weekdays (12 A, noisy), off otherwise.
        // Switching on and off are real level changes and count as steps.
        public static Scenario Machine(int seed)
        {
            var g = new Gauss(seed);
            var s = new Scenario { Name = "machine", IntervalMs = 10000 };
            int perDay = 8640, n = 7 * perDay;
            long t0 = Start();
            bool wasOn = false;
            double ar = 0;
            var pos = Positions(g, n, 6, perDay, 3000);
            var spikeAt = new HashSet<int>();
            for (int j = 0; j < 6; j++) spikeAt.Add(pos[j]);
            for (int i = 0; i < n; i++)
            {
                int day = i / perDay;
                double hour = (i % perDay) / 360.0;
                bool on = day < 5 && hour >= 8 && hour < 18;
                if (on != wasOn && i > 0) s.Truths.Add(new Truth { Kind = Kinds.Step, T0 = t0 + i * 10000L, T1 = t0 + i * 10000L, Note = "switch" });
                wasOn = on;
                ar = 0.8 * ar + g.N();
                double v = on ? 12 + 0.3 * ar : 0.2 + 0.02 * g.N();
                if (spikeAt.Contains(i))
                {
                    v += on ? 8 : 6;
                    s.Truths.Add(new Truth { Kind = Kinds.Spike, T0 = t0 + i * 10000L, T1 = t0 + i * 10000L });
                }
                s.T.Add(t0 + i * 10000L);
                s.V.Add(Math.Round(v, 2));
            }
            return s;
        }

        // Three weeks of a greenhouse, one row a minute, written as a CSV file, with two patterns put in:
        //   the soil sensor spikes far more often when the humidity is high at night (condensation), and
        //   the pressure sensor drops out a few minutes after the pump switches on (a voltage dip).
        // Returns the number of spikes and dropouts put in.
        public static int[] GreenhouseCsv(int seed, string path)
        {
            var g = new Gauss(seed);
            int n = 21 * 1440;
            DateTime start = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Local);
            var sb = new System.Text.StringBuilder("time,humidity,temperature,soil,pump,pressure\n");
            int spikes = 0, drops = 0, dropLeft = 0;
            double walkH = 0, walkS = 0;
            for (int i = 0; i < n; i++)
            {
                double hour = (i % 1440) / 60.0;
                walkH = 0.99 * walkH + 0.3 * g.N();
                walkS = 0.995 * walkS + 0.01 * g.N();
                double humidity = 72 + 18 * Math.Cos(2 * Math.PI * (hour - 3) / 24) + walkH + 0.5 * g.N();
                double temperature = 22 - 6 * Math.Cos(2 * Math.PI * (hour - 3) / 24) + 0.1 * g.N();
                double soil = 35 + walkS + 0.05 * g.N();
                bool pump = (hour >= 6 && hour < 6.5) || (hour >= 18 && hour < 18.5);
                double p = (humidity > 86 && hour >= 1 && hour < 6) ? 0.004 : 0.00005;
                if (g.U() < p) { soil += (g.U() < 0.5 ? -1 : 1) * (3 + 3 * g.U()); spikes++; }
                // A dropout of 15 minutes starts 3 to 8 minutes after the pump switches on, six times out of ten.
                int sincePumpOn = (int)Math.Round((hour - (hour >= 18 ? 18 : 6)) * 60);
                if ((hour >= 6 && hour < 6.5) || (hour >= 18 && hour < 18.5))
                    if (sincePumpOn == 3 + (i / 1440) % 6 && g.U() < 0.6) { dropLeft = 15; drops++; }
                string pressure = dropLeft > 0 ? "" : (101.3 + 0.02 * g.N()).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
                if (dropLeft > 0) dropLeft--;
                sb.Append(start.AddMinutes(i).ToString("yyyy-MM-dd HH:mm:ss")).Append(',')
                  .Append(humidity.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(temperature.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(soil.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append(pump ? "1" : "0").Append(',').Append(pressure).Append('\n');
            }
            System.IO.File.WriteAllText(path, sb.ToString());
            return new[] { spikes, drops };
        }

        public static List<Scenario> All(int seed)
        {
            return new List<Scenario> {
                Temperature(seed, false), Temperature(seed + 1, true), Pressure(seed + 2, false), Pressure(seed + 3, true), Machine(seed + 4) };
        }
    }

    public static class Evaluator
    {
        public static List<EventRec> Run(Scenario s, DetectSettings cfg)
        {
            var c = new Collector();
            var d = new Detector(c, cfg) { Lo = s.Lo, Hi = s.Hi };
            for (int i = 0; i < s.T.Count; i++) d.Feed(s.T[i], s.V[i]);
            d.Suspend();
            return c.Events;
        }

        static bool Overlap(long a0, long a1, long b0, long b1, long tol) { return a0 <= b1 + tol && b0 <= a1 + tol; }

        public static List<ScoreRow> Score(Scenario s, List<EventRec> events)
        {
            long tol = Math.Max(3 * s.IntervalMs, 60000);
            var rows = new List<ScoreRow>();
            foreach (string kind in Kinds.All)
            {
                var row = new ScoreRow { Scenario = s.Name, Kind = kind, Days = s.Days };
                foreach (Truth t in s.Truths)
                {
                    if (t.Kind != kind) continue;
                    row.Truths++;
                    long tl = kind == Kinds.Unusual ? 30 * 60000 : tol;
                    foreach (EventRec e in events)
                        if (e.Kind == kind && Overlap(t.T0, t.T1, e.T0, e.T1 ?? e.T0, tl)) { row.Found++; break; }
                }
                foreach (EventRec e in events)
                {
                    if (e.Kind != kind) continue;
                    row.Detections++;
                    bool real = false;
                    foreach (Truth t in s.Truths)
                    {
                        long tl = t.Kind == Kinds.Unusual || kind == Kinds.Unusual ? 30 * 60000 : tol;
                        if (Overlap(t.T0, t.T1, e.T0, e.T1 ?? e.T0, tl)) { real = true; break; }
                    }
                    if (!real) row.FalsePositives++;
                }
                if (row.Truths > 0 || row.Detections > 0) rows.Add(row);
            }
            return rows;
        }
    }
}
