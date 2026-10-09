using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrSearch.Services
{
    /// <summary>
    /// Bridges a Jellyfin user and their Seerr account.
    ///
    /// Seerr keeps the Jellyfin id of every imported user (field jellyfinUserId). It is used
    /// as the key: no per-user configuration, no password, and a renamed user stays linked.
    /// The result is cached and refreshed when an id is missing from a call.
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

        /// <summary>Whether this Jellyfin user (id without dashes) is known to be linked, from the current cache.</summary>
        public bool IsMapped(string jellyfinUserIdNoDashes) => _map.ContainsKey(Normalize(jellyfinUserIdNoDashes));

        /// <summary>
        /// The Seerr id matching this Jellyfin user, or null when they have not been
        /// imported into Seerr yet.
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
            // One refresh at a time; the other calls wait, then read the cache the
            // first one has just filled.
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
                _logger.LogInformation("Seerr Search: {Count} Seerr users are linked to Jellyfin accounts.", _map.Count);
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        /// <summary>Jellyfin serves its ids without dashes while Seerr may store either form: compare on the same form.</summary>
        private static string Normalize(Guid id) => id.ToString("N");

        private static string Normalize(string id) => id.Replace("-", string.Empty, StringComparison.Ordinal);
    }
}
