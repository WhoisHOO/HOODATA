# How HOODATA finds anomalies and patterns

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
- **Arrival time.** For a running source, silence is measured on this PC's clock from when new values last
  *arrived*, against the 90th percentile of the last 64 silences between arrivals. Values less than 50 ms apart (one
  message, one poll, a quick burst) are one arrival, and so is a backlog read in parts (a folder's lines written
  while HOODATA was closed: their dates run far ahead of the clock). A stream that keeps coming faster than that for a second,
  dated about as it comes (not a backlog read at once), is silent once it has stopped for the minimum gap, whatever
  outages it had before. A device's own timestamps can lag or come in batches; they are used only for the
  intervals between values. A value sent again with a time already seen does not count as an arrival, so a device
  that keeps repeating its last reading is reported silent. Until the second arrival after a start, HOODATA waits
  ten minutes before calling a source silent, or five times its usual interval when that is longer: a device that
  sends a batch a minute must not look silent after its first batch. When a source's settings are changed, what was
  learned of its rhythm is forgotten, since the new settings may make it send at another rate.
- **Downtime.** The time while HOODATA itself, or the source, was not running is never counted against a source:
  the interval across a restart or a pause is not a gap and is not learned. Silence *after* a start counts from the
  start, folders included, so a sensor that died while the PC was off is reported ten minutes after the start (or
  five times its usual interval, for a logger that writes less often than every two minutes).
- **Values from the future.** A running source's value dated more than ten minutes ahead of this PC's clock is not
  taken (a typo in a logger's line, a device clock or time zone set wrong), so that it cannot make every later value
  look old. The source says so until ten minutes after the last such value.

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
  *k*/2 times the deviation of a step between successive values (√2 times the noise), or the resolution if that is
  larger; *k* here is the setting's own value, not a channel's raised one. A slow swing or a morning warm-up moves
  away from the median without ever jumping, and is not reported. Spikes less than 10 intervals (at least a minute)
  apart are merged into one event with a count.
- **After a level change, the spread is learned again.** A machine that runs is noisier than one that stands still,
  so the old spread does not apply at the new level. For about 16 values the channel shows "learning".
- **Noise is compared at a similar level.** HOODATA keeps a noise estimate every 5 minutes or every 16 values,
  whichever is longer, 1024 of them (about 3.5 days at a value a second, longer for slower sources), each with the
  level it was taken at; after a restart they are rebuilt from the stored minute summaries of the last 3.5 days.
  Noise is judged once 32 estimates exist; the usual noise is the median of up to 32 estimates taken near the
  current level, and with fewer than 16 near ones noise is not judged. A machine switching on is therefore not
  "noisy", but a machine that is noisier than on other days while it runs is.
- **"Unusual" waits for five earlier days** of the same day type: weekdays and weekends have separate profiles,
  because offices, shops and machines run on a weekly rhythm. At start the profiles are rebuilt from five weeks of
  stored summaries. The usual range uses the larger of two spreads: the
  spread of the 5-minute means, and the spread between the days' means for that hour. The current day is left out
  of the usual range it is compared with. Holidays are not known.
- **The daily rhythm is not an anomaly.** Once the usual range for the hour is known, a level change into that range
  (lights on, a machine started on schedule) is not reported. Neither is a spike that comes on time in a regular
  cycle: when the last five intervals between spikes vary by less than 10% and a spike arrives within 15% of when
  the next was due (a compressor, a defrost, an hourly job). A spike off the schedule is still reported.
- **A channel that often spikes raises its own bar.** When five spikes or level changes come within 1000 values,
  the threshold *k* of that channel grows by 15%, up to 2.5 times; after 1000 quiet values it falls back by 10%
  every 200 values.
- **"Unusual" is one episode.** It ends only after half an hour back in range (or, for an hour that has no usual
  range yet, after half an hour that cannot be judged), and if it comes back within two hours the same event is
  reopened.
- **Status.** For a running source, a channel is *alert* while a gap, stuck value or out-of-range value is open,
  and for 10 minutes after any of these or a level change is found or grows; *warning* likewise for spikes, noise
  and unusual levels. A channel of an imported file shows "imported", one of a paused source "paused".

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

This data was made by the same author as the detectors, and the thresholds were set on one seed of it. Real sensors
fail in ways a generator does not imagine, so the detectors were also run on two public benchmarks with labelled
real data, with [`dev/eval/Evaluate-Benchmarks.ps1`](../dev/eval/Evaluate-Benchmarks.ps1) (the data is downloaded,
not kept in this repository), at the normal setting:

- **NAB**, the Numenta Anomaly Benchmark [9] (MIT licence, commit `ea702d7`): 58 series, mostly one value every
  5 minutes, from servers, traffic, ad exchanges, tweets, a machine's temperature and artificial signals, with 116
  labelled anomaly windows. Scored simply: a window is found when an anomaly overlaps it; an anomaly outside every
  window counts as a false positive (NAB's own weighted score is not used).
- **The UCR Time Series Anomaly Archive** [5]: 250 series with one labelled anomaly each after a training part, most
  of them physiological waveforms (ECG, gait, respiration). Scored as the archive's authors propose: a series is
  right when the strongest anomaly found after the training part lies within 100 values of the label.

| Benchmark | Found | False positives |
|---|---|---|
| NAB, first run | 108 of 116 windows (93%) | 3969 in 365 558 values (10.9 per 1000) |
| NAB, after the changes below | 107 of 116 windows (92%) | 2241 (6.1 per 1000) |
| UCR | right in 28 of 250 series (11%); an anomaly near the label in 176 (70%) | median 79 per series after the training part |

The first NAB run showed three ways real data differs from the synthetic data, and the detectors were changed for
them (the "daily rhythm", "raises its own bar" and "one episode" rules above): signals with a strong daily shape gave
a level change at every daily transition, a CPU load with an hourly job gave a spike every hour (1 092 of them in
one series), and a long unusual period was cut into hundreds of short ones. NAB is therefore no longer an unseen
test; UCR was run only after these changes and was not used for any of them. The synthetic results above did not
change.

What these numbers say:

- On NAB most labelled windows are found, but HOODATA also reports a lot outside them: about 1.8 per channel and day
  at 5-minute values. Part of this is data that really is missing (224 gaps, mostly in the traffic series), and NAB's
  labels are known to be incomplete [5]; part is HOODATA reporting rare but normal bursts (tweet volumes, server
  load). Those are what the "fine" mark and the low sensitivity are for.
- On UCR HOODATA is weak, and this is expected: most of its anomalies are a heartbeat or a step of a different
  *shape*, inside a waveform sampled hundreds of times a second. HOODATA looks at single values and levels; it is
  made for sensor readings that change over seconds to hours, not for the shape of a waveform.

Other limits:

- Rare but normal events, such as a machine's start-up surge, are reported, because they are rare.
- Only one channel is looked at a time. Relations between channels are the job of the pattern analysis.

# How HOODATA finds patterns

The pattern analysis ([`app/engine/Patterns.cs`](../app/engine/Patterns.cs)) asks two questions of the anomalies
found so far: in which situations does an anomaly happen more often than usual, and what tends to come shortly
before it. It is association, not cause: two things that happen together may both have a third cause.

**Slots and situations.** The period is cut into slots of 5 minutes (longer when there would be more than 20 000).
Only time with data counts, so the months between an imported file and today's live values are not slots. Each
slot gets situations:

- the time of day, in 4-hour bins;
- the weekday and weekday or weekend, when there are at least two weeks of slots;
- for every channel, *high* or *low*: its 5-minute mean in its own top or bottom 20%;
- for a switch (a pump on or off, a door open or shut), each value instead: a channel with at most four values,
  never anything in between (in no minute is its lowest or highest value another one), that keeps a single value
  through at least half of its minutes. So a switch read many times a minute is still a switch, and a measurement
  read many times a minute is not one, even one that rests at 0 while a machine is off.

A channel's own level is not used for its own level changes, unusual levels or spikes: these move the level
themselves, so the result would only restate them.

**Rates compared.** For each channel and kind with at least 5 anomalies, and each situation, the share of slots in
which such an anomaly started is compared with the share over all the slots that channel has data in. The lift is
the ratio of the two; Fisher's exact test (one-sided, hypergeometric) [6] gives how likely so many would fall in
that situation by chance.

**Two situations together.** The 8 strongest single situations of a target are also tried in pairs, as in subgroup
discovery [7]. A pair is kept only when it is at least 1.2 times as strong as either part and, within each part's
slots, the other part is still significant on its own (p ≤ 0.01). Without that second check, a weekday added to a
real condition picks up a few chance hits and looks like a sharper rule.

**What comes before.** For anomalies, and for switches changing to a value, HOODATA counts how many anomalies B start
within 5 minutes, 30 minutes or 2 hours after an A. By chance, the expected number is B's count times the share of
time covered by those windows; the binomial tail gives the p-value, and the window with the smallest is kept. A
sequence must also account for at least a fifth of B, so that two things that both happen at dawn do not look like
one leading to the other.

**Corrected for testing many.** Every test made, including those dropped along the way, goes into one
Benjamini–Hochberg correction [8]. Shown are results with a false discovery rate of at most 5%, a lift of at least
2 and at least 3 anomalies in the situation.

**Checked on planted patterns.** [`dev/eval/Synthetic.cs`](../dev/eval/Synthetic.cs) writes three weeks of a
greenhouse as a CSV file with two patterns put in: a soil sensor that spikes far more often when the humidity is
high at night, and a pressure sensor that drops out a few minutes after the pump switches on. Imported and analysed
like any file, the analysis reports the pressure gaps while the pump runs (lift 29, all 20 of them) and after the
pump switches on (within 30 minutes: 20 against 0.8 by chance), and the soil spikes when the humidity is high
(lift 4.8, 22 of 23). It also reports the soil spikes when the temperature is low and between 00:00 and 04:00: true
associations in this data, since the humid hours are the cold night hours, but not the cause. Nothing is reported
for the temperature channel, which had nothing put in.

## Sources

[1] Hampel, F. R. (1974). The influence curve and its role in robust estimation. *Journal of the American Statistical Association*, 69(346), 383–393. DOI: 10.1080/01621459.1974.10482962 · verified 2026-10-25

[2] Rousseeuw, P. J., & Croux, C. (1993). Alternatives to the median absolute deviation. *Journal of the American Statistical Association*, 88(424), 1273–1283. DOI: 10.1080/01621459.1993.10476408 · verified 2026-10-25

[3] von Neumann, J., Kent, R. H., Bellinson, H. R., & Hart, B. I. (1941). The mean square successive difference. *The Annals of Mathematical Statistics*, 12(2), 153–162. DOI: 10.1214/aoms/1177731746 · verified 2026-10-25

[4] Hyndman, R. J., & Fan, Y. (1996). Sample quantiles in statistical packages. *The American Statistician*, 50(4), 361–365. DOI: 10.1080/00031305.1996.10473566 · verified 2026-10-25

[5] Wu, R., & Keogh, E. J. (2023). Current time series anomaly detection benchmarks are flawed and are creating the illusion of progress. *IEEE Transactions on Knowledge and Data Engineering*, 35(3), 2421–2429. DOI: 10.1109/TKDE.2021.3112126 · verified 2026-10-25

[6] Fisher, R. A. (1922). On the interpretation of χ2 from contingency tables, and the calculation of P. *Journal of the Royal Statistical Society*, 85(1), 87–94. DOI: 10.2307/2340521 · verified 2026-10-25

[7] Herrera, F., Carmona, C. J., González, P., & del Jesus, M. J. (2011). An overview on subgroup discovery: foundations and applications. *Knowledge and Information Systems*, 29(3), 495–525. DOI: 10.1007/s10115-010-0356-2 · verified 2026-10-25

[8] Benjamini, Y., & Hochberg, Y. (1995). Controlling the false discovery rate: a practical and powerful approach to multiple testing. *Journal of the Royal Statistical Society: Series B (Methodological)*, 57(1), 289–300. DOI: 10.1111/j.2517-6161.1995.tb02031.x · verified 2026-10-25

[9] Lavin, A., & Ahmad, S. (2015). Evaluating real-time anomaly detection algorithms – the Numenta Anomaly Benchmark. *2015 IEEE 14th International Conference on Machine Learning and Applications (ICMLA)*, 38–44. DOI: 10.1109/ICMLA.2015.141 · verified 2026-10-25
