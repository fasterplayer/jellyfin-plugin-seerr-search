# Changelog

## 1.3.0.0

Made simple to set up: two fields at most, everything else optional and folded. An existing configuration behaves
exactly as before (the settings, their names and their defaults are unchanged).

- New: **Quick start** at the top of the configuration page. Seerr is looked for on the usual addresses (container
  names `seerr`, `jellyseerr`, `overseerr`, this machine, the Docker host, the container gateway, the
  host name the dashboard was opened at; port 5055) the first time the page is opened, and its address is filled in.
  A **Look for Seerr** button repeats the search and offers each answer. Administrators only, fixed list of
  candidates, no API key sent, a candidate counts only if it answers like Seerr.
- New: one **Save and test** button saves the page and runs the connection test; a green *Ready* message tells the
  administrator it works. A missing `http://` on the Seerr address is added.
- New: the **advanced settings** (public addresses, automatic sign-in, language, limits, 4K, messages, injection) are
  folded under one heading and open by themselves when one of them is not the default.
- New: the public Seerr address is taken from **Seerr's own *Application URL*** when the field is left empty (typed
  value first; hostile or malformed values are ignored). The "Open in Seerr" link then shows by itself.
- New: the page offers the address it was opened at as the public Jellyfin URL.
- New: the test **warns** when Seerr has not finished its first-run setup or is set up for Plex (then no Jellyfin
  account can match).
- Changed: messages say what to fix in the field they concern; the automatic sign-in is described as optional and its
  "not available" reason says that search and requests work without it; a wrong key says to copy the whole key.
- Tests: the suite gained the guided installation in the browser (English and French), the address search with its
  counter-tests (a page that is not Seerr, JSON without a version, Seerr stopped, rate limit, non-administrators),
  the Application URL (used, empty, hostile, outranked by a typed address) and the warnings; the fake Seerr serves
  `/settings/public` and `/settings/main`.
## 1.2.0.0

Made for installations that are not the author's: a plain LAN with IP addresses, Docker, other reverse proxies, a
sub-path, http or https. Each case was run in a browser against a new Jellyfin 12.2 and a new Seerr 3.5.0 (see
"What was tested" in the README). Nothing changes for an existing setup that works.

- Fix (security): the plugin kept Seerr's session cookies in a shared HTTP handler. When two users signed in to
  Seerr within a couple of minutes, the second sign-in re-used the first user's session at Seerr, so the first
  user's browser cookie could turn into the second user (and a repeated sign-in of the same user got no cookie at
  all). The plugin no longer stores any cookie.
- Fix: Seerr with *Settings → Network → CSRF protection* switched on refused every request and every automatic
  sign-in (403 "invalid csrf token"). The plugin now fetches and sends the token Seerr asks for.
- New: automatic sign-in on the **same host with different ports** (`http://192.168.1.10:8096` and
  `http://192.168.1.10:5055`), bare IP addresses included: no proxy, no domain, no TLS needed. The test says which
  of the three modes is used.
- Fix: the cookie's `Secure` flag now depends on the schemes of both addresses (it was derived from Seerr's only, so
  an http Jellyfin with an https Seerr produced a cookie the browser dropped).
- New: the connection test tells what is wrong: name not found, connection refused, timeout, certificate/TLS,
  redirect, sign-in proxy (401/403), not Seerr (404 or other page), server error, wrong key — with the address it
  tried, a hint for `localhost` inside Docker, in English and French. It used to say "Seerr does not answer (502)".
- New: the sign-in diagnosis names the cause when the relay cannot be detected, and a *My reverse proxy relays
  /SeerrSearch/* setting skips the detection for networks that do not loop back to their own public address.
- Fix: URLs typed with a user name, a query string or a `#` part were silently emptied when saved; the form now says
  what to remove (also for an address without `http://`). A trailing `/api/v1` on the Seerr URL is dropped. A slow
  connection test no longer overwrites a newer message.
- Fix: the frame that shows Seerr inside Jellyfin is only used when Seerr is on https and its proxy does not forbid
  frames (`X-Frame-Options`, `frame-ancestors`); otherwise Seerr opens in a tab instead of a blank frame.
- Fix: a reverse proxy that passes `X-Forwarded-Host` instead of the original `Host` no longer breaks the ticket redemption.
- Fix: the relay probe no longer fails on Seerr's large 404 page.
- Docs: "Installation scenarios" (LAN, Docker, reverse proxy by product, sub-path, http/https, troubleshooting) and
  tested configuration files for nginx, Caddy, Traefik, Nginx Proxy Manager, cloudflared in `docs/proxy/`.

## 1.1.0.0

First public version. The plugin works as before for existing installations (same settings, same
behavior), and is now meant for other people's servers.

- Configuration page in the Jellyfin dashboard, in English and French (follows the interface language).
  A first install, with nothing configured, does nothing and says so on that page. The API key is never
  loaded back into the form.
- English and French interface for the search section, following each user's Jellyfin language. The Seerr
  language is the user's own unless one is set in the settings (no built-in French default any more).
- New: choose what users who have no Seerr account see (a message, your own message, or nothing).
- New: working "Request in 4K" button when 4K requests are allowed.
- New: the import button only imports the accounts that are missing, and says how many are not linked.
- New: the connection test also checks the API key and counts the unlinked accounts.
- Security: the poster relay now requires a Jellyfin session (it was open to anyone who could reach the
  server) and always answers with an image type; posters are fetched with the user's token.
- Security: URLs typed in the settings are validated (plain http/https, no credentials, no query string);
  other schemes can no longer be used for server requests, redirects or links.
- Security: per-user rate limits on searches, requests and posters; search text length limit.
- Security: the script is not injected until Seerr is configured.
- Fix: a "409 Conflict" from Seerr (already requested) is no longer confused with "user not linked".
- Fix: the unused "request all seasons" setting was removed.
- Fix: language codes are normalized the way TMDB expects (`fr-ca` becomes `fr-CA`).

## 1.0.0.0

Initial version (private use).
