/*
 * Seerr Search — client
 *
 * Greffe une section « Disponible sur demande » sous les résultats de recherche
 * de Jellyfin. Le script ne parle qu'au plugin (/SeerrSearch/*), jamais à Seerr :
 * la clé API reste sur le serveur et l'identité vient du jeton Jellyfin déjà en
 * place dans le navigateur.
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

    var STATUS = {
        1: { label: 'Inconnu', requestable: true },
        2: { label: 'En attente d’approbation', requestable: false },
        3: { label: 'En traitement', requestable: false },
        4: { label: 'Partiellement disponible', requestable: true },
        5: { label: 'Déjà dans la bibliothèque', requestable: false }
    };

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
            .replace(/"/g, '&quot;');
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
            '#' + SECTION_ID + ' .seerrState{font-size:.8em;opacity:.75;margin-top:auto;}',
            '#' + SECTION_ID + ' button.seerrRequest{margin-top:auto;border:0;border-radius:.3em;padding:.45em .7em;',
            'font-size:.85em;font-weight:500;cursor:pointer;background:#00a4dc;color:#fff;}',
            '#' + SECTION_ID + ' button.seerrRequest:hover{filter:brightness(1.1);}',
            '#' + SECTION_ID + ' button.seerrRequest[disabled]{opacity:.55;cursor:default;filter:none;}',
            '#' + SECTION_ID + ' .seerrError{font-size:.8em;color:#ff8a80;}',
            '#' + SECTION_ID + ' .seerrNotice{opacity:.7;font-size:.9em;}',
            '#' + SECTION_ID + ' .seerrCard{cursor:pointer;}',
            '#' + SECTION_ID + ' .seerrCard:hover{background:rgba(255,255,255,.1);}',
            '.seerrOverlay{position:fixed;inset:0;z-index:1000;background:rgba(0,0,0,.75);',
            'display:flex;align-items:center;justify-content:center;padding:1.2em;}',
            '.seerrSheet{background:#1c1c1c;color:#fff;border-radius:.6em;max-width:44em;width:100%;',
            'max-height:88vh;overflow:auto;display:flex;gap:1.2em;padding:1.2em;}',
            '.seerrSheet .seerrSheetPoster{width:10em;flex-shrink:0;border-radius:.4em;}',
            '.seerrSheet .seerrSheetBody{display:flex;flex-direction:column;gap:.6em;flex:1;min-width:0;}',
            '.seerrSheet h3{margin:0;font-size:1.25em;font-weight:500;}',
            '.seerrSheet .seerrSheetMeta{opacity:.6;font-size:.85em;}',
            '.seerrSheet .seerrSheetOverview{font-size:.9em;line-height:1.45;opacity:.85;}',
            '.seerrSheet .seerrSheetActions{display:flex;gap:.6em;flex-wrap:wrap;margin-top:.4em;}',
            '.seerrSheet button{border:0;border-radius:.3em;padding:.55em .9em;font-size:.9em;',
            'font-weight:500;cursor:pointer;}',
            '.seerrSheet button.seerrRequest{background:#00a4dc;color:#fff;}',
            '.seerrSheet button.seerrClose{background:rgba(255,255,255,.14);color:#fff;}',
            '.seerrSheet button[disabled]{opacity:.55;cursor:default;}',
            '.seerrSheet .seerrState,.seerrSheet .seerrError{font-size:.85em;}',
            '.seerrSheet .seerrError{color:#ff8a80;}',
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
        section.innerHTML = '<div class="seerrNotice">' + escapeHtml(message) + '</div>';
    }

    function posterUrl(path) {
        return path ? apiUrl('image?size=w342&p=' + encodeURIComponent(path)) : '';
    }

    function renderCard(item) {
        var card = document.createElement('div');
        card.className = 'seerrCard';

        var poster = posterUrl(item.posterPath);

        card.innerHTML =
            (poster
                ? '<img class="seerrPoster" loading="lazy" alt="" src="' + escapeHtml(poster) + '">'
                : '<div class="seerrPoster"></div>') +
            '<div class="seerrBody">' +
            '<div class="seerrTitle">' + escapeHtml(item.title) + '</div>' +
            '<div class="seerrMeta">' + escapeHtml(subtitle(item)) + '</div>' +
            '</div>';

        var body = card.querySelector('.seerrBody');
        var state = STATUS[item.status] || STATUS[1];

        if (state.requestable) {
            var button = document.createElement('button');
            button.className = 'seerrRequest';
            button.type = 'button';
            button.textContent = item.status === 4 ? 'Demander la suite' : 'Demander';
            button.addEventListener('click', function (event) {
                // Sans ça, le clic ouvrirait aussi la fiche.
                event.stopPropagation();
                submitRequest(item, button, body);
            });
            body.appendChild(button);
        } else {
            var label = document.createElement('div');
            label.className = 'seerrState';
            label.textContent = state.label;
            body.appendChild(label);
        }

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
     * Ouvre la fiche Seerr, connecté.
     *
     * De préférence dans un cadre par-dessus la page Jellyfin : la page reste
     * vivante derrière, donc le bouton Retour de l'app Android — qui appelle
     * window.NavigationHelper.goBack(), une fonction du client Jellyfin —
     * continue de fonctionner et referme le cadre. Ce cadre n'est possible
     * que si Jellyfin et Seerr sont vus sur le même site (même domaine
     * parent, en https), sinon le navigateur retient les cookies de Seerr.
     * À défaut, on ouvre Seerr à l'extérieur.
     */
    var openingSeerr = false;

    function openInSeerr(item) {
        // Un appui peut arriver deux fois (image + conteneur, ou tap répété) :
        // une seule ouverture à la fois.
        if (openingSeerr || document.querySelector('.seerrFrame')) {
            return;
        }
        openingSeerr = true;

        var target = '/' + item.mediaType + '/' + item.id;
        var fallback = config.publicUrl + target;
        var embed = canEmbedSeerr();
        var native = !embed && window.NativeShell && typeof window.NativeShell.openUrl === 'function';

        // Un navigateur de bureau bloque les fenêtres ouvertes après un délai :
        // on ouvre l'onglet tout de suite, on le dirige quand le billet arrive.
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
        return Boolean(config && config.seerrHost)
            && window.location.protocol === 'https:'
            && !/^[0-9.]+$/.test(window.location.hostname)
            && sameSite(window.location.hostname, config.seerrHost);
    }

    function openFrame(url) {
        var frame = document.createElement('div');
        frame.className = 'seerrFrame';
        frame.innerHTML =
            '<div class="seerrFrameBar">' +
            '<button type="button" class="seerrFrameBack">\u2039 Jellyfin</button>' +
            '<span class="seerrFrameTitle">Seerr</span>' +
            '<button type="button" class="seerrFrameClose" aria-label="Fermer">\u2715</button>' +
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

    function subtitle(item) {
        var year = item.date ? String(item.date).slice(0, 4) : '';
        var kind = item.mediaType === 'tv' ? 'S\u00e9rie' : 'Film';
        return kind + (year ? ' \u00b7 ' + year : '');
    }

    /*
     * Fait fermer un panneau par le bouton Retour, sans jamais toucher à
     * l'historique : le routeur de Jellyfin garde son propre index dans les
     * entrées d'historique, et une entrée poussée par nous le désoriente
     * (retour à l'accueil, tiroir entrouvert).
     *
     * App Android : Retour appelle window.NavigationHelper.goBack(), qu'on
     * remplace le temps que le panneau est ouvert. App iOS : pas de Retour
     * matériel, le bouton du panneau suffit. Navigateur : si l'utilisateur
     * recule quand même, on ferme le panneau et on laisse Jellyfin naviguer.
     *
     * Renvoie une fonction de libération, à appeler quand le panneau se ferme
     * par un autre chemin.
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
     * Fiche interne plutôt qu'un lien vers Seerr : dans l'app mobile, un lien
     * externe ouvre une vue sans session Seerr et sans retour possible. Ici tout
     * reste dans Jellyfin, et le bouton Retour ferme la fiche — on pousse une
     * entrée d'historique à l'ouverture pour ça.
     */
    function openSheet(item) {
        var overlay = document.createElement('div');
        overlay.className = 'seerrOverlay';

        var poster = posterUrl(item.posterPath);
        var state = STATUS[item.status] || STATUS[1];

        overlay.innerHTML =
            '<div class="seerrSheet">' +
            (poster ? '<img class="seerrSheetPoster" alt="" src="' + escapeHtml(poster) + '">' : '') +
            '<div class="seerrSheetBody">' +
            '<h3>' + escapeHtml(item.title) + '</h3>' +
            '<div class="seerrSheetMeta">' + escapeHtml(subtitle(item)) + '</div>' +
            (item.overview
                ? '<div class="seerrSheetOverview">' + escapeHtml(item.overview) + '</div>'
                : '') +
            '<div class="seerrSheetActions"></div>' +
            '</div></div>';

        var actions = overlay.querySelector('.seerrSheetActions');
        var sheetBody = overlay.querySelector('.seerrSheetBody');

        if (state.requestable) {
            var request = document.createElement('button');
            request.className = 'seerrRequest';
            request.type = 'button';
            request.textContent = item.status === 4 ? 'Demander la suite' : 'Demander';
            request.addEventListener('click', function () {
                submitRequest(item, request, sheetBody);
            });
            actions.appendChild(request);
        } else {
            var label = document.createElement('div');
            label.className = 'seerrState';
            label.textContent = state.label;
            sheetBody.appendChild(label);
        }

        var close = document.createElement('button');
        close.className = 'seerrClose';
        close.type = 'button';
        close.textContent = 'Fermer';
        close.addEventListener('click', function () {
            dismiss(false);
        });
        actions.appendChild(close);

        if (config && config.showSeerrLink && config.publicUrl) {
            var target = '/' + item.mediaType + '/' + item.id;
            var link = document.createElement('a');
            link.className = 'seerrLink';
            link.href = config.publicUrl + target;
            link.textContent = 'Ouvrir dans Seerr';
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

    function submitRequest(item, button, body) {
        button.disabled = true;
        button.textContent = 'Envoi…';

        api('request', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                mediaType: item.mediaType,
                mediaId: item.id,
                is4k: false
            })
        }).then(function (response) {
            if (response.ok) {
                button.textContent = 'Demandé ✓';
                return null;
            }

            return response.json().catch(function () {
                return {};
            }).then(function (payload) {
                button.remove();

                var error = document.createElement('div');
                error.className = 'seerrError';

                if (response.status === 409) {
                    error.textContent = 'Déjà demandé.';
                } else if (response.status === 403) {
                    error.textContent = payload.message || 'Quota atteint ou permission refusée dans Seerr.';
                } else {
                    error.textContent = payload.message || 'Seerr a refusé la demande.';
                }

                body.appendChild(error);
                return null;
            });
        }).catch(function (err) {
            console.error(LOG, 'demande impossible', err);
            button.disabled = false;
            button.textContent = 'Réessayer';
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
            '<h2>Disponible sur demande</h2>' +
            '<span class="seerrHint">via Seerr</span>';

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

        fetch(apiUrl('search?query=' + encodeURIComponent(query)), {
            headers: Object.assign({ Accept: 'application/json' }, authHeaders()),
            signal: controller.signal
        }).then(function (response) {
            if (response.status === 409) {
                renderNotice(section, 'Ton compte n’est pas encore lié à Seerr. Demande à l’administrateur d’importer les utilisateurs Jellyfin.');
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
                console.error(LOG, 'recherche impossible', err);
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

        // Filet de sécurité : la navigation SPA ne remue pas toujours le DOM
        // observé, et la page de recherche peut être restaurée telle quelle.
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
            if (!payload || !payload.enabled) {
                return;
            }

            config = payload;
            start();
        }).catch(function (err) {
            console.error(LOG, 'configuration illisible', err);
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', boot);
    } else {
        boot();
    }
})();
