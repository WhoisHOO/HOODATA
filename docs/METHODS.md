# How HOODATA finds anomalies

HOODATA uses no AI model. Each channel (one sensor, one column of a file) has a detector fed with values in time
order. The detector keeps a few recent statistics and compares each new value with them. These are well-known,
published methods, implemented in this repository ([`app/engine/Detect.cs`](../app/engine/Detect.cs)). Every
anomaly is stored with the numbers behind it: the value, what was expected, and how far apart they were.

The sensitivity setting (low, normal, high) changes only the thresholds in the table below. A new value is judged
against the past; future values are never used, except that a spike is only told apart from a level change once a
few more values have arrived.

## Building blocks

- **Median and MAD.** The centre of recent values is their median. Their spread is the median absolute deviation
  (MAD) times 1.4826, which equals the standard deviation for normally distributed values but is not thrown off by
  the outliers it is meant to find [1][2].
- **Noise.** The jitter of a channel is estimated from successive differences: for independent noise with
  deviation *s*, *x(t) − x(t−1)* has deviation *s*·√2 [3]. The median of the differences, which is the local
  trend, is taken out, and the MAD of the rest gives *s*. A slow trend or a daily cycle therefore does not count
  as noise. When most differences are zero (a coarse sensor), the mean absolute deviation is used instead.
- **Resolution.** The smallest change ever seen is taken as the sensor's resolution. No spread is assumed to be
  smaller than it, so a sensor that reports whole numbers is not alarmed by a change of 1.
- **Usual interval.** The 90th percentile of the last 64 intervals between values (sample quantile, linear
  interpolation [4]). Using a high percentile keeps a source that sends unevenly from looking like one with gaps.
- **Downtime.** The time while HOODATA itself was not running is never counted against a source: the first value
  after a start is not a gap, and the interval across the restart is not learned.

## The anomalies

| Kind | Found when | Low | Normal | High |
|---|---|---|---|---|
| Gap | no value for longer than *k* × the usual interval (at least 10 s) | 10 | 5 | 3 |
| Spike | a value jumps more than *k* robust deviations from the median of the last 24 values, and returns within 5 values | 8 | 6 | 5 |
| Level change | as a spike, but it does not return within 5 values | 8 | 6 | 5 |
| Stuck | a value that usually changes stays exactly equal for longer than *k* × the 99th percentile of its past runs (at least 10 intervals and 1 minute) | 5 | 3 | 2 |
| Out of range | outside the limits set for the channel | – | – | – |
| Noisy | the noise of the last 24 differences is more than *k* times its usual level, three checks in a row | 4 | 3 | 2.5 |
| Unusual | the 5-minute mean is more than *k* robust deviations from the usual for that hour, three blocks (15 minutes) in a row | 5 | 4 | 3.5 |

Details that keep false alarms down:

- **Spikes and level changes must be sudden.** The value must also jump from the value just before it by more than
  *k*/2 times the noise. A slow swing or a morning warm-up moves away from the median without ever jumping, and is
  not reported. Spikes less than 10 intervals (at least a minute) apart are merged into one event with a count.
- **After a level change, the spread is learned again.** A machine that runs is noisier than one that stands still,
  so the old spread does not apply at the new level. For about 16 values the channel shows "learning".
- **Noise is compared at a similar level.** HOODATA keeps a noise estimate every 5 minutes for about 3.5 days,
  each with the level it was taken at. The usual noise is the median of up to 32 estimates taken near the current
  level; with fewer than 16 near ones, noise is not judged. A machine switching on is therefore not "noisy", but
  a machine that is noisier than on other days while it runs is.
- **"Unusual" waits for five earlier days** of the same day type: weekdays and weekends have separate profiles,
  because offices, shops and machines run on a weekly rhythm. The usual range uses the larger of two spreads: the
  spread of the 5-minute means, and the spread between the days' means for that hour. The current day is left out
  of the usual range it is compared with. Holidays are not known.
- **Status.** A channel is *alert* while a gap, stuck value or out-of-range value is open, or for 10 minutes after
  a level change; *warning* while noise or an unusual level is open, or for 10 minutes after a spike.

## How well it works

[`dev/eval/Synthetic.cs`](../dev/eval/Synthetic.cs) makes synthetic data with anomalies put in at known times, and
[`dev/eval/Evaluate-Detectors.ps1`](../dev/eval/Evaluate-Detectors.ps1) measures how many are found (recall) and how
many findings are not real (false positives). The scenarios:

- **temperature**: 14 days, one value a minute, a daily cycle of ±5 °C, a slow wander, noise of 0.08 °C, a sensor
  that reports tenths. In it: 8 spikes of 2–8 °C, 2 level changes of +3 °C for 6 hours, 2 stuck stretches of
  4 hours, 2 gaps of 2 hours, 2 stretches of 8 times the noise for 2 hours, and one night 3 °C warmer than usual,
  eased in over 40 minutes so that it is not a sudden change.
- **pressure**: 1 day, one value a second: 10 spikes, 3 level changes, 2 gaps of 5 minutes, 10 minutes of 5 times
  the noise, 10 minutes stuck.
- **machine**: 7 days of a motor current every 10 seconds, on (12 A, noisy) from 08:00 to 18:00 on weekdays and
  off otherwise, with 6 spikes. Switching on and off are real level changes and count as such.
- **temperature-clean** and **pressure-clean**: the same signals without anomalies, for false alarms.

With the normal setting, on the data the thresholds were tuned on (seed 7), every anomaly put in was found and
there were no false positives. On five other seeds, not used for tuning (185 channel-days in all):

| Setting | Found | False positives | Per channel and day |
|---|---|---|---|
| Low | 260 of 260 | 0 | 0 |
| Normal | 260 of 260 | 10 | 0.05 |
| High | 258 of 260 | 72 | 0.39 |

The 10 false positives at normal: 7 spikes (4 in the machine's running current, 3 in the 1-second pressure), 1 level
change in the machine and 2 unusual nights in the temperature.

What this does not show:

- The data was made by the same author as the detectors, and the thresholds were set on one seed of it. Real
  sensors fail in ways a generator does not imagine. A comparison on public benchmarks with labelled real data is
  planned ([ROADMAP](ROADMAP.md), stage 5); published benchmarks have known flaws of their own [5].
- Rare but normal events, such as a machine's start-up surge, are reported, because they are rare. The "fine" mark
  in the anomaly list is meant for them.
- Only one channel is looked at a time. Relations between channels are the job of the pattern analysis.

## Sources

[1] Hampel, F. R. (1974). The influence curve and its role in robust estimation. *Journal of the American Statistical Association*, 69(346), 383–393. DOI: 10.1080/01621459.1974.10482962 · verified 2026-10-25

[2] Rousseeuw, P. J., & Croux, C. (1993). Alternatives to the median absolute deviation. *Journal of the American Statistical Association*, 88(424), 1273–1283. DOI: 10.1080/01621459.1993.10476408 · verified 2026-10-25

[3] von Neumann, J., Kent, R. H., Bellinson, H. R., & Hart, B. I. (1941). The mean square successive difference. *The Annals of Mathematical Statistics*, 12(2), 153–162. DOI: 10.1214/aoms/1177731746 · verified 2026-10-25

[4] Hyndman, R. J., & Fan, Y. (1996). Sample quantiles in statistical packages. *The American Statistician*, 50(4), 361–365. DOI: 10.1080/00031305.1996.10473566 · verified 2026-10-25

[5] Wu, R., & Keogh, E. J. (2023). Current time series anomaly detection benchmarks are flawed and are creating the illusion of progress. *IEEE Transactions on Knowledge and Data Engineering*, 35(3), 2421–2429. DOI: 10.1109/TKDE.2021.3112126 · verified 2026-10-25
