# HOODATA

**Watch every sensor, device and data file in one dashboard, keep a record of noise and anomalies, and find the
situations they happen in.**

Windows 10/11 · nothing to install · everything stays on your PC · [Apache License 2.0](./LICENSE) ·
[한국어](./README.ko.md)

> **In development.** HOODATA has no release yet. The plan and progress are in [docs/ROADMAP.md](docs/ROADMAP.md)
> (Korean); how anomalies are found, and how well, is in [docs/METHODS.md](docs/METHODS.md).

## Try the development version

1. Click **Code → Download ZIP**. Before extracting it, right-click the ZIP, choose **Properties**, tick
   **Unblock** and click **OK** (with Smart App Control on, Windows otherwise blocks `start.bat`).
2. Extract it anywhere and double-click **`start.bat`**. A small HOODATA window opens, and the dashboard opens in
   your browser. This PC's own values (CPU, memory, disk, network, battery) appear within seconds.
3. To look at a data file, open **Data sources** and drop a CSV file there.

HOODATA collects while its window is open. The data stays in the `data` folder next to `start.bat`; delete the
folder to start over.

## What it will do

- **Collect** values from this PC, CSV and Excel files, folders that a data logger writes to, devices that send over
  HTTP or MQTT, Modbus TCP devices, and serial ports (Arduino, RS-485 Modbus RTU).
- **Show** a live dashboard: the current value, a graph and a status for every channel, plus statistics by period.
- **Record** noise and anomalies: gaps, spikes, level changes, stuck values, out-of-range values, rising noise and
  unusual levels for the time of day, each with when, where, how much and why.
- **Find patterns**: which situations (time of day, weekday, the state of other sensors, other anomalies just before)
  make an anomaly more likely than usual, and by how much.

It uses well-known statistical methods implemented in this repository, not AI models, so every finding comes with its
reason. A general engine comes first; versions for particular fields (smart farms, fish farms, factory equipment)
build on it as profiles.

## License

[Apache License 2.0](./LICENSE). See [NOTICE](./NOTICE).
