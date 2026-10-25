// A small HTTP/1.1 server on a TcpListener. HttpListener would need a URL reservation, which takes an administrator.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Hoodata
{
    public sealed class HttpRequest
    {
        public string Method, Path, RawQuery, Version, Remote;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Args = new Dictionary<string, string>(StringComparer.Ordinal);
        public long ContentLength;
        internal Stream Body;
        internal long BodyRead;
        byte[] body;
        public long MaxBody = 4 * 1024 * 1024;

        public string Header(string name)
        {
            string v;
            return Headers.TryGetValue(name, out v) ? v : null;
        }

        public string Arg(string name, string fallback)
        {
            string v;
            return Args.TryGetValue(name, out v) ? v : fallback;
        }

        public long ArgLong(string name, long fallback)
        {
            long v;
            return long.TryParse(Arg(name, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        public string Cookie(string name)
        {
            string all = Header("Cookie");
            if (all == null) return null;
            foreach (string part in all.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq).Trim() == name) return part.Substring(eq + 1).Trim();
            }
            return null;
        }

        // The body, read on first use, up to MaxBody bytes.
        public byte[] BodyBytes()
        {
            if (body != null) return body;
            if (ContentLength > MaxBody) throw new HttpError(413, "too_large");
            body = new byte[ContentLength];
            int off = 0;
            while (off < body.Length)
            {
                int n = Body.Read(body, off, body.Length - off);
                if (n <= 0) throw new IOException("The connection closed during the request body.");
                off += n;
            }
            BodyRead = ContentLength;
            return body;
        }

        public string BodyText() { return Encoding.UTF8.GetString(BodyBytes()); }

        public Dictionary<string, object> BodyJson()
        {
            object o;
            if (!Json.TryParse(BodyText(), out o) || !(o is Dictionary<string, object>)) throw new HttpError(400, "bad_json");
            return (Dictionary<string, object>)o;
        }

        // Copies the body to a stream without holding it in memory, e.g. an uploaded file.
        public void CopyBodyTo(Stream target, Action<long> progress)
        {
            var buf = new byte[81920];
            while (BodyRead < ContentLength)
            {
                int n = Body.Read(buf, 0, (int)Math.Min(buf.Length, ContentLength - BodyRead));
                if (n <= 0) throw new IOException("The connection closed during the upload.");
                target.Write(buf, 0, n);
                BodyRead += n;
                if (progress != null) progress(BodyRead);
            }
        }
    }

    public sealed class HttpResponse
    {
        public int Status = 200;
        public string ContentType = "application/json; charset=utf-8";
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];

        public static HttpResponse Json(string json)
        {
            return new HttpResponse { Body = Encoding.UTF8.GetBytes(json) };
        }

        public static HttpResponse Json(JsonWriter w) { return Json(w.ToString()); }

        public static HttpResponse Text(int status, string contentType, string text)
        {
            return new HttpResponse { Status = status, ContentType = contentType, Body = Encoding.UTF8.GetBytes(text) };
        }

        public static HttpResponse Error(int status, string code)
        {
            var r = Json(new JsonWriter().Obj().Prop("error", code).EndObj());
            r.Status = status;
            return r;
        }

        public static HttpResponse Redirect(string location)
        {
            var r = new HttpResponse { Status = 303, ContentType = "text/plain; charset=utf-8" };
            r.Headers["Location"] = location;
            return r;
        }
    }

    public sealed class HttpError : Exception
    {
        public readonly int Status;
        public readonly string Code;
        public HttpError(int status, string code) : base(code) { Status = status; Code = code; }
    }

    public delegate HttpResponse HttpHandler(HttpRequest request);

    public sealed class HttpServer
    {
        readonly TcpListener listener;
        readonly HttpHandler handler;
        Thread thread;
        volatile bool stopping;
        int open;
        public Action<string> Log;
        const int MaxHeaderBytes = 32 * 1024, MaxConnections = 64;

        public HttpServer(IPAddress address, int port, HttpHandler handler)
        {
            listener = new TcpListener(address, port);
            listener.Server.ExclusiveAddressUse = true;
            this.handler = handler;
        }

        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }

        public void Start()
        {
            listener.Start(64);
            thread = new Thread(Accept) { IsBackground = true, Name = "HOODATA http" };
            thread.Start();
        }

        public void Stop()
        {
            stopping = true;
            try { listener.Stop(); } catch { }
        }

        void Accept()
        {
            while (!stopping)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { if (stopping) return; continue; }
                if (Interlocked.Increment(ref open) > MaxConnections)
                {
                    Interlocked.Decrement(ref open);
                    client.Close();
                    continue;
                }
                ThreadPool.QueueUserWorkItem(Serve, client);
            }
        }

        void Serve(object state)
        {
            var client = (TcpClient)state;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                var stream = client.GetStream();
                // Reads go through a buffer (headers are read a byte at a time); writes go straight to the socket.
                var input = new BufferedStream(stream, 16384);
                string remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                while (!stopping)
                {
                    HttpRequest req;
                    try { req = ReadRequest(input, remote); }
                    catch (HttpError e) { Write(stream, HttpResponse.Error(e.Status, e.Code), false); return; }
                    catch { return; }
                    if (req == null) return;

                    HttpResponse res;
                    try { res = handler(req); }
                    catch (HttpError e) { res = HttpResponse.Error(e.Status, e.Code); }
                    catch (Exception e)
                    {
                        if (Log != null) Log("http " + req.Method + " " + req.Path + ": " + e);
                        res = HttpResponse.Error(500, "internal");
                    }

                    // An unread body makes the connection unusable for the next request.
                    bool keep = req.Version == "HTTP/1.1" && !"close".Equals(req.Header("Connection"), StringComparison.OrdinalIgnoreCase)
                        && req.BodyRead == req.ContentLength;
                    Write(stream, res, keep);
                    if (!keep) return;
                }
            }
            catch { }
            finally
            {
                Interlocked.Decrement(ref open);
                try { client.Close(); } catch { }
            }
        }

        static HttpRequest ReadRequest(Stream stream, string remote)
        {
            var head = new MemoryStream();
            int matched = 0;
            while (matched < 4)
            {
                int b = stream.ReadByte();
                if (b < 0) { if (head.Length == 0) return null; throw new IOException("closed"); }
                head.WriteByte((byte)b);
                if (head.Length > MaxHeaderBytes) throw new HttpError(431, "headers_too_large");
                matched = (b == '\r' && (matched == 0 || matched == 2)) || (b == '\n' && (matched == 1 || matched == 3)) ? matched + 1 : (b == '\r' ? 1 : 0);
            }
            string[] lines = Encoding.UTF8.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length != 3) throw new HttpError(400, "bad_request");
            var req = new HttpRequest { Method = first[0], Version = first[2], Remote = remote };
            string target = first[1];
            int q = target.IndexOf('?');
            req.Path = Uri.UnescapeDataString(q < 0 ? target : target.Substring(0, q));
            req.RawQuery = q < 0 ? "" : target.Substring(q + 1);
            foreach (string pair in req.RawQuery.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string k = eq < 0 ? pair : pair.Substring(0, eq), v = eq < 0 ? "" : pair.Substring(eq + 1);
                req.Args[Unescape(k)] = Unescape(v);
            }
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                req.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            if (req.Header("Transfer-Encoding") != null) throw new HttpError(411, "length_required");
            string len = req.Header("Content-Length");
            if (len != null && (!long.TryParse(len, NumberStyles.None, CultureInfo.InvariantCulture, out req.ContentLength) || req.ContentLength < 0))
                throw new HttpError(400, "bad_length");
            req.Body = stream;
            return req;
        }

        static string Unescape(string s) { return Uri.UnescapeDataString(s.Replace('+', ' ')); }

        static readonly Dictionary<int, string> Reasons = new Dictionary<int, string> {
            { 200, "OK" }, { 204, "No Content" }, { 303, "See Other" }, { 304, "Not Modified" }, { 400, "Bad Request" },
            { 401, "Unauthorized" }, { 403, "Forbidden" }, { 404, "Not Found" }, { 405, "Method Not Allowed" },
            { 409, "Conflict" }, { 411, "Length Required" }, { 413, "Payload Too Large" }, { 421, "Misdirected Request" },
            { 431, "Request Header Fields Too Large" }, { 500, "Internal Server Error" }, { 503, "Service Unavailable" } };

        static void Write(Stream stream, HttpResponse res, bool keep)
        {
            string reason;
            if (!Reasons.TryGetValue(res.Status, out reason)) reason = "Status";
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(res.Status).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Content-Type: ").Append(res.ContentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(res.Body.Length).Append("\r\n");
            sb.Append("Connection: ").Append(keep ? "keep-alive" : "close").Append("\r\n");
            sb.Append("X-Content-Type-Options: nosniff\r\n");
            sb.Append("Referrer-Policy: no-referrer\r\n");
            if (!res.Headers.ContainsKey("Cache-Control")) sb.Append("Cache-Control: no-store\r\n");
            foreach (var h in res.Headers) sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
            sb.Append("\r\n");
            byte[] head = Encoding.UTF8.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
            if (res.Body.Length > 0) stream.Write(res.Body, 0, res.Body.Length);
            stream.Flush();
        }
    }
}
