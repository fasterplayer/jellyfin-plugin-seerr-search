using System.Text.RegularExpressions;
using Jellyfin.Plugin.SeerrSearch.Services;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SeerrSearch.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public const string UnlinkedNotice = "notice";
        public const string UnlinkedHide = "hide";

        private static readonly Regex LanguagePattern = new(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})?$", RegexOptions.Compiled);

        /// <summary>
        /// Seerr URL as seen from the Jellyfin server (e.g. http://seerr:5055). No default value:
        /// the plugin stays dormant until an administrator fills it in.
        /// </summary>
        public string SeerrUrl { get; set; } = string.Empty;

        /// <summary>Public Seerr URL, used for the "Open in Seerr" links in the browser. Optional.</summary>
        public string SeerrPublicUrl { get; set; } = string.Empty;

        /// <summary>
        /// Public Jellyfin URL (e.g. https://jellyfin.example.com). Together with a public Seerr URL on the
        /// same parent domain, it lets "Open in Seerr" sign the user in automatically through Quick Connect.
        /// </summary>
        public string JellyfinPublicUrl { get; set; } = string.Empty;

        /// <summary>Seerr API key (Settings → General → API Key). Never leaves the server.</summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// The administrator states that the reverse proxy in front of Seerr forwards /SeerrSearch/ to Jellyfin.
        /// The plugin then skips the detection (useful when the Jellyfin server cannot reach its own public address).
        /// </summary>
        public bool AssumeRelay { get; set; }

        /// <summary>Turns the script injection and the calls to Seerr on or off.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Minimum number of characters before Seerr is queried.</summary>
        public int MinQueryLength { get; set; } = 3;

        /// <summary>Maximum number of results shown under the Jellyfin search results.</summary>
        public int MaxResults { get; set; } = 20;

        /// <summary>Hides titles that are already in the library (Jellyfin already lists them just above).</summary>
        public bool HideAvailable { get; set; } = true;

        /// <summary>
        /// Language passed to Seerr for titles and overviews (e.g. fr-CA, fr, en).
        /// Empty (the default) = follow each user's Jellyfin interface language, English if unknown.
        /// </summary>
        public string Language { get; set; } = string.Empty;

        /// <summary>Allows 4K requests (adds a 4K button) when the user has the permission in Seerr.</summary>
        public bool Allow4k { get; set; }

        /// <summary>Shows an "Open in Seerr" link in the detail sheet even without automatic sign-in.</summary>
        public bool ShowSeerrLink { get; set; }

        /// <summary>Stops the script injection without disabling the rest of the plugin.</summary>
        public bool DisableScriptInjection { get; set; }

        /// <summary>
        /// What a Jellyfin user who has no Seerr account sees: "notice" (a short message, the default)
        /// or "hide" (nothing at all).
        /// </summary>
        public string UnlinkedUserBehavior { get; set; } = UnlinkedNotice;

        /// <summary>Custom text for the "notice" behavior. Empty = the built-in translated message.</summary>
        public string UnlinkedUserMessage { get; set; } = string.Empty;

        /// <summary>Normalizes the values an administrator can type, so that bad input is never used.</summary>
        public void Sanitize()
        {
            SeerrUrl = UrlUtil.NormalizeSeerr(SeerrUrl);
            SeerrPublicUrl = UrlUtil.Normalize(SeerrPublicUrl);
            JellyfinPublicUrl = UrlUtil.Normalize(JellyfinPublicUrl);
            ApiKey = (ApiKey ?? string.Empty).Trim();
            MinQueryLength = System.Math.Clamp(MinQueryLength, 1, 10);
            MaxResults = System.Math.Clamp(MaxResults, 1, 60);

            Language = NormalizeLanguage(Language);

            UnlinkedUserBehavior = string.Equals(UnlinkedUserBehavior, UnlinkedHide, System.StringComparison.OrdinalIgnoreCase)
                ? UnlinkedHide
                : UnlinkedNotice;

            var message = (UnlinkedUserMessage ?? string.Empty).Trim();
            UnlinkedUserMessage = message.Length > 300 ? message[..300] : message;
        }

        /// <summary>
        /// A language code in the form TMDB expects ("fr", "fr-CA": language lower case, region upper case),
        /// or an empty string when the value does not look like a language code.
        /// </summary>
        public static string NormalizeLanguage(string? value)
        {
            var trimmed = (value ?? string.Empty).Trim().Replace('_', '-');

            if (!LanguagePattern.IsMatch(trimmed))
            {
                return string.Empty;
            }

            var parts = trimmed.Split('-');
            if (parts.Length == 1)
            {
                return parts[0].ToLowerInvariant();
            }

            var region = parts[1].Length == 2 ? parts[1].ToUpperInvariant() : parts[1];
            return parts[0].ToLowerInvariant() + "-" + region;
        }
    }
}
