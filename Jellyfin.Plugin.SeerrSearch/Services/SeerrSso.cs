using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.QuickConnect;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrSearch.Services
{
    /// <summary>
    /// Automatic sign-in to Seerr from a Jellyfin link.
    ///
    /// The problem: a Seerr session is a cookie on Seerr's own domain, and a browser
    /// arriving from Jellyfin has none. Seerr accepts neither a Jellyfin token nor a URL
    /// parameter, and a SameSite cookie can only be set by a top-level navigation — not by
    /// a fetch, not by an iframe.
    ///
    /// The solution, in both modes: the authenticated browser asks for a one-time ticket,
    /// then navigates to /SeerrSearch/sso?t=ticket. The plugin runs Quick Connect on the
    /// server side — initiates it at Seerr, authorizes the code for that user through
    /// IQuickConnect, completes it at Seerr —, receives Seerr's session cookie, sets it in
    /// its own response and redirects to Seerr, which recognizes the session. No password.
    ///
    /// Where the cookie is set depends on the mode:
    ///   - Relay mode (preferred): the proxy in front of Seerr forwards
    ///     seerr.example.com/SeerrSearch/sso to the Jellyfin server. The cookie is then set by
    ///     Seerr's own host, identical to the one Seerr would set — nothing to tell apart,
    ///     nothing left behind after a sign-out. Detected automatically:
    ///     seerr.example.com/SeerrSearch/ping answers.
    ///   - Domain mode: Jellyfin and Seerr share a parent domain (jellyfin.example.com and
    ///     seerr.example.com); the cookie is set by Jellyfin with Domain=example.com, which
    ///     the browser also sends to Seerr.
    ///   - Same-host mode: Jellyfin and Seerr are on the same host and differ only by port
    ///     (http://192.168.1.10:8096 and http://192.168.1.10:5055, or a name on the LAN). Cookies
    ///     ignore the port, so a host-only cookie set by Jellyfin (no Domain attribute) is also
    ///     sent to Seerr. This works with a bare IP address, which a Domain attribute cannot.
    /// </summary>
    public class SeerrSso
    {
        private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(90);
        private static readonly Regex SafePath = new(@"^/[A-Za-z0-9/_\-?=&.%~]*$", RegexOptions.Compiled);

        public const string PingBody = "seerr-search-ok";

        private static readonly TimeSpan ProbeTtl = TimeSpan.FromMinutes(10);

        private readonly SeerrClient _seerr;
        private readonly IQuickConnect _quickConnect;
        private readonly System.Net.Http.IHttpClientFactory _httpClientFactory;
        private readonly ILogger<SeerrSso> _logger;
        private readonly ConcurrentDictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);

        private bool _proxyMode;
        private bool _frameBlocked;
        private string _probeSummary = string.Empty;
        private int _probing;
        private DateTime _probedUtc = DateTime.MinValue;

        public SeerrSso(
            SeerrClient seerr,
            IQuickConnect quickConnect,
            System.Net.Http.IHttpClientFactory httpClientFactory,
            ILogger<SeerrSso> logger)
        {
            _seerr = seerr;
            _quickConnect = quickConnect;
            _httpClientFactory = httpClientFactory;
            _logger = logger;

            if (Plugin.Instance != null)
            {
                Plugin.Instance.ConfigurationChanged += (_, _) =>
                {
                    _proxyMode = false;
                    _frameBlocked = false;
                    _probeSummary = string.Empty;
                    _probedUtc = DateTime.MinValue;
                    StartProbe();
                };
            }

            // Warm the answer up so that the first user does not see the default.
            StartProbe();
        }

        public static string PublicSeerrHostName => PublicSeerrHost;

        public static string PublicJellyfinHostName => PublicJellyfinHost;

        private static bool AssumeRelay => Plugin.Instance?.Configuration.AssumeRelay == true;

        /// <summary>
        /// True when Seerr's public host forwards /SeerrSearch/ to this server (or the administrator says so). The answer
        /// comes from a probe that runs in the background (at start, after a configuration change and every ten minutes):
        /// a request never waits for it, even if the public host is slow or unreachable from the server.
        /// </summary>
        public bool ProxyMode
        {
            get
            {
                if (AssumeRelay)
                {
                    return !string.IsNullOrEmpty(PublicSeerrHost);
                }

                RefreshIfStale();
                return _proxyMode;
            }
        }

        /// <summary>
        /// True when the reverse proxy in front of Seerr sends a header that forbids showing Seerr inside a frame of
        /// another origin (X-Frame-Options, or a CSP frame-ancestors that excludes Jellyfin): the client then opens Seerr
        /// in a tab instead of a blank frame. False when unknown.
        /// </summary>
        public bool FrameBlocked
        {
            get
            {
                RefreshIfStale();
                return _frameBlocked;
            }
        }

        /// <summary>What the last probe of Seerr's public address saw: "present", "absent (404)", "unreachable (dns)"…, empty if none yet.</summary>
        public string ProbeSummary => _probeSummary;

        private void RefreshIfStale()
        {
            if (DateTime.UtcNow - _probedUtc >= ProbeTtl)
            {
                StartProbe();
            }
        }

        private void StartProbe()
        {
            if (Interlocked.Exchange(ref _probing, 1) == 1)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await ProbeNowAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Never let a background probe fault: the answer simply stays as it was.
                }
                finally
                {
                    Volatile.Write(ref _probing, 0);
                }
            });
        }

        /// <summary>Probes now and stores the answer, unless the public URL changed in the meantime (then the next call probes again).</summary>
        public async Task ProbeNowAsync()
        {
            var url = UrlUtil.Normalize(Plugin.Instance?.Configuration.SeerrPublicUrl);
            var result = await ProbeAsync(url).ConfigureAwait(false);

            if (url == UrlUtil.Normalize(Plugin.Instance?.Configuration.SeerrPublicUrl))
            {
                _proxyMode = result.Relay;
                _frameBlocked = result.FrameBlocked;
                _probeSummary = result.Summary;
                _probedUtc = DateTime.UtcNow;
            }
        }

        private sealed record ProbeResult(bool Relay, bool FrameBlocked, string Summary);

        private async Task<ProbeResult> ProbeAsync(string publicUrl)
        {
            if (string.IsNullOrEmpty(publicUrl))
            {
                return new ProbeResult(false, false, string.Empty);
            }

            var relay = false;
            var summary = "absent";

            try
            {
                var client = _httpClientFactory.CreateClient(SeerrClient.NamedClient);
                client.Timeout = TimeSpan.FromSeconds(8);

                // Headers first, then only the first bytes: Seerr's 404 page is large and must not make the probe fail.
                using var ping = await client.GetAsync(publicUrl + "/" + Plugin.PluginRoute + "/ping", System.Net.Http.HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                var body = string.Empty;
                if (ping.IsSuccessStatusCode)
                {
                    using var stream = await ping.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    var buffer = new byte[64];
                    var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                    body = System.Text.Encoding.UTF8.GetString(buffer, 0, read).Trim();
                }

                relay = body == PingBody;
                summary = relay ? "present" : "absent (" + (int)ping.StatusCode + ")";
                _logger.LogInformation("Seerr Search: /SeerrSearch/ relay on Seerr's host is {State}.", relay ? "present" : "absent");
            }
            catch (TaskCanceledException)
            {
                summary = "unreachable (timeout)";
                _logger.LogInformation("Seerr Search: could not probe {Host}: timeout.", UrlUtil.HostPort(publicUrl));
                return new ProbeResult(false, false, summary);
            }
            catch (Exception ex)
            {
                var kind = ex is System.Net.Http.HttpRequestException ? SeerrClient.Classify(ex) : "network";
                summary = "unreachable (" + kind + ")";
                _logger.LogInformation("Seerr Search: could not probe {Host}: {Kind}.", UrlUtil.HostPort(publicUrl), kind);
                return new ProbeResult(false, false, summary);
            }

            // Does the front of Seerr forbid being framed? Only a header that is actually seen counts as a "yes".
            var blocked = false;
            try
            {
                var client = _httpClientFactory.CreateClient(SeerrClient.NamedClient);
                client.Timeout = TimeSpan.FromSeconds(8);
                using var page = await client.GetAsync(publicUrl + "/login", System.Net.Http.HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                blocked = ForbidsFraming(page.Headers, page.Content.Headers, UrlUtil.Normalize(Plugin.Instance?.Configuration.JellyfinPublicUrl));
            }
            catch (Exception)
            {
                // Unknown: keep today's behavior (frame).
            }

            return new ProbeResult(relay, blocked, summary);
        }

        private static bool ForbidsFraming(
            System.Net.Http.Headers.HttpResponseHeaders headers,
            System.Net.Http.Headers.HttpContentHeaders contentHeaders,
            string jellyfinOrigin)
        {
            if (headers.Contains("X-Frame-Options") || contentHeaders.Contains("X-Frame-Options"))
            {
                return true;
            }

            if (!headers.TryGetValues("Content-Security-Policy", out var policies))
            {
                return false;
            }

            foreach (var policy in policies)
            {
                foreach (var directive in policy.Split(';'))
                {
                    var parts = directive.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0 || !string.Equals(parts[0], "frame-ancestors", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var allowed = parts.Skip(1).Any(p =>
                        p == "*"
                        || (jellyfinOrigin.Length > 0 && string.Equals(p.TrimEnd('/'), jellyfinOrigin, StringComparison.OrdinalIgnoreCase)));

                    if (!allowed)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public sealed record Ticket(Guid UserId, string To, DateTime ExpiresUtc);

        public sealed record SignInResult(bool Ok, string? CookieHeader, string? Error);

        private static string PublicJellyfinHost => HostOf(Plugin.Instance?.Configuration.JellyfinPublicUrl);

        private static string PublicSeerrHost => HostOf(Plugin.Instance?.Configuration.SeerrPublicUrl);

        /// <summary>True when both public addresses are on the same host (they only differ by port or path).</summary>
        public static bool SameHost =>
            !string.IsNullOrEmpty(PublicJellyfinHost)
            && string.Equals(PublicJellyfinHost, PublicSeerrHost, StringComparison.OrdinalIgnoreCase);

        /// <summary>Parent domain shared by two different public hosts, or empty when there is none (or an address is a bare IP).</summary>
        public static string CookieDomain
        {
            get
            {
                var a = PublicJellyfinHost;
                var b = PublicSeerrHost;

                if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || LooksLikeIp(a) || LooksLikeIp(b))
                {
                    return string.Empty;
                }

                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                var la = a.Split('.').Reverse().ToArray();
                var lb = b.Split('.').Reverse().ToArray();
                var common = la.Zip(lb).TakeWhile(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase)).Count();

                // At least "domain.tld" in common; a single label (e.g. "com") is not enough.
                return common >= 2 ? string.Join('.', la.Take(common).Reverse()) : string.Empty;
            }
        }

        public bool IsReady =>
            SeerrClient.IsConfigured
            && _quickConnect.IsEnabled
            && !string.IsNullOrEmpty(PublicSeerrHost)
            && (ProxyMode || (!string.IsNullOrEmpty(PublicJellyfinHost) && (SameHost || !string.IsNullOrEmpty(CookieDomain))));

        /// <summary>"proxy" (relay on Seerr's host), "samehost", "domain", or "off".</summary>
        public string Mode => !IsReady ? "off" : ProxyMode ? "proxy" : SameHost ? "samehost" : "domain";

        /// <summary>
        /// Why automatic sign-in is not available, as a stable code (translated by the
        /// configuration page), or null when it is ready.
        /// </summary>
        public string? WhyNotReady()
        {
            if (!SeerrClient.IsConfigured)
            {
                return "seerr-not-configured";
            }

            if (string.IsNullOrEmpty(PublicSeerrHost))
            {
                return "public-url-missing";
            }

            if (!_quickConnect.IsEnabled)
            {
                return "quickconnect-disabled";
            }

            if (ProxyMode)
            {
                return null;
            }

            if (string.IsNullOrEmpty(PublicJellyfinHost))
            {
                return "jellyfin-url-missing";
            }

            if (!SameHost && string.IsNullOrEmpty(CookieDomain))
            {
                return "no-common-domain";
            }

            return null;
        }

        /// <summary>Creates a one-time ticket and returns the starting URL, or null when automatic sign-in is not ready.</summary>
        public string? IssueTicket(Guid userId, string? to)
        {
            if (!IsReady)
            {
                return null;
            }

            var target = string.IsNullOrWhiteSpace(to) || !SafePath.IsMatch(to) || to.StartsWith("//", StringComparison.Ordinal)
                ? "/"
                : to;

            Purge();

            var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

            _tickets[id] = new Ticket(userId, target, DateTime.UtcNow + TicketLifetime);

            // In relay mode the ticket is redeemed on Seerr's host; otherwise on Jellyfin's.
            var config = Plugin.Instance!.Configuration;
            var origin = UrlUtil.Normalize(ProxyMode ? config.SeerrPublicUrl : config.JellyfinPublicUrl);
            return origin + "/" + Plugin.PluginRoute + "/sso?t=" + id;
        }

        public bool TryConsume(string? id, out Ticket ticket)
        {
            ticket = default!;

            if (string.IsNullOrEmpty(id) || !_tickets.TryRemove(id, out var found))
            {
                return false;
            }

            if (found.ExpiresUtc < DateTime.UtcNow)
            {
                return false;
            }

            ticket = found;
            return true;
        }

        public bool Peek(string? id) =>
            !string.IsNullOrEmpty(id) && _tickets.TryGetValue(id, out var t) && t.ExpiresUtc >= DateTime.UtcNow;

        /// <summary>The whole Quick Connect flow, on the server side, for this user.</summary>
        public async Task<SignInResult> SignInAsync(Guid userId, CancellationToken ct)
        {
            var initiate = await _seerr.PostAnonymousAsync("/auth/jellyfin/quickconnect/initiate", null, ct).ConfigureAwait(false);
            if (!initiate.IsSuccess)
            {
                return Fail("Seerr refused to start Quick Connect (" + (int)initiate.StatusCode + ").");
            }

            var node = initiate.AsNode();
            var code = node?["code"]?.GetValue<string>();
            var secret = node?["secret"]?.GetValue<string>();

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(secret))
            {
                return Fail("Incomplete initiation response.");
            }

            try
            {
                if (!await _quickConnect.AuthorizeRequest(userId, code).ConfigureAwait(false))
                {
                    return Fail("Jellyfin refused to authorize the code.");
                }
            }
            catch (Exception ex)
            {
                return Fail("Could not authorize the code: " + ex.Message);
            }

            var auth = await _seerr.PostAnonymousAsync("/auth/jellyfin/quickconnect/authenticate", new { secret }, ct).ConfigureAwait(false);
            if (!auth.IsSuccess)
            {
                return Fail("Seerr refused the session (" + (int)auth.StatusCode + "): " + (auth.AsNode()?["message"]?.GetValue<string>() ?? "no detail"));
            }

            // The session cookie, not the CSRF ones Seerr may also hand out.
            var sessionCookie = auth.SetCookies.FirstOrDefault(c => c.StartsWith("connect.sid=", StringComparison.Ordinal))
                ?? auth.SetCookies.FirstOrDefault(c => c.Contains('=', StringComparison.Ordinal)
                    && !c.StartsWith("_csrf=", StringComparison.Ordinal)
                    && !c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
            if (sessionCookie == null)
            {
                return Fail("Seerr did not return a session cookie.");
            }

            var nameValue = sessionCookie.Split(';', 2)[0].Trim();
            var config = Plugin.Instance?.Configuration;
            var seerrIsHttps = UrlUtil.Normalize(config?.SeerrPublicUrl).StartsWith("https:", StringComparison.OrdinalIgnoreCase);
            var relay = ProxyMode;

            // The cookie is set by the host the browser is talking to when it redeems the ticket: Seerr's in relay mode,
            // Jellyfin's otherwise. "Secure" only if that host is reached over https (a browser drops a Secure cookie set
            // over plain http) AND Seerr itself is over https (a Secure cookie is never sent to a plain-http Seerr).
            var settingIsHttps = UrlUtil.Normalize(relay ? config?.SeerrPublicUrl : config?.JellyfinPublicUrl)
                .StartsWith("https:", StringComparison.OrdinalIgnoreCase);
            var secure = settingIsHttps && seerrIsHttps;

            // Relay mode: host cookie, exactly as Seerr would set it.
            // Same-host mode: host cookie too (no Domain), which the browser also sends to Seerr's port.
            // Domain mode: parent-domain cookie, so that it travels to Seerr.
            var domain = !relay && !SameHost ? CookieDomain : string.Empty;
            var header = nameValue
                + (domain.Length > 0 ? "; Domain=" + domain : string.Empty)
                + "; Path=/; Max-Age=2592000; HttpOnly; SameSite=Lax"
                + (secure ? "; Secure" : string.Empty);

            _logger.LogInformation("Seerr Search: Seerr session opened through Quick Connect for user {UserId}.", userId);

            return new SignInResult(true, header, null);
        }

        private SignInResult Fail(string message)
        {
            _logger.LogWarning("Seerr Search SSO: {Message}", message);
            return new SignInResult(false, null, message);
        }

        private void Purge()
        {
            var now = DateTime.UtcNow;
            foreach (var pair in _tickets)
            {
                if (pair.Value.ExpiresUtc < now)
                {
                    _tickets.TryRemove(pair.Key, out _);
                }
            }
        }

        private static string HostOf(string? url)
        {
            var normalized = UrlUtil.Normalize(url);

            return normalized.Length > 0 && Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
        }

        private static bool LooksLikeIp(string host) => System.Net.IPAddress.TryParse(host, out _);
    }
}
