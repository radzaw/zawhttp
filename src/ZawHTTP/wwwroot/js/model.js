// The request model used by tabs, collections and history, and the URL <-> query-parameter sync.

export const METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'];
export const RAW_TYPES = [['json', 'JSON'], ['text', 'Text'], ['xml', 'XML'], ['html', 'HTML'], ['javascript', 'JavaScript']];

export function newRequest() {
  return {
    method: 'GET',
    url: '',
    params: [],
    headers: [],
    body: { mode: 'none', raw: '', rawType: 'json', urlencoded: [], form: [], file: '' },
    auth: { type: 'none', username: '', password: '', token: '', key: '', value: '', in: 'header' },
    settings: { timeoutMs: 30000, followRedirects: true, maxRedirects: 10, verifySsl: true },
  };
}

const rows = list => (Array.isArray(list) ? list : []).map(r => ({
  key: r?.key ?? '', value: r?.value ?? '', enabled: r?.enabled !== false, ...(r?.type ? { type: r.type } : {}), ...(r?.noValue ? { noValue: true } : {}),
}));

/** Fills in missing parts (imports, older files) and derives the parameter table from the URL when it has none. */
export function normalizeRequest(r) {
  const d = newRequest();
  r = r || {};
  const out = {
    method: String(r.method || 'GET').toUpperCase(),
    url: r.url ?? '',
    params: rows(r.params),
    headers: rows(r.headers),
    body: { ...d.body, ...(r.body || {}) },
    auth: { ...d.auth, ...(r.auth || {}) },
    settings: { ...d.settings, ...(r.settings || {}) },
  };
  for (const k of ['urlencoded', 'form']) out.body[k] = rows(out.body[k]);
  for (const k of ['raw', 'file']) out.body[k] ??= '';
  for (const k of ['username', 'password', 'token', 'key', 'value']) out.auth[k] ??= '';
  if (!out.params.length) out.params = parseQuery(splitUrl(out.url).query);
  return out;
}

/** Splits a URL into the part before "?", the query (without "?") and the fragment (with "#"). */
export function splitUrl(url) {
  url = url ?? '';
  let hash = '';
  const hi = url.indexOf('#');
  if (hi >= 0) { hash = url.slice(hi); url = url.slice(0, hi); }
  const qi = url.indexOf('?');
  return qi < 0 ? { base: url, query: null, hash } : { base: url.slice(0, qi), query: url.slice(qi + 1), hash };
}

/** Query parameters as typed (not decoded), so editing the table never re-encodes what the user wrote. */
export function parseQuery(query) {
  if (query == null || query === '') return [];
  return query.split('&').filter((p, i, a) => p !== '' || i < a.length - 1).map(p => {
    const eq = p.indexOf('=');
    return eq < 0 ? { key: p, value: '', enabled: true, noValue: true } : { key: p.slice(0, eq), value: p.slice(eq + 1), enabled: true };
  });
}

export function buildQuery(params) {
  return params.filter(p => p.enabled && (p.key !== '' || p.value !== ''))
    .map(p => (p.noValue && p.value === '' ? p.key : `${p.key}=${p.value}`)).join('&');
}

/** The URL with its query replaced by the enabled parameters. */
export function urlWithParams(url, params) {
  const { base, hash } = splitUrl(url);
  const q = buildQuery(params);
  return base + (q ? '?' + q : '') + hash;
}

/** The parameter table after the URL was edited: its query, plus the disabled parameters the URL can't show. */
export function paramsFromUrl(url, previous) {
  const fromUrl = parseQuery(splitUrl(url).query);
  return [...fromUrl, ...(previous || []).filter(p => !p.enabled)];
}

const VAR_RE = /\{\{\s*([^{}\s][^{}]*?)\s*\}\}/g;

/** Resolves {{variables}} for display (tooltips); the backend does the real substitution when sending. */
export function resolveVars(text, vars) {
  if (!text || !text.includes('{{')) return text;
  const map = new Map();
  for (const v of vars || []) if (v.enabled !== false && v.key) map.set(v.key.trim(), v.value ?? '');
  for (let pass = 0; pass < 5; pass++) {
    const next = text.replace(VAR_RE, (m, name) => (map.has(name) ? map.get(name) : m));
    if (next === text) break;
    text = next;
  }
  return text;
}

/** Variables the backend generates on every send. */
export const DYNAMIC_VARS = new Set(['$guid', '$randomUUID', '$timestamp', '$isoTimestamp', '$randomInt']);

function knownVars(vars) {
  return new Set([...DYNAMIC_VARS, ...(vars || []).filter(v => v.enabled !== false && v.key).map(v => v.key.trim())]);
}

/** Names of {{variables}} used in a text that aren't defined. */
export function missingVars(text, vars) {
  const known = knownVars(vars);
  const out = new Set();
  for (const m of String(text || '').matchAll(VAR_RE)) if (!known.has(m[1])) out.add(m[1]);
  return [...out];
}

/** Splits text into plain runs and {{variable}} runs: [{text, state: null | 'ok' | 'missing'}]. */
export function varSegments(text, vars) {
  text = String(text ?? '');
  const known = knownVars(vars);
  const out = [];
  let last = 0;
  for (const m of text.matchAll(VAR_RE)) {
    if (m.index > last) out.push({ text: text.slice(last, m.index), state: null });
    out.push({ text: m[0], state: known.has(m[1]) ? 'ok' : 'missing' });
    last = m.index + m[0].length;
  }
  if (last < text.length) out.push({ text: text.slice(last), state: null });
  return out;
}

/** A short name for an unsaved request: "GET example.com/users". */
export function requestLabel(r) {
  const u = (r.url || '').replace(/^https?:\/\//i, '').split('?')[0];
  return u ? u.slice(0, 60) : 'New request';
}
