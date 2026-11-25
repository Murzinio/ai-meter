using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// Grok (xAI): the weekly credits of the subscription, the same numbers grok.com's usage page and the Grok CLI
    /// show, from the billing endpoint of Grok Build. The sign-in is that of the Grok CLI on the PC (~\.grok\auth.json);
    /// when it expires the CLI itself renews it, never AI Meter. The answer is a gRPC-Web protobuf message, read with
    /// the small reader below, since the service publishes no JSON API for it.
    /// </summary>
    internal sealed class GrokProvider : Provider
    {
        private const string Endpoint = "https://grok.com/grok_api_v2.GrokBuildBilling/GetGrokCreditsConfig";
        private const string AuthFile = @".grok\auth.json";

        private Usage live;
        private bool needsLogin;
        private DateTime fetchedAt = DateTime.MinValue;

        public override string Id { get { return "grok"; } }

        public override bool Enabled(Settings s) { return s.GrokEnabled; }

        /// <summary>Grok CLI installed or signed in on this PC, on Windows.</summary>
        public static bool Detected()
        {
            return File.Exists(Path.Combine(Probe.Home, AuthFile)) || File.Exists(Path.Combine(Probe.Home, @".grok\bin\grok.exe"));
        }

        public override void InvalidateNetwork() { fetchedAt = DateTime.MinValue; }

        public override ProviderState Collect(Probe p)
        {
            var st = new ProviderState { Id = "grok", Name = "Grok" };
            double interval = p.SavingPower ? 15 : 5;
            if (p.AllowNetwork && (DateTime.Now - fetchedAt).TotalMinutes >= interval && NetworkBackoff.Ready("grok"))
            {
                fetchedAt = DateTime.Now;
                string token = Token();
                if (token == null) needsLogin = true;
                else
                {
                    var fresh = Fetch(token);
                    if (fresh != null) { live = fresh; needsLogin = false; }
                }
            }
            if (live != null) st.Usage = (DateTime.Now - live.At).TotalMinutes > interval + 2 ? live.AsCache() : live;
            if (needsLogin)
            {
                st.NeedsLogin = true;
                st.Notice = L.T("Sign in to the Grok CLI to see your weekly credits");
            }
            return st;
        }

        /// <summary>The session token of the xAI sign-in in the CLI's file (else the first sign-in with a token), or null when there is none.</summary>
        internal static string Token()
        {
            var doc = Json.ParseFile(Path.Combine(Probe.Home, AuthFile));
            if (doc == null) return null;
            string fallback = null;
            foreach (var kv in doc)
            {
                string token = Json.Str(kv.Value as Dictionary<string, object>, "key");
                if (string.IsNullOrEmpty(token)) continue;
                if (kv.Key.StartsWith("https://auth.x.ai", StringComparison.Ordinal)) return token;
                if (fallback == null) fallback = token;
            }
            return fallback;
        }

        /// <summary>The weekly credits answer, or null when the service refuses the CLI's sign-in or the network fails.</summary>
        private Usage Fetch(string token)
        {
            var req = (HttpWebRequest)WebRequest.Create(Endpoint);
            req.Method = "POST";
            // Content-Type, Accept, Referer and User-Agent are restricted headers on .NET Framework: only their properties set them.
            req.ContentType = "application/grpc-web+proto";
            req.Accept = "*/*";
            req.Referer = "https://grok.com/?_s=usage";
            req.UserAgent = "ai-meter/" + Controller.Version;
            req.AllowAutoRedirect = false;   // The bearer token must not follow a redirect to another site.
            req.Headers["Origin"] = "https://grok.com";
            req.Headers["Connect-Protocol-Version"] = "1";
            req.Headers["x-grpc-web"] = "1";
            req.Headers["x-user-agent"] = "connect-es/2.1.1";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            try
            {
                req.Headers["Authorization"] = "Bearer " + token;
                using (var s = req.GetRequestStream()) s.Write(new byte[5], 0, 5);   // An empty protobuf request, framed.
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)resp.StatusCode >= 300) return null;   // A redirect is not an answer, and not a refused sign-in.
                    byte[] body = ReadBody(resp);
                    if (body == null) return null;
                    string status = GrpcStatus(resp.Headers["grpc-status"], body);
                    if (status != null && status != "0")
                    {
                        // gRPC-Web reports a refused sign-in in a successful answer: 16 is unauthenticated, 7 permission denied.
                        if (status == "16" || status == "7") needsLogin = true;
                        return null;
                    }
                    return Parse(body);
                }
            }
            catch (WebException e)
            {
                NetworkBackoff.Note("grok", e);
                using (var hr = e.Response as HttpWebResponse)
                {
                    if (hr != null && (hr.StatusCode == HttpStatusCode.Unauthorized || hr.StatusCode == HttpStatusCode.Forbidden))
                        needsLogin = true;
                }
                return null;
            }
            catch (ArgumentException) { needsLogin = true; return null; }   // A token with control characters is no token.
            catch (IOException) { return null; }
        }

        private const int MaxBody = 1 << 20;

        /// <summary>The response body, or null when it is larger than a credits answer can be.</summary>
        private static byte[] ReadBody(HttpWebResponse resp)
        {
            using (var stream = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[8192];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (ms.Length + n > MaxBody) return null;
                    ms.Write(buffer, 0, n);
                }
                return ms.ToArray();
            }
        }

        /// <summary>
        /// The gRPC status, which the service can put in a response header or in the trailer frame of the body: a frame
        /// with the 0x80 flag whose payload is header lines. Null when neither has one.
        /// </summary>
        internal static string GrpcStatus(string header, byte[] body)
        {
            if (!string.IsNullOrEmpty(header)) return header.Trim();
            int pos = 0;
            while (body != null && body.Length - pos >= 5)
            {
                int length = (body[pos + 1] << 24) | (body[pos + 2] << 16) | (body[pos + 3] << 8) | body[pos + 4];
                if (length < 0 || length > body.Length - pos - 5) return null;
                if ((body[pos] & 0x80) != 0)
                {
                    foreach (var line in Encoding.ASCII.GetString(body, pos + 5, length).Split('\n'))
                    {
                        int colon = line.IndexOf(':');
                        if (colon > 0 && string.Equals(line.Substring(0, colon).Trim(), "grpc-status", StringComparison.OrdinalIgnoreCase))
                        {
                            string value = line.Substring(colon + 1).Trim();
                            return value.Length > 0 ? value : null;
                        }
                    }
                }
                pos += 5 + length;
            }
            return null;
        }

        /// <summary>
        /// A gRPC-Web data frame: one zero byte, a four-byte big-endian payload length, then the protobuf message.
        /// The message is one field: the credits config, where field 1 is the percentage of the weekly pool already
        /// used, fields 4 and 5 the week's start and end (Timestamps: field 1 seconds), and repeated field 7 the
        /// shares of that same pool each Grok product has used. The other fields are skipped, as protobuf requires.
        /// A message that is malformed anywhere, such as a length that overruns or a number cut short, gives null,
        /// never a percentage read from part of it.
        /// </summary>
        internal static Usage Parse(byte[] frame)
        {
            if (frame == null || frame.Length < 5 || frame[0] != 0) return null;   // A frame that is only trailers starts with 0x80.
            int length = (frame[1] << 24) | (frame[2] << 16) | (frame[3] << 8) | frame[4];
            if (length < 0 || length > frame.Length - 5) return null;
            var payload = new byte[length];
            Array.Copy(frame, 5, payload, 0, length);

            DateTime? start = null, end = null;
            var shares = new List<KeyValuePair<string, double>>();
            var outer = new Proto(payload);
            int number, wire;
            double? used = null;
            while (outer.Field(out number, out wire))
            {
                if (number == 1 && wire == 2) used = Config(outer.Message(), ref start, ref end, shares);
                else outer.Skip(wire);
            }
            if (used == null || outer.Broken) return null;   // No credits config, or one cut short: not a credits answer.
            var u = new Usage { Source = "live", At = DateTime.Now };
            u.Windows.Add(new UsageWindow
            {
                Label = "Weekly credits",
                Short = UsageWindow.ShortWeek,
                Used = Math.Max(0, Math.Min(100, used.Value)),
                StartedAt = start,
                ResetsAt = end,
            });
            foreach (var share in shares) u.Breakdown.Add(share);
            return u;
        }

        /// <summary>
        /// Reads the credits config. Its percentage is absent when nothing was used, so a config message without one
        /// means zero, and a percentage that is not a finite number is ignored instead of reaching the bar.
        /// </summary>
        private static double? Config(Proto config, ref DateTime? start, ref DateTime? end, List<KeyValuePair<string, double>> shares)
        {
            double used = 0;
            int number, wire;
            while (config.Field(out number, out wire))
            {
                if (number == 1 && wire == 5)
                {
                    float value;
                    if (config.Float(out value) && !float.IsNaN(value) && !float.IsInfinity(value)) used = value;
                }
                else if (number == 4 && wire == 2) start = Timestamp(config.Message());
                else if (number == 5 && wire == 2) end = Timestamp(config.Message());
                else if (number == 7 && wire == 2) Share(config.Message(), shares);
                else config.Skip(wire);
            }
            return used;
        }

        /// <summary>Which product a productUsage message describes: they are shares of the one weekly pool.</summary>
        private static readonly Dictionary<int, string> Products = new Dictionary<int, string>
        {
            { 2, "Grok Build" }, { 4, "Chat" }, { 7, "App Builder" }, { 8, "Automations" },
        };

        private static void Share(Proto product, List<KeyValuePair<string, double>> shares)
        {
            int? id = null;
            float? used = null;
            int number, wire;
            while (product.Field(out number, out wire))
            {
                if (number == 1 && wire == 0)
                {
                    long v;
                    if (!product.Varint(out v)) return;
                    if (v < int.MinValue || v > int.MaxValue) return;
                    id = (int)v;
                }
                else if (number == 2 && wire == 5)
                {
                    float v;
                    if (product.Float(out v)) used = v;
                }
                else product.Skip(wire);
            }
            string name;
            if (id.HasValue && used.HasValue && used.Value > 0 && !float.IsInfinity(used.Value) && Products.TryGetValue(id.Value, out name))
                shares.Add(new KeyValuePair<string, double>(name, Math.Max(0, Math.Min(100, used.Value))));
        }

        private static DateTime? Timestamp(Proto t)
        {
            long seconds = 0;
            int number, wire;
            while (t.Field(out number, out wire))
            {
                if (number == 1 && wire == 0)
                {
                    long v;
                    if (!t.Varint(out v)) return null;
                    seconds = v;
                }
                else t.Skip(wire);
            }
            // A value past the year 9999, such as a millisecond epoch, is not a time: no date rather than an exception.
            return seconds > 0 && seconds < 253402214400 ? Json.FromUnix(seconds) : (DateTime?)null;
        }

        /// <summary>
        /// A minimal protobuf reader: the base-128 varint, the 32-bit float and length-delimited fields the credits
        /// answer uses, with anything else skipped so that fields added later do not break the reading. Input that
        /// ends inside a field marks the reader, and the message it came from, Broken.
        /// </summary>
        internal sealed class Proto
        {
            private readonly byte[] buf;
            private readonly Proto parent;
            private int pos;

            public Proto(byte[] buf) : this(buf, null) { }

            private Proto(byte[] buf, Proto parent) { this.buf = buf; this.parent = parent; }

            /// <summary>True once any read found the input malformed or cut short, here or in a sub-message.</summary>
            public bool Broken { get; private set; }

            /// <summary>Stops all further reading and lets the enclosing messages know.</summary>
            private void Fail()
            {
                for (var p = this; p != null; p = p.parent) { p.Broken = true; p.pos = p.buf.Length; }
            }

            /// <summary>The field tag at the current position, split into number and wire type; false at the end or on malformed input.</summary>
            public bool Field(out int number, out int wire)
            {
                number = wire = 0;
                if (pos >= buf.Length) return false;   // The clean end of the message.
                long tag;
                if (!Varint(out tag) || tag <= 0 || tag > uint.MaxValue) { Fail(); return false; }
                number = (int)(tag >> 3);
                wire = (int)(tag & 7);
                if (number > 0 && (wire == 0 || wire == 1 || wire == 2 || wire == 5)) return true;   // Wire types 3 and 4 (groups) are deprecated: stop rather than desynchronise.
                Fail();
                return false;
            }

            /// <summary>A base-128 varint at the current position; false at the end or on malformed input.</summary>
            public bool Varint(out long value)
            {
                value = 0;
                for (int shift = 0; shift < 64; shift += 7)
                {
                    if (pos >= buf.Length) { Fail(); return false; }
                    int b = buf[pos++];
                    if (shift == 63 && (b & 0x7e) != 0) { Fail(); return false; }   // The tenth byte may only carry the top bit of a 64-bit value.
                    value |= (long)(b & 0x7f) << shift;
                    if ((b & 0x80) == 0) return true;
                }
                Fail();
                return false;
            }

            /// <summary>An IEEE little-endian 32-bit float at the current position; false when fewer than four bytes remain.</summary>
            public bool Float(out float value)
            {
                value = 0;
                if (pos + 4 > buf.Length) { Fail(); return false; }
                value = BitConverter.ToSingle(buf, pos);
                pos += 4;
                return true;
            }

            /// <summary>The sub-message of a length-delimited field at the current position, read on its own.</summary>
            public Proto Message()
            {
                long length;
                if (!Varint(out length) || length < 0 || length > buf.Length - pos)
                {
                    Fail();   // A length that cannot be right: stop, so the caller reads no further fields.
                    return new Proto(new byte[0]);
                }
                var sub = new byte[length];
                Array.Copy(buf, pos, sub, 0, length);
                pos += (int)length;
                return new Proto(sub, this);
            }

            /// <summary>Steps over the rest of a field this version does not know, of the given wire type.</summary>
            public void Skip(int wire)
            {
                long length;
                if (wire == 0) Varint(out length);
                else if (wire == 1) Advance(8);                                      // A 64-bit field.
                else if (wire == 5) Advance(4);                                      // A 32-bit field.
                else if (wire == 2 && Varint(out length) && length >= 0 && length <= buf.Length - pos) pos += (int)length;
                else Fail();
            }

            private void Advance(int count)
            {
                if (count > buf.Length - pos) Fail();
                else pos += count;
            }
        }
    }
}
