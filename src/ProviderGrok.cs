using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// Grok (xAI): the weekly credits of the subscription, the same numbers grok.com's usage page and the Grok CLI
    /// show, from the billing endpoint the Grok CLI itself reads. The sign-in is that of the Grok CLI on this Windows PC
    /// (auth.json in GROK_HOME, else ~\.grok); when it expires the CLI itself renews it, never AI Meter.
    /// </summary>
    internal sealed class GrokProvider : Provider
    {
        private const string DefaultEndpoint = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
        private const string Issuer = "https://auth.x.ai";
        private const string LegacyIssuer = "https://accounts.x.ai/sign-in";
        private const string DefaultScope = Issuer + "::b1a00492-073a-47ea-816f-4c329264a828";   // The CLI's own client id (xai-grok-login config.rs).

        private readonly string endpoint;
        private Usage live;
        private bool needsLogin;
        private DateTime fetchedAt = DateTime.MinValue;

        public GrokProvider() : this(DefaultEndpoint) { }

        internal GrokProvider(string endpoint) { this.endpoint = endpoint; }

        /// <summary>The last answer, and whether the Grok CLI has to sign in again: what the card shows.</summary>
        internal Usage Live { get { return live; } }
        internal bool NeedsLogin { get { return needsLogin; } }

        public override string Id { get { return "grok"; } }

        public override bool Enabled(Settings s) { return s.GrokEnabled; }

        /// <summary>The Grok CLI's folder: GROK_HOME when it is set and not empty, as the CLI reads it, else ~\.grok.</summary>
        internal static string GrokHome()
        {
            string home = Environment.GetEnvironmentVariable("GROK_HOME");
            return string.IsNullOrEmpty(home) ? Path.Combine(Probe.Home, ".grok") : home;
        }

        /// <summary>Grok CLI installed or signed in on this PC, on Windows.</summary>
        public static bool Detected()
        {
            string home = GrokHome();
            return File.Exists(Path.Combine(home, "auth.json"))
                || File.Exists(Path.Combine(Probe.Home, @".grok\bin\grok.exe"))
                || File.Exists(Path.Combine(home, "bin", "grok.exe"));
        }

        public override void InvalidateNetwork() { fetchedAt = DateTime.MinValue; }

        public override ProviderState Collect(Probe p)
        {
            var st = new ProviderState { Id = "grok", Name = "Grok" };
            double interval = p.SavingPower ? 15 : 5;
            if (p.AllowNetwork && (DateTime.Now - fetchedAt).TotalMinutes >= interval && NetworkBackoff.Ready("grok"))
            {
                fetchedAt = DateTime.Now;
                Refresh();
            }
            if (live != null) st.Usage = (DateTime.Now - live.At).TotalMinutes > interval + 2 ? live.AsCache() : live;
            if (needsLogin)
            {
                st.NeedsLogin = true;
                st.Notice = L.T("Sign in to the Grok CLI to see your weekly credits");
            }
            return st;
        }

        /// <summary>Reads the sign-in and asks for the credits. Without a usable sign-in no request is made and the card asks to sign in.</summary>
        internal void Refresh()
        {
            string token = Token();
            if (token == null)
            {
                needsLogin = true;
                return;
            }
            var fresh = Fetch(token);
            if (fresh != null)
            {
                live = fresh;
                needsLogin = false;
            }
        }

        /// <summary>
        /// The session token of the xAI sign-in in the CLI's auth.json, or null when there is none that can be used. The
        /// file is keyed "issuer::client id", or by the bare issuer for the legacy sign-in; only the two xAI issuers count,
        /// so no other service's token is ever sent to xAI. As in the CLI, the entry of its own client id comes first, then
        /// any other of the xAI issuer, then the legacy one. Entries of the retired web login (auth_mode web_login, or its
        /// old name grok) are skipped, as the CLI skips them, and so is a token past its expiry: the CLI renews it, not us.
        /// </summary>
        internal static string Token() { return Token(Path.Combine(GrokHome(), "auth.json"), DateTime.UtcNow); }

        internal static string Token(string authFile, DateTime nowUtc)
        {
            var doc = Json.ParseFile(authFile);
            if (doc == null) return null;
            string token = Usable(doc, DefaultScope, nowUtc);
            if (token != null) return token;
            foreach (var key in doc.Keys)
            {
                string issuer = key;
                int scope = issuer.IndexOf("::", StringComparison.Ordinal);
                if (scope >= 0) issuer = issuer.Substring(0, scope);
                if (issuer != Issuer || key == DefaultScope) continue;
                token = Usable(doc, key, nowUtc);
                if (token != null) return token;
            }
            return Usable(doc, LegacyIssuer, nowUtc);
        }

        /// <summary>The token of one entry, or null when it is missing, of the retired web login, or expired.</summary>
        private static string Usable(Dictionary<string, object> doc, string key, DateTime nowUtc)
        {
            object value;
            var entry = doc.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
            if (entry == null) return null;
            string mode = Json.Str(entry, "auth_mode");
            if (mode == "web_login" || mode == "grok") return null;
            string token = Json.Str(entry, "key");
            return string.IsNullOrEmpty(token) || Expired(entry, nowUtc) ? null : token;
        }

        /// <summary>
        /// The CLI writes expires_at as an RFC 3339 timestamp (a chrono DateTime), and when it is absent the token lives
        /// 30 days from create_time. A timestamp that cannot be read counts as not expired: the service then decides.
        /// Both are compared as UTC instants, never through local time.
        /// </summary>
        private static bool Expired(Dictionary<string, object> entry, DateTime nowUtc)
        {
            DateTime expires;
            if (Utc(Json.Str(entry, "expires_at"), out expires)) return expires <= nowUtc;
            DateTime created;
            return Utc(Json.Str(entry, "create_time"), out created) && created.AddDays(30) <= nowUtc;
        }

        private static bool Utc(string text, out DateTime utc)
        {
            DateTimeOffset t;
            if (!string.IsNullOrEmpty(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out t))
            {
                utc = t.UtcDateTime;
                return true;
            }
            utc = DateTime.MinValue;
            return false;
        }

        /// <summary>The weekly credits answer, or null when the service refuses the CLI's sign-in or the network fails.</summary>
        private Usage Fetch(string token)
        {
            var req = (HttpWebRequest)WebRequest.Create(endpoint);
            req.Method = "GET";
            // Accept and User-Agent are restricted headers on .NET Framework: only their properties set them.
            req.Accept = "application/json";
            req.UserAgent = "ai-meter/" + Controller.Version;
            req.AllowAutoRedirect = false;   // The bearer token must not follow a redirect to another site.
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            try
            {
                req.Headers["Authorization"] = "Bearer " + token;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)resp.StatusCode >= 300) return null;   // A redirect is not an answer, and not a refused sign-in.
                    string body = ReadBody(resp);
                    return body == null ? null : Parse(body);
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
            catch (ArgumentException)
            {
                needsLogin = true;   // A token with control characters is no token.
                return null;
            }
            catch (IOException) { return null; }
        }

        private const int MaxBody = 1 << 20;

        /// <summary>The response body, or null when it is larger than a credits answer can be.</summary>
        private static string ReadBody(HttpWebResponse resp)
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
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        /// <summary>
        /// The credits answer: config.creditUsagePercent is the percentage of the weekly pool used, config.currentPeriod
        /// the week (start and end as ISO 8601 with an offset) and config.productUsage the share of that same pool each
        /// Grok product has used. Without a percentage that is a finite number there is no credits answer, and no number
        /// is made up; a missing or unreadable period only leaves the reset out.
        /// </summary>
        internal static Usage Parse(string json)
        {
            var config = Json.Obj(Json.Parse(json), "config");
            double? used = Number(config, "creditUsagePercent");
            if (!used.HasValue || double.IsNaN(used.Value) || double.IsInfinity(used.Value)) return null;
            var period = Json.Obj(config, "currentPeriod");
            var u = new Usage { Source = "live", At = DateTime.Now };
            u.Windows.Add(new UsageWindow
            {
                Label = "Weekly credits",
                Short = UsageWindow.ShortWeek,
                Used = Math.Max(0, Math.Min(100, used.Value)),
                StartedAt = Json.FromIso(Json.Str(period, "start")),
                ResetsAt = Json.FromIso(Json.Str(period, "end")),
            });
            foreach (var product in Json.Items(config, "productUsage"))
            {
                string name;
                double? share = Number(product, "usagePercent");
                if (share.HasValue && share.Value > 0 && !double.IsInfinity(share.Value) && Products.TryGetValue(Json.Str(product, "product") ?? "", out name))
                    u.Breakdown.Add(new KeyValuePair<string, double>(name, Math.Min(100, share.Value)));
            }
            return u;
        }

        /// <summary>A value the service sent as a JSON number: not a boolean, which Json.Num would read as 0 or 1.</summary>
        private static double? Number(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v is bool) return null;
            return Json.Num(d, key);
        }

        /// <summary>The names of the products, which share the one weekly pool.</summary>
        private static readonly Dictionary<string, string> Products = new Dictionary<string, string>
        {
            { "GrokBuild", "Grok Build" }, { "GrokChat", "Chat" }, { "AppBuilder", "App Builder" }, { "Automations", "Automations" },
        };
    }
}
