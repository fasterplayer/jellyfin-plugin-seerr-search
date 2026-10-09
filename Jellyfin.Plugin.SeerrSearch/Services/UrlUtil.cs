using System;

namespace Jellyfin.Plugin.SeerrSearch.Services
{
    /// <summary>
    /// Every URL an administrator can type goes through here before it is used, whether it is
    /// requested by the server, written into a redirect or put in a link shown to users.
    /// Only plain http(s) URLs are accepted: no javascript:, file:, data: or ftp: scheme, no
    /// embedded credentials, no query string or fragment.
    /// </summary>
    public static class UrlUtil
    {
        /// <summary>The URL without a trailing slash, or an empty string when it is not acceptable.</summary>
        public static string Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var value = raw.Trim();

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                return string.Empty;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                return string.Empty;
            }

            if (string.IsNullOrEmpty(uri.Host)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                return string.Empty;
            }

            return value.TrimEnd('/');
        }
    }
}
