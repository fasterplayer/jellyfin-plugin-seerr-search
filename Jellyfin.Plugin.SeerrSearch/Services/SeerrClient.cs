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
    /// Toutes les communications avec Seerr. La clé API reste ici : le navigateur
    /// ne parle qu'au contrôleur du plugin, jamais à Seerr directement.
    ///
    /// L'identité est portée par l'en-tête X-Api-User, que Seerr interprète comme
    /// « agis au nom de cet utilisateur » quand la clé API est valide. C'est ce qui
    /// fait que les quotas, les permissions et la paternité des demandes sont ceux
    /// de l'utilisateur Jellyfin connecté, sans qu'il ait à se connecter à Seerr.
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
            && !string.IsNullOrWhiteSpace(Config.SeerrUrl)
            && !string.IsNullOrWhiteSpace(Config.ApiKey);

        private static string BaseUrl => (Config?.SeerrUrl ?? string.Empty).TrimEnd('/');

        /// <summary>Résultat brut d'un appel Seerr : le corps JSON et le code HTTP, tels quels.</summary>
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
        /// Appel sans clé API, comme le ferait un navigateur : sert au Quick Connect,
        /// dont la réponse porte le cookie de session à relayer.
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
                return new SeerrResponse(HttpStatusCode.ServiceUnavailable, "{\"error\":\"Seerr n'est pas configuré.\"}", Array.Empty<string>());
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

                using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
                var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Seerr a répondu {Status} pour {Method} {Path}",
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
                _logger.LogWarning("Délai dépassé en contactant Seerr ({Url})", url);
                return new SeerrResponse(HttpStatusCode.GatewayTimeout, "{\"error\":\"Seerr n'a pas répondu à temps.\"}", Array.Empty<string>());
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Impossible de joindre Seerr ({Url})", url);
                return new SeerrResponse(HttpStatusCode.BadGateway, "{\"error\":\"Impossible de joindre Seerr.\"}", Array.Empty<string>());
            }
        }

        /// <summary>Liste paginée des utilisateurs Seerr (clé API = droits admin).</summary>
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
    }
}
