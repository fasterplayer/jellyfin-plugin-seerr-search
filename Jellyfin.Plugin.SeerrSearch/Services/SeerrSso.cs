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
                Plugin.Instance.ConfigurationChanged += (_, _) => _probedUtc = DateTime.MinValue;
            }
        }

        public static string PublicSeerrHostName => PublicSeerrHost;

        public static string PublicJellyfinHostName => PublicJellyfinHost;

        /// <summary>True when Seerr's public host forwards /SeerrSearch/ to this server.</summary>
        public bool ProxyMode
        {
            get
            {
                if (DateTime.UtcNow - _probedUtc < ProbeTtl)
                {
                    return _proxyMode;
                }

                _probedUtc = DateTime.UtcNow;
                _proxyMode = Probe();
                return _proxyMode;
            }
        }

        private bool Probe()
        {
            var publicUrl = UrlUtil.Normalize(Plugin.Instance?.Configuration.SeerrPublicUrl);
            if (string.IsNullOrEmpty(publicUrl))
            {
                return false;
            }

            try
            {
                var client = _httpClientFactory.CreateClient(SeerrClient.NamedClient);
                client.Timeout = TimeSpan.FromSeconds(8);
                client.MaxResponseContentBufferSize = 4096;
                var body = client.GetStringAsync(publicUrl + "/" + Plugin.PluginRoute + "/ping").GetAwaiter().GetResult();
                var ok = body.Trim() == PingBody;
                _logger.LogInformation("Seerr Search: /SeerrSearch/ relay on Seerr's host is {State}.", ok ? "present" : "absent");
                return ok;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public sealed record Ticket(Guid UserId, string To, DateTime ExpiresUtc);

        public sealed record SignInResult(bool Ok, string? CookieHeader, string? Error);

        private static string PublicJellyfinHost => HostOf(Plugin.Instance?.Configuration.JellyfinPublicUrl);

        private static string PublicSeerrHost => HostOf(Plugin.Instance?.Configuration.SeerrPublicUrl);

        /// <summary>Parent domain shared by the two public hosts, or empty when automatic sign-in is not possible.</summary>
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
                    return a;
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
            && (ProxyMode || (!string.IsNullOrEmpty(CookieDomain) && !string.IsNullOrEmpty(PublicJellyfinHost)));

        public string Mode => !IsReady ? "off" : ProxyMode ? "proxy" : "domain";

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

            if (string.IsNullOrEmpty(CookieDomain))
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

            var sessionCookie = auth.SetCookies.FirstOrDefault(c => c.Contains('=', StringComparison.Ordinal));
            if (sessionCookie == null)
            {
                return Fail("Seerr did not return a session cookie.");
            }

            var nameValue = sessionCookie.Split(';', 2)[0].Trim();
            var secure = UrlUtil.Normalize(Plugin.Instance?.Configuration.SeerrPublicUrl).StartsWith("https", StringComparison.OrdinalIgnoreCase);

            // Relay mode: host cookie, exactly as Seerr would set it.
            // Domain mode: parent-domain cookie, so that it travels to Seerr.
            var header = nameValue
                + (ProxyMode ? string.Empty : "; Domain=" + CookieDomain)
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
