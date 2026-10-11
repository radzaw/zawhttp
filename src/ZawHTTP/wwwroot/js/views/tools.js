// Dialogs: environments, save request, import, code, cookies, preferences, shortcuts, about.
import { get, post, del } from '../api.js';
import { h, clone, uid, copyText, pickFile } from '../util.js';
import { icon } from '../icons.js';
import { modal, alertError, confirmDlg, promptDlg } from '../dialogs.js';
import { KvTable } from '../kvtable.js';

// ---------------------------------------------------------------- environments

/** Edits globals and environments on a copy; resolves with the new {globals, environments} or null. */
export function environmentsDialog(app, selectId) {
  const work = clone({ globals: app.envs.globals, environments: app.envs.environments });
  let sel = selectId && work.environments.some(e => e.id === selectId) ? selectId : app.activeEnvId && work.environments.some(e => e.id === app.activeEnvId) ? app.activeEnvId : 'globals';
  const list = h('div', { class: 'env-list', tabIndex: 0 });
  const right = h('div', { class: 'env-right' });

  const current = () => (sel === 'globals' ? null : work.environments.find(e => e.id === sel));
  const renderList = () => {
    list.replaceChildren(
      h('div', { class: 'env-item' + (sel === 'globals' ? ' sel' : ''), onclick: () => { sel = 'globals'; render(); } }, h('span', { html: icon('environment') }), h('i', null, 'Globals')),
      ...work.environments.map(e => h('div', { class: 'env-item' + (sel === e.id ? ' sel' : ''), onclick: () => { sel = e.id; render(); }, ondblclick: () => rename() },
        h('span', { html: icon('environment') }), h('span', { class: 'tl' }, e.name), e.id === app.activeEnvId ? h('span', { class: 'env-active' }, 'active') : null)));
  };
  const renderRight = () => {
    const env = current();
    const vars = env ? env.variables : work.globals;
    const table = new KvTable({ rows: vars, keyPlaceholder: 'Variable', valuePlaceholder: 'Value', onChange: () => {} });
    right.replaceChildren(
      h('div', { class: 'env-title' }, env ? env.name : 'Globals'),
      h('div', { class: 'hint' }, env
        ? 'Use variables as {{name}} in URLs, headers, bodies and auth. They override globals with the same name while this environment is active.'
        : 'Global variables are available in every environment.'),
      h('div', { class: 'env-table' }, table.el));
  };
  const render = () => { renderList(); renderRight(); };

  const add = async () => {
    const name = await promptDlg('New environment', 'Name:', '', { placeholder: 'e.g. Development' });
    if (!name?.trim()) return;
    const env = { id: uid(), name: name.trim(), variables: [] };
    work.environments.push(env);
    sel = env.id;
    render();
  };
  const rename = async () => {
    const env = current();
    if (!env) return;
    const name = await promptDlg('Rename environment', 'Name:', env.name);
    if (!name?.trim()) return;
    env.name = name.trim();
    render();
  };
  const duplicate = () => {
    const env = current();
    if (!env) return;
    const copy = { ...clone(env), id: uid(), name: env.name + ' copy' };
    work.environments.splice(work.environments.indexOf(env) + 1, 0, copy);
    sel = copy.id;
    render();
  };
  const remove = async () => {
    const env = current();
    if (!env) return;
    if (!(await confirmDlg(`Delete the environment "${env.name}"?`, { title: 'Delete environment', ok: 'Delete', danger: true }))) return;
    work.environments.splice(work.environments.indexOf(env), 1);
    sel = 'globals';
    render();
  };

  render();
  return modal({
    title: 'Environments',
    width: 860,
    className: 'env-dialog',
    body: h('div', { class: 'env' },
      h('div', { class: 'env-left' }, list, h('div', { class: 'env-btns' },
        h('button', { class: 'tbtn', title: 'New environment', html: icon('plus'), onclick: add }),
        h('button', { class: 'tbtn', title: 'Rename', html: icon('edit'), onclick: rename }),
        h('button', { class: 'tbtn', title: 'Duplicate', html: icon('duplicate'), onclick: duplicate }),
        h('button', { class: 'tbtn', title: 'Delete', html: icon('trash'), onclick: remove }))),
      right),
    buttons: [
      { label: 'OK', primary: true, onClick: () => ({ ...work, selected: sel }) },
      { label: 'Cancel', value: null },
    ],
  });
}

// ---------------------------------------------------------------- save request

/** Asks for a name and a collection / folder. Resolves with {name, parentId} or null. */
export function saveRequestDialog(app, { name, title = 'Save request' }) {
  const nameIn = h('input', { class: 'inp wide', value: name, spellcheck: false });
  const sel = h('select', { class: 'inp wide', size: 12 });
  const fill = selectId => {
    const opts = [];
    const walk = (n, depth) => {
      if (n.type === 'request') return;
      opts.push(h('option', { value: n.id, selected: n.id === selectId }, '  '.repeat(depth) + (n.type === 'collection' ? '▣ ' : '▸ ') + n.name));
      for (const c of n.items || []) walk(c, depth + 1);
    };
    for (const c of app.collections) walk(c, 0);
    sel.replaceChildren(...opts);
    if (!sel.value && opts.length) sel.value = opts[0].value;
  };
  fill(app.lastSaveParent);
  const newCol = async () => {
    const c = await app.newCollection();
    if (c) fill(c.id);
  };
  return modal({
    title,
    width: 460,
    body: h('div', { class: 'form' },
      h('label', null, 'Request name'), nameIn,
      h('label', null, 'Save to'), sel,
      h('div', null, h('button', { class: 'linkbtn', onclick: newCol }, '+ New collection'))),
    buttons: [
      { label: 'Save', primary: true, onClick: () => {
        if (!nameIn.value.trim()) { nameIn.focus(); return false; }
        if (!sel.value) { alertError('Create a collection to save the request in.'); return false; }
        app.lastSaveParent = sel.value;
        return { name: nameIn.value.trim(), parentId: sel.value };
      } },
      { label: 'Cancel', value: null },
    ],
  });
}

// ---------------------------------------------------------------- import

export function importDialog(app) {
  const ta = h('textarea', { class: 'inp import-ta mono', spellcheck: false,
    placeholder: 'Paste a cURL command, a Postman collection (v2.0 / v2.1) or environment, or a ZawHTTP export…' });
  const doImport = async text => {
    const r = await post('/import', { text });
    return app.applyImport(r);
  };
  return modal({
    title: 'Import',
    width: 640,
    body: ({ close }) => {
      const drop = h('div', { class: 'import-box' }, ta,
        h('div', { class: 'import-foot' },
          h('button', { class: 'btn', onclick: async () => {
            const f = await pickFile('.json,.txt,.sh,.curl,application/json,text/plain');
            if (!f) return;
            try { if (await doImport(await f.text())) close(true); } catch (e) { alertError(e.message); }
          } }, 'Open file…'),
          h('span', { class: 'muted' }, 'or drop a file here')));
      drop.addEventListener('dragover', e => { e.preventDefault(); drop.classList.add('drop'); });
      drop.addEventListener('dragleave', () => drop.classList.remove('drop'));
      drop.addEventListener('drop', async e => {
        e.preventDefault();
        drop.classList.remove('drop');
        const f = e.dataTransfer.files[0];
        if (!f) return;
        try { if (await doImport(await f.text())) close(true); } catch (err) { alertError(err.message); }
      });
      return drop;
    },
    buttons: [
      { label: 'Import', primary: true, onClick: async () => {
        if (!ta.value.trim()) return false;
        return (await doImport(ta.value)) ? true : false;
      } },
      { label: 'Cancel', value: null },
    ],
  });
}

// ---------------------------------------------------------------- code

export async function codeDialog(app, request) {
  const resolve = h('input', { type: 'checkbox' });
  const pre = h('pre', { class: 'code-preview mono code-box' });
  const load = async () => {
    try {
      const r = await post('/export/curl', { request, variables: resolve.checked ? app.activeVariables() : null });
      pre.textContent = r.text;
    } catch (e) {
      pre.textContent = e.message;
    }
  };
  resolve.addEventListener('change', load);
  await load();
  return modal({
    title: 'Generate code: cURL',
    width: 720,
    body: h('div', { class: 'form' }, pre, h('label', { class: 'chk' }, resolve, 'Replace {{variables}} with the values of the active environment')),
    buttons: [
      { label: 'Copy', primary: true, onClick: async () => { await copyText(pre.textContent); app.status('cURL command copied.'); return true; } },
      { label: 'Close', value: null },
    ],
  });
}

// ---------------------------------------------------------------- cookies

export function cookiesDialog() {
  const tbody = h('tbody');
  const info = h('span', { class: 'muted' });
  const load = async () => {
    const list = await get('/cookies');
    info.textContent = `${list.length} cookie${list.length === 1 ? '' : 's'}`;
    tbody.replaceChildren(...(list.length ? list.map(c => h('tr', null,
      h('td', null, c.domain), h('td', { class: 'mono' }, c.name), h('td', { class: 'mono sel-text cookie-val', title: c.value }, c.value), h('td', null, c.path),
      h('td', null, c.expires ?? 'Session'), h('td', null, [c.secure ? 'Secure' : '', c.httpOnly ? 'HttpOnly' : ''].filter(Boolean).join(', ')),
      h('td', null, h('button', { class: 'kv-x', title: 'Delete', html: icon('close'), onclick: async () => { await del('/cookies', { domain: c.domain, name: c.name, path: c.path }); load(); } }))))
      : [h('tr', null, h('td', { colspan: 7, class: 'placeholder' }, 'The cookie jar is empty. Cookies set by responses are kept here and sent with later requests.'))]));
  };
  load().catch(e => alertError(e.message));
  return modal({
    title: 'Cookies',
    width: 900,
    body: h('div', { class: 'form' },
      h('div', { class: 'cookie-scroll' }, h('table', { class: 'edit-table info-table' },
        h('thead', null, h('tr', null, ['Domain', 'Name', 'Value', 'Path', 'Expires', 'Flags', ''].map(t => h('th', null, t)))), tbody)), info),
    buttons: [
      { label: 'Clear all', align: 'left', onClick: async () => {
        if (await confirmDlg('Delete all cookies from the cookie jar?', { title: 'Clear cookies', ok: 'Delete', danger: true })) { await del('/cookies'); await load(); }
        return false;
      } },
      { label: 'Refresh', onClick: async () => { await load(); return false; } },
      { label: 'Close', primary: true, value: null },
    ],
  });
}

// ---------------------------------------------------------------- preferences

export async function preferencesDialog(app) {
  const p = app.prefs;
  const theme = h('select', { class: 'inp' }, [['system', 'Same as the system'], ['light', 'Light'], ['dark', 'Dark']].map(([v, l]) => h('option', { value: v, selected: p.theme === v }, l)));
  const font = h('input', { class: 'inp num-inp', type: 'number', min: 9, max: 24, value: p.editorFontSize });
  const timeout = h('input', { class: 'inp num-inp', type: 'number', min: 0, max: 3600000, value: p.defaultTimeoutMs });
  const verify = h('input', { type: 'checkbox', checked: p.defaultVerifySsl });
  const wrap = h('input', { type: 'checkbox', checked: p.wrap });
  const r = await modal({
    title: 'Preferences',
    width: 520,
    body: h('div', { class: 'form2' },
      h('label', { class: 'frow' }, h('span', null, 'Theme'), theme),
      h('label', { class: 'frow' }, h('span', null, 'Editor font size'), h('span', null, font, ' px')),
      h('div', { class: 'sm-sep' }, 'New requests'),
      h('label', { class: 'frow' }, h('span', null, 'Timeout'), h('span', null, timeout, ' ms')),
      h('div', { class: 'frow' }, h('span', null, 'SSL'), h('label', { class: 'chk' }, verify, 'Verify SSL certificates')),
      h('div', { class: 'sm-sep' }, 'Responses'),
      h('div', { class: 'frow' }, h('span', null, 'Body'), h('label', { class: 'chk' }, wrap, 'Wrap long lines'))),
    buttons: [{ label: 'OK', primary: true, value: true }, { label: 'Cancel', value: false }],
  });
  if (!r) return;
  p.theme = theme.value;
  p.editorFontSize = Math.min(24, Math.max(9, parseInt(font.value, 10) || 13));
  p.defaultTimeoutMs = Math.max(0, parseInt(timeout.value, 10) || 0);
  p.defaultVerifySsl = verify.checked;
  p.wrap = wrap.checked;
  app.applyPrefs();
  app.saveStateSoon();
}

// ---------------------------------------------------------------- help

export function shortcutsDialog() {
  const rows = [
    ['Ctrl+Enter', 'Send the request'], ['Ctrl+S', 'Save the request'], ['Ctrl+Shift+S', 'Save as…'], ['Ctrl+T', 'New request tab'],
    ['Ctrl+W', 'Close the tab'], ['Ctrl+Tab / Ctrl+Shift+Tab', 'Next / previous tab'], ['Ctrl+L', 'Go to the URL'], ['Ctrl+O', 'Import'],
    ['Ctrl+E', 'Environments'], ['F2', 'Rename the request'], ['Esc', 'Cancel the running request'],
  ];
  return modal({
    title: 'Keyboard shortcuts',
    width: 440,
    body: h('table', { class: 'kbd-table' }, rows.map(([k, d]) => h('tr', null, h('td', null, k.split(' / ').map((x, i) => [i ? ' / ' : '', ...x.split('+').flatMap((y, j) => [j ? '+' : '', h('kbd', null, y)])])), h('td', null, d)))),
  });
}

export function aboutDialog(app) {
  const v = app.version;
  return modal({
    title: 'About ZawHTTP',
    width: 440,
    body: h('div', { class: 'about' }, h('img', { src: 'icon-256.png', width: 64, height: 64, alt: '' }),
      h('div', null, h('h2', null, 'ZawHTTP'),
        h('p', null, `Version ${v?.version ?? '?'}`),
        h('p', null, 'An HTTP client for testing APIs: collections, environments, history, cURL and Postman import.'),
        h('p', { class: 'muted' }, `${v?.os ?? ''} · .NET ${v?.runtime ?? ''}`))),
  });
}
