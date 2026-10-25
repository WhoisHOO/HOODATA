// "This PC": CPU, memory, disk, network and battery, read with Windows API calls that do not depend on the display
// language (performance counter names are translated on a Korean Windows).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;

namespace Hoodata
{
    public sealed class PcSource : ISource
    {
        [StructLayout(LayoutKind.Sequential)]
        struct FileTime { public uint Low, High; public ulong Value { get { return ((ulong)High << 32) | Low; } } }

        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus status);

        readonly Engine engine;
        readonly SourceInfo info;
        readonly int intervalMs;
        Thread thread;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        string state = "stopped", error;
        ulong lastIdle, lastBusy;
        long lastRx = -1, lastTx = -1, lastNetT;
        Dictionary<string, ChannelHint> hints;

        public PcSource(Engine engine, SourceInfo info)
        {
            this.engine = engine;
            this.info = info;
            var cfg = Json.Obj(SafeParse(info.Config));
            double s = Json.Num(cfg, "interval", 5);
            intervalMs = (int)(Math.Max(1, Math.Min(3600, s)) * 1000);
        }

        static object SafeParse(string json)
        {
            object o;
            return Json.TryParse(json, out o) ? o : null;
        }

        public string State { get { return state; } }
        public string Error { get { return error; } }
        public bool Live { get { return true; } }

        public void Start()
        {
            hints = new Dictionary<string, ChannelHint> {
                { "cpu", new ChannelHint { Name = engine.Text("Pc_cpu"), Unit = "%" } },
                { "memory", new ChannelHint { Name = engine.Text("Pc_memory"), Unit = "%" } },
                { "disk", new ChannelHint { Name = engine.Text("Pc_disk"), Unit = "%" } },
                { "net_in", new ChannelHint { Name = engine.Text("Pc_net_in"), Unit = "KB/s" } },
                { "net_out", new ChannelHint { Name = engine.Text("Pc_net_out"), Unit = "KB/s" } },
                { "battery", new ChannelHint { Name = engine.Text("Pc_battery"), Unit = "%" } },
                { "plugged", new ChannelHint { Name = engine.Text("Pc_plugged"), Unit = "" } },
            };
            stop.Reset();
            thread = new Thread(Run) { IsBackground = true, Name = "HOODATA pc" };
            thread.Start();
            state = "running";
        }

        public void Stop()
        {
            stop.Set();
            if (thread != null) thread.Join(5000);
            state = "stopped";
        }

        void Run()
        {
            Sample(false);   // the first CPU and network figures need a previous reading
            while (!stop.WaitOne(intervalMs))
            {
                try { Sample(true); error = null; state = "running"; }
                catch (Exception e) { error = e.Message; state = "error"; }
            }
        }

        void Sample(bool send)
        {
            long now = Clock.Now();
            var r = new List<Reading>();

            FileTime idle, kernel, user;
            if (GetSystemTimes(out idle, out kernel, out user))
            {
                ulong busy = kernel.Value + user.Value;   // kernel time includes idle time
                if (lastBusy != 0 && busy > lastBusy)
                {
                    double total = busy - lastBusy, idleD = idle.Value - lastIdle;
                    r.Add(new Reading("cpu", now, Math.Round(Math.Max(0, Math.Min(100, 100 * (1 - idleD / total))), 1)));
                }
                lastBusy = busy;
                lastIdle = idle.Value;
            }

            var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
            if (GlobalMemoryStatusEx(ref mem) && mem.TotalPhys > 0)
                r.Add(new Reading("memory", now, Math.Round(100.0 * (mem.TotalPhys - mem.AvailPhys) / mem.TotalPhys, 1)));

            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                if (drive.IsReady && drive.TotalSize > 0)
                    r.Add(new Reading("disk", now, Math.Round(100.0 * drive.AvailableFreeSpace / drive.TotalSize, 2)));
            }
            catch (IOException) { }

            long rx = 0, tx = 0;
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                try
                {
                    IPv4InterfaceStatistics st = ni.GetIPv4Statistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
                }
                catch (NetworkInformationException) { }
            }
            if (lastRx >= 0 && now > lastNetT && rx >= lastRx && tx >= lastTx)
            {
                double sec = (now - lastNetT) / 1000.0;
                r.Add(new Reading("net_in", now, Math.Round((rx - lastRx) / 1024.0 / sec, 1)));
                r.Add(new Reading("net_out", now, Math.Round((tx - lastTx) / 1024.0 / sec, 1)));
            }
            lastRx = rx;
            lastTx = tx;
            lastNetT = now;

            PowerStatus ps;
            if (GetSystemPowerStatus(out ps))
            {
                // 128 means no battery; 255 means unknown.
                if (ps.BatteryLifePercent <= 100 && (ps.BatteryFlag & 128) == 0) r.Add(new Reading("battery", now, ps.BatteryLifePercent));
                if (ps.ACLineStatus <= 1) r.Add(new Reading("plugged", now, ps.ACLineStatus));
            }

            if (send && r.Count > 0) engine.Submit(info.Id, r, hints);
        }
    }
}
