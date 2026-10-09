using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SeerrSearch.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>URL interne de Seerr, vue depuis le serveur Jellyfin (ex. http://seerr:5055).</summary>
        public string SeerrUrl { get; set; } = string.Empty;

        /// <summary>URL publique de Seerr, pour les liens « Ouvrir dans Seerr » du navigateur. Facultatif.</summary>
        public string SeerrPublicUrl { get; set; } = string.Empty;

        /// <summary>
        /// URL publique de Jellyfin (ex. https://jellyfin.mondomaine.ca). Avec une URL publique
        /// de Seerr sur le même domaine parent, le lien « Ouvrir dans Seerr » connecte
        /// l'utilisateur automatiquement par Quick Connect.
        /// </summary>
        public string JellyfinPublicUrl { get; set; } = string.Empty;

        /// <summary>Clé API Seerr (Paramètres → Général → Clé API). Ne quitte jamais le serveur.</summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>Active l'injection du script et les appels à Seerr.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Nombre minimum de caractères avant d'interroger Seerr.</summary>
        public int MinQueryLength { get; set; } = 3;

        /// <summary>Nombre maximum de résultats affichés sous la recherche Jellyfin.</summary>
        public int MaxResults { get; set; } = 20;

        /// <summary>Masque les titres déjà disponibles dans la bibliothèque (ils sont déjà dans les résultats Jellyfin).</summary>
        public bool HideAvailable { get; set; } = true;

        /// <summary>Langue passée à Seerr pour les titres et résumés (ex. fr-CA, fr, en).</summary>
        public string Language { get; set; } = "fr";

        /// <summary>Demande les séries en entier. Sinon, seules les saisons manquantes sont demandées.</summary>
        public bool RequestAllSeasons { get; set; } = true;

        /// <summary>Autorise les demandes 4K quand l'utilisateur en a la permission dans Seerr.</summary>
        public bool Allow4k { get; set; }

        /// <summary>Affiche un lien « Ouvrir dans Seerr » dans la fiche. Hors de Jellyfin, la session Seerr n'existe pas.</summary>
        public bool ShowSeerrLink { get; set; }

        /// <summary>Coupe l'injection du script sans désactiver le reste du plugin.</summary>
        public bool DisableScriptInjection { get; set; }
    }
}
