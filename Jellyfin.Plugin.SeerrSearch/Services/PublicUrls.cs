using System;

namespace Jellyfin.Plugin.SeerrSearch.Services
{
    /// <summary>
    /// The address the users' browsers use to reach Seerr. What the administrator typed always wins; when the field
    /// is empty, the "Application URL" of Seerr's own settings (Settings → General) is used, so that a normal
    /// installation needs no second address. The value read from Seerr is only kept in memory and is validated
    /// like everything else (plain http/https, no credentials, no query).
    /// </summary>
    public static class PublicUrls
    {
        private static volatile string _fromSeerr = string.Empty;

        /// <summary>The Application URL read from Seerr (normalized), or empty.</summary>
        public static string SeerrFromSeerr => _fromSeerr;

        public static void SetSeerrFromSeerr(string? value) => _fromSeerr = UrlUtil.Normalize(value);

        /// <summary>The public Seerr address in use: the configured one, else Seerr's Application URL, else empty.</summary>
        public static string Seerr
        {
            get
            {
                var configured = UrlUtil.Normalize(Plugin.Instance?.Configuration.SeerrPublicUrl);
                return configured.Length > 0 ? configured : _fromSeerr;
            }
        }

        /// <summary>True when <see cref="Seerr"/> comes from Seerr rather than from the configuration.</summary>
        public static bool SeerrIsFromSeerr =>
            UrlUtil.Normalize(Plugin.Instance?.Configuration.SeerrPublicUrl).Length == 0 && _fromSeerr.Length > 0;

        public static string Jellyfin => UrlUtil.Normalize(Plugin.Instance?.Configuration.JellyfinPublicUrl);
    }
}
