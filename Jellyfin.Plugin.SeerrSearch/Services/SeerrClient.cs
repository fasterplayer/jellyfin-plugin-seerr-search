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
        private static string BaseUrl => UrlUtil.Normalize(Config?.SeerrUrl);

        /// <summary>Raw result of a Seerr call: the JSON body and the HTTP status, untouched.</summary>
        public sealed record SeerrResponse(HttpStatusCode StatusCode, string Body, IReadOnlyList<string> SetCookies)
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

        private async Task<SeerrResponse> SendAsync(
            HttpMethod method,
            string path,
            object? body,
            int? seerrUserId,
            bool anonymous,
            CancellationToken ct)
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

                return new SeerrResponse(response.StatusCode, content, cookies);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Timed out contacting Seerr ({Url})", url);
                return new SeerrResponse(HttpStatusCode.GatewayTimeout, "{\"error\":\"Seerr did not answer in time.\"}", Array.Empty<string>());
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Could not reach Seerr ({Url})", url);
                return new SeerrResponse(HttpStatusCode.BadGateway, "{\"error\":\"Could not reach Seerr.\"}", Array.Empty<string>());
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

        /// <summary>The Jellyfin accounts Seerr can import (ids without dashes), as Seerr lists them.</summary>
        public async Task<IReadOnlyList<string>> GetImportableJellyfinUserIdsAsync(CancellationToken ct = default)
        {
            var response = await GetAsync("/settings/jellyfin/users", null, ct).ConfigureAwait(false);
            var ids = new List<string>();

            if (response.IsSuccess && response.AsNode() is JsonArray array)
            {
                foreach (var item in array)
                {
                    var id = item?["id"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        ids.Add(id);
                    }
                }
            }

            return ids;
        }
    }
}
