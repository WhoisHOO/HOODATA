// Serial ports: text lines (an Arduino printing "temp:23.5,hum:61", or any format Payload reads), or Modbus RTU over
// RS-485. The PC HOODATA was written on has no serial port, so this was tested with the parsers and a simulated
// RTU device only (see tests), not with real hardware.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace Hoodata
{
    public sealed class SerialSource : LoopSource
    {
        SerialPort port;
        public SerialSource(Engine engine, SourceInfo info) : base(engine, info) { }

        public static string[] Ports()
        {
            string[] p = SerialPort.GetPortNames();
            Array.Sort(p, StringComparer.OrdinalIgnoreCase);
            return p;
        }

        protected override void Interrupt()
        {
            SerialPort p = port;
            if (p != null) try { p.Close(); } catch { }
        }

        protected override void Run()
        {
            string name = Str("port", "");
            if (name.Length == 0) throw new InvalidOperationException("Choose a serial port.");
            int baud = (int)Num("baud", 9600);
            string parity = Str("parity", "none");
            port = new SerialPort(name, baud,
                parity == "even" ? Parity.Even : parity == "odd" ? Parity.Odd : Parity.None,
                (int)Num("data_bits", 8),
                Num("stop_bits", 1) >= 2 ? StopBits.Two : StopBits.One);
            port.Encoding = Encoding.UTF8;
            port.NewLine = "\n";
            port.ReadTimeout = 1000;
            port.WriteTimeout = 1000;
            port.DtrEnable = true;   // many USB serial adapters (and Arduinos) send only with DTR on
            port.RtsEnable = true;
            port.Open();
            try
            {
                Connected();
                if (Str("mode", "text") == "rtu") RunRtu(baud);
                else RunText();
            }
            finally { try { port.Close(); } catch { } }
        }

        void RunText()
        {
            while (!Stopping)
            {
                string line;
                try { line = port.ReadLine(); }
                catch (TimeoutException) { continue; }
                line = line.Trim();
                if (line.Length == 0) continue;
                Send(Payload.ToReadings(Payload.Parse(line, null), null, Clock.Now()), null);
            }
        }

        void RunRtu(int baud)
        {
            byte unit = (byte)Num("unit", 1);
            int interval = (int)(Math.Max(0.2, Math.Min(3600, Num("interval", 5))) * 1000);
            List<ModbusItem> items = ModbusItem.FromConfig(cfg);
            if (items.Count == 0) throw new InvalidOperationException("Add at least one register.");
            Dictionary<string, ChannelHint> hints = Modbus.Hints(items);
            var t = new ModbusRtuTransport(new PacedStream(port, baud));
            while (!Stopping)
            {
                var errors = new List<string>();
                Send(Modbus.Poll(t, unit, items, Clock.Now(), errors), hints);
                error = errors.Count > 0 ? string.Join("; ", errors) : null;
                if (Wait(interval)) break;
            }
        }

        // Clears stale bytes and keeps the 3.5-character silence RTU needs before each request.
        sealed class PacedStream : Stream
        {
            readonly SerialPort p;
            readonly int silenceMs;
            public PacedStream(SerialPort port, int baud) { p = port; silenceMs = Math.Max(2, (int)Math.Ceiling(3.5 * 11 * 1000.0 / baud)); }
            public override void Write(byte[] buffer, int offset, int count)
            {
                Thread.Sleep(silenceMs);
                p.DiscardInBuffer();
                p.Write(buffer, offset, count);
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                try { return p.Read(buffer, offset, count); }
                catch (TimeoutException) { throw new IOException("No answer from the device."); }
            }
            public override void Flush() { }
            public override bool CanRead { get { return true; } }
            public override bool CanWrite { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
        }
    }
}
