using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SeerrSearch.Configuration;
using Jellyfin.Plugin.SeerrSearch.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrSearch.Controllers
{
    /// <summary>
    /// The browser's only point of contact. Every call is authenticated by Jellyfin, then
    /// translated into a Seerr call on behalf of the matching user. The API key never leaves
    /// this class and its services.
    ///
    /// Three endpoints are anonymous on purpose, and none of them returns anything private:
    /// <c>script</c> (the client script, loaded by a plain script tag that cannot carry a
    /// token), <c>ping</c> (a fixed string) and <c>sso</c> (it only redeems a one-time ticket
    /// that an authenticated user obtained from <c>sso/ticket</c>).
    /// </summary>
    [ApiController]
    [Route("SeerrSearch")]
    public class SeerrSearchController : ControllerBase
    {
        private static readonly string[] AllowedImageSizes = { "w92", "w154", "w185", "w342", "w500", "original" };
        private static readonly Regex ImagePathPattern = new(@"^/[A-Za-z0-9._-]{4,64}\.(jpg|png|webp)$", RegexOptions.Compiled);
        private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".png"] = "image/png",
            [".webp"] = "image/webp"
        };

        private const int MaxQueryLength = 200;
        private const int SearchesPerMinute = 60;
        private const int RequestsPerMinute = 20;
        private const int ImagesPerMinute = 600;

        private readonly SeerrClient _seerr;
        private readonly SeerrUserMap _userMap;
        private readonly SeerrSso _sso;
        private readonly RateLimiter _limiter;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<SeerrSearchController> _logger;

        public SeerrSearchController(
            SeerrClient seerr,
            SeerrUserMap userMap,
            SeerrSso sso,
            RateLimiter limiter,
            IHttpClientFactory httpClientFactory,
            ILogger<SeerrSearchController> logger)
        {
            _seerr = seerr;
            _userMap = userMap;
            _sso = sso;
            _limiter = limiter;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>The client script, served without authentication: it is an ordinary script tag and holds no secret.</summary>
        [HttpGet("script")]
        [AllowAnonymous]
        public ActionResult GetScript()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("seerr-search.js", StringComparison.OrdinalIgnoreCase));

            if (resource == null)
            {
                return NotFound();
            }

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream == null)
            {
                return NotFound();
            }

            using var reader = new StreamReader(stream);
            var js = reader.ReadToEnd();

            Response.Headers.CacheControl = "public, max-age=3600";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Content(js, "application/javascript; charset=utf-8");
        }

        /// <summary>What the browser is allowed to know: never the API key.</summary>
        [HttpGet("config")]
        [Authorize]
        public async Task<ActionResult> GetClientConfig(CancellationToken ct)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return StatusCode((int)HttpStatusCode.ServiceUnavailable);
            }

            if (!config.Enabled || !SeerrClient.IsConfigured)
            {
                return new JsonResult(new { enabled = false });
            }

            var userId = GetJellyfinUserId();
            int? seerrUserId = null;

            if (userId.HasValue)
            {
                seerrUserId = await _userMap.GetSeerrUserIdAsync(userId.Value, ct).ConfigureAwait(false);
            }

            return new JsonResult(new
            {
                enabled = true,
                linked = seerrUserId.HasValue,
                unlinkedBehavior = config.UnlinkedUserBehavior,
                unlinkedMessage = config.UnlinkedUserMessage ?? string.Empty,
                minQueryLength = Math.Clamp(config.MinQueryLength, 1, 10),
                maxResults = Math.Clamp(config.MaxResults, 1, 60),
                hideAvailable = config.HideAvailable,
                allow4k = config.Allow4k,
                publicUrl = UrlUtil.Normalize(config.SeerrPublicUrl),
                sso = _sso.IsReady,
                ssoMode = _sso.Mode,
                seerrHost = SeerrSso.PublicSeerrHostName,
                frameBlocked = _sso.FrameBlocked,
                showSeerrLink = config.ShowSeerrLink || _sso.IsReady,
                language = config.Language ?? string.Empty
            });
        }

        /// <summary>Seerr search, filtered and bounded, on behalf of the signed-in user.</summary>
        [HttpGet("search")]
        [Authorize]
        public async Task<ActionResult> Search(
            [FromQuery] string? query,
            [FromQuery] int page = 1,
            [FromQuery] string? lang = null,
            CancellationToken ct = default)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.Enabled || !SeerrClient.IsConfigured)
            {
                return StatusCode((int)HttpStatusCode.ServiceUnavailable, new { error = "not-configured" });
            }

            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < Math.Clamp(config.MinQueryLength, 1, 10))
            {
                return new JsonResult(new { results = Array.Empty<object>() });
            }

            if (query.Length > MaxQueryLength)
            {
                return BadRequest(new { error = "query-too-long" });
            }

            if (!_limiter.Allow("search", GetJellyfinUserId(), SearchesPerMinute))
            {
                return StatusCode((int)HttpStatusCode.TooManyRequests, new { error = "rate-limited" });
            }

            var seerrUserId = await ResolveSeerrUserIdAsync(ct).ConfigureAwait(false);
            if (seerrUserId == null)
            {
                return StatusCode((int)HttpStatusCode.Conflict, new { error = "user-not-linked" });
            }

            var path = string.Format(
                CultureInfo.InvariantCulture,
                "/search?query={0}&page={1}&language={2}",
                Uri.EscapeDataString(query.Trim()),
                Math.Clamp(page, 1, 20),
                Uri.EscapeDataString(ResolveLanguage(config, lang)));

            var response = await _seerr.GetAsync(path, seerrUserId, ct).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                return StatusCode((int)response.StatusCode, new { error = "seerr-error" });
            }

            var payload = response.AsNode();
            var results = payload?["results"]?.AsArray();
            if (results == null)
            {
                return new JsonResult(new { results = Array.Empty<object>() });
            }

            var trimmed = results
                .Where(r => r != null)
                .Select(r => r!)
                .Where(r =>
                {
                    var mediaType = SafeString(r["mediaType"]);
                    return mediaType == "movie" || mediaType == "tv";
                })
                .Where(r => !config.HideAvailable || GetMediaStatus(r) != 5)
                .Take(Math.Clamp(config.MaxResults, 1, 60))
                .Select(r => new
                {
                    id = SafeInt(r["id"]),
                    mediaType = SafeString(r["mediaType"]),
                    title = SafeString(r["title"]) ?? SafeString(r["name"]),
                    overview = SafeString(r["overview"]),
                    posterPath = SafeString(r["posterPath"]),
                    date = SafeString(r["releaseDate"]) ?? SafeString(r["firstAirDate"]),
                    status = GetMediaStatus(r),
                    status4k = GetMediaStatus(r, true)
                })
                .ToList();

            return new JsonResult(new { results = trimmed });
        }

        /// <summary>Creates the request in Seerr, owned by the signed-in user's account.</summary>
        [HttpPost("request")]
        [Authorize]
        public async Task<ActionResult> CreateRequest([FromBody] RequestPayload payload, CancellationToken ct)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.Enabled || !SeerrClient.IsConfigured)
            {
                return StatusCode((int)HttpStatusCode.ServiceUnavailable, new { error = "not-configured" });
            }

            if (payload == null
                || payload.MediaId <= 0
                || (payload.MediaType != "movie" && payload.MediaType != "tv"))
            {
                return BadRequest(new { error = "bad-request" });
            }

            if (!_limiter.Allow("request", GetJellyfinUserId(), RequestsPerMinute))
            {
                return StatusCode((int)HttpStatusCode.TooManyRequests, new { error = "rate-limited" });
            }

            var seerrUserId = await ResolveSeerrUserIdAsync(ct).ConfigureAwait(false);
            if (seerrUserId == null)
            {
                return StatusCode((int)HttpStatusCode.Conflict, new { error = "user-not-linked" });
            }

            var is4k = config.Allow4k && payload.Is4k;

            object body = payload.MediaType == "tv"
                ? new
                {
                    mediaType = "tv",
                    mediaId = payload.MediaId,
                    seasons = "all",
                    is4k
                }
                : new
                {
                    mediaType = "movie",
                    mediaId = payload.MediaId,
                    is4k
                };

            var response = await _seerr.PostAsync("/request", body, seerrUserId, ct).ConfigureAwait(false);

            if (response.IsSuccess)
            {
                _logger.LogInformation(
                    "Seerr Search: request created ({MediaType} {MediaId}) for Seerr user {SeerrUserId}.",
                    payload.MediaType,
                    payload.MediaId,
                    seerrUserId);

                return new JsonResult(new { ok = true });
            }

            var message = SafeString(response.AsNode()?["message"]);
            return StatusCode((int)response.StatusCode, new { error = "seerr-error", message });
        }

        /// <summary>
        /// Relay for TMDB posters, so the browser has a single counterpart. Authenticated like the
        /// rest (the client fetches it with its token): otherwise it would be an open image proxy.
        /// The host is fixed and the path is restricted to a plain image file name.
        /// </summary>
        [HttpGet("image")]
        [Authorize]
        public async Task<ActionResult> GetImage([FromQuery] string? p, [FromQuery] string size = "w185", CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(p) || !ImagePathPattern.IsMatch(p) || !AllowedImageSizes.Contains(size))
            {
                return BadRequest();
            }

            if (!_limiter.Allow("image", GetJellyfinUserId(), ImagesPerMinute))
            {
                return StatusCode((int)HttpStatusCode.TooManyRequests);
            }

            try
            {
                var client = _httpClientFactory.CreateClient(SeerrClient.NamedClient);
                client.Timeout = TimeSpan.FromSeconds(15);
                client.MaxResponseContentBufferSize = 5 * 1024 * 1024;

                using var upstream = await client
                    .GetAsync($"https://image.tmdb.org/t/p/{size}{p}", ct)
                    .ConfigureAwait(false);

                if (!upstream.IsSuccessStatusCode)
                {
                    return StatusCode((int)upstream.StatusCode);
                }

                var bytes = await upstream.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

                // The content type comes from the file extension we validated, never from the upstream.
                var contentType = ImageTypes[Path.GetExtension(p)];

                Response.Headers.CacheControl = "private, max-age=604800";
                Response.Headers["X-Content-Type-Options"] = "nosniff";

                return File(bytes, contentType);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return StatusCode((int)HttpStatusCode.BadGateway);
            }
        }

        /// <summary>Connection test for the configuration page (administrators only).</summary>
        [HttpGet("status")]
        [Authorize]
        public async Task<ActionResult> GetStatus(CancellationToken ct)
        {
            if (!IsAdministrator())
            {
                return StatusCode((int)HttpStatusCode.Forbidden);
            }

            if (!SeerrClient.IsConfigured)
            {
                return new JsonResult(new { ok = false, error = "not-configured" });
            }

            var seerrUrl = UrlUtil.NormalizeSeerr(Plugin.Instance?.Configuration.SeerrUrl);
            var hostPort = UrlUtil.HostPort(seerrUrl);
            var loopback = UrlUtil.IsLoopback(seerrUrl);

            var status = await _seerr.GetAsync("/status", null, ct).ConfigureAwait(false);
            var problem = DescribeProblem(status);
            if (problem != null)
            {
                return new JsonResult(new
                {
                    ok = false,
                    error = problem,
                    code = status.Failure == null ? (int)status.StatusCode : 0,
                    host = hostPort,
                    loopback,
                    location = SafeLocation(status.Location, seerrUrl)
                });
            }

            // /status is public in Seerr: check the key too, with a call that needs it.
            var users = await _seerr.GetAsync("/user?take=1", null, ct).ConfigureAwait(false);
            if (!users.IsSuccess)
            {
                var keyProblem = users.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "bad-key"
                    : DescribeProblem(users) ?? "http-error";
                return new JsonResult(new { ok = false, error = keyProblem, code = users.Failure == null ? (int)users.StatusCode : 0, host = hostPort, loopback, location = SafeLocation(users.Location, seerrUrl) });
            }

            // The test button should show the real answer, not the one cached from before the last change.
            await _sso.ProbeNowAsync().ConfigureAwait(false);

            _userMap.Invalidate();
            await _userMap.RefreshAsync(ct).ConfigureAwait(false);

            var importable = await _seerr.GetImportableJellyfinUserIdsAsync(ct).ConfigureAwait(false);
            int? unlinked = importable?.Count(id => !_userMap.IsMapped(id));

            return new JsonResult(new
            {
                ok = true,
                version = SafeString(status.AsNode()?["version"]),
                linkedUsers = _userMap.MappedUserCount,
                unlinkedUsers = unlinked,
                sso = _sso.IsReady,
                ssoMode = _sso.Mode,
                ssoReason = _sso.WhyNotReady(),
                ssoProbe = _sso.ProbeSummary,
                ssoPublicHost = SeerrSso.PublicSeerrHostName,
                frameBlocked = _sso.FrameBlocked
            });
        }

        /// <summary>
        /// What is wrong with an answer to <c>GET /status</c>, as a stable code for the configuration page, or null when
        /// it looks like Seerr. A connection that failed outright is "net-dns", "net-refused", "net-timeout", "net-tls"
        /// or "net-network".
        /// </summary>
        private static string? DescribeProblem(SeerrClient.SeerrResponse response)
        {
            if (response.Failure != null)
            {
                return "net-" + response.Failure;
            }

            var code = (int)response.StatusCode;

            if (code is >= 300 and < 400)
            {
                return "redirect";
            }

            if (code is 401 or 403)
            {
                return "blocked";
            }

            if (code == 404)
            {
                return "not-found";
            }

            if (code >= 500)
            {
                return "server-error";
            }

            if (code is < 200 or >= 300)
            {
                return "http-error";
            }

            return SafeString(response.AsNode()?["version"]) == null ? "not-seerr" : null;
        }

        /// <summary>Where a redirect leads, without its query string, for the message shown to the administrator.</summary>
        private static string? SafeLocation(string? location, string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return null;
            }

            var absolute = Uri.TryCreate(location, UriKind.Absolute, out var uri)
                ? uri
                : Uri.TryCreate(new Uri(baseUrl + "/"), location, out var relative) ? relative : null;

            return absolute == null ? null : absolute.GetLeftPart(UriPartial.Path);
        }

        /// <summary>Imports the Jellyfin accounts that are not in Seerr yet (administrators only), then reloads the map.</summary>
        [HttpPost("import-users")]
        [Authorize]
        public async Task<ActionResult> ImportUsers(CancellationToken ct)
        {
            if (!IsAdministrator())
            {
                return StatusCode((int)HttpStatusCode.Forbidden);
            }

            if (!SeerrClient.IsConfigured)
            {
                return StatusCode((int)HttpStatusCode.ServiceUnavailable, new { ok = false, error = "not-configured" });
            }

            _userMap.Invalidate();
            await _userMap.RefreshAsync(ct).ConfigureAwait(false);

            var importable = await _seerr.GetImportableJellyfinUserIdsAsync(ct).ConfigureAwait(false);
            if (importable == null)
            {
                return StatusCode((int)HttpStatusCode.BadGateway, new { ok = false, error = "seerr-error" });
            }

            var missing = importable.Where(id => !_userMap.IsMapped(id)).ToList();

            if (missing.Count == 0)
            {
                return new JsonResult(new { ok = true, imported = 0, linkedUsers = _userMap.MappedUserCount });
            }

            var response = await _seerr.PostAsync("/user/import-from-jellyfin", new { jellyfinUserIds = missing }, null, ct).ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                return StatusCode((int)response.StatusCode, new { ok = false });
            }

            _userMap.Invalidate();
            await _userMap.RefreshAsync(ct).ConfigureAwait(false);

            var imported = response.AsNode() is JsonArray array ? array.Count : 0;

            return new JsonResult(new { ok = true, imported, linkedUsers = _userMap.MappedUserCount });
        }

        /// <summary>The plugin's signature: used to detect the /SeerrSearch/ relay on Seerr's host.</summary>
        [HttpGet("ping")]
        [AllowAnonymous]
        public ActionResult Ping()
        {
            Response.Headers.CacheControl = "no-store";
            return Content(SeerrSso.PingBody, "text/plain; charset=utf-8");
        }

        /// <summary>One-time ticket to open Seerr already signed in (see SeerrSso).</summary>
        [HttpPost("sso/ticket")]
        [Authorize]
        public ActionResult IssueSsoTicket([FromBody] SsoTicketRequest? payload)
        {
            var userId = GetJellyfinUserId();
            if (!userId.HasValue)
            {
                return StatusCode((int)HttpStatusCode.Unauthorized);
            }

            if (!_limiter.Allow("ticket", userId, RequestsPerMinute))
            {
                return StatusCode((int)HttpStatusCode.TooManyRequests, new { error = "rate-limited" });
            }

            var url = _sso.IssueTicket(userId.Value, payload?.To);
            if (url == null)
            {
                return StatusCode((int)HttpStatusCode.ServiceUnavailable, new { error = "sso-not-ready", reason = _sso.WhyNotReady() });
            }

            return new JsonResult(new { url });
        }

        /// <summary>
        /// Landing point of the navigation: redeems the ticket, opens the Seerr session on the
        /// server side, sets the cookie and redirects. If anything goes wrong, the user lands on
        /// Seerr's own sign-in page — never on a raw error.
        /// </summary>
        [HttpGet("sso")]
        [AllowAnonymous]
        public async Task<ActionResult> Sso([FromQuery] string? t, [FromQuery] int hop = 0, CancellationToken ct = default)
        {
            var config = Plugin.Instance?.Configuration;
            var seerrPublic = UrlUtil.Normalize(config?.SeerrPublicUrl);
            if (config == null || seerrPublic.Length == 0)
            {
                return NotFound();
            }

            var loginPage = seerrPublic + "/login";

            // The cookie is only worth something if it is set from the right host: Seerr's in
            // relay mode, Jellyfin's in domain mode. Arriving through another address (a local
            // IP, say), we go back through the right host without redeeming the ticket — once.
            var expectedOrigin = UrlUtil.Normalize(_sso.ProxyMode ? config.SeerrPublicUrl : config.JellyfinPublicUrl);
            if (!Uri.TryCreate(expectedOrigin, UriKind.Absolute, out var expectedUri))
            {
                return Redirect(loginPage);
            }

            if (!HostMatches(expectedUri.Host))
            {
                if (hop == 0 && _sso.Peek(t))
                {
                    return Redirect(expectedOrigin + "/" + Plugin.PluginRoute + "/sso?t=" + Uri.EscapeDataString(t!) + "&hop=1");
                }

                return Redirect(loginPage);
            }

            if (!_sso.TryConsume(t, out var ticket))
            {
                return Redirect(loginPage);
            }

            var result = await _sso.SignInAsync(ticket.UserId, ct).ConfigureAwait(false);
            if (!result.Ok)
            {
                // Go to the target page anyway: if a Seerr session already exists in this
                // browser it is used; otherwise Seerr shows its own sign-in page, keeping the target.
                return Redirect(seerrPublic + ticket.To);
            }

            Response.Headers.SetCookie = result.CookieHeader!;
            Response.Headers.CacheControl = "no-store";

            return Redirect(seerrPublic + ticket.To);
        }

        /// <summary>
        /// Whether the browser reached us through the expected host name. A reverse proxy that does not pass the original
        /// Host header (proxy_pass without proxy_set_header Host) still tells us in X-Forwarded-Host, most of the time.
        /// Being wrong here costs nothing: the user simply lands on Seerr's own sign-in page.
        /// </summary>
        private bool HostMatches(string expectedHost)
        {
            if (string.Equals(Request.Host.Host, expectedHost, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var forwarded = Request.Headers["X-Forwarded-Host"].ToString().Split(',')[0].Trim();
            if (forwarded.Length == 0)
            {
                return false;
            }

            var colon = forwarded.LastIndexOf(':');
            var name = colon > 0 && !forwarded.EndsWith(']') ? forwarded[..colon] : forwarded;

            return string.Equals(name.Trim('[', ']'), expectedHost.Trim('[', ']'), StringComparison.OrdinalIgnoreCase);
        }

        private async Task<int?> ResolveSeerrUserIdAsync(CancellationToken ct)
        {
            var userId = GetJellyfinUserId();
            if (!userId.HasValue)
            {
                return null;
            }

            return await _userMap.GetSeerrUserIdAsync(userId.Value, ct).ConfigureAwait(false);
        }

        private Guid? GetJellyfinUserId()
        {
            var value = User.Claims
                .FirstOrDefault(c => string.Equals(c.Type, "Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))
                ?.Value;

            return Guid.TryParse(value, out var id) ? id : null;
        }

        private bool IsAdministrator() => User.IsInRole("Administrator");

        /// <summary>The configured language, else the one the browser reported (if it looks like a language code), else English.</summary>
        private static string ResolveLanguage(PluginConfiguration config, string? requested)
        {
            var configured = PluginConfiguration.NormalizeLanguage(config.Language);
            if (configured.Length > 0)
            {
                return configured;
            }

            var fromBrowser = PluginConfiguration.NormalizeLanguage(requested);
            return fromBrowser.Length > 0 ? fromBrowser : "en";
        }

        private static string? SafeString(JsonNode? node)
        {
            try
            {
                return node?.GetValue<string>();
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                return null;
            }
        }

        private static int? SafeInt(JsonNode? node)
        {
            try
            {
                return node?.GetValue<int>();
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                return null;
            }
        }

        private static int GetMediaStatus(JsonNode result, bool fourK = false)
        {
            var key = fourK ? "status4k" : "status";
            return SafeInt(result["mediaInfo"]?[key]) ?? 0;
        }

        public class SsoTicketRequest
        {
            public string? To { get; set; }
        }

        public class RequestPayload
        {
            public string MediaType { get; set; } = string.Empty;

            public int MediaId { get; set; }

            public bool Is4k { get; set; }
        }
    }
}
