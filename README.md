# Seerr Search

*Lire en français : [README.fr.md](README.fr.md)*

A Jellyfin plugin that shows the results of [Seerr](https://github.com/seerr-team/seerr) under the
search of the Jellyfin web client, with a **Request** button.

A user searches for a movie in Jellyfin. What is already in the library shows up as usual; right
below it, an "Available on request" section lists what Seerr can fetch. One click creates the
request, **as that user's own Seerr account**: no second sign-in, no password, and Seerr's quotas,
permissions and approval rules keep applying per user.

![Seerr results under the Jellyfin search](docs/screenshots/search-en.png)

## How identity works

The browser never talks to Seerr. It calls the plugin, which Jellyfin already protects with its own
authentication. The plugin recognizes the user, finds their Seerr account (Seerr keeps the Jellyfin id
of every imported account) and relays the call with the `X-Api-Key` and `X-Api-User` headers. Seerr then
handles the request as if the user had made it themselves: their quota, their permissions, their name on
the request.

**The Seerr API key never leaves the server.**

## Requirements

- Jellyfin **12.x** (built and tested on 12.2). The web client is the target: the section is added to its
  search page. The native apps that embed the web client show it too; apps with their own interface (Roku,
  Apple TV, etc.) are not affected.
- [Seerr](https://github.com/seerr-team/seerr) reachable **from the Jellyfin server** (tested with 3.5.0).
  The Seerr users must be Jellyfin users (imported from Jellyfin).

## Installation

1. Dashboard → Plugins → Repositories → add
   `https://raw.githubusercontent.com/fasterplayer/jellyfin-plugin-seerr-search/main/manifest.json`
2. Install **Seerr Search** from the catalog.
3. Restart Jellyfin.
4. Dashboard → Plugins → Seerr Search: enter the Seerr URL and the API key, save, then **Test the connection**.

Until the URL and the key are filled in, the plugin does nothing: the web client is not modified and
your users see no change.

## Configuration

![Configuration page](docs/screenshots/config-en.png)

| Setting | Purpose |
|---|---|
| Seerr URL | Address of Seerr **as seen from the Jellyfin server**, e.g. `http://seerr:5055`. Only `http` and `https`. |
| API key | Seerr → Settings → General → API Key. Stays on the server; the form never shows it again (leave the field empty to keep it). |
| Public Seerr URL | Optional. Used by the "Open in Seerr" link. |
| Public Jellyfin URL | Optional. With a public Seerr URL, enables the automatic sign-in (see below). |
| Language | Optional TMDB code (`en`, `fr`, `fr-CA`…) for titles and overviews. **Empty = each user's Jellyfin interface language.** |
| Minimum characters / maximum results | When Seerr is queried and how many cards are shown. |
| Hide titles already in the library | Jellyfin already lists them just above. |
| Allow 4K requests | Adds a "Request in 4K" button. Seerr still applies its own per-user permissions. |
| Users who have no Seerr account | Show a short message (default, optionally your own text) or show nothing. |
| Show the "Open in Seerr" link | Even without the automatic sign-in. |
| Do not inject the script | Only if another plugin takes care of the injection. |

### Users must exist in Seerr

A Jellyfin user who has no Seerr account sees a short message (or nothing, if you chose so) and no button.
The **Import the Jellyfin users into Seerr** button on the configuration page creates the missing accounts
with Seerr's default permissions, like Seerr → Users → Import from Jellyfin. **Test the connection** tells
you how many Jellyfin accounts are not linked yet.

### Automatic sign-in to Seerr (optional)

Tapping a card can open the Seerr page already signed in, through Quick Connect, with no password.
Quick Connect must be enabled in Jellyfin, and the public Seerr URL must be filled in. Two ways:

- **Relay (preferred, detected automatically).** The reverse proxy in front of Seerr forwards
  `/SeerrSearch/` to Jellyfin. The session cookie is then set by Seerr's own host, exactly as Seerr would.
- **Parent domain.** Jellyfin and Seerr share a parent domain (`jellyfin.example.com`, `seerr.example.com`)
  and the public Jellyfin URL is filled in. The cookie is set with `Domain=example.com`: it is therefore
  also sent to the other subdomains of that domain. Prefer the relay if other applications live there.

The **Test the connection** button says whether the automatic sign-in is ready and why not otherwise.
Without it, a tap opens a detail sheet inside Jellyfin (with the request buttons).

![Detail sheet](docs/screenshots/sheet-en.png)

## Languages

The interface follows the language of the Jellyfin web client: **English** and **French** are included.
Other languages fall back to English. To add one, add a block to `STRINGS` in
`Jellyfin.Plugin.SeerrSearch/js/seerr-search.js` and in `Configuration/configPage.html`: contributions welcome.

## Security

- The API key stays on the server: it is not part of any answer to the browser, and the configuration
  form does not load it back.
- Every endpoint requires a valid Jellyfin session, except three that return nothing private:
  `script` (the client script, loaded by a plain script tag), `ping` (a fixed string) and `sso` (it only
  redeems a one-time ticket obtained by an authenticated user). `status` and `import-users` are
  administrators only.
- The Seerr user a call acts for is always derived from the authenticated Jellyfin user, never from
  anything the browser sends.
- Seerr and public URLs are validated (plain `http`/`https`, no credentials, no query) and only
  administrators can set them. The Seerr URL may point to your LAN, by design. Redirects are not followed.
- Everything coming from Seerr (titles, overviews, error messages) is shown as text, never as HTML.
- The poster relay is authenticated, talks to a fixed host (`image.tmdb.org`), accepts only a plain image
  file name and a known size, and always answers with an image type.
- Searches, requests and posters are rate limited per user.
- Automatic sign-in tickets are single use, tied to the user who asked, valid 90 seconds, and the target
  path is restricted to a plain Seerr path.

Found something? See [SECURITY.md](SECURITY.md).

## Compatibility

| Build target | Jellyfin | .NET | Status |
|---|---|---|---|
| `-p:JellyfinTarget=jf12` (default) | 12.x | net10.0 | released, tested on 12.2 |
| `-p:JellyfinTarget=jf10` | 10.11.x | net9.0 | compiles in CI, **not tested, not released** |

The plugin adds its script by rewriting `index.html` while the page is served (an ASP.NET middleware).
Nothing is written to disk, so no writable web folder is needed and a `jellyfin-web` update does not erase
the injection. The script relies on three elements of the web client's search page (`#searchPage`,
`#searchTextInput`, `.searchResults`): a major web client update may require an update of the plugin.

## Troubleshooting

- **Nothing appears.** Check that the URL and the key are saved and that **Test the connection** is green.
  Reload the page with the browser cache cleared. The browser console shows messages prefixed with
  `[Seerr Search]`. Make sure the user has a Seerr account.
- **The section appears but searching fails.** Seerr must be reachable from the Jellyfin *server* (not from
  the browser): use the Docker service name or the LAN address.
- **"Seerr answers, but refuses the API key".** Copy the key again from Seerr → Settings → General.

## Build

```bash
dotnet build Jellyfin.Plugin.SeerrSearch/Jellyfin.Plugin.SeerrSearch.csproj -c Release                        # Jellyfin 12
dotnet build Jellyfin.Plugin.SeerrSearch/Jellyfin.Plugin.SeerrSearch.csproj -c Release -p:JellyfinTarget=jf10 # Jellyfin 10.11
```

`tests/mock-seerr.mjs` is a small fake Seerr (it checks the API key, records the acting user and the
language, and forwards Quick Connect to a real Jellyfin) that can be used to try the plugin without a real
Seerr; see the header of the file.

A release is made by pushing a tag equal to the version (`1.1.0.0`): the workflow builds, checks that the
tag, `Directory.Build.props` and `build.yaml` agree, publishes the zip and adds the version (with its
checksum) to `manifest.json`.

## License

GPL-3.0-only, like the rest of the Jellyfin plugin ecosystem (see [LICENSE](LICENSE)). The injection method
and the use of `X-Api-User` are inspired by [Jellyfin Enhanced](https://github.com/n00bcodr/Jellyfin-Enhanced),
also GPLv3.
