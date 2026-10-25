// MQTT 3.1.1 subscriber (OASIS standard), written for this project: connect, subscribe, receive PUBLISH with QoS
// 0 or 1 (2 handled too), keep alive with PINGREQ. Each topic becomes a channel; JSON payloads give one channel per
// field (topic/field). Retained messages are skipped by default: they arrive now but were measured earlier.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;

namespace Hoodata
{
    public static class MqttPacket
    {
        public const byte Connect = 0x10, Connack = 0x20, Publish = 0x30, Puback = 0x40, Pubrec = 0x50, Pubrel = 0x62,
            Pubcomp = 0x70, Subscribe = 0x82, Suback = 0x90, Pingreq = 0xC0, Pingresp = 0xD0, Disconnect = 0xE0;

        public static void WriteLength(Stream s, int n)
        {
            do
            {
                int b = n % 128;
                n /= 128;
                if (n > 0) b |= 0x80;
                s.WriteByte((byte)b);
            } while (n > 0);
        }

        public static void WriteString(Stream s, string text) { WriteBytes(s, Encoding.UTF8.GetBytes(text)); }

        public static void WriteBytes(Stream s, byte[] b)
        {
            s.WriteByte((byte)(b.Length >> 8));
            s.WriteByte((byte)(b.Length & 0xFF));
            s.Write(b, 0, b.Length);
        }

        public static byte[] Build(byte header, byte[] body)
        {
            var ms = new MemoryStream();
            ms.WriteByte(header);
            WriteLength(ms, body.Length);
            ms.Write(body, 0, body.Length);
            return ms.ToArray();
        }

        public static byte[] BuildConnect(string clientId, string user, string password, int keepAlive, bool clean)
        {
            var b = new MemoryStream();
            WriteString(b, "MQTT");
            b.WriteByte(4);   // protocol level 3.1.1
            byte flags = (byte)((clean ? 0x02 : 0) | (string.IsNullOrEmpty(user) ? 0 : 0x80) | (string.IsNullOrEmpty(password) ? 0 : 0x40));
            b.WriteByte(flags);
            b.WriteByte((byte)(keepAlive >> 8));
            b.WriteByte((byte)(keepAlive & 0xFF));
            WriteString(b, clientId);
            if (!string.IsNullOrEmpty(user)) WriteString(b, user);
            if (!string.IsNullOrEmpty(password)) WriteBytes(b, Encoding.UTF8.GetBytes(password));
            return Build(Connect, b.ToArray());
        }

        public static byte[] BuildSubscribe(int packetId, IList<string> topics, int qos)
        {
            var b = new MemoryStream();
            b.WriteByte((byte)(packetId >> 8));
            b.WriteByte((byte)(packetId & 0xFF));
            foreach (string t in topics) { WriteString(b, t); b.WriteByte((byte)qos); }
            return Build(Subscribe, b.ToArray());
        }

        public static byte[] BuildAck(byte type, int packetId) { return new[] { type, (byte)2, (byte)(packetId >> 8), (byte)(packetId & 0xFF) }; }

        static int ReadByteOrThrow(Stream s)
        {
            int b = s.ReadByte();
            if (b < 0) throw new IOException("The broker closed the connection.");
            return b;
        }

        // One packet: its first byte and its body.
        public static byte[] Read(Stream s, out byte header)
        {
            header = (byte)ReadByteOrThrow(s);
            int length = 0, mult = 1;
            for (int i = 0; ; i++)
            {
                if (i == 4) throw new IOException("Bad MQTT length.");
                int b = ReadByteOrThrow(s);
                length += (b & 0x7F) * mult;
                if ((b & 0x80) == 0) break;
                mult *= 128;
            }
            if (length > 16 * 1024 * 1024) throw new IOException("MQTT packet too large.");
            var body = new byte[length];
            int off = 0;
            while (off < length)
            {
                int n = s.Read(body, off, length - off);
                if (n <= 0) throw new IOException("The broker closed the connection.");
                off += n;
            }
            return body;
        }

        public static string ReadString(byte[] b, ref int i)
        {
            int len = (b[i] << 8) | b[i + 1];
            string s = Encoding.UTF8.GetString(b, i + 2, len);
            i += 2 + len;
            return s;
        }
    }

    public sealed class MqttSource : LoopSource
    {
        TcpClient client;
        Stream stream;
        readonly object writeLock = new object();
        public long Messages;

        public MqttSource(Engine engine, SourceInfo info) : base(engine, info) { }

        protected override void Interrupt()
        {
            TcpClient c = client;
            if (c != null) c.Close();
        }

        void Write(byte[] packet)
        {
            lock (writeLock) { stream.Write(packet, 0, packet.Length); stream.Flush(); }
        }

        static readonly string[] ConnackErrors = { "", "unacceptable protocol version", "client id rejected", "server unavailable",
            "bad user name or password", "not authorized" };

        protected override void Run()
        {
            string host = Str("host", "localhost");
            int port = (int)Num("port", Flag("tls", false) ? 8883 : 1883);
            int keepAlive = 60;
            var topics = new List<string>();
            foreach (string t in Str("topics", "#").Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (t.Trim().Length > 0) topics.Add(t.Trim());
            int qos = (int)Math.Max(0, Math.Min(1, Num("qos", 0)));
            bool keepRetained = Flag("retained", false);

            client = new TcpClient();
            IAsyncResult ar = client.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(10000)) { client.Close(); throw new IOException("No answer from " + host + ":" + port + "."); }
            client.EndConnect(ar);
            client.NoDelay = true;
            Stream s = client.GetStream();
            if (Flag("tls", false))
            {
                var ssl = new SslStream(s, false);
                ssl.AuthenticateAsClient(host, null, SslProtocols.None, true);   // the OS picks the protocol; certificates are checked
                s = ssl;
            }
            s.ReadTimeout = keepAlive * 1500;
            stream = s;
            try
            {
                string clientId = "hoodata-" + Secrets.NewKey(6);
                Write(MqttPacket.BuildConnect(clientId, Str("user", ""), Secrets.Unprotect(Str("password_enc", "")), keepAlive, true));
                byte h;
                byte[] body = MqttPacket.Read(s, out h);
                if ((h & 0xF0) != MqttPacket.Connack || body.Length < 2) throw new IOException("The broker did not accept the connection.");
                if (body[1] != 0) throw new IOException("The broker refused: " + (body[1] < ConnackErrors.Length ? ConnackErrors[body[1]] : "code " + body[1]) + ".");
                Write(MqttPacket.BuildSubscribe(1, topics, qos));
                Connected();
                using (new Timer(_ => { try { Write(new byte[] { MqttPacket.Pingreq, 0 }); } catch { } }, null, keepAlive * 500, keepAlive * 500))
                {
                    while (!Stopping)
                    {
                        body = MqttPacket.Read(s, out h);
                        int type = h & 0xF0;
                        if (type == MqttPacket.Publish) OnPublish(h, body, keepRetained);
                        else if (type == (MqttPacket.Pubrel & 0xF0) && body.Length >= 2) Write(MqttPacket.BuildAck(MqttPacket.Pubcomp, (body[0] << 8) | body[1]));
                        else if (type == MqttPacket.Suback)
                        {
                            for (int i = 2; i < body.Length; i++)
                                if (body[i] == 0x80) error = "The broker refused the subscription to " + topics[Math.Min(i - 2, topics.Count - 1)] + ".";
                        }
                    }
                }
                try { Write(new byte[] { MqttPacket.Disconnect, 0 }); } catch { }
            }
            finally
            {
                stream = null;
                try { client.Close(); } catch { }
            }
        }

        void OnPublish(byte h, byte[] body, bool keepRetained)
        {
            int q = (h >> 1) & 3, i = 0;
            bool retained = (h & 1) != 0;
            string topic = MqttPacket.ReadString(body, ref i);
            int packetId = 0;
            if (q > 0) { packetId = (body[i] << 8) | body[i + 1]; i += 2; }
            if (q == 1) Write(MqttPacket.BuildAck(MqttPacket.Puback, packetId));
            if (q == 2) Write(MqttPacket.BuildAck(MqttPacket.Pubrec, packetId));
            if (retained && !keepRetained) return;
            string text = Encoding.UTF8.GetString(body, i, body.Length - i);
            List<Reading> readings = Payload.ToReadings(Payload.Parse(text, null), topic, Clock.Now());
            Interlocked.Increment(ref Messages);
            Send(readings, null);
        }
    }
}
