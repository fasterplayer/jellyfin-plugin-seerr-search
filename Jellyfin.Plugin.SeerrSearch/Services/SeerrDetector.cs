using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;

namespace Jellyfin.Plugin.SeerrSearch.Services
{
    /// <summary>
    /// Looks for a Seerr on the usual addresses, so that an administrator rarely has to type the URL: the container
    /// names people give it (seerr, jellyseerr, overseerr), the Docker host, this machine, the gateway of the
    /// container network and the host name the administrator used to open the dashboard, all on Seerr's default port
    /// 5055. A candidate counts only when <c>/api/v1/status</c> answers like Seerr does. The list is fixed (nothing the
    /// browser sends is requested except a plain host name or IPv4 address), the call is made without the API key,
    /// and the endpoint that uses it is for administrators only.
    /// </summary>
    public class SeerrDetector
    {
        public const int DefaultPort = 5055;

        private static readonly string[] KnownNames = { "seerr", "jellyseerr", "overseerr", "host.docker.internal", "localhost" };
        private static readonly Regex PlainHost = new(@"^(?:[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(?:\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$", RegexOptions.Compiled);

        private readonly IHttpClientFactory _httpClientFactory;

        public SeerrDetector(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public sealed record Detected(string Url, string Version);

        /// <param name="requestHost">The host name the administrator's browser used (without port), tried as one more candidate.</param>
        public async Task<IReadOnlyList<Detected>> DetectAsync(string? requestHost, CancellationToken ct)
        {
            var hosts = Candidates(requestHost);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));

            var tasks = hosts.Select(h => ProbeAsync("http://" + h + ":" + DefaultPort, deadline.Token)).ToList();

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Each probe handles its own failure; only a cancellation can get here, and what finished is kept.
            }

            return tasks
                .Where(t => t.IsCompletedSuccessfully && t.Result != null)
                .Select(t => t.Result!)
                .ToList();
        }

        private static List<string> Candidates(string? requestHost)
        {
            var hosts = new List<string>(KnownNames);

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    var props = nic.GetIPProperties();

                    foreach (var gateway in props.GatewayAddresses)
                    {
                        AddIfPrivate(hosts, gateway.Address);
                    }

                    foreach (var unicast in props.UnicastAddresses)
                    {
                        AddIfPrivate(hosts, unicast.Address);
                    }
                }
            }
            catch (Exception)
            {
                // Some platforms refuse to list their interfaces: the fixed names are still tried.
            }

            var asked = (requestHost ?? string.Empty).Trim().Trim('.');
            if (asked.Length > 0 && asked.Length <= 253 && PlainHost.IsMatch(asked) && !hosts.Contains(asked, StringComparer.OrdinalIgnoreCase))
            {
                hosts.Add(asked);
            }

            return hosts.Take(24).ToList();
        }

        private static void AddIfPrivate(List<string> hosts, IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any))
            {
                return;
            }

            var b = address.GetAddressBytes();
            var isPrivate = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
            var text = address.ToString();

            if (isPrivate && !hosts.Contains(text))
            {
                hosts.Add(text);
            }
        }

        private async Task<Detected?> ProbeAsync(string baseUrl, CancellationToken ct)
        {
            try
            {
                var client = _httpClientFactory.CreateClient(SeerrClient.NamedClient);
                client.Timeout = TimeSpan.FromSeconds(4);
                client.MaxResponseContentBufferSize = 64 * 1024;

                using var response = await client.GetAsync(baseUrl + "/api/v1/status", ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var node = JsonNode.Parse(body);
                var version = node?["version"]?.GetValue<string>();

                // Seerr (and its predecessors) always send commitTag next to the version.
                return !string.IsNullOrEmpty(version) && node?["commitTag"] != null
                    ? new Detected(baseUrl, version.Length > 40 ? version[..40] : version)
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
