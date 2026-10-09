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
            // Pas de redirection automatique : un proxy d'authentification devant Seerr
            // (Authelia, Authentik…) répond 302 vers sa page de login; mieux vaut le voir
            // comme une erreur que suivre la redirection et recevoir du HTML à la place du JSON.
            serviceCollection.AddHttpClient(SeerrClient.NamedClient)
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    AllowAutoRedirect = false
                });

            serviceCollection.AddSingleton<SeerrClient>();
            serviceCollection.AddSingleton<SeerrUserMap>();
            serviceCollection.AddSingleton<SeerrSso>();
            serviceCollection.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>();
        }
    }
}
