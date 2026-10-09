using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrSearch.Services
{
    /// <summary>
    /// Fait le pont entre un utilisateur Jellyfin et son compte Seerr.
    ///
    /// Seerr garde l'identifiant Jellyfin de chaque utilisateur importé
    /// (champ jellyfinUserId). On s'en sert comme clé : pas de configuration
    /// par utilisateur, pas de mot de passe, et un utilisateur renommé reste lié.
    /// Le résultat est mis en cache et rafraîchi quand un identifiant manque à l'appel.
    /// </summary>
    public class SeerrUserMap
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

        private readonly SeerrClient _seerr;
        private readonly ILogger<SeerrUserMap> _logger;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private readonly ConcurrentDictionary<string, int> _map = new(StringComparer.OrdinalIgnoreCase);

        private DateTime _lastRefreshUtc = DateTime.MinValue;

        public SeerrUserMap(SeerrClient seerr, ILogger<SeerrUserMap> logger)
        {
            _seerr = seerr;
            _logger = logger;

            if (Plugin.Instance != null)
            {
                Plugin.Instance.ConfigurationChanged += (_, _) => Invalidate();
            }
        }

        public void Invalidate()
        {
            _map.Clear();
            _lastRefreshUtc = DateTime.MinValue;
        }

        public int MappedUserCount => _map.Count;

        /// <summary>
        /// Identifiant Seerr correspondant à cet utilisateur Jellyfin, ou null s'il
        /// n'a pas encore été importé dans Seerr.
        /// </summary>
        public async Task<int?> GetSeerrUserIdAsync(Guid jellyfinUserId, CancellationToken ct = default)
        {
            var key = Normalize(jellyfinUserId);

            if (_map.TryGetValue(key, out var cached) && DateTime.UtcNow - _lastRefreshUtc < CacheTtl)
            {
                return cached;
            }

            await RefreshAsync(ct).ConfigureAwait(false);

            return _map.TryGetValue(key, out var found) ? found : null;
        }

        public async Task RefreshAsync(CancellationToken ct = default)
        {
            // Un seul rafraîchissement à la fois; les autres appels attendent puis
            // relisent le cache que le premier vient de remplir.
            if (!await _refreshLock.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                if (DateTime.UtcNow - _lastRefreshUtc < TimeSpan.FromSeconds(10))
                {
                    return;
                }

                var users = await _seerr.GetUsersAsync(ct).ConfigureAwait(false);
                if (users.Count == 0)
                {
                    return;
                }

                _map.Clear();

                foreach (var user in users)
                {
                    var jellyfinId = user["jellyfinUserId"]?.GetValue<string>();
                    var id = user["id"]?.GetValue<int>();

                    if (!string.IsNullOrWhiteSpace(jellyfinId) && id.HasValue)
                    {
                        _map[Normalize(jellyfinId)] = id.Value;
                    }
                }

                _lastRefreshUtc = DateTime.UtcNow;
                _logger.LogInformation("Seerr Search : {Count} utilisateurs liés à des comptes Jellyfin.", _map.Count);
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        /// <summary>Jellyfin sert ses identifiants sans tirets, Seerr les stocke tels quels : on compare sur la même forme.</summary>
        private static string Normalize(Guid id) => id.ToString("N");

        private static string Normalize(string id) => id.Replace("-", string.Empty, StringComparison.Ordinal);
    }
}
