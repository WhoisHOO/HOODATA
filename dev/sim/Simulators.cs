// Stand-ins for devices this PC does not have, for the tests: a small MQTT 3.1.1 broker, a Modbus TCP server, and a
// Modbus RTU device on an in-memory serial line. Not part of the app.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Hoodata.Sim
{
    public sealed class MqttTestBroker : IDisposable
    {
        readonly TcpListener listener;
        readonly string user, password;
        readonly List<Peer> peers = new List<Peer>();
        readonly Dictionary<string, byte[]> retained = new Dictionary<string, byte[]>();
        public int Port, Pubacks, Pings, Connects, Refused;
        public string LastClientId;
        volatile bool stopping;

        sealed class Peer
        {
            public TcpClient Client;
            public Stream Stream;
            public readonly List<string> Filters = new List<string>();
            public readonly object Lock = new object();
        }

        public MqttTestBroker(string user, string password)
        {
            this.user = user;
            this.password = password;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            new Thread(Accept) { IsBackground = true }.Start();
        }

        public int Subscribed { get { lock (peers) { int n = 0; foreach (Peer p in peers) n += p.Filters.Count; return n; } } }

        void Accept()
        {
            while (!stopping)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); } catch { return; }
                var p = new Peer { Client = c, Stream = c.GetStream() };
                new Thread(() => Serve(p)) { IsBackground = true }.Start();
            }
        }

        void Send(Peer p, byte[] packet) { lock (p.Lock) p.Stream.Write(packet, 0, packet.Length); }

        void Serve(Peer p)
        {
            try
            {
                byte h;
                byte[] body = MqttPacket.Read(p.Stream, out h);
                int i = 0;
                string proto = MqttPacket.ReadString(body, ref i);
                int level = body[i++], flags = body[i++];
                i += 2;   // keep alive
                string clientId = MqttPacket.ReadString(body, ref i);
                string u = (flags & 0x80) != 0 ? MqttPacket.ReadString(body, ref i) : null;
                string pw = (flags & 0x40) != 0 ? MqttPacket.ReadString(body, ref i) : null;
                Interlocked.Increment(ref Connects);
                LastClientId = clientId;
                bool ok = proto == "MQTT" && level == 4 && (user == null || (u == user && pw == password));
                Send(p, new byte[] { MqttPacket.Connack, 2, 0, (byte)(ok ? 0 : 4) });
                if (!ok) { Interlocked.Increment(ref Refused); p.Client.Close(); return; }
                lock (peers) peers.Add(p);
                while (!stopping)
                {
                    body = MqttPacket.Read(p.Stream, out h);
                    if (h == MqttPacket.Subscribe)
                    {
                        int k = 2;
                        var granted = new List<byte>();
                        var added = new List<string>();
                        while (k < body.Length)
                        {
                            string f = MqttPacket.ReadString(body, ref k);
                            int q = body[k++];
                            added.Add(f);
                            granted.Add((byte)Math.Min(q, 1));
                        }
                        var ack = new MemoryStream();
                        ack.WriteByte(body[0]); ack.WriteByte(body[1]);
                        foreach (byte g in granted) ack.WriteByte(g);
                        Send(p, MqttPacket.Build(MqttPacket.Suback, ack.ToArray()));
                        lock (peers) p.Filters.AddRange(added);
                        lock (retained)
                            foreach (var kv in retained)
                                foreach (string f in added) if (Matches(f, kv.Key)) Send(p, PublishPacket(kv.Key, kv.Value, 0, true, 0));
                    }
                    else if (h == MqttPacket.Pingreq) { Interlocked.Increment(ref Pings); Send(p, new byte[] { MqttPacket.Pingresp, 0 }); }
                    else if ((h & 0xF0) == MqttPacket.Puback) Interlocked.Increment(ref Pubacks);
                    else if (h == MqttPacket.Disconnect) break;
                }
            }
            catch { }
            finally
            {
                lock (peers) peers.Remove(p);
                try { p.Client.Close(); } catch { }
            }
        }

        public static bool Matches(string filter, string topic)
        {
            string[] f = filter.Split('/'), t = topic.Split('/');
            for (int i = 0; i < f.Length; i++)
            {
                if (f[i] == "#") return true;
                if (i >= t.Length) return false;
                if (f[i] != "+" && f[i] != t[i]) return false;
            }
            return f.Length == t.Length;
        }

        static byte[] PublishPacket(string topic, byte[] payload, int qos, bool retain, int id)
        {
            var b = new MemoryStream();
            MqttPacket.WriteString(b, topic);
            if (qos > 0) { b.WriteByte((byte)(id >> 8)); b.WriteByte((byte)id); }
            b.Write(payload, 0, payload.Length);
            return MqttPacket.Build((byte)(MqttPacket.Publish | (qos << 1) | (retain ? 1 : 0)), b.ToArray());
        }

        int nextId;

        public void Publish(string topic, string payload, int qos, bool retain)
        {
            byte[] data = Encoding.UTF8.GetBytes(payload);
            if (retain) lock (retained) retained[topic] = data;
            List<Peer> list;
            lock (peers) list = new List<Peer>(peers);
            foreach (Peer p in list)
            {
                bool match = false;
                lock (peers) foreach (string f in p.Filters) if (Matches(f, topic)) match = true;
                if (match) try { Send(p, PublishPacket(topic, data, qos, false, Interlocked.Increment(ref nextId) & 0xFFFF)); } catch { }
            }
        }

        public void Dispose()
        {
            stopping = true;
            listener.Stop();
            lock (peers) foreach (Peer p in peers) try { p.Client.Close(); } catch { }
        }
    }

    // A Modbus device's memory and its answers to functions 1 to 4.
    public sealed class ModbusMemory
    {
        public readonly ushort[] Holding = new ushort[200], Input = new ushort[200];
        public readonly bool[] Coils = new bool[200], Discrete = new bool[200];
        public int Requests;

        public byte[] Answer(byte[] pdu)
        {
            Interlocked.Increment(ref Requests);
            byte fn = pdu[0];
            int addr = (pdu[1] << 8) | pdu[2], qty = (pdu[3] << 8) | pdu[4];
            if (fn < 1 || fn > 4) return new byte[] { (byte)(fn | 0x80), 1 };
            int size = fn == 1 ? Coils.Length : fn == 2 ? Discrete.Length : fn == 3 ? Holding.Length : Input.Length;
            if (qty < 1 || addr + qty > size) return new byte[] { (byte)(fn | 0x80), 2 };
            var o = new MemoryStream();
            o.WriteByte(fn);
            if (fn <= 2)
            {
                bool[] bits = fn == 1 ? Coils : Discrete;
                int n = (qty + 7) / 8;
                o.WriteByte((byte)n);
                for (int b = 0; b < n; b++)
                {
                    int v = 0;
                    for (int k = 0; k < 8 && b * 8 + k < qty; k++) if (bits[addr + b * 8 + k]) v |= 1 << k;
                    o.WriteByte((byte)v);
                }
            }
            else
            {
                ushort[] regs = fn == 3 ? Holding : Input;
                o.WriteByte((byte)(qty * 2));
                for (int k = 0; k < qty; k++) { o.WriteByte((byte)(regs[addr + k] >> 8)); o.WriteByte((byte)regs[addr + k]); }
            }
            return o.ToArray();
        }

        public void SetFloat(int addr, float v, bool swapWords)
        {
            uint u = BitConverter.ToUInt32(BitConverter.GetBytes(v), 0);
            ushort hi = (ushort)(u >> 16), lo = (ushort)u;
            Holding[addr] = swapWords ? lo : hi;
            Holding[addr + 1] = swapWords ? hi : lo;
        }
    }

    public sealed class ModbusTestServer : IDisposable
    {
        readonly TcpListener listener;
        public readonly ModbusMemory Memory = new ModbusMemory();
        public int Port;
        volatile bool stopping;

        public ModbusTestServer()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            new Thread(Accept) { IsBackground = true }.Start();
        }

        void Accept()
        {
            while (!stopping)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); } catch { return; }
                new Thread(() => Serve(c)) { IsBackground = true }.Start();
            }
        }

        void Serve(TcpClient c)
        {
            try
            {
                Stream s = c.GetStream();
                var head = new byte[7];
                while (!stopping)
                {
                    if (!Fill(s, head)) return;
                    int len = (head[4] << 8) | head[5];
                    var pdu = new byte[len - 1];
                    if (!Fill(s, pdu)) return;
                    byte[] ans = Memory.Answer(pdu);
                    var o = new byte[7 + ans.Length];
                    Array.Copy(head, o, 4);
                    o[4] = (byte)((ans.Length + 1) >> 8);
                    o[5] = (byte)(ans.Length + 1);
                    o[6] = head[6];
                    Array.Copy(ans, 0, o, 7, ans.Length);
                    s.Write(o, 0, o.Length);
                }
            }
            catch { }
            finally { c.Close(); }
        }

        static bool Fill(Stream s, byte[] b)
        {
            int off = 0;
            while (off < b.Length)
            {
                int n = s.Read(b, off, b.Length - off);
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }

        public void Dispose() { stopping = true; listener.Stop(); }
    }

    // One end of an in-memory serial line: what one side writes, the other reads.
    public sealed class LineEnd : Stream
    {
        readonly BlockingCollection<byte> input, output;
        public int TimeoutMs = 1000;
        public LineEnd(BlockingCollection<byte> input, BlockingCollection<byte> output) { this.input = input; this.output = output; }

        public static void Pair(out LineEnd a, out LineEnd b)
        {
            var ab = new BlockingCollection<byte>();
            var ba = new BlockingCollection<byte>();
            a = new LineEnd(ba, ab);
            b = new LineEnd(ab, ba);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            byte x;
            if (!input.TryTake(out x, TimeoutMs)) throw new IOException("No answer.");
            buffer[offset] = x;
            int n = 1;
            while (n < count && input.TryTake(out x)) buffer[offset + n++] = x;
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count) { for (int i = 0; i < count; i++) output.Add(buffer[offset + i]); }
        public override void Flush() { }
        public override bool CanRead { get { return true; } }
        public override bool CanWrite { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
        public override void SetLength(long v) { throw new NotSupportedException(); }
    }

    // A Modbus RTU device on a LineEnd: checks each request's CRC and answers with one.
    public sealed class RtuDevice
    {
        public readonly ModbusMemory Memory = new ModbusMemory();
        readonly LineEnd line;
        readonly byte unit;
        public int BadCrc;
        public bool CorruptNext;

        public RtuDevice(LineEnd line, byte unit)
        {
            this.line = line;
            this.unit = unit;
            line.TimeoutMs = 60000;
            new Thread(Run) { IsBackground = true }.Start();
        }

        void Run()
        {
            try
            {
                var req = new byte[8];
                while (true)
                {
                    int off = 0;
                    while (off < 8) off += line.Read(req, off, 8 - off);
                    ushort crc = (ushort)(req[6] | (req[7] << 8));
                    if (crc != Modbus.Crc(req, 6)) { BadCrc++; continue; }
                    if (req[0] != unit) continue;
                    var pdu = new byte[5];
                    Array.Copy(req, 1, pdu, 0, 5);
                    byte[] ans = Memory.Answer(pdu);
                    var frame = new byte[ans.Length + 3];
                    frame[0] = unit;
                    Array.Copy(ans, 0, frame, 1, ans.Length);
                    ushort c = Modbus.Crc(frame, frame.Length - 2);
                    frame[frame.Length - 2] = (byte)c;
                    frame[frame.Length - 1] = (byte)(c >> 8);
                    if (CorruptNext) { frame[frame.Length - 1] ^= 0xFF; CorruptNext = false; }
                    line.Write(frame, 0, frame.Length);
                }
            }
            catch { }
        }
    }
}
