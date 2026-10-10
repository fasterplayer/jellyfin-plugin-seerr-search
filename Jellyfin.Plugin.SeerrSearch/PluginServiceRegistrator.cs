using System.Net.Http;
using Jellyfin.Plugin.SeerrSearch.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SeerrSearch
{
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            // No automatic redirects: an authentication proxy in front of Seerr (Authelia, Authentik…)
            // answers 302 to its login page. It is better to see that as an error than to follow it
            // and receive HTML instead of JSON. It also keeps the API key from being replayed to
            // another host by a redirect.
            serviceCollection.AddHttpClient(SeerrClient.NamedClient)
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AllowAutoRedirect = false,

                    // Seerr answers Quick Connect with a session cookie. The handler is pooled and shared: with a cookie
                    // container, the session of one user would be sent back to Seerr when the next user signs in, Seerr
                    // would re-use that session, and the first user's browser cookie would turn into the second user.
                    UseCookies = false
                });

            serviceCollection.AddSingleton<RateLimiter>();
            serviceCollection.AddSingleton<SeerrClient>();
            serviceCollection.AddSingleton<SeerrDetector>();
            serviceCollection.AddSingleton<SeerrUserMap>();
            serviceCollection.AddSingleton<SeerrSso>();
            serviceCollection.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>();
        }
    }
}
