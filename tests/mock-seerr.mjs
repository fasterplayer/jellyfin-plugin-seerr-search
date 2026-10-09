// A tiny fake Seerr, for testing the plugin without touching a real Seerr.
//
//   MOCK_KEY=secret JELLYFIN_URL=http://jellyfin:8096 PORT=5099 node tests/mock-seerr.mjs
//
// It implements just what the plugin calls (/api/v1/status, /user, /settings/jellyfin/users,
// /user/import-from-jellyfin, /search, /request and the Quick Connect sign-in), checks the
// X-Api-Key header like Seerr does, and records what it receives (/__admin/last) so a test can
// assert that the right Seerr user (X-Api-User) and language were used.
//
// The Quick Connect calls are forwarded to the real Jellyfin at JELLYFIN_URL, exactly as Seerr
// does, so the whole automatic sign-in flow can be exercised.
import http from 'node:http';

const KEY = process.env.MOCK_KEY || 'mock-key';
const JELLYFIN = (process.env.JELLYFIN_URL || '').replace(/\/$/, '');
const PORT = Number(process.env.PORT || 5099);

// Jellyfin id (no dashes) -> Seerr user id
let linked = {};
// Jellyfin accounts Seerr could import
let importable = [];
const seen = { searches: [], requests: [], imports: [] };

const RESULTS = [
  { id: 438631, mediaType: 'movie', title: 'Dune', overview: 'A mythic and emotionally charged hero’s journey.', posterPath: '/d5NXSklXo0qyIYkgV94XAgMIckC.jpg', releaseDate: '2021-09-15' },
  { id: 693134, mediaType: 'movie', title: 'Dune: Part Two (already available)', overview: 'Already in the library.', posterPath: '/1pdfLvkbY9ohJlCjQH2CZjjYVvJ.jpg', releaseDate: '2024-02-27', mediaInfo: { status: 5, status4k: 5 } },
  { id: 1399, mediaType: 'tv', name: 'Game of Thrones', overview: 'Some seasons are in the library.', posterPath: '/1XS1oqL89opfnbLl8WnZY1O1uJx.jpg', firstAirDate: '2011-04-17', mediaInfo: { status: 4, status4k: 1 } },
  { id: 1001, mediaType: 'movie', title: '<img src=x onerror="window.__xss=1">Evil "quoted" & \'single\'', overview: '<script>window.__xss=2</script><b>bold</b>', posterPath: '/../../etc/passwd', releaseDate: '2020-01-01' },
  { id: 999, mediaType: 'movie', title: 'Request conflict test', overview: 'The request is refused with 409.', releaseDate: '2019-01-01' },
  { id: 998, mediaType: 'movie', title: 'Request quota test', overview: 'The request is refused with 403.', releaseDate: '2018-01-01' },
  { id: 1002, mediaType: 'movie', title: 'Pending approval test', overview: 'Already requested.', releaseDate: '2017-01-01', mediaInfo: { status: 2, status4k: 1 } },
  { id: 77, mediaType: 'person', name: 'A person (must be filtered out)' }
];

function send(res, status, body, headers = {}) {
  const text = typeof body === 'string' ? body : JSON.stringify(body);
  res.writeHead(status, { 'Content-Type': typeof body === 'string' ? 'text/plain' : 'application/json', ...headers });
  res.end(text);
}

function readBody(req) {
  return new Promise((resolve) => {
    let data = '';
    req.on('data', (d) => (data += d));
    req.on('end', () => {
      try { resolve(data ? JSON.parse(data) : {}); } catch { resolve({}); }
    });
  });
}

async function jellyfin(path, method = 'GET', body = null) {
  const res = await fetch(JELLYFIN + path, {
    method,
    headers: {
      'Content-Type': 'application/json',
      Authorization: 'MediaBrowser Client="Seerr", Device="Seerr", DeviceId="mock-seerr", Version="1"'
    },
    body: body ? JSON.stringify(body) : undefined
  });
  const text = await res.text();
  let json = null;
  try { json = JSON.parse(text); } catch { /* not json */ }
  return { status: res.status, json };
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://mock');
  const path = url.pathname;

  // ---- test control, no authentication
  if (path === '/__admin/state' && req.method === 'POST') {
    const body = await readBody(req);
    linked = body.linked || {};
    importable = body.importable || [];
    seen.searches = []; seen.requests = []; seen.imports = [];
    return send(res, 200, { ok: true });
  }
  if (path === '/__admin/last') return send(res, 200, seen);

  // ---- the Quick Connect calls are anonymous in Seerr too
  if (path === '/api/v1/auth/jellyfin/quickconnect/initiate' && req.method === 'POST') {
    const r = await jellyfin('/QuickConnect/Initiate', 'POST');
    if (r.status !== 200) return send(res, 502, { message: 'jellyfin ' + r.status });
    return send(res, 200, { code: r.json.Code, secret: r.json.Secret });
  }
  if (path === '/api/v1/auth/jellyfin/quickconnect/authenticate' && req.method === 'POST') {
    const body = await readBody(req);
    const r = await jellyfin('/Users/AuthenticateWithQuickConnect', 'POST', { Secret: body.secret });
    if (r.status !== 200) return send(res, 401, { message: 'quick connect not authorized' });
    return send(res, 200, { id: 1 }, { 'Set-Cookie': `connect.sid=mock-${r.json.User.Id}; Path=/; HttpOnly` });
  }

  if (!path.startsWith('/api/v1/')) return send(res, 404, { message: 'not found' });
  const api = path.slice('/api/v1'.length);

  // /status is public in Seerr
  if (api === '/status') return send(res, 200, { version: 'mock-1.0', commitTag: 'mock', updateAvailable: false });

  if (req.headers['x-api-key'] !== KEY) return send(res, 403, { message: 'You do not have permission to access this endpoint' });
  const asUser = req.headers['x-api-user'] || null;

  if (api === '/user' && req.method === 'GET') {
    const results = Object.entries(linked).map(([jf, id]) => ({ id, jellyfinUserId: jf, displayName: 'user' + id }));
    results.push({ id: 900, displayName: 'local account without a Jellyfin id' });
    const take = Number(url.searchParams.get('take') || 20);
    const skip = Number(url.searchParams.get('skip') || 0);
    return send(res, 200, { pageInfo: { results: results.length }, results: results.slice(skip, skip + take) });
  }

  if (api === '/settings/jellyfin/users' && req.method === 'GET') {
    return send(res, 200, importable.map((id) => ({ id, username: 'jf-' + id.slice(0, 6), thumb: '', email: '' })));
  }

  if (api === '/user/import-from-jellyfin' && req.method === 'POST') {
    const body = await readBody(req);
    if (!Array.isArray(body.jellyfinUserIds)) return send(res, 500, { message: 'jellyfinUserIds is not iterable' });
    seen.imports.push(body.jellyfinUserIds);
    const created = [];
    for (const id of body.jellyfinUserIds) {
      if (!linked[id]) {
        linked[id] = Math.max(100, ...Object.values(linked)) + 1;
        created.push({ id: linked[id], jellyfinUserId: id });
      }
    }
    return send(res, 201, created);
  }

  if (api === '/search' && req.method === 'GET') {
    seen.searches.push({ asUser, language: url.searchParams.get('language'), query: url.searchParams.get('query'), page: url.searchParams.get('page') });
    if (url.searchParams.get('query') === 'nothing') return send(res, 200, { page: 1, results: [] });
    return send(res, 200, { page: 1, totalResults: RESULTS.length, results: RESULTS });
  }

  if (api === '/request' && req.method === 'POST') {
    const body = await readBody(req);
    seen.requests.push({ asUser, body });
    if (body.mediaId === 999) return send(res, 409, { message: 'Request for this media already exists.' });
    if (body.mediaId === 998) return send(res, 403, { message: 'Quota exceeded for this user.' });
    return send(res, 201, { id: 5000 + seen.requests.length, status: 1 });
  }

  return send(res, 404, { message: 'mock: ' + req.method + ' ' + api });
});

server.listen(PORT, '0.0.0.0', () => console.log(`mock Seerr on :${PORT}, Jellyfin at ${JELLYFIN || '(none)'}`));
