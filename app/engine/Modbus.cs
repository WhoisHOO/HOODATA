// Modbus reading (Modbus Application Protocol V1.1b3; TCP and serial RTU framing), written for this project. A
// register map names each value: where it is (holding/input register, coil, discrete input, address from 0), how it
// is stored (16 or 32 bits, signed, float, word order) and how to scale it. TCP and RTU share the map and decoding.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;

namespace Hoodata
{
    public sealed class ModbusException : Exception
    {
        public readonly int Code;
        public ModbusException(int code) : base("Modbus exception " + code + (code == 2 ? " (illegal address)" : code == 1 ? " (illegal function)" : code == 3 ? " (illegal value)" : code == 4 ? " (device failure)" : "")) { Code = code; }
    }

    public sealed class ModbusItem
    {
        public string Name, Type = "uint16", Unit = "";
        public byte Function = 3;   // 1 coil, 2 discrete input, 3 holding register, 4 input register
        public ushort Address;
        public bool SwapWords;      // 32-bit values with the low word first (CDAB)
        public double Scale = 1, Offset;

        public ushort Count { get { return (ushort)(Function <= 2 ? 1 : Type == "int32" || Type == "uint32" || Type == "float32" ? 2 : 1); } }

        public static List<ModbusItem> FromConfig(Dictionary<string, object> cfg)
        {
            var list = new List<ModbusItem>();
            var regs = Json.Get(cfg, "registers") as List<object>;
            if (regs == null) return list;
            foreach (object o in regs)
            {
                var r = Json.Obj(o);
                if (r == null) continue;
                string fn = Json.Str(r, "fn", "holding");
                var item = new ModbusItem
                {
                    Name = Json.Str(r, "name", "").Trim(),
                    Function = (byte)(fn == "coil" ? 1 : fn == "discrete" ? 2 : fn == "input" ? 4 : 3),
                    Address = (ushort)Math.Max(0, Math.Min(65535, Json.Num(r, "address", 0))),
                    Type = Json.Str(r, "type", "uint16"),
                    SwapWords = Json.Str(r, "order", "big") == "little",
                    Scale = Json.Num(r, "scale", 1),
                    Offset = Json.Num(r, "offset", 0),
                    Unit = Json.Str(r, "unit", "")
                };
                if (item.Name.Length == 0) item.Name = fn + " " + item.Address;
                list.Add(item);
            }
            return list;
        }

        // The value from the data bytes of a response.
        public double Decode(byte[] data)
        {
            if (Function <= 2) return (data[0] & 1) != 0 ? 1 : 0;
            ushort w0 = (ushort)((data[0] << 8) | data[1]);
            double raw;
            if (Count == 1) raw = Type == "int16" ? (short)w0 : (double)w0;
            else
            {
                ushort w1 = (ushort)((data[2] << 8) | data[3]);
                uint u = SwapWords ? ((uint)w1 << 16) | w0 : ((uint)w0 << 16) | w1;
                if (Type == "float32") raw = BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
                else if (Type == "int32") raw = (int)u;
                else raw = u;
            }
            return raw * Scale + Offset;
        }
    }

    public interface IModbusTransport
    {
        // The data bytes of a read (function 1 to 4), after the byte count.
        byte[] Read(byte unit, byte function, ushort address, ushort count);
    }

    public sealed class ModbusTcpTransport : IModbusTransport
    {
        readonly Stream s;
        ushort tid;
        public ModbusTcpTransport(Stream stream) { s = stream; }

        public byte[] Read(byte unit, byte function, ushort address, ushort count)
        {
            tid++;
            var req = new byte[] { (byte)(tid >> 8), (byte)tid, 0, 0, 0, 6, unit, function, (byte)(address >> 8), (byte)address, (byte)(count >> 8), (byte)count };
            s.Write(req, 0, req.Length);
            s.Flush();
            byte[] head = ReadExactly(s, 7);
            int len = (head[4] << 8) | head[5];
            if (len < 2 || len > 260) throw new IOException("Bad Modbus TCP length.");
            byte[] pdu = ReadExactly(s, len - 1);
            if (((head[0] << 8) | head[1]) != tid) throw new IOException("Modbus TCP answer out of order.");
            return Modbus.DataFromPdu(pdu, function);
        }

        static byte[] ReadExactly(Stream s, int n)
        {
            var b = new byte[n];
            int off = 0;
            while (off < n)
            {
                int k = s.Read(b, off, n - off);
                if (k <= 0) throw new IOException("The device closed the connection.");
                off += k;
            }
            return b;
        }
    }

    // RTU over a serial port: the unit, the request and a CRC; the answer ends with a CRC too.
    public sealed class ModbusRtuTransport : IModbusTransport
    {
        readonly Stream s;
        public ModbusRtuTransport(Stream stream) { s = stream; }

        public byte[] Read(byte unit, byte function, ushort address, ushort count)
        {
            var req = new byte[] { unit, function, (byte)(address >> 8), (byte)address, (byte)(count >> 8), (byte)count, 0, 0 };
            ushort crc = Modbus.Crc(req, 6);
            req[6] = (byte)crc;
            req[7] = (byte)(crc >> 8);
            s.Write(req, 0, req.Length);
            s.Flush();
            var head = ReadExactly(3);
            if (head[0] != unit) throw new IOException("An answer from another unit (" + head[0] + ").");
            byte[] frame;
            if ((head[1] & 0x80) != 0) frame = Join(head, ReadExactly(2));
            else frame = Join(head, ReadExactly(head[2] + 2));
            ushort got = (ushort)(frame[frame.Length - 2] | (frame[frame.Length - 1] << 8));
            if (got != Modbus.Crc(frame, frame.Length - 2)) throw new IOException("Modbus RTU CRC error.");
            var pdu = new byte[frame.Length - 3];
            Array.Copy(frame, 1, pdu, 0, pdu.Length);
            return Modbus.DataFromPdu(pdu, function);
        }

        byte[] ReadExactly(int n)
        {
            var b = new byte[n];
            int off = 0;
            while (off < n)
            {
                int k = s.Read(b, off, n - off);   // the port's ReadTimeout ends a missing answer
                if (k <= 0) throw new IOException("No answer.");
                off += k;
            }
            return b;
        }

        static byte[] Join(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }
    }

    public static class Modbus
    {
        // CRC-16/MODBUS: polynomial 0xA001 (reflected 0x8005), start 0xFFFF; sent low byte first.
        public static ushort Crc(byte[] b, int n)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < n; i++)
            {
                crc ^= b[i];
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
            return crc;
        }

        // pdu: function, then byte count and data, or the exception code.
        public static byte[] DataFromPdu(byte[] pdu, byte function)
        {
            if (pdu.Length < 2) throw new IOException("Short Modbus answer.");
            if (pdu[0] == (function | 0x80)) throw new ModbusException(pdu[1]);
            if (pdu[0] != function) throw new IOException("Modbus answer for another function.");
            int n = pdu[1];
            if (pdu.Length < 2 + n) throw new IOException("Short Modbus answer.");
            var data = new byte[n];
            Array.Copy(pdu, 2, data, 0, n);
            return data;
        }

        // Reads every item; items the device refuses are reported in `errors` and skipped.
        public static List<Reading> Poll(IModbusTransport t, byte unit, List<ModbusItem> items, long now, List<string> errors)
        {
            var list = new List<Reading>();
            foreach (ModbusItem item in items)
            {
                try
                {
                    byte[] data = t.Read(unit, item.Function, item.Address, item.Count);
                    double v = item.Decode(data);
                    if (!double.IsNaN(v) && !double.IsInfinity(v)) list.Add(new Reading(item.Name, now, v));
                }
                catch (ModbusException e) { errors.Add(item.Name + ": " + e.Message); }
            }
            return list;
        }

        public static Dictionary<string, ChannelHint> Hints(List<ModbusItem> items)
        {
            var d = new Dictionary<string, ChannelHint>();
            foreach (ModbusItem i in items) d[i.Name] = new ChannelHint { Name = i.Name, Unit = i.Unit };
            return d;
        }
    }

    public sealed class ModbusTcpSource : LoopSource
    {
        TcpClient client;
        public ModbusTcpSource(Engine engine, SourceInfo info) : base(engine, info) { }

        protected override void Interrupt()
        {
            TcpClient c = client;
            if (c != null) c.Close();
        }

        protected override void Run()
        {
            string host = Str("host", "");
            int port = (int)Num("port", 502);
            byte unit = (byte)Num("unit", 1);
            int interval = (int)(Math.Max(0.2, Math.Min(3600, Num("interval", 5))) * 1000);
            List<ModbusItem> items = ModbusItem.FromConfig(cfg);
            if (host.Length == 0 || items.Count == 0) throw new InvalidOperationException("Set the address and at least one register.");
            Dictionary<string, ChannelHint> hints = Modbus.Hints(items);
            client = new TcpClient();
            IAsyncResult ar = client.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(5000)) { client.Close(); throw new IOException("No answer from " + host + ":" + port + "."); }
            client.EndConnect(ar);
            try
            {
                NetworkStream s = client.GetStream();
                s.ReadTimeout = 3000;
                var t = new ModbusTcpTransport(s);
                Connected();
                while (!Stopping)
                {
                    var errors = new List<string>();
                    Send(Modbus.Poll(t, unit, items, Clock.Now(), errors), hints);
                    error = errors.Count > 0 ? string.Join("; ", errors) : null;
                    if (Wait(interval)) break;
                }
            }
            finally { try { client.Close(); } catch { } }
        }
    }
}
