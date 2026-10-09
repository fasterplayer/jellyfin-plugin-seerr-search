using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SeerrSearch.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrSearch.Controllers
{
    /// <summary>
    /// Le seul point de contact du navigateur. Chaque appel est authentifié par
    /// Jellyfin, puis traduit en appel Seerr au nom de l'utilisateur correspondant.
    /// La clé API ne sort jamais d'ici.
    /// </summary>
    [ApiController]
    [Route("SeerrSearch")]
    public class SeerrSearchController : ControllerBase
    {
        private static readonly string[] AllowedImageSizes = { "w92", "w154", "w185", "w342", "w500", "original" };
        private static readonly Regex ImagePathPattern = new(@"^/[A-Za-z0-9._-]{4,64}\.(jpg|png|webp)$", RegexOptions.Compiled);

        private readonly SeerrClient _seerr;
        private readonly SeerrUserMap _userMap;
        private readonly SeerrSso _sso;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<SeerrSearchController> _logger;

        public SeerrSearchController(
            SeerrClient seerr,
            SeerrUserMap userMap,
            SeerrSso sso,
            IHttpClientFactory httpClientFactory,
            ILogger<SeerrSearchController> logger)
        {
            _seerr = seerr;
            _userMap = userMap;
            _sso = sso;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>Le script client, servi sans authentification : c'est une balise &lt;script&gt; ordinaire.</summary>
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
            return Content(js, "application/javascript; charset=utf-8");
        }

        /// <summary>Ce que le navigateur a le droit de savoir : jamais la clé API.</summary>
        [HttpGet("config")]
        [Authorize]
        public async Task<ActionResult> GetClientConfig(CancellationToken ct)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return StatusCode((int)HttpStatusCode.ServiceUnavailable);
            }

            var userId = GetJellyfinUserId();
            int? seerrUserId = null;

            if (userId.HasValue && SeerrClient.IsConfigured)
            {
                seerrUserId = await _userMap.GetSeerrUserIdAsync(userId.Value, ct).ConfigureAwait(false);
            }

            return new JsonResult(new
            {
                enabled = config.Enabled && SeerrClient.IsConfigured,
                linked = seerrUserId.HasValue,
                minQueryLength = Math.Max(1, config.MinQueryLength),
                maxResults = Math.Clamp(config.MaxResults, 1, 60),
                hideAvailable = config.HideAvailable,
                allow4k = config.Allow4k,
                publicUrl = (config.SeerrPublicUrl ?? string.Empty).TrimEnd('/'),
                sso = _sso.IsReady,
                ssoMode = _sso.Mode,
                seerrHost = SeerrSso.PublicSeerrHostName,
                showSeerrLink = config.ShowSeerrLink || _sso.IsReady,
                language = config.Language
            });
        }

        /// <summary>Recherche Seerr, filtrée et bornée, au nom de l'utilisateur connecté.</summary>
        [HttpGet("search")]
        [Authorize]
        public async Task<ActionResult> Search([FromQuery] string? query, [FromQuery] int page = 1, CancellationToken ct = default)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.Enabled || !SeerrClient.IsConfigured)
            {
                return StatusCode((int)HttpStatusCode.ServiceUnavailable, new { error = "not-configured" });
            }

            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < Math.Max(1, config.MinQueryLength))
            {
                return new JsonResult(new { results = Array.Empty<object>() });
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
                Uri.EscapeDataString(config.Language ?? "fr"));

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
                    var mediaType = r["mediaType"]?.GetValue<string>();
                    return mediaType == "movie" || mediaType == "tv";
                })
                .Where(r => !config.HideAvailable || GetMediaStatus(r) != 5)
                .Take(Math.Clamp(config.MaxResults, 1, 60))
                .Select(r => new
                {
                    id = r["id"]?.GetValue<int>(),
                    mediaType = r["mediaType"]?.GetValue<string>(),
                    title = r["title"]?.GetValue<string>() ?? r["name"]?.GetValue<string>(),
                    overview = r["overview"]?.GetValue<string>(),
                    posterPath = r["posterPath"]?.GetValue<string>(),
                    date = r["releaseDate"]?.GetValue<string>() ?? r["firstAirDate"]?.GetValue<string>(),
                    status = GetMediaStatus(r),
                    status4k = GetMediaStatus(r, true)
                })
                .ToList();

            return new JsonResult(new { results = trimmed });
        }

        /// <summary>Crée la demande dans Seerr, attribuée au compte de l'utilisateur connecté.</summary>
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

            var seerrUserId = await ResolveSeerrUserIdAsync(ct).ConfigureAwait(false);
            if (seerrUserId == null)
            {
                return StatusCode((int)HttpStatusCode.Conflict, new { error = "user-not-linked" });
            }

            object body = payload.MediaType == "tv"
                ? new
                {
                    mediaType = "tv",
                    mediaId = payload.MediaId,
                    seasons = "all",
                    is4k = config.Allow4k && payload.Is4k
                }
                : new
                {
                    mediaType = "movie",
                    mediaId = payload.MediaId,
                    is4k = config.Allow4k && payload.Is4k
                };

            var response = await _seerr.PostAsync("/request", body, seerrUserId, ct).ConfigureAwait(false);

            if (response.IsSuccess)
            {
                _logger.LogInformation(
                    "Seerr Search : demande créée ({MediaType} {MediaId}) pour l'utilisateur Seerr {SeerrUserId}.",
                    payload.MediaType,
                    payload.MediaId,
                    seerrUserId);

                return new JsonResult(new { ok = true });
            }

            var message = response.AsNode()?["message"]?.GetValue<string>();
            return StatusCode((int)response.StatusCode, new { error = "seerr-error", message });
        }

        /// <summary>Relais des affiches TMDB, pour que le navigateur n'ait qu'un seul interlocuteur.</summary>
        [HttpGet("image")]
        [AllowAnonymous]
        public async Task<ActionResult> GetImage([FromQuery] string? p, [FromQuery] string size = "w185", CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(p) || !ImagePathPattern.IsMatch(p) || !AllowedImageSizes.Contains(size))
            {
                return BadRequest();
            }

            try
            {
                var client = _httpClientFactory.CreateClient(SeerrClient.NamedClient);
                client.Timeout = TimeSpan.FromSeconds(15);

                using var upstream = await client
                    .GetAsync($"https://image.tmdb.org/t/p/{size}{p}", ct)
                    .ConfigureAwait(false);

                if (!upstream.IsSuccessStatusCode)
                {
                    return StatusCode((int)upstream.StatusCode);
                }

                var bytes = await upstream.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                Response.Headers.CacheControl = "public, max-age=604800";

                return File(bytes, upstream.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return StatusCode((int)HttpStatusCode.BadGateway);
            }
        }

        /// <summary>Test de connexion pour la page de configuration (administrateurs).</summary>
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

            var status = await _seerr.GetAsync("/status", null, ct).ConfigureAwait(false);
            if (!status.IsSuccess)
            {
                return new JsonResult(new { ok = false, error = "unreachable", code = (int)status.StatusCode });
            }

            await _userMap.RefreshAsync(ct).ConfigureAwait(false);

            return new JsonResult(new
            {
                ok = true,
                version = status.AsNode()?["version"]?.GetValue<string>(),
                linkedUsers = _userMap.MappedUserCount,
                sso = _sso.IsReady,
                ssoMode = _sso.Mode,
                ssoReason = _sso.WhyNotReady()
            });
        }

        /// <summary>Importe les comptes Jellyfin dans Seerr (administrateurs), puis relit la table de correspondance.</summary>
        [HttpPost("import-users")]
        [Authorize]
        public async Task<ActionResult> ImportUsers(CancellationToken ct)
        {
            if (!IsAdministrator())
            {
                return StatusCode((int)HttpStatusCode.Forbidden);
            }

            var response = await _seerr.PostAsync("/user/import-from-jellyfin", new { }, null, ct).ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                return StatusCode((int)response.StatusCode, new { ok = false });
            }

            _userMap.Invalidate();
            await _userMap.RefreshAsync(ct).ConfigureAwait(false);

            var imported = response.AsNode() is JsonArray array ? array.Count : 0;

            return new JsonResult(new { ok = true, imported, linkedUsers = _userMap.MappedUserCount });
        }

        /// <summary>Signature du plugin : sert à détecter le relais /SeerrSearch/ sur l'hôte de Seerr.</summary>
        [HttpGet("ping")]
        [AllowAnonymous]
        public ActionResult Ping()
        {
            Response.Headers.CacheControl = "no-store";
            return Content(SeerrSso.PingBody, "text/plain; charset=utf-8");
        }

        /// <summary>Billet à usage unique pour ouvrir Seerr déjà connecté (voir SeerrSso).</summary>
        [HttpPost("sso/ticket")]
        [Authorize]
        public ActionResult IssueSsoTicket([FromBody] SsoTicketRequest? payload)
        {
            var userId = GetJellyfinUserId();
            if (!userId.HasValue)
            {
                return StatusCode((int)HttpStatusCode.Unauthorized);
            }

            var url = _sso.IssueTicket(userId.Value, payload?.To);
            if (url == null)
            {
                return StatusCode((int)HttpStatusCode.ServiceUnavailable, new { error = "sso-not-ready", reason = _sso.WhyNotReady() });
            }

            return new JsonResult(new { url });
        }

        /// <summary>
        /// Point d'arrivée de la navigation : consomme le billet, ouvre la session
        /// Seerr côté serveur, dépose le cookie sur le domaine parent et redirige.
        /// En cas de pépin, on atterrit sur la page de connexion de Seerr — jamais
        /// sur une erreur brute.
        /// </summary>
        [HttpGet("sso")]
        [AllowAnonymous]
        public async Task<ActionResult> Sso([FromQuery] string? t, [FromQuery] int hop = 0, CancellationToken ct = default)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(config.SeerrPublicUrl))
            {
                return NotFound();
            }

            var seerrPublic = config.SeerrPublicUrl.TrimEnd('/');
            var loginPage = seerrPublic + "/login";

            // Le cookie ne vaut que s'il est déposé depuis le bon hôte : celui de Seerr
            // en mode relais, celui de Jellyfin en mode domaine. Arrivé par une autre
            // adresse (IP locale, par exemple), on repasse par le bon hôte sans
            // consommer le billet — une seule fois.
            var expectedOrigin = (_sso.ProxyMode ? config.SeerrPublicUrl : config.JellyfinPublicUrl).TrimEnd('/');
            if (!Uri.TryCreate(expectedOrigin, UriKind.Absolute, out var expectedUri))
            {
                return Redirect(loginPage);
            }

            if (!string.Equals(Request.Host.Host, expectedUri.Host, StringComparison.OrdinalIgnoreCase))
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
                // On va quand même à la page visée : si une session Seerr existe
                // déjà dans ce navigateur, elle sert; sinon Seerr affichera lui-même
                // sa page de connexion, en gardant la cible.
                return Redirect(seerrPublic + ticket.To);
            }

            Response.Headers.SetCookie = result.CookieHeader!;
            Response.Headers.CacheControl = "no-store";

            return Redirect(seerrPublic + ticket.To);
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

        private static int GetMediaStatus(JsonNode result, bool fourK = false)
        {
            var key = fourK ? "status4k" : "status";
            var status = result["mediaInfo"]?[key];

            try
            {
                return status?.GetValue<int>() ?? 0;
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                return 0;
            }
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
