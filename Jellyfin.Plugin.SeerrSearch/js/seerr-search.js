/*
 * Seerr Search — client
 *
 * Adds an "Available on request" section under the Jellyfin search results. The script only
 * talks to the plugin (/SeerrSearch/*), never to Seerr: the API key stays on the server and
 * the identity comes from the Jellyfin token already present in the browser.
 *
 * Every string shown to the user goes through t() (English, French). Everything that comes
 * from the server or from Seerr is escaped (escapeHtml) or inserted as text (textContent).
 */
(function () {
    'use strict';

    var LOG = '[Seerr Search]';
    var SECTION_ID = 'seerrSearchSection';
    var DEBOUNCE_MS = 450;

    var config = null;
    var lastQuery = '';
    var debounceTimer = null;
    var inFlight = null;

    var STRINGS = {
        en: {
            heading: 'Available on request',
            via: 'via Seerr',
            movie: 'Movie',
            series: 'Series',
            request: 'Request',
            requestRest: 'Request the rest',
            request4k: 'Request in 4K',
            sending: 'Sending…',
            requested: 'Requested ✓',
            retry: 'Try again',
            alreadyRequested: 'Already requested.',
            quotaOrPermission: 'Quota reached or permission denied in Seerr.',
            refused: 'Seerr refused the request.',
            rateLimited: 'Too many requests. Wait a moment and try again.',
            notLinked: 'Your account is not linked to Seerr yet. Ask the administrator to import the Jellyfin users into Seerr.',
            close: 'Close',
            openInSeerr: 'Open in Seerr',
            back: '‹ Jellyfin',
            statusUnknown: 'Unknown',
            statusPending: 'Pending approval',
            statusProcessing: 'Processing',
            statusPartial: 'Partially available',
            statusAvailable: 'Already in the library'
        },
        fr: {
            heading: 'Disponible sur demande',
            via: 'via Seerr',
            movie: 'Film',
            series: 'Série',
            request: 'Demander',
            requestRest: 'Demander la suite',
            request4k: 'Demander en 4K',
            sending: 'Envoi…',
            requested: 'Demandé ✓',
            retry: 'Réessayer',
            alreadyRequested: 'Déjà demandé.',
            quotaOrPermission: 'Quota atteint ou permission refusée dans Seerr.',
            refused: 'Seerr a refusé la demande.',
            rateLimited: 'Trop de demandes. Patientez un instant puis réessayez.',
            notLinked: 'Votre compte n’est pas encore lié à Seerr. Demandez à l’administrateur d’importer les utilisateurs Jellyfin dans Seerr.',
            close: 'Fermer',
            openInSeerr: 'Ouvrir dans Seerr',
            back: '‹ Jellyfin',
            statusUnknown: 'Inconnu',
            statusPending: 'En attente d’approbation',
            statusProcessing: 'En traitement',
            statusPartial: 'Partiellement disponible',
            statusAvailable: 'Déjà dans la bibliothèque'
        }
    };

    /* Seerr media status: 1 unknown, 2 pending, 3 processing, 4 partially available, 5 available. */
    var STATUS = {
        1: { key: 'statusUnknown', requestable: true },
        2: { key: 'statusPending', requestable: false },
        3: { key: 'statusProcessing', requestable: false },
        4: { key: 'statusPartial', requestable: true },
        5: { key: 'statusAvailable', requestable: false }
    };

    /* The language of the Jellyfin interface (the web client sets <html lang>), else the browser's. */
    function uiLocale() {
        var fromPage = document.documentElement && document.documentElement.getAttribute('lang');
        var fromBrowser = (navigator.languages && navigator.languages[0]) || navigator.language;
        var value = String(fromPage || fromBrowser || 'en').replace('_', '-');

        if (!/^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})?$/.test(value)) {
            return 'en';
        }

        // TMDB form: language lower case, two-letter region upper case (fr-ca -> fr-CA).
        var parts = value.split('-');
        parts[0] = parts[0].toLowerCase();
        if (parts[1] && parts[1].length === 2) {
            parts[1] = parts[1].toUpperCase();
        }

        return parts.join('-');
    }

    function t(key) {
        var lang = uiLocale().toLowerCase().split('-')[0];
        var table = STRINGS[lang] || STRINGS.en;

        return table[key] || STRINGS.en[key] || key;
    }

    function apiUrl(path) {
        var base = window.ApiClient && window.ApiClient.serverAddress ? window.ApiClient.serverAddress() : '';
        return (base || '').replace(/\/$/, '') + '/SeerrSearch/' + path;
    }

    function authHeaders() {
        var token = window.ApiClient && window.ApiClient.accessToken ? window.ApiClient.accessToken() : null;
        return token ? { Authorization: 'MediaBrowser Token="' + token + '"' } : {};
    }

    function api(path, options) {
        var opts = options || {};
        var headers = Object.assign({ Accept: 'application/json' }, authHeaders(), opts.headers || {});

        return fetch(apiUrl(path), {
            method: opts.method || 'GET',
            headers: headers,
            body: opts.body
        });
    }

    function escapeHtml(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function injectStyles() {
        if (document.getElementById('seerrSearchStyles')) {
            return;
        }

        var style = document.createElement('style');
        style.id = 'seerrSearchStyles';
        style.textContent = [
            '#' + SECTION_ID + '{margin:1.5em 0 2em;padding:0 3.3%;}',
            '#' + SECTION_ID + ' .seerrHeader{display:flex;align-items:baseline;gap:.8em;margin-bottom:.6em;}',
            '#' + SECTION_ID + ' .seerrHeader h2{margin:0;font-size:1.3em;font-weight:500;}',
            '#' + SECTION_ID + ' .seerrHeader .seerrHint{opacity:.6;font-size:.85em;}',
            '#' + SECTION_ID + ' .seerrGrid{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:1em;}',
            '#' + SECTION_ID + ' .seerrCard{display:flex;flex-direction:column;background:rgba(255,255,255,.05);border-radius:.5em;overflow:hidden;}',
            '#' + SECTION_ID + ' .seerrPoster{width:100%;aspect-ratio:2/3;object-fit:cover;background:rgba(255,255,255,.08);}',
            '#' + SECTION_ID + ' .seerrBody{padding:.6em .7em .7em;display:flex;flex-direction:column;gap:.35em;flex:1;}',
            '#' + SECTION_ID + ' .seerrTitle{font-size:.95em;font-weight:500;line-height:1.25;}',
            '#' + SECTION_ID + ' .seerrMeta{font-size:.8em;opacity:.6;}',
            '.seerrActions{margin-top:auto;display:flex;gap:.4em;flex-wrap:wrap;align-items:center;}',
            '.seerrState{font-size:.8em;opacity:.75;}',
            '.seerrActions button.seerrRequest{border:0;border-radius:.3em;padding:.45em .7em;',
            'font-size:.85em;font-weight:500;cursor:pointer;background:#00a4dc;color:#fff;}',
            '.seerrActions button.seerrRequest:hover{filter:brightness(1.1);}',
            '.seerrActions button.seerrRequest[disabled]{opacity:.55;cursor:default;filter:none;}',
            '.seerrError{font-size:.8em;color:#ff8a80;}',
            '#' + SECTION_ID + ' .seerrNotice{opacity:.7;font-size:.9em;}',
            '#' + SECTION_ID + ' .seerrCard{cursor:pointer;}',
            '#' + SECTION_ID + ' .seerrCard:hover{background:rgba(255,255,255,.1);}',
            '.seerrOverlay{position:fixed;inset:0;z-index:1000;background:rgba(0,0,0,.75);',
            'display:flex;align-items:center;justify-content:center;padding:1.2em;}',
            '.seerrSheet{background:#1c1c1c;color:#fff;border-radius:.6em;max-width:44em;width:100%;',
            'max-height:88vh;overflow:auto;display:flex;gap:1.2em;padding:1.2em;}',
            '.seerrSheet .seerrSheetPoster{width:10em;flex-shrink:0;border-radius:.4em;align-self:flex-start;}',
            '.seerrSheet .seerrSheetBody{display:flex;flex-direction:column;gap:.6em;flex:1;min-width:0;}',
            '.seerrSheet h3{margin:0;font-size:1.25em;font-weight:500;}',
            '.seerrSheet .seerrSheetMeta{opacity:.6;font-size:.85em;}',
            '.seerrSheet .seerrSheetOverview{font-size:.9em;line-height:1.45;opacity:.85;}',
            '.seerrSheet .seerrSheetActions{display:flex;gap:.6em;flex-wrap:wrap;margin-top:.4em;align-items:center;}',
            '.seerrSheet .seerrActions{margin-top:0;gap:.6em;}',
            '.seerrSheet button{border:0;border-radius:.3em;padding:.55em .9em;font-size:.9em;',
            'font-weight:500;cursor:pointer;}',
            '.seerrSheet button.seerrRequest{background:#00a4dc;color:#fff;}',
            '.seerrSheet button.seerrClose{background:rgba(255,255,255,.14);color:#fff;}',
            '.seerrSheet button[disabled]{opacity:.55;cursor:default;}',
            '.seerrSheet .seerrState,.seerrSheet .seerrError{font-size:.85em;}',
            '@media (max-width:600px){.seerrSheet{flex-direction:column;}',
            '.seerrSheet .seerrSheetPoster{width:7em;align-self:center;}}',
            '.seerrFrame{position:fixed;inset:0;z-index:1100;background:#101010;display:flex;flex-direction:column;}',
            '.seerrFrameBar{display:flex;align-items:center;gap:.5em;padding:.4em .6em;',
            'padding-top:calc(.4em + env(safe-area-inset-top));background:#1c1c1c;color:#fff;flex-shrink:0;}',
            '.seerrFrameBar button{border:0;background:transparent;color:#fff;font-size:1em;padding:.4em .6em;cursor:pointer;border-radius:.3em;}',
            '.seerrFrameBar button:hover{background:rgba(255,255,255,.12);}',
            '.seerrFrameBar .seerrFrameTitle{flex:1;text-align:center;opacity:.7;font-size:.9em;}',
            '.seerrFrame iframe{flex:1;width:100%;border:0;background:#101010;}'
        ].join('');

        document.head.appendChild(style);
    }

    function getSearchPage() {
        var pages = document.querySelectorAll('#searchPage');

        for (var i = 0; i < pages.length; i++) {
            if (!pages[i].classList.contains('hide')) {
                return pages[i];
            }
        }

        return null;
    }

    function ensureSection(page) {
        var section = page.querySelector('#' + SECTION_ID);

        if (!section) {
            section = document.createElement('div');
            section.id = SECTION_ID;

            var results = page.querySelector('.searchResults, [class*="searchResults"], .padded-top.padded-bottom-page');
            (results || page).appendChild(section);
        }

        return section;
    }

    function clearSection() {
        var section = document.getElementById(SECTION_ID);

        if (section) {
            section.innerHTML = '';
        }
    }

    function renderNotice(section, message) {
        section.innerHTML = '';

        var notice = document.createElement('div');
        notice.className = 'seerrNotice';
        notice.textContent = message;
        section.appendChild(notice);
    }

    /*
     * Posters come through the plugin (never from TMDB directly) and the endpoint needs the
     * Jellyfin token, which an <img> tag cannot send: they are fetched with the token and shown
     * from a blob URL, only when the card is about to scroll into view.
     */
    var posterCache = {};

    function loadPoster(img, path, size) {
        var key = size + path;

        if (!posterCache[key]) {
            posterCache[key] = fetch(apiUrl('image?size=' + size + '&p=' + encodeURIComponent(path)), { headers: authHeaders() })
                .then(function (response) {
                    return response.ok ? response.blob() : Promise.reject(new Error('poster ' + response.status));
                })
                .then(function (blob) {
                    return URL.createObjectURL(blob);
                });

            posterCache[key].catch(function () {
                delete posterCache[key];
            });
        }

        posterCache[key].then(function (url) {
            img.src = url;
        }).catch(function () {
            /* No poster: the grey placeholder stays. */
        });
    }

    function lazyPoster(img, path, size) {
        if (!path) {
            return;
        }

        if (!('IntersectionObserver' in window)) {
            loadPoster(img, path, size);
            return;
        }

        var observer = new IntersectionObserver(function (entries, self) {
            entries.forEach(function (entry) {
                if (entry.isIntersecting) {
                    self.unobserve(entry.target);
                    loadPoster(img, path, size);
                }
            });
        }, { rootMargin: '200px' });

        observer.observe(img);
    }

    function subtitle(item) {
        var year = item.date ? String(item.date).slice(0, 4) : '';
        var kind = item.mediaType === 'tv' ? t('series') : t('movie');
        return kind + (year ? ' · ' + year : '');
    }

    /*
     * The request buttons (and the state label when nothing can be requested) of a card or sheet.
     * `messageHost` receives the error text, if any.
     */
    function appendRequestControls(item, host, messageHost) {
        var state = STATUS[item.status] || STATUS[1];

        if (state.requestable) {
            host.appendChild(makeRequestButton(item, false, item.status === 4 ? t('requestRest') : t('request'), messageHost));
        } else {
            var label = document.createElement('div');
            label.className = 'seerrState';
            label.textContent = t(state.key);
            host.appendChild(label);
        }

        if (config && config.allow4k) {
            var state4k = STATUS[item.status4k] || STATUS[1];

            if (state4k.requestable) {
                host.appendChild(makeRequestButton(item, true, t('request4k'), messageHost));
            }
        }
    }

    function makeRequestButton(item, is4k, label, messageHost) {
        var button = document.createElement('button');
        button.className = 'seerrRequest';
        button.type = 'button';
        button.textContent = label;
        button.addEventListener('click', function (event) {
            // Without this, the click would also open the detail sheet.
            event.stopPropagation();
            submitRequest(item, is4k, button, label, messageHost);
        });

        return button;
    }

    function renderCard(item) {
        var card = document.createElement('div');
        card.className = 'seerrCard';

        card.innerHTML =
            (item.posterPath ? '<img class="seerrPoster" alt="">' : '<div class="seerrPoster"></div>') +
            '<div class="seerrBody">' +
            '<div class="seerrTitle">' + escapeHtml(item.title) + '</div>' +
            '<div class="seerrMeta">' + escapeHtml(subtitle(item)) + '</div>' +
            '</div>';

        var img = card.querySelector('img.seerrPoster');
        if (img) {
            lazyPoster(img, item.posterPath, 'w342');
        }

        var body = card.querySelector('.seerrBody');
        var actions = document.createElement('div');
        actions.className = 'seerrActions';
        body.appendChild(actions);
        appendRequestControls(item, actions, body);

        card.addEventListener('click', function () {
            if (config && config.sso && config.publicUrl) {
                openInSeerr(item);
            } else {
                openSheet(item);
            }
        });

        return card;
    }

    /*
     * Opens the Seerr page, signed in.
     *
     * Preferably in a frame over the Jellyfin page: the page stays alive behind it, so the
     * Android app's Back button — which calls window.NavigationHelper.goBack(), a function of
     * the Jellyfin client — keeps working and closes the frame. The frame is only possible
     * when Jellyfin and Seerr are seen on the same site (same parent domain, over https),
     * otherwise the browser withholds Seerr's cookies. Failing that, Seerr opens outside.
     */
    var openingSeerr = false;

    function openInSeerr(item) {
        // A tap can arrive twice (image + container, or a repeated tap): one opening at a time.
        if (openingSeerr || document.querySelector('.seerrFrame')) {
            return;
        }
        openingSeerr = true;

        var target = '/' + item.mediaType + '/' + item.id;
        var fallback = config.publicUrl + target;
        var embed = canEmbedSeerr();
        var native = !embed && window.NativeShell && typeof window.NativeShell.openUrl === 'function';

        // A desktop browser blocks windows opened after a delay: open the tab right away
        // and point it somewhere once the ticket arrives.
        var tab = (embed || native) ? null : window.open('about:blank', '_blank');

        function go(url) {
            if (embed) {
                openFrame(url);
            } else if (native) {
                window.NativeShell.openUrl(url, '_blank');
            } else if (tab) {
                tab.opener = null;
                tab.location.href = url;
            } else {
                window.location.assign(url);
            }
        }

        api('sso/ticket', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ to: target })
        }).then(function (response) {
            return response.ok ? response.json() : null;
        }).then(function (payload) {
            openingSeerr = false;
            go(payload && payload.url ? payload.url : fallback);
        }).catch(function () {
            openingSeerr = false;
            go(fallback);
        });
    }

    function sameSite(a, b) {
        var la = String(a || '').toLowerCase().split('.');
        var lb = String(b || '').toLowerCase().split('.');

        if (la.length < 2 || lb.length < 2) {
            return la.join('.') === lb.join('.');
        }

        return la.slice(-2).join('.') === lb.slice(-2).join('.');
    }

    function canEmbedSeerr() {
        // https page + https Seerr (a plain-http frame inside an https page is blocked), same site, and a Seerr whose
        // proxy does not forbid being framed (X-Frame-Options / frame-ancestors seen by the server): otherwise a tab.
        return Boolean(config && config.seerrHost)
            && !config.frameBlocked
            && String(config.publicUrl || '').toLowerCase().indexOf('https:') === 0
            && window.location.protocol === 'https:'
            && !/^[0-9.]+$/.test(window.location.hostname)
            && sameSite(window.location.hostname, config.seerrHost);
    }

    function openFrame(url) {
        var frame = document.createElement('div');
        frame.className = 'seerrFrame';
        frame.innerHTML =
            '<div class="seerrFrameBar">' +
            '<button type="button" class="seerrFrameBack">' + escapeHtml(t('back')) + '</button>' +
            '<span class="seerrFrameTitle">Seerr</span>' +
            '<button type="button" class="seerrFrameClose" aria-label="' + escapeHtml(t('close')) + '">✕</button>' +
            '</div>' +
            '<iframe allow="fullscreen" referrerpolicy="no-referrer-when-downgrade"></iframe>';

        var iframe = frame.querySelector('iframe');
        var closed = false;
        var releaseBack = null;

        function dismiss(fromBack) {
            if (closed) {
                return;
            }
            closed = true;
            frame.remove();
            document.removeEventListener('keydown', onKey);

            if (!fromBack && releaseBack) {
                releaseBack();
            }
        }

        function onKey(event) {
            if (event.key === 'Escape') {
                dismiss(false);
            }
        }

        frame.querySelector('.seerrFrameBack').addEventListener('click', function () {
            dismiss(false);
        });
        frame.querySelector('.seerrFrameClose').addEventListener('click', function () {
            dismiss(false);
        });

        document.body.appendChild(frame);
        document.addEventListener('keydown', onKey);
        releaseBack = captureBack(function () {
            dismiss(true);
        });

        iframe.src = url;
    }

    /*
     * Makes the Back button close a panel, without ever touching the history: Jellyfin's
     * router keeps its own index in the history entries, and an entry pushed by us confuses
     * it (back to the home page, drawer half open).
     *
     * Android app: Back calls window.NavigationHelper.goBack(), which we replace while the
     * panel is open. iOS app: no hardware Back, the panel's own button is enough. Browser: if
     * the user goes back anyway, we close the panel and let Jellyfin navigate.
     *
     * Returns a release function, to call when the panel closes some other way.
     */
    function captureBack(onBack) {
        var nav = window.NavigationHelper;
        var released = false;

        function release() {
            if (released) {
                return;
            }
            released = true;

            if (nav && nav.goBack === handler) {
                delete nav.goBack;
            }
            window.removeEventListener('popstate', handler);
        }

        function handler() {
            release();
            onBack();
        }

        if (nav && typeof nav.goBack === 'function') {
            nav.goBack = handler;
        } else {
            window.addEventListener('popstate', handler);
        }

        return release;
    }

    /*
     * An internal detail sheet rather than a link to Seerr: in the mobile app an external link
     * opens a view with no Seerr session and no way back. Here everything stays inside Jellyfin.
     */
    function openSheet(item) {
        var overlay = document.createElement('div');
        overlay.className = 'seerrOverlay';

        overlay.innerHTML =
            '<div class="seerrSheet">' +
            (item.posterPath ? '<img class="seerrSheetPoster" alt="">' : '') +
            '<div class="seerrSheetBody">' +
            '<h3>' + escapeHtml(item.title) + '</h3>' +
            '<div class="seerrSheetMeta">' + escapeHtml(subtitle(item)) + '</div>' +
            (item.overview
                ? '<div class="seerrSheetOverview">' + escapeHtml(item.overview) + '</div>'
                : '') +
            '<div class="seerrSheetActions"></div>' +
            '</div></div>';

        var img = overlay.querySelector('img.seerrSheetPoster');
        if (img) {
            loadPoster(img, item.posterPath, 'w342');
        }

        var actions = overlay.querySelector('.seerrSheetActions');
        var sheetBody = overlay.querySelector('.seerrSheetBody');

        var requestHost = document.createElement('div');
        requestHost.className = 'seerrActions';
        actions.appendChild(requestHost);
        appendRequestControls(item, requestHost, sheetBody);

        var close = document.createElement('button');
        close.className = 'seerrClose';
        close.type = 'button';
        close.textContent = t('close');
        close.addEventListener('click', function () {
            dismiss(false);
        });
        actions.appendChild(close);

        if (config && config.showSeerrLink && config.publicUrl) {
            var target = '/' + item.mediaType + '/' + item.id;
            var link = document.createElement('a');
            link.className = 'seerrLink';
            link.href = config.publicUrl + target;
            link.textContent = t('openInSeerr');
            link.style.alignSelf = 'center';
            link.style.opacity = '.7';
            link.style.fontSize = '.85em';

            if (config.sso) {
                link.addEventListener('click', function (event) {
                    event.preventDefault();
                    openInSeerr(item);
                });
            } else {
                link.target = '_blank';
                link.rel = 'noopener';
            }

            actions.appendChild(link);
        }

        overlay.addEventListener('click', function (event) {
            if (event.target === overlay) {
                dismiss(false);
            }
        });

        function onKey(event) {
            if (event.key === 'Escape') {
                dismiss(false);
            }
        }

        var closed = false;
        var releaseBack = null;

        function dismiss(fromBack) {
            if (closed) {
                return;
            }
            closed = true;
            overlay.remove();
            document.removeEventListener('keydown', onKey);

            if (!fromBack && releaseBack) {
                releaseBack();
            }
        }

        document.body.appendChild(overlay);
        document.addEventListener('keydown', onKey);
        releaseBack = captureBack(function () {
            dismiss(true);
        });
    }

    function showError(messageHost, text) {
        var previous = messageHost.querySelector(':scope > .seerrError');
        if (previous) {
            previous.remove();
        }

        var error = document.createElement('div');
        error.className = 'seerrError';
        error.textContent = text;
        messageHost.appendChild(error);
    }

    function submitRequest(item, is4k, button, label, messageHost) {
        button.disabled = true;
        button.textContent = t('sending');

        api('request', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                mediaType: item.mediaType,
                mediaId: item.id,
                is4k: is4k
            })
        }).then(function (response) {
            if (response.ok) {
                button.textContent = t('requested');
                return null;
            }

            return response.json().catch(function () {
                return {};
            }).then(function (payload) {
                button.remove();

                if (payload.error === 'user-not-linked') {
                    showError(messageHost, (config && config.unlinkedMessage) || t('notLinked'));
                } else if (response.status === 429) {
                    showError(messageHost, t('rateLimited'));
                } else if (response.status === 409) {
                    showError(messageHost, t('alreadyRequested'));
                } else if (response.status === 403) {
                    showError(messageHost, payload.message || t('quotaOrPermission'));
                } else {
                    showError(messageHost, payload.message || t('refused'));
                }

                return null;
            });
        }).catch(function (err) {
            console.error(LOG, 'request failed', err);
            button.disabled = false;
            button.textContent = t('retry');
        });
    }

    function render(section, items) {
        section.innerHTML = '';

        if (!items.length) {
            return;
        }

        var header = document.createElement('div');
        header.className = 'seerrHeader';
        header.innerHTML =
            '<h2>' + escapeHtml(t('heading')) + '</h2>' +
            '<span class="seerrHint">' + escapeHtml(t('via')) + '</span>';

        var grid = document.createElement('div');
        grid.className = 'seerrGrid';

        items.forEach(function (item) {
            grid.appendChild(renderCard(item));
        });

        section.appendChild(header);
        section.appendChild(grid);
    }

    function runSearch(query) {
        var page = getSearchPage();

        if (!page) {
            return;
        }

        var section = ensureSection(page);

        if (inFlight) {
            inFlight.abort();
            inFlight = null;
        }

        var controller = new AbortController();
        inFlight = controller;

        fetch(apiUrl('search?query=' + encodeURIComponent(query) + '&lang=' + encodeURIComponent(uiLocale())), {
            headers: Object.assign({ Accept: 'application/json' }, authHeaders()),
            signal: controller.signal
        }).then(function (response) {
            if (response.status === 409) {
                renderNotice(section, config.unlinkedMessage || t('notLinked'));
                return null;
            }

            if (!response.ok) {
                clearSection();
                return null;
            }

            return response.json();
        }).then(function (payload) {
            if (payload && payload.results) {
                render(section, payload.results);
            }
        }).catch(function (err) {
            if (err && err.name !== 'AbortError') {
                console.error(LOG, 'search failed', err);
            }
        });
    }

    function onQueryChanged(value) {
        var query = (value || '').trim();

        if (query === lastQuery) {
            return;
        }

        lastQuery = query;
        clearTimeout(debounceTimer);

        if (query.length < (config.minQueryLength || 3)) {
            clearSection();
            return;
        }

        debounceTimer = setTimeout(function () {
            runSearch(query);
        }, DEBOUNCE_MS);
    }

    function attach() {
        var page = getSearchPage();

        if (!page) {
            lastQuery = '';
            return;
        }

        var input = page.querySelector('#searchTextInput') || document.querySelector('#searchPage:not(.hide) #searchTextInput');

        if (!input) {
            return;
        }

        if (!input.dataset.seerrBound) {
            input.dataset.seerrBound = '1';
            input.addEventListener('input', function (event) {
                onQueryChanged(event.target.value);
            });
        }

        if (input.value && input.value.trim() !== lastQuery) {
            onQueryChanged(input.value);
        }
    }

    function start() {
        injectStyles();

        var observer = new MutationObserver(function () {
            attach();
        });

        observer.observe(document.body, { childList: true, subtree: true });

        // Safety net: SPA navigation does not always stir the observed DOM, and the
        // search page can be restored as it was.
        window.addEventListener('popstate', attach);
        window.addEventListener('hashchange', attach);
        setInterval(attach, 2000);

        attach();
    }

    function boot() {
        if (!window.ApiClient || !window.ApiClient.accessToken || !window.ApiClient.accessToken()) {
            setTimeout(boot, 500);
            return;
        }

        api('config').then(function (response) {
            return response.ok ? response.json() : null;
        }).then(function (payload) {
            // Not configured, disabled, or (when the administrator chose to hide it) this
            // user has no Seerr account: nothing is added to the page.
            if (!payload || !payload.enabled) {
                return;
            }

            if (!payload.linked && payload.unlinkedBehavior === 'hide') {
                return;
            }

            config = payload;
            start();
        }).catch(function (err) {
            console.error(LOG, 'could not read the configuration', err);
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', boot);
    } else {
        boot();
    }
})();
