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

        /// <summary>
        /// The Seerr address: like <see cref="Normalize"/>, and a trailing "/api/v1" (people paste the API address
        /// from the Seerr documentation) is dropped, since the plugin adds it itself.
        /// </summary>
        public static string NormalizeSeerr(string? raw)
        {
            var value = Normalize(raw);

            foreach (var suffix in new[] { "/api/v1", "/api" })
            {
                if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    value = value[..^suffix.Length].TrimEnd('/');
                    break;
                }
            }

            return value;
        }

        /// <summary>"host:port" of a URL, for messages shown to the administrator (never credentials or paths).</summary>
        public static string HostPort(string? url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? (uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port)
                : string.Empty;
        }

        /// <summary>True for localhost / loopback addresses: seen from a Docker container, that is the container itself.</summary>
        public static bool IsLoopback(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        }
    }
}
