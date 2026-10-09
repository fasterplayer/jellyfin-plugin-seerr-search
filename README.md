# Seerr Search

Un plugin Jellyfin qui ajoute les résultats de [Seerr](https://github.com/seerr-team/seerr)
sous la recherche du client web, avec un bouton **Demander**.

L'utilisateur cherche un film dans Jellyfin. Ce qui est déjà dans la bibliothèque s'affiche
normalement; juste en dessous, une section « Disponible sur demande » montre ce que Seerr
peut aller chercher. Un clic crée la demande — au nom du compte Seerr de cet utilisateur-là,
sans seconde connexion et sans mot de passe.

## Comment l'identité fonctionne

Le navigateur ne parle jamais à Seerr : il appelle le plugin, qui est déjà protégé par
l'authentification de Jellyfin. Le plugin reconnaît l'utilisateur, retrouve son compte Seerr
(Seerr conserve l'identifiant Jellyfin de chaque compte importé) et relaie l'appel avec les
en-têtes `X-Api-Key` et `X-Api-User`. Seerr traite alors la demande comme si l'utilisateur
l'avait faite lui-même : ses quotas, ses permissions, son nom sur la demande.

La clé API ne quitte pas le serveur.

## Installation

1. Tableau de bord → Extensions → Dépôts → ajouter
   `https://raw.githubusercontent.com/fasterplayer/jellyfin-plugin-seerr-search/main/manifest.json`
2. Installer **Seerr Search** depuis le catalogue.
3. Redémarrer Jellyfin.
4. Tableau de bord → Extensions → Mes extensions → Seerr Search.
5. Remplir l'URL de Seerr et la clé API, enregistrer, puis cliquer sur **Tester la connexion**.

## Configuration

| Réglage | À quoi ça sert |
|---|---|
| URL de Seerr | Adresse vue depuis le serveur Jellyfin, par exemple `http://seerr:5055`. |
| Clé API | Seerr → Paramètres → Général. Reste côté serveur. |
| URL publique | Facultatif; sert aux liens « voir la fiche dans Seerr ». |
| Langue | Code TMDB des titres et résumés (`fr`, `fr-CA`, `en`). |
| Masquer les titres déjà disponibles | Évite de répéter ce que Jellyfin affiche déjà juste au-dessus. |
| Autoriser les demandes 4K | Seerr applique quand même ses permissions par utilisateur. |

## Les utilisateurs doivent exister dans Seerr

Un utilisateur Jellyfin absent de Seerr voit un message le lui disant, et aucun bouton.
Le bouton **Importer les utilisateurs Jellyfin dans Seerr**, sur la page de configuration,
fait l'import en un clic (équivalent de Seerr → Utilisateurs → Importer depuis Jellyfin).

## Compatibilité

Le plugin s'injecte dans `index.html` au moment où la page est servie, par un middleware
ASP.NET. Rien n'est écrit sur le disque : pas besoin d'un dossier web accessible en écriture,
et une mise à jour de `jellyfin-web` n'efface pas l'injection.

| Cible de compilation | Jellyfin | .NET |
|---|---|---|
| `-p:JellyfinTarget=jf12` (défaut) | 12.x | net10.0 |
| `-p:JellyfinTarget=jf10` | 10.11.x | net9.0 |

Un même DLL ne sert qu'à une seule lignée; il faut publier un artefact par cible.

## Compilation

```bash
dotnet build -c Release                        # Jellyfin 12
dotnet build -c Release -p:JellyfinTarget=jf10 # Jellyfin 10.11
```

## Licence

GPLv3, comme l'écosystème des plugins Jellyfin. La méthode d'injection et l'usage de
`X-Api-User` s'inspirent de [Jellyfin Enhanced](https://github.com/n00bcodr/Jellyfin-Enhanced),
également sous GPLv3.
