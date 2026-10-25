// Runs the detectors over two public benchmarks with labelled anomalies, unchanged (nothing is tuned on them):
//   NAB, the Numenta Anomaly Benchmark (MIT): 58 series with anomaly windows. A window counts as found when an
//   anomaly overlaps it; anomalies outside every window are false positives.
//   The UCR Time Series Anomaly Archive: 250 series with one anomaly each, after a training part. As in the archive's
//   own scoring, a series counts as right when the strongest anomaly found after the training part lies within 100
//   points of the labelled one.
// The data is downloaded by Evaluate-Benchmarks.ps1 and not kept in the repository.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Hoodata.Eval
{
    public sealed class NabResult
    {
        public string File, Category;
        public int Points, Windows, Found, Events, FalsePositives;
    }

    public sealed class UcrResult
    {
        public string File;
        public int Points, TrainEnd, Begin, End, TestEvents;
        public bool Right, AnyInside;
        public string TopKind;
    }

    public static class Benchmarks
    {
        static long Ms(string s)
        {
            DateTime dt = DateTime.ParseExact(s.Substring(0, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            return Clock.FromUtc(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        }

        // windows: each [start, end] as NAB's text timestamps.
        public static NabResult Nab(string path, string category, List<string[]> windows, DetectSettings cfg)
        {
            var s = new Scenario { Name = Path.GetFileName(path) };
            foreach (string line in File.ReadLines(path))
            {
                int comma = line.IndexOf(',');
                if (comma < 0 || line.StartsWith("timestamp")) continue;
                double v;
                if (!double.TryParse(line.Substring(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                s.T.Add(Ms(line.Substring(0, comma)));
                s.V.Add(v);
            }
            List<EventRec> events = Evaluator.Run(s, cfg);
            var r = new NabResult { File = s.Name, Category = category, Points = s.T.Count, Windows = windows.Count, Events = events.Count };
            var w = new List<long[]>();
            foreach (string[] win in windows) w.Add(new[] { Ms(win[0]), Ms(win[1]) });
            foreach (long[] win in w)
                foreach (EventRec e in events)
                    if (e.T0 <= win[1] && (e.T1 ?? e.T0) >= win[0]) { r.Found++; break; }
            foreach (EventRec e in events)
            {
                bool inside = false;
                foreach (long[] win in w) if (e.T0 <= win[1] && (e.T1 ?? e.T0) >= win[0]) { inside = true; break; }
                if (!inside) r.FalsePositives++;
            }
            return r;
        }

        static readonly Regex UcrName = new Regex(@"_(\d+)_(\d+)_(\d+)\.txt$");

        public static UcrResult Ucr(string path, DetectSettings cfg)
        {
            Match m = UcrName.Match(path);
            if (!m.Success) return null;
            var r = new UcrResult
            {
                File = Path.GetFileName(path),
                TrainEnd = int.Parse(m.Groups[1].Value),
                Begin = int.Parse(m.Groups[2].Value),
                End = int.Parse(m.Groups[3].Value)
            };
            var s = new Scenario { Name = r.File, IntervalMs = 1000 };
            long t0 = Clock.FromUtc(new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));
            int i = 0;
            foreach (string tok in File.ReadAllText(path).Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double v;
                if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                i++;   // the archive counts from 1
                s.T.Add(t0 + i * 1000L);
                s.V.Add(v);
            }
            r.Points = s.T.Count;
            List<EventRec> events = Evaluator.Run(s, cfg);
            EventRec top = null;
            foreach (EventRec e in events)
            {
                long a = (e.T0 - t0) / 1000, b = ((e.T1 ?? e.T0) - t0) / 1000;
                if (a <= r.TrainEnd) continue;
                r.TestEvents++;
                if (a <= r.End + 100 && b >= r.Begin - 100) r.AnyInside = true;
                if (top == null || e.Score.GetValueOrDefault() > top.Score.GetValueOrDefault()) top = e;
            }
            if (top != null)
            {
                long a = (top.T0 - t0) / 1000, b = ((top.T1 ?? top.T0) - t0) / 1000;
                r.Right = a <= r.End + 100 && b >= r.Begin - 100;
                r.TopKind = top.Kind;
            }
            return r;
        }
    }
}
