using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AIMeter
{
    /// <summary>
    /// Grok's weekly credits come from the Grok CLI's JSON billing endpoint with the CLI's own sign-in. The answer must
    /// give a number only when the service sent one, and the sign-in must be an unexpired token of an xAI issuer, found
    /// where the CLI keeps it. Everything runs on fake tokens, in isolated folders, against a loopback server.
    /// </summary>
    internal static class GrokTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("Grok's billing answer gives one weekly window with its reset and the share of each product", FullAnswer);
            test("A Grok answer without a percentage is no answer, and one without a period only has no reset", MissingFields);
            test("A Grok answer that is not JSON or not a credits config gives no answer and never throws", Malformed);
            test("Grok's unknown products and shares that are not positive numbers are left out", Products);
            test("Only a sign-in of an xAI issuer is used, the legacy one too", Issuers);
            test("A Grok token past its expiry is not used, with expires_at or else 30 days after create_time", Expiry);
            test("No Grok sign-in file, or one that is not JSON, gives no token", NoFile);
            test("GROK_HOME moves the Grok CLI's folder, and an empty one does not", GrokHome);
            test("An expired or missing Grok sign-in sends no request and asks to sign in", NoRequest);
            test("A Grok request carries the bearer token only, and the answer is read", Request);
            test("Grok answering 401 or 403 asks to sign in, and a server error does not", Refused);
            test("A Grok redirect is not followed and not an answer", Redirect);
            test("Entries of the retired web login are never used", WebLogin);
            test("The CLI's own sign-in comes before other xAI ones, and the legacy one comes last", Preference);
            test("The Grok CLI is detected by its auth.json or its grok.exe in GROK_HOME", DetectedExe);
            test("A boolean is no percentage, in the credits or in a product", Booleans);
            test("An expires_at that cannot be read falls back to create_time", BadExpiry);
            test("A Grok 429 or a network failure leaves the sign-in prompt as it was", Transient);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private const string Full = "{\"config\":{\"currentPeriod\":{\"type\":\"WEEKLY\",\"start\":\"2026-11-22T07:25:00.723+00:00\",\"end\":\"2026-11-29T07:25:00.723+00:00\"},"
            + "\"creditUsagePercent\":42.5,\"productUsage\":[{\"product\":\"GrokBuild\",\"usagePercent\":40.0},{\"product\":\"GrokChat\",\"usagePercent\":2.5}],"
            + "\"isUnifiedBillingUser\":true,\"prepaidBalance\":{\"val\":0},\"topUpMethod\":\"x\"}}";

        private static readonly DateTime Now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

        private static void FullAnswer()
        {
            var u = GrokProvider.Parse(Full);
            Assert(u != null, "A complete billing answer was not read.");
            Assert(u.Source == "live", "The answer was not marked live.");
            Assert(u.Windows.Count == 1, "There was not exactly one window.");
            var w = u.Windows[0];
            Assert(w.Short == UsageWindow.ShortWeek && w.Label == "Weekly credits", "The window was not the weekly one.");
            Assert(w.Used == 42.5, "The percentage used was " + w.Used);
            Assert(w.StartedAt.HasValue && w.StartedAt.Value.ToUniversalTime() == new DateTime(2026, 11, 22, 7, 25, 0, 723, DateTimeKind.Utc), "The week's start was not read.");
            Assert(w.ResetsAt.HasValue && w.ResetsAt.Value.ToUniversalTime() == new DateTime(2026, 11, 29, 7, 25, 0, 723, DateTimeKind.Utc), "The week's end was not read as the reset.");
            Assert(u.Breakdown.Count == 2, "The product shares were not both kept.");
            Assert(u.Breakdown[0].Key == "Grok Build" && u.Breakdown[0].Value == 40, "The Grok Build share was wrong.");
            Assert(u.Breakdown[1].Key == "Chat" && u.Breakdown[1].Value == 2.5, "The Chat share was wrong.");
            // An offset other than UTC is the same instant.
            var shifted = GrokProvider.Parse("{\"config\":{\"creditUsagePercent\":1,\"currentPeriod\":{\"end\":\"2026-11-29T09:25:00+02:00\"}}}");
            Assert(shifted.Windows[0].ResetsAt.Value.ToUniversalTime() == new DateTime(2026, 11, 29, 7, 25, 0, DateTimeKind.Utc), "An offset was not applied to the reset.");
            // A percentage past the ends is held to them.
            Assert(GrokProvider.Parse("{\"config\":{\"creditUsagePercent\":140}}").Windows[0].Used == 100, "A percentage over 100 reached the bar.");
            Assert(GrokProvider.Parse("{\"config\":{\"creditUsagePercent\":0}}").Windows[0].Used == 0, "A zero percentage was not read as zero.");
        }

        private static void MissingFields()
        {
            Assert(GrokProvider.Parse("{\"config\":{\"currentPeriod\":{\"end\":\"2026-11-29T07:25:00+00:00\"},\"productUsage\":[]}}") == null, "A config without a percentage gave a number.");
            Assert(GrokProvider.Parse("{\"config\":{\"creditUsagePercent\":null}}") == null, "A null percentage gave a number.");
            Assert(GrokProvider.Parse("{\"config\":{\"creditUsagePercent\":\"high\"}}") == null, "A percentage that is text gave a number.");
            var u = GrokProvider.Parse("{\"config\":{\"creditUsagePercent\":12}}");
            Assert(u != null && u.Windows[0].Used == 12, "A percentage without a period was not read.");
            Assert(u.Windows[0].ResetsAt == null && u.Windows[0].StartedAt == null, "A missing period gave dates.");
            var bad = GrokProvider.Parse("{\"config\":{\"creditUsagePercent\":12,\"currentPeriod\":{\"start\":\"soon\",\"end\":7}}}");
            Assert(bad != null && bad.Windows[0].ResetsAt == null && bad.Windows[0].StartedAt == null, "A period that is not a date gave dates.");
        }

        private static void Malformed()
        {
            foreach (var text in new[] { null, "", "not json", "{\"config\":", "[]", "[1,2]", "{}", "{\"config\":null}", "{\"config\":[]}", "{\"config\":\"x\"}", "{\"creditUsagePercent\":5}" })
                Assert(GrokProvider.Parse(text) == null, "A malformed answer gave a number: " + (text ?? "null"));
        }

        private static void Products()
        {
            var u = GrokProvider.Parse("{\"config\":{\"creditUsagePercent\":5,\"productUsage\":[{\"product\":\"Mystery\",\"usagePercent\":9},"
                + "{\"product\":\"GrokBuild\",\"usagePercent\":0},{\"product\":\"GrokChat\",\"usagePercent\":-3},{\"usagePercent\":4},"
                + "{\"product\":\"AppBuilder\",\"usagePercent\":\"many\"},{\"product\":\"Automations\",\"usagePercent\":3},7]}}");
            Assert(u != null && u.Breakdown.Count == 1 && u.Breakdown[0].Key == "Automations" && u.Breakdown[0].Value == 3, "Unknown products or unusable shares were kept.");
            var none = GrokProvider.Parse("{\"config\":{\"creditUsagePercent\":5,\"productUsage\":\"x\"}}");
            Assert(none != null && none.Breakdown.Count == 0, "A product list that is not a list was read.");
        }

        private static string Entry(string key, string expires)
        {
            return "\"" + key + "\":{\"key\":\"fake-token-" + key.Length + "\",\"auth_mode\":\"oidc\"" + (expires == null ? "" : ",\"expires_at\":\"" + expires + "\"") + "}";
        }

        private static string Write(Dir dir, params string[] entries)
        {
            string path = Path.Combine(dir.Path, "auth.json");
            File.WriteAllText(path, "{" + string.Join(",", entries) + "}");
            return path;
        }

        private static void Issuers()
        {
            using (var dir = new Dir())
            {
                string other = Write(dir, Entry("https://auth.example.test::11111111-aaaa", "2030-01-01T00:00:00Z"), Entry("https://auth.x.ai.evil.test::22222222-bbbb", "2030-01-01T00:00:00Z"), Entry("xai::api_key", null));
                Assert(GrokProvider.Token(other, Now) == null, "A token of another issuer was used.");
                string both = Write(dir, Entry("https://auth.example.test::11111111-aaaa", "2030-01-01T00:00:00Z"), Entry("https://auth.x.ai::33333333-cccccccc", "2030-01-01T00:00:00Z"));
                Assert(GrokProvider.Token(both, Now) == "fake-token-" + "https://auth.x.ai::33333333-cccccccc".Length, "The xAI issuer's token was not the one used.");
                string legacy = Write(dir, Entry("https://accounts.x.ai/sign-in", "2030-01-01T00:00:00Z"));
                Assert(GrokProvider.Token(legacy, Now) == "fake-token-" + "https://accounts.x.ai/sign-in".Length, "The legacy sign-in was not accepted.");
                string noKey = Write(dir, "\"https://auth.x.ai::44444444-dddd\":{\"auth_mode\":\"oidc\"}", "\"https://auth.x.ai::55555555-eeee\":7");
                Assert(GrokProvider.Token(noKey, Now) == null, "An entry without a token gave one.");
            }
        }

        private static void Expiry()
        {
            using (var dir = new Dir())
            {
                string key = "https://auth.x.ai::66666666-ffff";
                Assert(GrokProvider.Token(Write(dir, Entry(key, "2026-11-25T11:59:59Z")), Now) == null, "An expired token was used.");
                Assert(GrokProvider.Token(Write(dir, Entry(key, "2026-11-25T12:00:00.000+00:00")), Now) == null, "A token expiring now was used.");
                Assert(GrokProvider.Token(Write(dir, Entry(key, "2026-11-25T13:59:59+02:00")), Now) == null, "An expiry with an offset was read as local time.");
                Assert(GrokProvider.Token(Write(dir, Entry(key, "2026-11-25T12:00:01Z")), Now) != null, "A token still valid was refused.");
                Assert(GrokProvider.Token(Write(dir, Entry(key, "2026-11-25T14:00:01+02:00")), Now) != null, "A valid token with an offset was refused.");
                // An expired entry does not hide a valid one of another sign-in.
                string both = Write(dir, Entry("https://accounts.x.ai/sign-in", "2020-01-01T00:00:00Z"), Entry(key, "2030-01-01T00:00:00Z"));
                Assert(GrokProvider.Token(both, Now) == "fake-token-" + key.Length, "An expired entry hid a valid one.");
                // Without expires_at the CLI counts 30 days from create_time.
                string old = Write(dir, "\"" + key + "\":{\"key\":\"fake-old\",\"create_time\":\"2026-10-25T11:00:00Z\"}");
                Assert(GrokProvider.Token(old, Now) == null, "A token older than 30 days without expires_at was used.");
                string fresh = Write(dir, "\"" + key + "\":{\"key\":\"fake-new\",\"create_time\":\"2026-11-20T11:00:00Z\"}");
                Assert(GrokProvider.Token(fresh, Now) == "fake-new", "A young token without expires_at was refused.");
                string unknown = Write(dir, "\"" + key + "\":{\"key\":\"fake-unknown\"}");
                Assert(GrokProvider.Token(unknown, Now) == "fake-unknown", "A token with no dates at all was refused.");
            }
        }

        private static void NoFile()
        {
            using (var dir = new Dir())
            {
                Assert(GrokProvider.Token(Path.Combine(dir.Path, "auth.json"), Now) == null, "A missing file gave a token.");
                File.WriteAllText(Path.Combine(dir.Path, "auth.json"), "not json");
                Assert(GrokProvider.Token(Path.Combine(dir.Path, "auth.json"), Now) == null, "A file that is not JSON gave a token.");
                File.WriteAllText(Path.Combine(dir.Path, "auth.json"), "");
                Assert(GrokProvider.Token(Path.Combine(dir.Path, "auth.json"), Now) == null, "An empty file gave a token.");
            }
        }

        private static void GrokHome()
        {
            using (var dir = new Dir())
            using (new Home(dir.Path))
            {
                Assert(GrokProvider.GrokHome() == dir.Path, "GROK_HOME was not the CLI's folder.");
                Write(dir, Entry("https://auth.x.ai::77777777-aaaa", "2999-01-01T00:00:00Z"));
                Assert(GrokProvider.Detected(), "The sign-in in GROK_HOME was not detected.");
                Assert(GrokProvider.Token() != null, "The sign-in in GROK_HOME was not read.");
            }
            using (new Home(""))
            {
                Assert(GrokProvider.GrokHome() == Path.Combine(Probe.Home, ".grok"), "An empty GROK_HOME did not fall back to ~\\.grok.");
            }
            using (new Home(null))
            {
                Assert(GrokProvider.GrokHome() == Path.Combine(Probe.Home, ".grok"), "Without GROK_HOME the folder was not ~\\.grok.");
            }
        }

        private static void NoRequest()
        {
            using (var dir = new Dir())
            using (new Home(dir.Path))
            using (var server = new Server(200, Full))
            {
                var provider = new GrokProvider(server.Url);
                provider.Refresh();
                Assert(provider.NeedsLogin && provider.Live == null, "A missing sign-in did not ask to sign in.");
                Write(dir, Entry("https://auth.x.ai::88888888-bbbb", "2020-01-01T00:00:00Z"));
                provider.Refresh();
                Assert(provider.NeedsLogin && provider.Live == null, "An expired sign-in did not ask to sign in.");
                Write(dir, Entry("https://auth.example.test::99999999-cccc", "2999-01-01T00:00:00Z"));
                provider.Refresh();
                Assert(provider.NeedsLogin && provider.Live == null, "A sign-in of another issuer did not ask to sign in.");
                Assert(server.Requests.Count == 0, "A request was sent without a usable sign-in.");
            }
        }

        private static void Request()
        {
            using (var dir = new Dir())
            using (new Home(dir.Path))
            using (var server = new Server(200, Full))
            {
                var provider = new GrokProvider(server.Url);
                provider.Refresh();
                Assert(provider.NeedsLogin, "The prompt was not shown without a sign-in.");
                Write(dir, "\"https://auth.x.ai::12121212-dddd\":{\"key\":\"fake-bearer-token\",\"expires_at\":\"2999-01-01T00:00:00Z\",\"user_id\":\"fake-user\",\"email\":\"nobody@example.test\"}");
                provider.Refresh();
                Assert(server.Requests.Count == 1, "There was not exactly one request.");
                string request = server.Requests[0];
                Assert(request.StartsWith("GET /v1/billing?format=credits ", StringComparison.Ordinal), "The request was not the billing GET: " + request.Split('\r')[0]);
                Assert(request.IndexOf("Authorization: Bearer fake-bearer-token", StringComparison.OrdinalIgnoreCase) >= 0, "The bearer token was not sent.");
                foreach (var header in new[] { "Origin:", "Referer:", "x-user-agent:", "x-grpc-web:", "fake-user", "nobody@example.test" })
                    Assert(request.IndexOf(header, StringComparison.OrdinalIgnoreCase) < 0, "The request carried " + header);
                Assert(!provider.NeedsLogin, "A good answer left the sign-in prompt.");
                Assert(provider.Live != null && provider.Live.Windows[0].Used == 42.5, "The answer was not read.");
            }
        }

        private static void Refused()
        {
            foreach (var status in new[] { 401, 403 })
                using (var dir = new Dir())
                using (new Home(dir.Path))
                using (var server = new Server(status, "{\"error\":\"no\"}"))
                {
                    Write(dir, Entry("https://auth.x.ai::13131313-eeee", "2999-01-01T00:00:00Z"));
                    var provider = new GrokProvider(server.Url);
                    provider.Refresh();
                    Assert(server.Requests.Count == 1, "The request for " + status + " was not sent.");
                    Assert(provider.NeedsLogin && provider.Live == null, "HTTP " + status + " did not ask to sign in.");
                }
            using (var dir = new Dir())
            using (new Home(dir.Path))
            using (var server = new Server(500, "oops"))
            {
                Write(dir, Entry("https://auth.x.ai::14141414-ffff", "2999-01-01T00:00:00Z"));
                var provider = new GrokProvider(server.Url);
                provider.Refresh();
                Assert(!provider.NeedsLogin && provider.Live == null, "A server error was taken for a refused sign-in.");
            }
            using (var dir = new Dir())
            using (new Home(dir.Path))
            using (var server = new Server(200, "{\"config\":{}}"))
            {
                Write(dir, Entry("https://auth.x.ai::15151515-aaaa", "2999-01-01T00:00:00Z"));
                var provider = new GrokProvider(server.Url);
                provider.Refresh();
                Assert(!provider.NeedsLogin && provider.Live == null, "An answer without a percentage gave a number or a prompt.");
            }
        }

        private static void Redirect()
        {
            using (var dir = new Dir())
            using (new Home(dir.Path))
            using (var target = new Server(200, Full))
            using (var server = new Server(302, "", "Location: " + target.Url + "\r\n"))
            {
                Write(dir, Entry("https://auth.x.ai::16161616-bbbb", "2999-01-01T00:00:00Z"));
                var provider = new GrokProvider(server.Url);
                provider.Refresh();
                Assert(target.Requests.Count == 0, "The redirect was followed with the token.");
                Assert(provider.Live == null && !provider.NeedsLogin, "A redirect was taken for an answer or a refused sign-in.");
            }
        }

        private const string DefaultKey = "https://auth.x.ai::b1a00492-073a-47ea-816f-4c329264a828";

        private static string Named(string key, string mode)
        {
            return "\"" + key + "\":{\"key\":\"fake-" + mode + "\",\"auth_mode\":\"" + mode + "\",\"expires_at\":\"2999-01-01T00:00:00Z\"}";
        }

        private static void WebLogin()
        {
            using (var dir = new Dir())
            {
                foreach (var mode in new[] { "web_login", "grok" })
                {
                    Assert(GrokProvider.Token(Write(dir, Named(DefaultKey, mode)), Now) == null, "A " + mode + " entry was used.");
                    Assert(GrokProvider.Token(Write(dir, Named("https://accounts.x.ai/sign-in", mode)), Now) == null, "A legacy " + mode + " entry was used.");
                    // It does not hide a usable entry that follows it.
                    Assert(GrokProvider.Token(Write(dir, Named(DefaultKey, mode), Named("https://auth.x.ai::22222222-bbbb", "oidc")), Now) == "fake-oidc", "A " + mode + " entry hid an oidc one.");
                }
                Assert(GrokProvider.Token(Write(dir, Named(DefaultKey, "api_key")), Now) == "fake-api_key", "An entry of another mode was refused.");
            }
        }

        private static void Preference()
        {
            using (var dir = new Dir())
            {
                string other = Named("https://auth.x.ai::22222222-bbbb", "other");
                string legacy = Named("https://accounts.x.ai/sign-in", "legacy");
                string primary = Named(DefaultKey, "primary");
                Assert(GrokProvider.Token(Write(dir, legacy, other, primary), Now) == "fake-primary", "The CLI's own client id did not come first.");
                Assert(GrokProvider.Token(Write(dir, legacy, other), Now) == "fake-other", "Another xAI client id did not come before the legacy one.");
                Assert(GrokProvider.Token(Write(dir, legacy), Now) == "fake-legacy", "The legacy sign-in was not the last resort.");
                string expired = "\"" + DefaultKey + "\":{\"key\":\"fake-old\",\"auth_mode\":\"oidc\",\"expires_at\":\"2020-01-01T00:00:00Z\"}";
                Assert(GrokProvider.Token(Write(dir, expired, legacy, other), Now) == "fake-other", "An expired primary entry was used or hid the others.");
            }
        }

        private static void DetectedExe()
        {
            using (var dir = new Dir())
            using (new Home(dir.Path))
            {
                Directory.CreateDirectory(Path.Combine(dir.Path, "bin"));
                File.WriteAllText(Path.Combine(dir.Path, "bin", "grok.exe"), "");
                Assert(GrokProvider.Detected(), "A grok.exe in GROK_HOME was not detected.");
            }
        }

        private static void Booleans()
        {
            Assert(GrokProvider.Parse("{\"config\":{\"creditUsagePercent\":true}}") == null, "A boolean percentage gave a number.");
            Assert(GrokProvider.Parse("{\"config\":{\"creditUsagePercent\":false}}") == null, "A false percentage gave a number.");
            var u = GrokProvider.Parse("{\"config\":{\"creditUsagePercent\":5,\"productUsage\":[{\"product\":\"GrokBuild\",\"usagePercent\":true}]}}");
            Assert(u != null && u.Breakdown.Count == 0, "A boolean product share was kept.");
        }

        private static void BadExpiry()
        {
            using (var dir = new Dir())
            {
                string key = "https://auth.x.ai::66666666-ffff";
                string old = Write(dir, "\"" + key + "\":{\"key\":\"fake-old\",\"expires_at\":\"soon\",\"create_time\":\"2026-10-25T11:00:00Z\"}");
                Assert(GrokProvider.Token(old, Now) == null, "An unreadable expires_at hid an old create_time.");
                string fresh = Write(dir, "\"" + key + "\":{\"key\":\"fake-new\",\"expires_at\":\"soon\",\"create_time\":\"2026-11-20T11:00:00Z\"}");
                Assert(GrokProvider.Token(fresh, Now) == "fake-new", "An unreadable expires_at refused a young create_time.");
                string none = Write(dir, "\"" + key + "\":{\"key\":\"fake-none\",\"expires_at\":\"soon\",\"create_time\":\"later\"}");
                Assert(GrokProvider.Token(none, Now) == "fake-none", "Dates that cannot be read refused the token.");
            }
        }

        private static void Transient()
        {
            using (var dir = new Dir())
            using (new Home(dir.Path))
            {
                Write(dir, Entry("https://auth.x.ai::17171717-cccc", "2999-01-01T00:00:00Z"));
                using (var server = new Server(429, "{\"error\":\"slow down\"}"))
                {
                    var provider = new GrokProvider(server.Url);
                    provider.Refresh();
                    Assert(server.Requests.Count == 1 && !provider.NeedsLogin && provider.Live == null, "A 429 changed the prompt or gave an answer.");
                }
                string url;
                using (var server = new Server(200, Full)) url = server.Url;   // Closed again: connection refused.
                var calm = new GrokProvider(url);
                calm.Refresh();
                Assert(!calm.NeedsLogin && calm.Live == null, "A network failure asked to sign in.");
                // Once asked to sign in, only an answer clears it, not a failure.
                var asked = new GrokProvider(url);
                File.Delete(Path.Combine(dir.Path, "auth.json"));
                asked.Refresh();
                Assert(asked.NeedsLogin, "The prompt was not shown without a sign-in.");
                Write(dir, Entry("https://auth.x.ai::17171717-cccc", "2999-01-01T00:00:00Z"));
                asked.Refresh();
                Assert(asked.NeedsLogin && asked.Live == null, "A network failure cleared the sign-in prompt.");
            }
        }

        /// <summary>A folder of its own, removed afterwards.</summary>
        private sealed class Dir : IDisposable
        {
            public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AIMeter-grok-" + Guid.NewGuid().ToString("N"));

            public Dir() { Directory.CreateDirectory(Path); }

            public void Dispose()
            {
                try { Directory.Delete(Path, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>GROK_HOME set to a value for a while, then put back.</summary>
        private sealed class Home : IDisposable
        {
            private readonly string previous = Environment.GetEnvironmentVariable("GROK_HOME");

            public Home(string value) { Environment.SetEnvironmentVariable("GROK_HOME", value); }

            public void Dispose() { Environment.SetEnvironmentVariable("GROK_HOME", previous); }
        }

        /// <summary>A loopback server that answers every request with one status and body, and records the requests.</summary>
        private sealed class Server : IDisposable
        {
            private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly Thread thread;
            public readonly string Url;
            public readonly List<string> Requests = new List<string>();

            public Server(int status, string body) : this(status, body, "") { }

            public Server(int status, string body, string headers)
            {
                listener.Start();
                Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) + "/v1/billing?format=credits";
                thread = new Thread(() =>
                {
                    try
                    {
                        while (true)
                            using (var client = listener.AcceptTcpClient())
                            using (var stream = client.GetStream())
                            {
                                client.ReceiveTimeout = 3000;
                                var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
                                var request = new StringBuilder();
                                string line;
                                while (!string.IsNullOrEmpty(line = reader.ReadLine())) request.Append(line).Append("\r\n");
                                lock (Requests) Requests.Add(request.ToString());
                                var bytes = Encoding.UTF8.GetBytes(body);
                                var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Fixture\r\nContent-Type: application/json\r\n" + headers
                                    + "Content-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                                stream.Write(head, 0, head.Length);
                                stream.Write(bytes, 0, bytes.Length);
                            }
                    }
                    catch (SocketException) { }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                });
                thread.IsBackground = true;
                thread.Start();
            }

            public void Dispose()
            {
                listener.Stop();
                thread.Join(4000);
            }
        }
    }
}
