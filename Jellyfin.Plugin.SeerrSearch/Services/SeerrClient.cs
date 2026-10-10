using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SeerrSearch.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrSearch.Services
{
    /// <summary>
    /// All the communication with Seerr. The API key stays here: the browser only
    /// talks to the plugin's controller, never to Seerr directly.
    ///
    /// The identity is carried by the X-Api-User header, which Seerr reads as "act on
    /// behalf of this user" when the API key is valid. That is what makes quotas,
    /// permissions and request ownership those of the signed-in Jellyfin user,
    /// without that user having to sign in to Seerr.
    /// </summary>
    public class SeerrClient
    {
        public const string NamedClient = "SeerrSearch";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<SeerrClient> _logger;

        public SeerrClient(IHttpClientFactory httpClientFactory, ILogger<SeerrClient> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        private static PluginConfiguration? Config => Plugin.Instance?.Configuration;

        public static bool IsConfigured =>
            Config != null
            && BaseUrl.Length > 0
            && !string.IsNullOrWhiteSpace(Config.ApiKey);

        // Only a plain http(s) URL is ever requested (see UrlUtil).
        private static string BaseUrl => UrlUtil.NormalizeSeerr(Config?.SeerrUrl);

        /// <summary>Raw result of a Seerr call: the JSON body and the HTTP status, untouched.</summary>
        /// <param name="Failure">
        /// Set when no HTTP answer came back: "dns" (name not found), "refused" (nothing listens there), "timeout",
        /// "tls" (certificate or protocol problem), or "network" (anything else). Used for the connection test.
        /// </param>
        /// <param name="Location">The Location header of a redirect (3xx), if any.</param>
        public sealed record SeerrResponse(HttpStatusCode StatusCode, string Body, IReadOnlyList<string> SetCookies, string? Failure = null, string? Location = null)
        {
            public bool IsSuccess => (int)StatusCode >= 200 && (int)StatusCode < 300;

            public JsonNode? AsNode()
            {
                if (string.IsNullOrWhiteSpace(Body))
                {
                    return null;
                }

                try
                {
                    return JsonNode.Parse(Body);
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }

        public Task<SeerrResponse> GetAsync(string path, int? seerrUserId = null, CancellationToken ct = default)
            => SendAsync(HttpMethod.Get, path, null, seerrUserId, false, ct);

        public Task<SeerrResponse> PostAsync(string path, object? body, int? seerrUserId = null, CancellationToken ct = default)
            => SendAsync(HttpMethod.Post, path, body, seerrUserId, false, ct);

        /// <summary>
        /// A call without the API key, as a browser would make it: used by Quick Connect,
        /// whose response carries the session cookie to relay.
        /// </summary>
        public Task<SeerrResponse> PostAnonymousAsync(string path, object? body, CancellationToken ct = default)
            => SendAsync(HttpMethod.Post, path, body, null, true, ct);

        /// <summary>The cookies and the token Seerr wants back on a POST when its "CSRF protection" setting is on.</summary>
        public sealed record Csrf(string Cookie, string Token);

        private Csrf? _csrf;
        private string _csrfFor = string.Empty;
        private DateTime _csrfUtc = DateTime.MinValue;

        /// <summary>
        /// With "CSRF protection" enabled in Seerr, a POST without a valid token is refused (403 "invalid csrf token"),
        /// API key or not. A GET hands out the two cookies (_csrf and XSRF-TOKEN); the token is sent back in a header,
        /// like Seerr's own web page does. The pair is kept for ten minutes (and renewed when Seerr refuses it).
        /// Null when Seerr does not use CSRF protection.
        /// </summary>
        private async Task<Csrf?> GetCsrfAsync(bool renew, CancellationToken ct)
        {
            if (!renew && _csrfFor == BaseUrl && DateTime.UtcNow - _csrfUtc < TimeSpan.FromMinutes(10))
            {
                return _csrf;
            }

            var response = await SendCoreAsync(HttpMethod.Get, "/status", null, null, true, ct, null).ConfigureAwait(false);

            string? token = null;
            var pairs = new List<string>();
            foreach (var cookie in response.SetCookies)
            {
                var pair = cookie.Split(';', 2)[0].Trim();
                if (pair.StartsWith("_csrf=", StringComparison.Ordinal) || pair.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal))
                {
                    pairs.Add(pair);
                    if (pair.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal))
                    {
                        token = Uri.UnescapeDataString(pair["XSRF-TOKEN=".Length..]);
                    }
                }
            }

            _csrf = token == null ? null : new Csrf(string.Join("; ", pairs), token);
            _csrfFor = BaseUrl;
            _csrfUtc = DateTime.UtcNow;
            return _csrf;
        }

        private async Task<SeerrResponse> SendAsync(
            HttpMethod method,
            string path,
            object? body,
            int? seerrUserId,
            bool anonymous,
            CancellationToken ct)
        {
            if (method == HttpMethod.Get || !IsConfigured)
            {
                return await SendCoreAsync(method, path, body, seerrUserId, anonymous, ct, null).ConfigureAwait(false);
            }

            var csrf = await GetCsrfAsync(false, ct).ConfigureAwait(false);
            var response = await SendCoreAsync(method, path, body, seerrUserId, anonymous, ct, csrf).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden && response.Body.Contains("csrf", StringComparison.OrdinalIgnoreCase))
            {
                // The setting was switched on (or Seerr restarted) since the pair was kept: renew it once.
                csrf = await GetCsrfAsync(true, ct).ConfigureAwait(false);
                if (csrf != null)
                {
                    response = await SendCoreAsync(method, path, body, seerrUserId, anonymous, ct, csrf).ConfigureAwait(false);
                }
            }

            return response;
        }

        private async Task<SeerrResponse> SendCoreAsync(
            HttpMethod method,
            string path,
            object? body,
            int? seerrUserId,
            bool anonymous,
            CancellationToken ct,
            Csrf? csrf)
        {
            if (!IsConfigured)
            {
                return new SeerrResponse(HttpStatusCode.ServiceUnavailable, "{\"error\":\"Seerr is not configured.\"}", Array.Empty<string>());
            }

            var url = BaseUrl + "/api/v1" + (path.StartsWith('/') ? path : "/" + path);

            using var request = new HttpRequestMessage(method, url);
            if (!anonymous)
            {
                request.Headers.TryAddWithoutValidation("X-Api-Key", Config!.ApiKey);
            }

            if (!anonymous && seerrUserId.HasValue && seerrUserId.Value > 0)
            {
                request.Headers.TryAddWithoutValidation(
                    "X-Api-User",
                    seerrUserId.Value.ToString(CultureInfo.InvariantCulture));
            }

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (csrf != null)
            {
                request.Headers.TryAddWithoutValidation("Cookie", csrf.Cookie);
                request.Headers.TryAddWithoutValidation("XSRF-TOKEN", csrf.Token);
            }

            if (body != null)
            {
                request.Content = new StringContent(
                    JsonSerializer.Serialize(body),
                    Encoding.UTF8,
                    "application/json");
            }

            try
            {
                var client = _httpClientFactory.CreateClient(NamedClient);
                client.Timeout = TimeSpan.FromSeconds(20);
                client.MaxResponseContentBufferSize = 10 * 1024 * 1024;

                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
                var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Seerr answered {Status} for {Method} {Path}",
                        (int)response.StatusCode,
                        method.Method,
                        path);
                }

                var cookies = response.Headers.TryGetValues("Set-Cookie", out var values)
                    ? values.ToList()
                    : new List<string>();

                var location = response.Headers.Location?.ToString();

                return new SeerrResponse(response.StatusCode, content, cookies, null, location);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Timed out contacting Seerr ({Url})", url);
                return new SeerrResponse(HttpStatusCode.GatewayTimeout, "{\"error\":\"Seerr did not answer in time.\"}", Array.Empty<string>(), "timeout");
            }
            catch (HttpRequestException ex)
            {
                var kind = Classify(ex);
                _logger.LogWarning("Could not reach Seerr ({Url}): {Kind} - {Reason}", url, kind, ex.InnerException?.Message ?? ex.Message);
                return new SeerrResponse(HttpStatusCode.BadGateway, "{\"error\":\"Could not reach Seerr.\"}", Array.Empty<string>(), kind);
            }
        }

        /// <summary>Why a connection failed, in terms an administrator can act on.</summary>
        public static string Classify(Exception ex)
        {
            if (ex is HttpRequestException http)
            {
                if (http.HttpRequestError == HttpRequestError.NameResolutionError)
                {
                    return "dns";
                }

                if (http.HttpRequestError == HttpRequestError.SecureConnectionError)
                {
                    return "tls";
                }
            }

            Exception? inner = ex;
            while (inner != null)
            {
                if (inner is System.Security.Authentication.AuthenticationException)
                {
                    return "tls";
                }

                if (inner is System.Net.Sockets.SocketException socket)
                {
                    switch (socket.SocketErrorCode)
                    {
                        case System.Net.Sockets.SocketError.HostNotFound:
                        case System.Net.Sockets.SocketError.TryAgain:
                        case System.Net.Sockets.SocketError.NoData:
                            return "dns";
                        case System.Net.Sockets.SocketError.ConnectionRefused:
                            return "refused";
                        case System.Net.Sockets.SocketError.TimedOut:
                        case System.Net.Sockets.SocketError.HostUnreachable:
                        case System.Net.Sockets.SocketError.NetworkUnreachable:
                            return "timeout";
                    }
                }

                inner = inner.InnerException;
            }

            return "network";
        }

        /// <summary>
        /// The "Application URL" of Seerr (Settings → General), the address its own users type, or an empty string when it
        /// is not set or cannot be read. Used as the public address when the administrator did not type one.
        /// </summary>
        public async Task<string> GetApplicationUrlAsync(CancellationToken ct = default)
        {
            var response = await GetAsync("/settings/main", null, ct).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                return string.Empty;
            }

            try
            {
                return UrlUtil.Normalize(response.AsNode()?["applicationUrl"]?.GetValue<string>());
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// What Seerr says about its own setup (public endpoint): whether the first-run wizard was finished and which media
        /// server it uses (1 Plex, 2 Jellyfin, 3 Emby, 4 not chosen yet). Null values when Seerr does not tell.
        /// </summary>
        public async Task<(bool? Initialized, int? MediaServerType)> GetSetupAsync(CancellationToken ct = default)
        {
            var response = await GetAsync("/settings/public", null, ct).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                return (null, null);
            }

            var node = response.AsNode();

            try
            {
                return (node?["initialized"]?.GetValue<bool>(), node?["mediaServerType"]?.GetValue<int>());
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                return (null, null);
            }
        }

        /// <summary>Paged list of the Seerr users (the API key has admin rights).</summary>
        public async Task<IReadOnlyList<JsonNode>> GetUsersAsync(CancellationToken ct = default)
        {
            var users = new List<JsonNode>();
            var skip = 0;
            const int take = 100;

            while (skip < 2000)
            {
                var response = await GetAsync(
                    $"/user?take={take}&skip={skip}&sort=created",
                    null,
                    ct).ConfigureAwait(false);

                if (!response.IsSuccess)
                {
                    break;
                }

                var results = response.AsNode()?["results"]?.AsArray();
                if (results == null || results.Count == 0)
                {
                    break;
                }

                foreach (var item in results)
                {
                    if (item != null)
                    {
                        users.Add(item);
                    }
                }

                if (results.Count < take)
                {
                    break;
                }

                skip += take;
            }

            return users;
        }

        /// <summary>
        /// The Jellyfin accounts Seerr can import (ids without dashes), as Seerr lists them,
        /// or null when Seerr could not be asked.
        /// </summary>
        public async Task<IReadOnlyList<string>?> GetImportableJellyfinUserIdsAsync(CancellationToken ct = default)
        {
            var response = await GetAsync("/settings/jellyfin/users", null, ct).ConfigureAwait(false);

            if (!response.IsSuccess || response.AsNode() is not JsonArray array)
            {
                return null;
            }

            var ids = new List<string>();
            foreach (var item in array)
            {
                var id = item?["id"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
    }
}
