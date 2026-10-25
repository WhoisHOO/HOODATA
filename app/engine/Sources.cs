// Sources that run in the background and reconnect by themselves, and the HTTP receiver for devices on the network.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Hoodata
{
    // A source with its own thread. Run() works until the connection fails (it throws) or Stop is asked; then the
    // loop waits, 1 second at first and up to a minute, and tries again.
    public abstract class LoopSource : ISource
    {
        protected readonly Engine engine;
        protected readonly SourceInfo info;
        protected readonly Dictionary<string, object> cfg;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        Thread thread;
        protected volatile string state = "stopped", error;

        protected LoopSource(Engine engine, SourceInfo info)
        {
            this.engine = engine;
            this.info = info;
            object o;
            cfg = (Json.TryParse(info.Config ?? "{}", out o) ? Json.Obj(o) : null) ?? new Dictionary<string, object>();
        }

        public string State { get { return state; } }
        public string Error { get { return error; } }
        public virtual bool Live { get { return true; } }
        protected bool Stopping { get { return stop.WaitOne(0); } }
        protected bool Wait(int ms) { return stop.WaitOne(ms); }

        public void Start()
        {
            stop.Reset();
            state = "connecting";
            thread = new Thread(Loop) { IsBackground = true, Name = "HOODATA " + info.Kind + " " + info.Id };
            thread.Start();
        }

        public void Stop()
        {
            stop.Set();
            try { Interrupt(); } catch { }
            if (thread != null) thread.Join(8000);
            state = "stopped";
        }

        // Unblocks Run (closes its socket or port).
        protected virtual void Interrupt() { }

        void Loop()
        {
            int backoff = 1000;
            while (!Stopping)
            {
                try
                {
                    state = "connecting";
                    Run();
                    backoff = 1000;
                }
                catch (Exception e)
                {
                    if (Stopping) break;
                    error = e.Message;
                    state = "error";
                    engine.Say("source " + info.Id + " (" + info.Kind + "): " + e.Message);
                }
                if (Wait(backoff)) break;
                backoff = Math.Min(backoff * 2, 60000);
            }
            state = "stopped";
        }

        protected abstract void Run();

        protected void Connected() { state = "running"; error = null; }

        protected void Send(List<Reading> readings, Dictionary<string, ChannelHint> hints)
        {
            if (readings.Count > 0) engine.Submit(info.Id, readings, hints);
        }

        protected string Str(string key, string fallback) { return Json.Str(cfg, key, fallback); }
        protected double Num(string key, double fallback) { return Json.Num(cfg, key, fallback); }
        protected bool Flag(string key, bool fallback) { return Json.Bool(cfg, key, fallback); }
    }

    public static class Secrets
    {
        // Passwords are kept encrypted for this Windows user (DPAPI), so a copy of the data folder does not give
        // them away.
        public static string Protect(string plain)
        {
            byte[] b = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(b);
        }

        public static string Unprotect(string protectedText)
        {
            if (string.IsNullOrEmpty(protectedText)) return "";
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), null, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException) { return ""; }
            catch (FormatException) { return ""; }
        }

        public static string NewKey(int bytes)
        {
            var b = new byte[bytes];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        public static bool Same(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    public static class Network
    {
        // This PC's IPv4 addresses on networks that are up, for the instructions shown to the user.
        public static List<string> Addresses()
        {
            var list = new List<string>();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !list.Contains(a.Address.ToString())) list.Add(a.Address.ToString());
            }
            return list;
        }
    }

    // Receives values over HTTP, from devices on the network (with "lan") or from programs on this PC:
    //   POST /ingest[/<device>]   with a JSON, name=value, CSV or line-protocol body
    //   GET  /ingest[/<device>]?temp=23.5&hum=61
    // Each request carries the source's key, as ?key=... or in an X-HOODATA-Key header. It has its own port; the
    // dashboard's port never listens beyond this PC.
    public sealed class HttpIngestSource : ISource
    {
        readonly Engine engine;
        readonly SourceInfo info;
        readonly int port;
        readonly bool lan;
        readonly string key;
        HttpServer server;
        string state = "stopped", error;
        public long Requests;

        public HttpIngestSource(Engine engine, SourceInfo info)
        {
            this.engine = engine;
            this.info = info;
            object o;
            var cfg = (Json.TryParse(info.Config ?? "{}", out o) ? Json.Obj(o) : null) ?? new Dictionary<string, object>();
            port = (int)Json.Num(cfg, "port", 47110);
            lan = Json.Bool(cfg, "lan", false);
            key = Json.Str(cfg, "key", "");
        }

        public string State { get { return state; } }
        public string Error { get { return error; } }
        public bool Live { get { return true; } }

        public void Start()
        {
            try
            {
                server = new HttpServer(lan ? IPAddress.Any : IPAddress.Loopback, port, Handle) { Log = engine.Say };
                server.Start();
                state = "running";
                error = null;
            }
            catch (SocketException e)
            {
                state = "error";
                error = "port " + port + ": " + e.Message;
            }
        }

        public void Stop()
        {
            if (server != null) server.Stop();
            state = "stopped";
        }

        HttpResponse Handle(HttpRequest req)
        {
            if (req.Path == "/" && req.Method == "GET")
                return HttpResponse.Text(200, "text/plain; charset=utf-8", "HOODATA: send values to /ingest with this source's key.\n");
            if (req.Path != "/ingest" && !req.Path.StartsWith("/ingest/")) return HttpResponse.Error(404, "not_found");
            string given = req.Header("X-HOODATA-Key") ?? req.Arg("key", null);
            if (key.Length == 0 || !Secrets.Same(given, key)) return HttpResponse.Error(401, "wrong_key");
            string device = req.Path.Length > 8 ? req.Path.Substring(8).Trim('/') : null;
            if (device != null && device.Length == 0) device = null;
            long now = Clock.Now();
            List<ParsedValues> parsed;
            if (req.Method == "GET")
            {
                var pv = new ParsedValues();
                foreach (var kv in req.Args)
                {
                    if (kv.Key == "key") continue;
                    long t;
                    if ((kv.Key == "ts" || kv.Key == "time" || kv.Key == "timestamp") && Payload.TryTime(kv.Value, out t)) { pv.Time = t; continue; }
                    double v;
                    string unit;
                    if (Values.TryNumber(kv.Value, false, out v, out unit)) pv.Values.Add(new KeyValuePair<string, double>(kv.Key, v));
                }
                parsed = new List<ParsedValues> { pv };
            }
            else if (req.Method == "POST" || req.Method == "PUT")
            {
                req.MaxBody = 1024 * 1024;
                parsed = Payload.Parse(req.BodyText(), null);
            }
            else return HttpResponse.Error(405, "method");
            List<Reading> readings = Payload.ToReadings(parsed, device, now);
            if (readings.Count == 0) return HttpResponse.Error(400, "no_values");
            Interlocked.Increment(ref Requests);
            engine.Submit(info.Id, readings, null);
            return HttpResponse.Json("{\"ok\":true,\"values\":" + readings.Count + "}");
        }
    }
}
