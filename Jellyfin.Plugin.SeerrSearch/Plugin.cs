using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.SeerrSearch.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SeerrSearch
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public const string PluginRoute = "SeerrSearch";

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            ScriptCacheKey = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        public static Plugin? Instance { get; private set; }

        public override string Name => "Seerr Search";

        public override Guid Id => new Guid("e12caac0-0a7f-495a-a2b3-1a7b261b556b");

        public override string Description =>
            "Ajoute les résultats de Seerr sous la recherche Jellyfin, avec un bouton Demander, "
            + "chaque demande étant attribuée au compte Seerr de l'utilisateur connecté.";

        /// <summary>Change à chaque démarrage et à chaque sauvegarde de config, pour casser le cache du script.</summary>
        public string ScriptCacheKey { get; private set; }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            };
        }

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            base.UpdateConfiguration(configuration);

            // BasePlugin lève déjà ConfigurationChanged; ici on ne fait que
            // renouveler la clé de cache du script client.
            ScriptCacheKey = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        internal string BuildScriptTag() =>
            string.Format(
                CultureInfo.InvariantCulture,
                "<script plugin=\"{0}\" src=\"../{1}/script?v={2}\" defer></script>",
                Name,
                PluginRoute,
                ScriptCacheKey);
    }
}
