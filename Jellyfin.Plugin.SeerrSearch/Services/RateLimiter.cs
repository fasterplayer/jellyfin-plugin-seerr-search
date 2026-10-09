using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SeerrSearch.Services
{
    /// <summary>
    /// Small in-memory sliding-window limiter (one minute), per user and per kind of call. It keeps a
    /// logged-in user (or a script using their token) from turning the plugin into a way to flood
    /// Seerr or TMDB.
    /// </summary>
    public class RateLimiter
    {
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        private readonly ConcurrentDictionary<string, Queue<DateTime>> _hits = new(StringComparer.Ordinal);
        private DateTime _lastSweepUtc = DateTime.UtcNow;

        /// <summary>Records a call and tells whether it is allowed: at most <paramref name="limitPerMinute"/> per minute.</summary>
        public bool Allow(string bucket, Guid? userId, int limitPerMinute)
        {
            var now = DateTime.UtcNow;
            var key = bucket + ":" + (userId?.ToString("N") ?? "anonymous");
            var queue = _hits.GetOrAdd(key, _ => new Queue<DateTime>());

            lock (queue)
            {
                while (queue.Count > 0 && now - queue.Peek() > Window)
                {
                    queue.Dequeue();
                }

                if (queue.Count >= limitPerMinute)
                {
                    return false;
                }

                queue.Enqueue(now);
            }

            Sweep(now);
            return true;
        }

        private void Sweep(DateTime now)
        {
            if (now - _lastSweepUtc < TimeSpan.FromMinutes(5))
            {
                return;
            }

            _lastSweepUtc = now;

            foreach (var pair in _hits)
            {
                lock (pair.Value)
                {
                    if (pair.Value.Count == 0 || now - pair.Value.Peek() > Window)
                    {
                        _hits.TryRemove(pair.Key, out _);
                    }
                }
            }
        }
    }
}
