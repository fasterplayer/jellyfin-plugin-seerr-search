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
    /// Connexion automatique à Seerr depuis un lien Jellyfin.
    ///
    /// Le problème : une session Seerr est un cookie sur le domaine de Seerr, et
    /// un navigateur qui arrive depuis Jellyfin n'en a pas. Seerr n'accepte ni
    /// jeton Jellyfin ni paramètre d'URL, et un cookie SameSite ne peut être
    /// déposé que par une navigation de premier niveau — pas par un fetch, pas
    /// par un iframe.
    ///
    /// La solution, dans les deux cas : le navigateur, authentifié, demande un
    /// billet à usage unique, puis navigue vers /SeerrSearch/sso?t=billet. Le
    /// plugin fait le Quick Connect côté serveur — initie chez Seerr, autorise
    /// le code pour cet utilisateur via IQuickConnect, termine chez Seerr —,
    /// reçoit le cookie de session de Seerr, le redépose dans la réponse et
    /// redirige vers Seerr, qui reconnaît la session. Aucun mot de passe.
    ///
    /// Où le cookie est déposé dépend du mode :
    ///   - Mode relais (préféré) : le proxy devant Seerr envoie
    ///     seerr.x.ca/SeerrSearch/sso au serveur Jellyfin. Le cookie est alors
    ///     déposé par l'hôte de Seerr lui-même, identique à celui que Seerr
    ///     pose — rien à distinguer, rien qui traîne après une déconnexion.
    ///     Détecté automatiquement : seerr.x.ca/SeerrSearch/ping répond.
    ///   - Mode domaine : Jellyfin et Seerr partagent un domaine parent
    ///     (jellyfin.x.ca et seerr.x.ca); le cookie est déposé par Jellyfin
    ///     avec Domain=x.ca, que le navigateur envoie aussi à Seerr.
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

        /// <summary>Vrai si l'hôte public de Seerr relaie /SeerrSearch/ vers ce serveur.</summary>
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
            var publicUrl = (Plugin.Instance?.Configuration.SeerrPublicUrl ?? string.Empty).TrimEnd('/');
            if (string.IsNullOrEmpty(publicUrl))
            {
                return false;
            }

            try
            {
                var client = _httpClientFactory.CreateClient(SeerrClient.NamedClient);
                client.Timeout = TimeSpan.FromSeconds(8);
                var body = client.GetStringAsync(publicUrl + "/" + Plugin.PluginRoute + "/ping").GetAwaiter().GetResult();
                var ok = body.Trim() == PingBody;
                _logger.LogInformation("Seerr Search : relais /SeerrSearch/ sur l'hôte de Seerr — {State}.", ok ? "présent" : "absent");
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

        /// <summary>Domaine parent commun aux deux hôtes publics, ou vide si le SSO n'est pas possible.</summary>
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

                // Il faut au moins « domaine.tld » en commun; un seul label (ex. « ca ») ne suffit pas.
                return common >= 2 ? string.Join('.', la.Take(common).Reverse()) : string.Empty;
            }
        }

        public bool IsReady =>
            SeerrClient.IsConfigured
            && _quickConnect.IsEnabled
            && !string.IsNullOrEmpty(PublicSeerrHost)
            && (ProxyMode || (!string.IsNullOrEmpty(CookieDomain) && !string.IsNullOrEmpty(PublicJellyfinHost)));

        public string Mode => !IsReady ? "off" : ProxyMode ? "proxy" : "domain";

        public string? WhyNotReady()
        {
            if (!SeerrClient.IsConfigured)
            {
                return "URL ou clé API Seerr manquante.";
            }

            if (string.IsNullOrEmpty(PublicSeerrHost))
            {
                return "Il manque l'URL publique de Seerr.";
            }

            if (!_quickConnect.IsEnabled)
            {
                return "Le Quick Connect est désactivé dans Jellyfin (Tableau de bord → Général).";
            }

            if (ProxyMode)
            {
                return null;
            }

            if (string.IsNullOrEmpty(PublicJellyfinHost))
            {
                return "Il manque l'URL publique de Jellyfin (ou un relais de /SeerrSearch/ sur l'hôte de Seerr).";
            }

            if (string.IsNullOrEmpty(CookieDomain))
            {
                return "Les URL publiques de Jellyfin et de Seerr ne partagent pas de domaine parent, et l'hôte de Seerr ne relaie pas /SeerrSearch/.";
            }

            return null;
        }

        /// <summary>Crée un billet à usage unique et renvoie l'URL de départ, ou null si le SSO n'est pas prêt.</summary>
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

            // En mode relais, le billet s'encaisse sur l'hôte de Seerr; sinon sur celui de Jellyfin.
            var config = Plugin.Instance!.Configuration;
            var origin = (ProxyMode ? config.SeerrPublicUrl : config.JellyfinPublicUrl).TrimEnd('/');
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

        /// <summary>Le Quick Connect complet, côté serveur, pour cet utilisateur.</summary>
        public async Task<SignInResult> SignInAsync(Guid userId, CancellationToken ct)
        {
            var initiate = await _seerr.PostAnonymousAsync("/auth/jellyfin/quickconnect/initiate", null, ct).ConfigureAwait(false);
            if (!initiate.IsSuccess)
            {
                return Fail("Seerr a refusé d'initier le Quick Connect (" + (int)initiate.StatusCode + ").");
            }

            var node = initiate.AsNode();
            var code = node?["code"]?.GetValue<string>();
            var secret = node?["secret"]?.GetValue<string>();

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(secret))
            {
                return Fail("Réponse d'initiation incomplète.");
            }

            try
            {
                if (!await _quickConnect.AuthorizeRequest(userId, code).ConfigureAwait(false))
                {
                    return Fail("Jellyfin a refusé d'autoriser le code.");
                }
            }
            catch (Exception ex)
            {
                return Fail("Autorisation du code impossible : " + ex.Message);
            }

            var auth = await _seerr.PostAnonymousAsync("/auth/jellyfin/quickconnect/authenticate", new { secret }, ct).ConfigureAwait(false);
            if (!auth.IsSuccess)
            {
                return Fail("Seerr a refusé la session (" + (int)auth.StatusCode + ") : " + (auth.AsNode()?["message"]?.GetValue<string>() ?? "sans détail"));
            }

            var sessionCookie = auth.SetCookies.FirstOrDefault(c => c.Contains('=', StringComparison.Ordinal));
            if (sessionCookie == null)
            {
                return Fail("Seerr n'a pas renvoyé de cookie de session.");
            }

            var nameValue = sessionCookie.Split(';', 2)[0].Trim();
            var secure = (Plugin.Instance?.Configuration.SeerrPublicUrl ?? string.Empty).StartsWith("https", StringComparison.OrdinalIgnoreCase);

            // Mode relais : cookie d'hôte, exactement comme Seerr le poserait.
            // Mode domaine : cookie de domaine parent, pour qu'il voyage jusqu'à Seerr.
            var header = nameValue
                + (ProxyMode ? string.Empty : "; Domain=" + CookieDomain)
                + "; Path=/; Max-Age=2592000; HttpOnly; SameSite=Lax"
                + (secure ? "; Secure" : string.Empty);

            _logger.LogInformation("Seerr Search : session Seerr ouverte par Quick Connect pour l'utilisateur {UserId}.", userId);

            return new SignInResult(true, header, null);
        }

        private SignInResult Fail(string message)
        {
            _logger.LogWarning("Seerr Search SSO : {Message}", message);
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
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
        }

        private static bool LooksLikeIp(string host) => System.Net.IPAddress.TryParse(host, out _);
    }
}
