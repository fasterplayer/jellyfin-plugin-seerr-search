# Seerr Search

*Read in English: [README.md](README.md)*

Un plugin Jellyfin qui affiche les résultats de [Seerr](https://github.com/seerr-team/seerr) sous la
recherche du client web, avec un bouton **Demander**.

L'utilisateur cherche un film dans Jellyfin. Ce qui est déjà dans la bibliothèque s'affiche normalement;
juste en dessous, une section « Disponible sur demande » montre ce que Seerr peut aller chercher. Un clic
crée la demande **au nom du compte Seerr de cet utilisateur-là** : pas de seconde connexion, pas de mot de
passe, et les quotas, permissions et règles d'approbation de Seerr continuent de s'appliquer par utilisateur.

![Résultats Seerr sous la recherche Jellyfin](docs/screenshots/search-fr.png)

## Comment l'identité fonctionne

Le navigateur ne parle jamais à Seerr. Il appelle le plugin, que Jellyfin protège déjà par sa propre
authentification. Le plugin reconnaît l'utilisateur, retrouve son compte Seerr (Seerr conserve l'identifiant
Jellyfin de chaque compte importé) et relaie l'appel avec les en-têtes `X-Api-Key` et `X-Api-User`. Seerr
traite alors la demande comme si l'utilisateur l'avait faite lui-même : son quota, ses permissions, son nom
sur la demande.

**La clé API de Seerr ne quitte jamais le serveur.**

## Prérequis

- Jellyfin **12.x** (compilé et testé sur 12.2). La cible est le client web : la section s'ajoute à sa page
  de recherche. Les applications natives qui intègrent le client web l'affichent aussi; celles qui ont leur
  propre interface (Roku, Apple TV, etc.) ne sont pas touchées.
- [Seerr](https://github.com/seerr-team/seerr) joignable **depuis le serveur Jellyfin** (testé avec 3.5.0).
  Les utilisateurs Seerr doivent être des utilisateurs Jellyfin (importés depuis Jellyfin).

## Installation

1. Tableau de bord → Extensions → Dépôts → ajouter
   `https://raw.githubusercontent.com/fasterplayer/jellyfin-plugin-seerr-search/main/manifest.json`
2. Installer **Seerr Search** depuis le catalogue.
3. Redémarrer Jellyfin.
4. Tableau de bord → Extensions → Seerr Search : saisir l'URL de Seerr et la clé API, enregistrer, puis
   cliquer sur **Tester la connexion**.

Tant que l'URL et la clé ne sont pas remplies, le plugin ne fait rien : le client web n'est pas modifié et
vos utilisateurs ne voient aucun changement.

## Configuration

![Page de configuration](docs/screenshots/config-fr.png)

| Réglage | À quoi ça sert |
|---|---|
| URL de Seerr | Adresse de Seerr **vue depuis le serveur Jellyfin**, par exemple `http://seerr:5055` (même réseau Docker) ou `http://192.168.1.10:5055`. Jamais `localhost` dans Docker. `http` et `https` seulement, rien après le port; un `/api/v1` final est retiré. |
| Clé API | Seerr → Paramètres → Général → Clé API. Reste côté serveur; le formulaire ne la réaffiche jamais (laisser le champ vide pour la conserver). |
| URL publique de Seerr | Facultatif. L'adresse que vos utilisateurs tapent **dans leur navigateur** pour joindre Seerr. Sert au lien « Ouvrir dans Seerr » et à la connexion automatique. |
| URL publique de Jellyfin | Facultatif. L'adresse que vos utilisateurs tapent dans leur navigateur pour joindre Jellyfin. Avec une URL publique de Seerr, active la connexion automatique (voir plus bas). |
| Mon proxy inverse relaie `/SeerrSearch/` | Facultatif. Normalement détecté tout seul. À cocher seulement si le test dit que le serveur ne peut pas joindre l'adresse publique de Seerr (pas de bouclage sur votre réseau) alors que votre proxy relaie bien le chemin. |
| Langue | Code TMDB facultatif (`en`, `fr`, `fr-CA`…) pour les titres et résumés. **Vide = la langue de l'interface Jellyfin de chaque utilisateur.** |
| Caractères minimum / résultats maximum | Quand Seerr est interrogé et combien de cartes sont affichées. |
| Masquer les titres déjà dans la bibliothèque | Jellyfin les affiche déjà juste au-dessus. |
| Autoriser les demandes 4K | Ajoute un bouton « Demander en 4K ». Seerr applique quand même ses permissions par utilisateur. |
| Utilisateurs sans compte Seerr | Afficher un court message (par défaut, avec votre propre texte si vous voulez) ou ne rien afficher. |
| Afficher le lien « Ouvrir dans Seerr » | Même sans connexion automatique. |
| Ne pas injecter le script | Seulement si un autre plugin s'occupe de l'injection. |

### Les utilisateurs doivent exister dans Seerr

Un utilisateur Jellyfin sans compte Seerr voit un court message (ou rien, si vous l'avez choisi) et aucun
bouton. Le bouton **Importer les utilisateurs Jellyfin dans Seerr** de la page de configuration crée les
comptes manquants avec les permissions par défaut de Seerr, comme Seerr → Utilisateurs → Importer depuis
Jellyfin. **Tester la connexion** indique combien de comptes Jellyfin ne sont pas encore liés.

### Connexion automatique à Seerr (facultatif)

Un appui sur une carte peut ouvrir la fiche Seerr déjà connectée, par Quick Connect, sans mot de passe. Le
Quick Connect doit être activé dans Jellyfin (il l'est par défaut) et l'URL publique de Seerr remplie. Trois
façons, essayées dans cet ordre :

- **Relais (préférable, détecté tout seul).** Le proxy inverse devant Seerr relaie `/SeerrSearch/` vers
  Jellyfin. Le cookie de session est alors posé par l'hôte de Seerr lui-même, comme Seerr le ferait.
- **Même machine, ports différents.** Jellyfin et Seerr sont sur la même machine et ne diffèrent que par le port
  (`http://192.168.1.10:8096` et `http://192.168.1.10:5055`, ou `http://nas.lan:8096` et `http://nas.lan:5055`).
  Les cookies ignorent le port : un simple cookie d'hôte posé par Jellyfin est donc aussi envoyé à Seerr. Cela
  marche avec une adresse IP nue et ne demande ni proxy, ni domaine, ni TLS.
- **Domaine parent.** Jellyfin et Seerr sont des sous-domaines d'un même domaine (`jellyfin.exemple.com`,
  `seerr.exemple.com`) et l'URL publique de Jellyfin est remplie. Le cookie est posé avec
  `Domain=exemple.com` : il est donc aussi envoyé aux autres sous-domaines. Préférez le relais si d'autres
  applications vivent sur ce domaine.

Le bouton **Tester la connexion** dit si la connexion automatique est prête, laquelle des trois elle utilise et,
sinon, pourquoi. Sans elle, un appui ouvre une fiche à l'intérieur de Jellyfin (avec les boutons de demande) :
rien n'est cassé, vous restez simplement dans Jellyfin. Voir les [Scénarios d'installation](#scénarios-dinstallation)
pour des montages prêts à l'emploi.

![Fiche](docs/screenshots/sheet-en.png)

## Scénarios d'installation

La recherche et les demandes ne demandent que **deux** réglages : l'**URL de Seerr** et la **clé API**. Les deux
URL *publiques* ne servent qu'au lien « Ouvrir dans Seerr » et à la connexion automatique; le plugin fonctionne
sans elles. Ne les confondez pas :

| Réglage | Qui s'en sert | Quoi y mettre |
|---|---|---|
| URL de Seerr | le **serveur** Jellyfin, pour appeler Seerr | l'adresse qui marche **depuis la machine (ou le conteneur) qui héberge Jellyfin** |
| URL publique de Seerr | les **navigateurs** des utilisateurs | ce que vos utilisateurs tapent pour joindre Seerr |
| URL publique de Jellyfin | les **navigateurs** (connexion automatique seulement) | ce que vos utilisateurs tapent pour joindre Jellyfin |

### Réseau local simple : adresses IP et ports, sans proxy ni domaine

Jellyfin sur `http://192.168.1.10:8096`, Seerr sur `http://192.168.1.10:5055` (même machine) :

| Réglage | Valeur |
|---|---|
| URL de Seerr | `http://192.168.1.10:5055` |
| URL publique de Seerr | `http://192.168.1.10:5055` |
| URL publique de Jellyfin | `http://192.168.1.10:8096` |

Le test annonce alors *Connexion automatique à Seerr : prête (même machine, ports différents)* : un appui sur une
carte ouvre Seerr déjà connecté sous le nom de cet utilisateur. Si vos utilisateurs joignent Jellyfin par un nom
(`http://nas.lan:8096`), utilisez ce nom, pour les deux URL publiques, à la place de l'adresse IP.

Comme les cookies ignorent les ports, une autre application de la même machine qui utiliserait un cookie nommé
`connect.sid` le partagerait avec Seerr. C'est le fonctionnement des navigateurs, et celui de Seerr lui-même.

**Jellyfin et Seerr sur deux machines différentes (deux adresses IP)**, disons Jellyfin sur
`http://192.168.1.10:8096` et Seerr sur `http://192.168.1.20:5055` : un navigateur ne peut pas partager une
connexion entre deux hôtes, donc pas de connexion automatique, et la fiche de détail à l'intérieur de Jellyfin est
le repli complet (rien n'est cassé) :

| Réglage | Valeur |
|---|---|
| URL de Seerr | `http://192.168.1.20:5055` |
| URL publique de Seerr | `http://192.168.1.20:5055` |
| URL publique de Jellyfin | à laisser vide (ou à remplir, cela ne change rien ici) |
| Afficher le lien « Ouvrir dans Seerr » même sans connexion automatique | à cocher |

Un appui sur une carte ouvre la fiche : **Demander**, **Demander en 4K** (si permis, et si l'utilisateur en a le
droit dans Seerr; sinon le refus de Seerr est affiché) et le lien **Ouvrir dans Seerr** vers l'adresse locale (un
nouvel onglet, où l'utilisateur se connecte à Seerr comme d'habitude). **Tester la connexion** dit pourquoi la
connexion automatique est inactive et ce qui l'activerait.

### Docker : quelle URL de Seerr ?

`localhost` et `127.0.0.1` ne marchent jamais depuis un conteneur : ils désignent le conteneur Jellyfin lui-même.

- Les deux dans le même fichier compose, ou sur un réseau commun : le **nom du service/conteneur**,
  `http://seerr:5055`. Deux piles séparées peuvent partager un réseau : `docker network create media`, puis le
  déclarer `external: true` dans les deux fichiers (ou `docker network connect media jellyfin`).
- Sinon, l'**adresse IP de la machine** et le port *publié* : `http://192.168.1.10:5055`.
- Jellyfin en `network_mode: host` : `http://127.0.0.1:5055` marche, puisque c'est alors la machine elle-même.

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin:12.2
    ports: ["8096:8096"]
    networks: [media]
  seerr:
    image: ghcr.io/seerr-team/seerr:latest
    ports: ["5055:5055"]
    networks: [media]
networks:
  media:
```

URL de Seerr : `http://seerr:5055`. Les URL publiques sont ce que tapent les utilisateurs
(`http://192.168.1.10:5055`, ou votre domaine).

### Proxy inverse

Avec un proxy, donnez à Jellyfin et à Seerr un nom d'hôte chacun (`jellyfin.exemple.com`, `seerr.exemple.com`).
Le plugin demande **une** règle de plus, pour la connexion automatique : sur le nom d'hôte de **Seerr**, le chemin
`/SeerrSearch/` va vers **Jellyfin**. Le cookie de session est alors posé par l'hôte de Seerr lui-même, comme Seerr
le ferait. Le plugin détecte la règle tout seul (il interroge `https://seerr.exemple.com/SeerrSearch/ping`).

Sans cette règle, les deux noms fonctionnent quand même grâce au cookie de *domaine parent* (ils partagent
`exemple.com`), et sans domaine commun les cartes ouvrent simplement la fiche. Quel que soit le proxy, transmettez
l'en-tête `Host` d'origine (tous les fichiers ci-dessous le font).

| Proxy | Fichier à utiliser | Testé ici |
|---|---|---|
| nginx | [`docs/proxy/nginx.conf`](docs/proxy/nginx.conf) | **Oui** : http, et https (certificat auto-signé) |
| Caddy 2 | [`docs/proxy/Caddyfile`](docs/proxy/Caddyfile) | **Oui** : Caddy 2.11, http, avec la route de relais et sans (repli sur la fiche de détail) |
| Traefik v3 (étiquettes Docker) | [`docs/proxy/traefik-labels.yml`](docs/proxy/traefik-labels.yml) | **Oui** : Traefik 3.7, http, avec et sans la route (les lignes du résolveur de certificats n'ont pas été exercées) |
| Nginx Proxy Manager | [`docs/proxy/npm-advanced.txt`](docs/proxy/npm-advanced.txt) : onglet *Advanced* de l'hôte proxy de Seerr, ou son onglet *Custom locations* | **Oui** : 2.16, les deux façons, avec et sans la route (réglé par l'API de NPM avec les champs des formulaires; les formulaires web eux-mêmes n'ont pas été cliqués) |
| cloudflared (ingress du tunnel) | [`docs/proxy/cloudflared.yml`](docs/proxy/cloudflared.yml) | **Règles seulement** : vérifiées par `ingress validate` / `ingress rule` de cloudflared; pas passées par un vrai tunnel |

Réglages du plugin avec un proxy : URL de Seerr `http://seerr:5055` (le proxy n'intervient pas de ce côté), URL
publique de Seerr `https://seerr.exemple.com`, URL publique de Jellyfin `https://jellyfin.exemple.com`.

À savoir :

- Mettez le TLS du proxy devant comme d'habitude; le plugin ne voit que les URL publiques. Le *serveur* parle à
  Seerr par l'adresse interne en clair, donc aucun certificat n'intervient de ce côté. Si le serveur doit appeler
  une adresse `https://` de Seerr à certificat auto-signé, il la refusera (le test le dit) : utilisez l'adresse
  interne `http://`.
- Si le serveur **ne peut pas joindre sa propre adresse publique** (beaucoup de routeurs domestiques ne bouclent
  pas), le relais ne peut pas être détecté : le test dit *Le serveur n'a pas pu joindre l'adresse publique de
  Seerr*, et la case *Mon proxy inverse relaie /SeerrSearch/* active la fonction sans détection.
- Une couche d'authentification devant Seerr (Authelia, Authentik, Cloudflare Access…) doit laisser passer
  `/SeerrSearch/*` sur le nom d'hôte de Seerr, comme tout chemin qui n'est pas celui de Seerr (**non testé**).
- Si le proxy de Seerr envoie `X-Frame-Options` ou un `frame-ancestors` restrictif, Seerr s'ouvre dans un nouvel
  onglet plutôt que dans un cadre à l'intérieur de Jellyfin. C'est détecté, rien à faire.
- Le réglage *Paramètres → Réseau → Protection CSRF* de Seerr peut rester activé : le plugin s'en occupe.
- Le Quick Connect doit être activé dans Jellyfin (Tableau de bord → Général; actif par défaut), et la connexion
  par compte Jellyfin dans Seerr (**non testé** quand elle est désactivée : la connexion automatique retombe alors
  sur la page de connexion de Seerr).

### Jellyfin sous un sous-chemin (`/jellyfin`)

Réglez *Tableau de bord → Réseau → URL de base* à `/jellyfin` (ne retirez pas le préfixe dans le proxy) et
utilisez `https://maison.exemple.com/jellyfin` comme URL publique de Jellyfin; le plugin suit l'URL de base tout
seul. **Seerr ne peut pas fonctionner sous un sous-chemin** (sa documentation indique que seuls les sous-domaines
sont pris en charge) : donnez-lui un nom d'hôte à lui. Avec un proxy, la règle de relais ajoute alors l'URL de
base; voir [`docs/proxy/nginx-subpath.conf`](docs/proxy/nginx-subpath.conf) (**testé**, ainsi qu'un accès direct
`http://192.168.1.10:8096/jellyfin`).

### HTTP et HTTPS, et le cookie

La connexion automatique pose un seul cookie, `HttpOnly; SameSite=Lax; Path=/`, avec ces différences :

| Montage | `Domain` | `Secure` |
|---|---|---|
| Relais sur l'hôte de Seerr | aucun (cookie d'hôte) | si l'URL publique de Seerr est en https |
| Même machine, ports différents | aucun (cookie d'hôte) | si les deux URL publiques sont en https |
| Domaine parent | le domaine parent commun | si les deux URL publiques sont en https |

`Secure` n'est jamais posé quand il rendrait le cookie inutile : un navigateur refuse un cookie `Secure` posé en
http simple, et ne l'envoie jamais à un Seerr en http. Le cadre dans Jellyfin n'est utilisé que sur une page
https, avec un Seerr en https, du même site, et quand le proxy de Seerr n'interdit pas les cadres; sinon Seerr
s'ouvre dans un nouvel onglet.

### Ce qui a été testé

Chaque ligne a été jouée dans un navigateur (Chromium 140 piloté par Playwright) contre un Jellyfin 12.2.0
**neuf** et un Seerr 3.5.0 **neuf**, le plugin étant installé depuis un dépôt de plugins, configuré par sa propre
page, avec le test de connexion, l'import des utilisateurs, une recherche, une demande, l'appui sur « Ouvrir dans
Seerr » et un utilisateur sans compte Seerr.

| Cas | Montage | Résultat |
|---|---|---|
| A1 | Réseau local, une machine, adresse IP + deux ports, sans proxy, sans TLS, sans domaine | OK, la connexion automatique marche (même machine) |
| A2 | Réseau local, Jellyfin et Seerr sur deux hôtes différents | OK : la fiche offre Demander, Demander en 4K (accordée et refusée par Seerr) et le lien « Ouvrir dans Seerr » vers l'adresse locale; aucune erreur; le test explique pourquoi la connexion est inactive |
| B | Mauvaise adresse de Seerr : `localhost` avec un port faux ou bon, mauvais port sur l'IP, hôte inconnu, sans `http://`, `https` sur un port http, suffixe `/api/v1`, paramètres d'URL, Jellyfin au lieu de Seerr, une redirection, un 401, une page qui n'est pas Seerr, un 502, une mauvaise clé | OK, un message clair pour chacun (anglais et français) |
| C | nginx, Caddy, Traefik, Nginx Proxy Manager : sous-domaines d'un nom local, relais | OK (4 sur 4) |
| C | nginx sans la règle de relais (cookie de domaine parent); nginx en https | OK |
| C | règles d'ingress cloudflared | règles vérifiées, tunnel non lancé |
| D | Jellyfin avec URL de base `/jellyfin`, en direct; et derrière nginx avec Seerr sur son propre hôte | OK |
| D | Seerr sous un sous-chemin | non pris en charge par Seerr |
| E | Attributs du cookie en http/https pour chaque mode (10 combinaisons) et cadre en https | OK |
| En plus | Seerr avec protection CSRF; deux utilisateurs qui se connectent l'un après l'autre; proxy de Seerr qui interdit les cadres; serveur qui ne joint pas son adresse publique; mise à jour depuis 1.1.0.0 par le catalogue | OK |

**Non testé** : Jellyfin 10.11 (la compilation passe, rien de plus), Jellyseerr et Overseerr, les applications
mobiles et TV de Jellyfin, les proxys d'authentification devant Seerr, un vrai tunnel Cloudflare, les adresses
IPv6, Seerr avec la connexion Jellyfin désactivée.

## Langues

L'interface suit la langue du client web Jellyfin : **anglais** et **français** sont inclus. Les autres
langues retombent sur l'anglais. Pour en ajouter une, ajouter un bloc à `STRINGS` dans
`Jellyfin.Plugin.SeerrSearch/js/seerr-search.js` et dans `Configuration/configPage.html` : les
contributions sont les bienvenues.

## Sécurité

- La clé API reste sur le serveur : elle ne fait partie d'aucune réponse au navigateur, et le formulaire de
  configuration ne la recharge pas.
- Chaque point d'accès exige une session Jellyfin valide, sauf trois qui ne renvoient rien de privé :
  `script` (le script client, chargé par une simple balise script), `ping` (une chaîne fixe) et `sso` (il ne
  fait qu'encaisser un billet à usage unique obtenu par un utilisateur authentifié). `status` et
  `import-users` sont réservés aux administrateurs.
- L'utilisateur Seerr au nom duquel un appel agit est toujours déduit de l'utilisateur Jellyfin
  authentifié, jamais de ce qu'envoie le navigateur.
- Les URL de Seerr et publiques sont validées (`http`/`https` simples, sans identifiants ni paramètres) et
  seuls les administrateurs peuvent les saisir. L'URL de Seerr peut pointer vers votre réseau local, c'est
  voulu. Les redirections ne sont pas suivies.
- Tout ce qui vient de Seerr (titres, résumés, messages d'erreur) est affiché comme du texte, jamais comme du HTML.
- Le relais d'affiches est authentifié, parle à un hôte fixe (`image.tmdb.org`), n'accepte qu'un nom de
  fichier image simple et une taille connue, et répond toujours par un type d'image.
- Les recherches, demandes et affiches sont limitées en débit par utilisateur.
- Les billets de connexion automatique sont à usage unique, liés à l'utilisateur qui les a demandés,
  valables 90 secondes, et la cible est restreinte à un chemin Seerr simple.

Une faille ? Voir [SECURITY.md](SECURITY.md).

## Compatibilité

| Cible de compilation | Jellyfin | .NET | État |
|---|---|---|---|
| `-p:JellyfinTarget=jf12` (défaut) | 12.x | net10.0 | publiée, testée sur 12.2 |
| `-p:JellyfinTarget=jf10` | 10.11.x | net9.0 | compile en CI, **non testée, non publiée** |

Le plugin ajoute son script en réécrivant `index.html` au moment où la page est servie (un middleware
ASP.NET). Rien n'est écrit sur le disque : pas besoin d'un dossier web accessible en écriture, et une mise à
jour de `jellyfin-web` n'efface pas l'injection. Le script s'appuie sur trois éléments de la page de
recherche du client web (`#searchPage`, `#searchTextInput`, `.searchResults`) : une mise à jour majeure du
client web peut exiger une mise à jour du plugin.

## Dépannage

- **Rien n'apparaît.** Vérifiez que l'URL et la clé sont enregistrées et que **Tester la connexion** est au
  vert. Rechargez la page en vidant le cache du navigateur. La console du navigateur affiche des messages
  préfixés `[Seerr Search]`. Vérifiez que l'utilisateur a un compte Seerr.
- **La section apparaît mais la recherche échoue.** Seerr doit être joignable depuis le *serveur* Jellyfin
  (pas depuis le navigateur) : utilisez le nom du service Docker ou l'adresse du réseau local.
- **Tester la connexion signale un problème.** Le message nomme l'adresse essayée :

  | Message | Sens et remède |
  |---|---|
  | *Impossible de trouver l'hôte « x »* | Orthographe, ou conteneurs sans réseau commun : nom du service sur un réseau commun, ou IP de la machine. |
  | *Connexion refusée par x:5055* | Rien n'écoute là : mauvais port, Seerr arrêté, ou `localhost` dans Docker. |
  | *Pas de réponse de x (délai dépassé)* | Un pare-feu, ou une mauvaise adresse. |
  | *La connexion sécurisée à x a échoué* | Une adresse `https://` au certificat non reconnu par ce serveur, ou un port http. Utilisez l'adresse interne `http://`. |
  | *x redirige vers y* | Mettez y dans le champ (souvent l'adresse `https://`). |
  | *x répond 401 / 403* | Un proxy de connexion ou un pare-feu devant Seerr : utilisez l'adresse directe de Seerr. |
  | *x répond 404 / n'est pas Seerr* | Mauvais port ou chemin, ou une autre application. |
  | *Seerr répond, mais refuse la clé API* | Recopiez la clé depuis Seerr → Paramètres → Général → Clé API. |

- **Connexion automatique : « non disponible — … ».** Le test donne la raison : une URL publique manque, le Quick
  Connect est désactivé dans Jellyfin, ou les deux adresses sont sur des hôtes différents sans relais (même machine
  avec des ports différents, sous-domaines d'un même domaine, ou règle de relais de votre proxy). Quand le serveur
  ne peut pas joindre l'adresse publique, cochez *Mon proxy inverse relaie /SeerrSearch/*.
- **Un appui sur une carte ouvre la page de connexion de Seerr.** Le billet à usage unique n'a pas pu être encaissé :
  vérifiez que le proxy transmet `Host`, que `/SeerrSearch/` atteint Jellyfin sur le nom d'hôte de Seerr, et le
  journal de Jellyfin (`Seerr Search SSO`).
- **Un champ s'est vidé après l'enregistrement.** L'adresse n'était pas une URL simple `http(s)://hôte:port` : le
  formulaire dit maintenant quoi retirer (nom d'utilisateur, mot de passe, parties `?` ou `#`).

## Compilation

```bash
dotnet build Jellyfin.Plugin.SeerrSearch/Jellyfin.Plugin.SeerrSearch.csproj -c Release                        # Jellyfin 12
dotnet build Jellyfin.Plugin.SeerrSearch/Jellyfin.Plugin.SeerrSearch.csproj -c Release -p:JellyfinTarget=jf10 # Jellyfin 10.11
```

`tests/mock-seerr.mjs` est un petit faux Seerr (il vérifie la clé API, note l'utilisateur et la langue, et
relaie le Quick Connect à un vrai Jellyfin) qui permet d'essayer le plugin sans vrai Seerr; voir l'en-tête du
fichier.

Une version se publie en poussant une étiquette égale au numéro de version (`1.2.0.0`) : le workflow compile,
vérifie que l'étiquette, `Directory.Build.props` et `build.yaml` concordent, publie le zip et ajoute la
version (avec sa somme de contrôle) à `manifest.json`.

## Licence

GPL-3.0-only, comme le reste de l'écosystème des plugins Jellyfin (voir [LICENSE](LICENSE)). La méthode
d'injection et l'usage de `X-Api-User` s'inspirent de
[Jellyfin Enhanced](https://github.com/n00bcodr/Jellyfin-Enhanced), également sous GPLv3.
