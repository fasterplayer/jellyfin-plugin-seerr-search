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
            "Shows Seerr results under the Jellyfin search, with a Request button. "
            + "Each request is made as the Seerr account of the signed-in Jellyfin user.";

        /// <summary>Changes at every start and every configuration save, to break the browser cache of the script.</summary>
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
            // Whatever an administrator typed is normalized before it is stored or used.
            if (configuration is PluginConfiguration typed)
            {
                typed.Sanitize();
            }

            base.UpdateConfiguration(configuration);

            // BasePlugin already raises ConfigurationChanged; here we only renew the
            // cache key of the client script.
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
