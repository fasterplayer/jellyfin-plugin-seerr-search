# Changelog

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
