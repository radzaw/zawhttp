// A request tab: URL bar, request editors (params, headers, body, auth, settings) and the response viewer.
import { post, urlWithToken } from '../api.js';
import { h, fmtBytes, fmtMs, makeSplitter, copyText, statusClass, methodClass, uid } from '../util.js';
import { icon } from '../icons.js';
import { KvTable } from '../kvtable.js';
import { METHODS, RAW_TYPES, normalizeRequest, urlWithParams, paramsFromUrl, resolveVars, missingVars, varSegments } from '../model.js';
import { formatJson, formatXml, bodyKind } from '../format.js';
import { promptDlg, alertError } from '../dialogs.js';

const BODY_MODES = [['none', 'none'], ['formdata', 'form-data'], ['urlencoded', 'x-www-form-urlencoded'], ['raw', 'raw'], ['binary', 'binary']];
const AUTH_TYPES = [['none', 'No auth'], ['basic', 'Basic auth'], ['bearer', 'Bearer token'], ['apikey', 'API key']];
const HIGHLIGHT_LIMIT = 2_000_000;

export class RequestView {
  constructor(app, tab) {
    this.app = app;
    this.tab = tab;
    this.response = null;
    this.error = null;
    this.running = null;
    this.reqTab = 'params';
    this.respTab = 'body';
    this.bodyView = 'pretty';
    this.el = h('div', { class: 'view rv' });
    this.build();
  }

  get req() { return this.tab.request; }

  /** Replaces the whole request (cURL paste) and rebuilds the editors. */
  replaceRequest(r) {
    this.tab.request = normalizeRequest(r);
    this.build();
    this.changed();
  }

  changed() {
    this.updateCounts();
    this.app.requestChanged(this.tab);
  }

  focusUrl() {
    this.urlIn.focus();
    this.urlIn.select();
  }

  // ------------------------------------------------------------------ layout

  build() {
    const req = this.req;
    this.methodSel = h('select', { class: 'inp method-sel ' + methodClass(req.method), title: 'HTTP method' });
    this.fillMethods();
    this.methodSel.addEventListener('change', async () => {
      if (this.methodSel.value === '__custom') {
        const m = await promptDlg('Custom method', 'HTTP method name:', '', { placeholder: 'e.g. PURGE' });
        if (m && /^[A-Za-z][A-Za-z0-9_-]*$/.test(m.trim())) req.method = m.trim().toUpperCase();
        this.fillMethods();
      } else {
        req.method = this.methodSel.value;
      }
      this.methodSel.className = 'inp method-sel ' + methodClass(req.method);
      this.changed();
    });

    this.urlIn = h('input', { class: 'inp url-inp mono', value: req.url, spellcheck: false, placeholder: 'Enter a URL or paste a cURL command' });
    // The input's own text is transparent; this layer behind it draws the same text with {{variables}} colored.
    this.urlHl = h('div', { class: 'url-hl', 'aria-hidden': 'true' });
    this.urlIn.addEventListener('input', () => {
      req.url = this.urlIn.value;
      req.params = paramsFromUrl(req.url, req.params);
      this.paramsTable.setRows(req.params);
      this.highlightUrl();
      this.changed();
    });
    const syncScroll = () => { this.urlHl.scrollLeft = this.urlIn.scrollLeft; };
    for (const ev of ['scroll', 'keyup', 'click', 'select', 'focus', 'blur']) this.urlIn.addEventListener(ev, syncScroll);
    this.urlIn.addEventListener('keydown', () => requestAnimationFrame(syncScroll));
    this.urlIn.addEventListener('keydown', e => {
      if (e.key === 'Enter' && !e.ctrlKey) { e.preventDefault(); this.send(); }
    });
    this.urlIn.addEventListener('paste', e => {
      const text = e.clipboardData?.getData('text') || '';
      if (/^\s*curl\s/i.test(text)) {
        e.preventDefault();
        this.importCurl(text);
      }
    });
    this.urlIn.addEventListener('mouseenter', () => {
      const vars = this.app.activeVariables();
      const resolved = resolveVars(req.url, vars);
      const missing = missingVars(req.url, vars);
      this.urlIn.title = [resolved !== req.url ? resolved : '', missing.length ? `Not defined: ${missing.map(v => `{{${v}}}`).join(', ')}` : '']
        .filter(Boolean).join('\n');
    });

    this.sendBtn = h('button', { class: 'btn primary send-btn', onclick: () => (this.running ? this.cancel() : this.send()) });
    this.updateSendBtn();
    const urlbar = h('div', { class: 'urlbar' },
      h('div', { class: 'url-group' }, this.methodSel, h('div', { class: 'url-field' }, this.urlHl, this.urlIn)),
      this.sendBtn,
      h('button', { class: 'tbtn', title: 'Save (Ctrl+S)', html: icon('save') + '<span>Save</span>', onclick: () => this.app.saveTab(this.tab) }),
      h('button', { class: 'tbtn', title: 'Generate code (cURL)', html: icon('code'), onclick: () => this.app.codeDialog(this.tab) }));

    // Request editors.
    this.reqTabsEl = h('div', { class: 'subtabs' });
    this.paramsTable = new KvTable({
      rows: req.params,
      onChange: () => {
        req.url = urlWithParams(req.url, req.params);
        this.urlIn.value = req.url;
        this.highlightUrl();
        this.changed();
      },
    });
    this.headersTable = new KvTable({ rows: req.headers, headers: true, onChange: () => this.changed() });
    this.panes = {
      params: h('div', { class: 'rv-pane' }, this.paramsTable.el),
      headers: h('div', { class: 'rv-pane' }, this.headersTable.el,
        h('div', { class: 'hint' }, 'Added automatically unless set here: User-Agent, Accept, Content-Type (from the body) and Authorization (from the Auth tab).')),
      body: h('div', { class: 'rv-pane' }),
      auth: h('div', { class: 'rv-pane' }),
      settings: h('div', { class: 'rv-pane' }),
    };
    this.buildBody();
    this.buildAuth();
    this.buildSettings();

    const tabs = [['params', 'Params'], ['headers', 'Headers'], ['body', 'Body'], ['auth', 'Auth'], ['settings', 'Settings']];
    this.reqTabBtns = {};
    for (const [id, label] of tabs) {
      const b = h('button', { class: 'subtab', onclick: () => this.showReqTab(id) }, label, h('span', { class: 'count' }));
      this.reqTabBtns[id] = b;
      this.reqTabsEl.append(b);
    }
    this.reqArea = h('div', { class: 'rv-req', style: { height: (this.app.prefs.reqHeight || 250) + 'px' } },
      h('div', { class: 'rv-tabs' }, this.reqTabsEl), ...Object.values(this.panes));
    const split = h('div', { class: 'splitter-h' });
    makeSplitter(split, {
      axis: 'y',
      get: () => this.reqArea.offsetHeight,
      set: v => { this.reqArea.style.height = Math.max(90, Math.min(v, this.el.offsetHeight - 120)) + 'px'; },
      onEnd: () => { this.app.prefs.reqHeight = this.reqArea.offsetHeight; this.app.saveStateSoon(); },
    });
    this.respEl = h('div', { class: 'rv-resp' });

    this.el.replaceChildren(urlbar, this.reqArea, split, this.respEl);
    this.showReqTab(this.reqTab);
    this.updateCounts();
    this.highlightUrl();
    this.renderResponse();
  }

  /** Redraws the URL's highlight layer: defined {{variables}} in the accent color, undefined ones flagged. */
  highlightUrl() {
    const segs = varSegments(this.urlIn.value, this.app.activeVariables());
    this.urlHl.replaceChildren(...segs.map(s => (s.state ? h('span', { class: 'var ' + s.state }, s.text) : s.text)), ' ');
    this.urlHl.scrollLeft = this.urlIn.scrollLeft;
  }

  fillMethods() {
    const m = this.req.method;
    const list = METHODS.includes(m) ? METHODS : [...METHODS, m];
    this.methodSel.replaceChildren(...list.map(x => h('option', { value: x, selected: x === m, class: methodClass(x) }, x)),
      h('option', { value: '__custom' }, 'Custom…'));
    this.methodSel.value = m;
  }

  showReqTab(id) {
    this.reqTab = id;
    for (const [k, p] of Object.entries(this.panes)) p.style.display = k === id ? '' : 'none';
    for (const [k, b] of Object.entries(this.reqTabBtns)) b.classList.toggle('active', k === id);
  }

  updateCounts() {
    const req = this.req;
    const n = list => list.filter(r => r.enabled && (r.key || r.value)).length;
    const set = (id, text) => { this.reqTabBtns[id].querySelector('.count').textContent = text; };
    set('params', n(req.params) ? String(n(req.params)) : '');
    set('headers', n(req.headers) ? String(n(req.headers)) : '');
    set('body', req.body.mode !== 'none' ? '•' : '');
    set('auth', req.auth.type !== 'none' ? '•' : '');
    set('settings', '');
  }

  buildBody() {
    const b = this.req.body;
    const pane = this.panes.body;
    const content = h('div', { class: 'body-content' });
    const rawType = h('select', { class: 'inp' }, RAW_TYPES.map(([v, l]) => h('option', { value: v, selected: b.rawType === v }, l)));
    const beautify = h('button', { class: 'linkbtn', onclick: () => this.beautify() }, 'Beautify');
    const rawTools = h('span', { class: 'body-raw-tools' }, rawType, beautify);
    rawType.addEventListener('change', () => { b.rawType = rawType.value; this.changed(); });

    const modes = h('div', { class: 'body-modes' }, BODY_MODES.map(([v, l]) => {
      const r = h('input', { type: 'radio', name: 'bm-' + this.tab.id, value: v, checked: b.mode === v });
      r.addEventListener('change', () => { b.mode = v; render(); this.changed(); });
      return h('label', { class: 'chk' }, r, l);
    }), rawTools);

    const render = () => {
      rawTools.style.display = b.mode === 'raw' ? '' : 'none';
      if (b.mode === 'none') {
        content.replaceChildren(h('div', { class: 'placeholder' }, 'This request does not have a body.'));
      } else if (b.mode === 'raw') {
        this.rawTa = h('textarea', { class: 'inp body-raw mono', spellcheck: false, value: b.raw, placeholder: b.rawType === 'json' ? '{\n  "key": "value"\n}' : '' });
        this.rawTa.addEventListener('input', () => { b.raw = this.rawTa.value; this.changed(); });
        this.rawTa.addEventListener('keydown', e => {
          if (e.key === 'Tab' && !e.ctrlKey && !e.altKey) {
            e.preventDefault();
            document.execCommand('insertText', false, '  ');
          }
        });
        content.replaceChildren(this.rawTa);
      } else if (b.mode === 'urlencoded') {
        content.replaceChildren(new KvTable({ rows: b.urlencoded, onChange: () => this.changed() }).el);
      } else if (b.mode === 'formdata') {
        content.replaceChildren(new KvTable({ rows: b.form, files: true, onChange: () => this.changed() }).el);
      } else if (b.mode === 'binary') {
        const file = h('input', { class: 'inp wide mono', value: b.file, spellcheck: false, placeholder: 'Path of a local file, e.g. C:\\data\\upload.zip' });
        file.addEventListener('input', () => { b.file = file.value; this.changed(); });
        content.replaceChildren(h('div', { class: 'form' }, h('label', null, 'File to send as the request body:'), file,
          h('div', { class: 'hint' }, 'The file is read when the request is sent; Content-Type is guessed from its extension unless set in Headers.')));
      }
    };
    render();
    pane.replaceChildren(modes, content);
  }

  beautify() {
    const b = this.req.body;
    if (b.mode !== 'raw' || !this.rawTa) return;
    let out = null;
    if (b.rawType === 'json') {
      out = formatJson(b.raw);
      if (out == null) { alertError('The body is not valid JSON.'); return; }
    } else if (b.rawType === 'xml' || b.rawType === 'html') {
      out = formatXml(b.raw);
    }
    if (out == null || out === b.raw) return;
    this.rawTa.focus();
    this.rawTa.select();
    document.execCommand('insertText', false, out); // keeps Ctrl+Z working
  }

  buildAuth() {
    const a = this.req.auth;
    const pane = this.panes.auth;
    const sel = h('select', { class: 'inp' }, AUTH_TYPES.map(([v, l]) => h('option', { value: v, selected: a.type === v }, l)));
    const fields = h('div', { class: 'form2' });
    const field = (label, key, { secret = false, placeholder = '' } = {}) => {
      const inp = h('input', { class: 'inp wide mono', value: a[key] ?? '', type: secret ? 'password' : 'text', spellcheck: false, placeholder });
      inp.addEventListener('input', () => { a[key] = inp.value; this.changed(); });
      const row = h('label', { class: 'frow' }, h('span', null, label), inp);
      if (secret) {
        const eye = h('button', { class: 'tbtn eye', title: 'Show / hide', html: icon('eye'), onclick: e => { e.preventDefault(); inp.type = inp.type === 'password' ? 'text' : 'password'; } });
        row.lastChild.replaceWith(h('span', { class: 'with-btn' }, inp, eye));
      }
      return row;
    };
    const render = () => {
      if (a.type === 'basic') fields.replaceChildren(field('Username', 'username'), field('Password', 'password', { secret: true }));
      else if (a.type === 'bearer') fields.replaceChildren(field('Token', 'token', { secret: true, placeholder: '{{token}}' }));
      else if (a.type === 'apikey') {
        const where = h('select', { class: 'inp' }, h('option', { value: 'header', selected: a.in !== 'query' }, 'Header'), h('option', { value: 'query', selected: a.in === 'query' }, 'Query parameter'));
        where.addEventListener('change', () => { a.in = where.value; this.changed(); });
        fields.replaceChildren(field('Key', 'key', { placeholder: 'X-API-Key' }), field('Value', 'value', { secret: true }), h('label', { class: 'frow' }, h('span', null, 'Add to'), where));
      } else fields.replaceChildren(h('div', { class: 'hint' }, 'This request does not use authorization.'));
    };
    sel.addEventListener('change', () => { a.type = sel.value; render(); this.changed(); });
    render();
    pane.replaceChildren(h('div', { class: 'form2' }, h('label', { class: 'frow' }, h('span', null, 'Type'), sel)), h('div', { style: { height: '8px' } }), fields,
      h('div', { class: 'hint' }, 'Values can use {{variables}} from the active environment.'));
  }

  buildSettings() {
    const s = this.req.settings;
    const num = (key, min, max) => {
      const inp = h('input', { class: 'inp num-inp', type: 'number', min, max, value: s[key] });
      inp.addEventListener('input', () => { const v = parseInt(inp.value, 10); if (!isNaN(v)) { s[key] = v; this.changed(); } });
      return inp;
    };
    const chk = (key, label) => {
      const c = h('input', { type: 'checkbox', checked: s[key] });
      c.addEventListener('change', () => { s[key] = c.checked; this.changed(); });
      return h('label', { class: 'chk' }, c, label);
    };
    this.panes.settings.replaceChildren(h('div', { class: 'form2' },
      h('label', { class: 'frow' }, h('span', null, 'Timeout (ms)'), h('span', null, num('timeoutMs', 0, 3600000), h('span', { class: 'muted' }, '  0 = no timeout'))),
      h('div', { class: 'frow' }, h('span', null, 'Redirects'), chk('followRedirects', 'Follow redirects automatically')),
      h('label', { class: 'frow' }, h('span', null, 'Maximum redirects'), h('span', null, num('maxRedirects', 0, 50))),
      h('div', { class: 'frow' }, h('span', null, 'SSL'), chk('verifySsl', 'Verify SSL certificates')),
    ));
  }

  async importCurl(text) {
    try {
      const r = await post('/import', { text });
      if (r.kind !== 'request') throw new Error('Not a cURL command.');
      this.replaceRequest(r.request);
      this.app.status('Imported the cURL command.');
    } catch (e) {
      alertError(e.message);
    }
  }

  // ------------------------------------------------------------------ sending

  updateSendBtn() {
    this.sendBtn.innerHTML = this.running ? icon('stop') + '<span>Cancel</span>' : icon('send') + '<span>Send</span>';
    this.sendBtn.title = this.running ? 'Cancel the request' : 'Send (Ctrl+Enter)';
  }

  async send() {
    if (this.running) return;
    const runId = uid();
    this.running = runId;
    this.error = null;
    this.app.tabs.setBusy(this.tab.id, true);
    this.updateSendBtn();
    this.renderResponse();
    const started = performance.now();
    try {
      const res = await post('/send', { request: this.req, variables: this.app.activeVariables(), runId, name: this.tab.nodeId ? this.tab.name : null });
      if (this.running !== runId) return;
      this.response = res;
      this.app.status(`${res.status} ${res.reason} · ${fmtMs(res.totalMs)} · ${fmtBytes(res.size)}`);
    } catch (e) {
      if (this.running !== runId) return;
      this.error = e.message;
      this.response = null;
      this.app.status(`Request failed after ${fmtMs(performance.now() - started)}.`);
    } finally {
      if (this.running === runId) {
        this.running = null;
        this.app.tabs.setBusy(this.tab.id, false);
        this.updateSendBtn();
        this.renderResponse();
        this.app.sidebar.refreshHistory();
      }
    }
  }

  cancel() {
    if (this.running) post('/cancel', { runId: this.running }).catch(() => {});
  }

  // ------------------------------------------------------------------ response

  renderResponse() {
    const el = this.respEl;
    if (this.running) {
      el.replaceChildren(h('div', { class: 'resp-empty' }, h('span', { class: 'spin' }), h('div', null, 'Sending request…'),
        h('button', { class: 'btn', onclick: () => this.cancel() }, 'Cancel')));
      return;
    }
    if (this.error) {
      el.replaceChildren(h('div', { class: 'resp-error' }, h('span', { html: icon('error') }), h('div', null, h('b', null, 'Could not get a response'), h('div', { class: 'resp-error-msg' }, this.error))));
      return;
    }
    const r = this.response;
    if (!r) {
      el.replaceChildren(h('div', { class: 'resp-empty muted' }, h('div', { class: 'resp-empty-big', html: icon('send') }),
        h('div', null, 'Enter a URL and press ', h('kbd', null, 'Ctrl'), '+', h('kbd', null, 'Enter'), ' to send the request.')));
      return;
    }

    const timing = `Waiting (TTFB): ${fmtMs(r.ttfbMs)}\nDownload: ${fmtMs(r.totalMs - r.ttfbMs)}\nTotal: ${fmtMs(r.totalMs)}`;
    const meta = h('div', { class: 'resp-meta' },
      h('span', { class: 'status-pill ' + statusClass(r.status) }, `${r.status} ${r.reason}`),
      h('span', { class: 'resp-stat', title: timing }, h('span', { class: 'muted' }, 'Time '), fmtMs(r.totalMs)),
      h('span', { class: 'resp-stat' }, h('span', { class: 'muted' }, 'Size '), fmtBytes(r.size), r.truncated ? ' (truncated)' : ''),
      r.redirects.length ? h('span', { class: 'resp-stat muted' }, `${r.redirects.length} redirect${r.redirects.length > 1 ? 's' : ''}`) : null,
      r.unresolved?.length ? h('span', { class: 'resp-warn', title: 'These variables are not defined in the active environment or globals and were sent as typed.' },
        h('span', { html: icon('warning') }), ' Unresolved: ', r.unresolved.map(v => `{{${v}}}`).join(', ')) : null,
      h('span', { class: 'grow' }),
      r.isText ? h('button', { class: 'tbtn', title: 'Copy the response body', html: icon('copy'), onclick: () => { copyText(r.text ?? ''); this.app.status('Response body copied.'); } }) : null,
      h('a', { class: 'tbtn', title: 'Save the response body to a file', html: icon('save'), href: urlWithToken('/response/' + r.id, { download: 'true' }), download: '' }));

    const tabsEl = h('div', { class: 'subtabs' });
    const content = h('div', { class: 'resp-content' });
    const counted = (label, n) => [label, n ? h('span', { class: 'count' }, n) : null];
    const tabs = [['body', 'Body'], ['headers', counted('Headers', r.headers.length)], ['cookies', counted('Cookies', r.cookies.length)], ['timeline', 'Timeline']];
    const show = id => {
      this.respTab = id;
      for (const b of tabsEl.children) b.classList.toggle('active', b.dataset.id === id);
      content.replaceChildren(id === 'body' ? this.bodyEl(r) : id === 'headers' ? this.headersEl(r) : id === 'cookies' ? this.cookiesEl(r) : this.timelineEl(r));
    };
    for (const [id, label] of tabs) tabsEl.append(h('button', { class: 'subtab', 'data-id': id, onclick: () => show(id) }, label));
    el.replaceChildren(meta, h('div', { class: 'rv-tabs' }, tabsEl), content);
    show(this.respTab);
  }

  bodyEl(r) {
    const kind = bodyKind(r.contentType, r.text);
    const fileUrl = urlWithToken('/response/' + r.id);
    if (!r.isText) {
      if (kind === 'image') return h('div', { class: 'resp-preview center' }, h('img', { src: fileUrl, alt: 'Response image' }));
      if (kind === 'pdf') return h('iframe', { class: 'resp-frame', src: fileUrl });
      return h('div', { class: 'placeholder' }, `Binary response (${r.contentType || 'unknown type'}, ${fmtBytes(r.size)}). Use the save button to store it in a file.`);
    }
    if (r.size === 0) return h('div', { class: 'placeholder' }, 'The response has no body.');

    const canPreview = kind === 'html' || (r.contentType || '').includes('svg');
    if (this.bodyView === 'preview' && !canPreview) this.bodyView = 'pretty';
    const views = [['pretty', 'Pretty'], ['raw', 'Raw'], ...(canPreview ? [['preview', 'Preview']] : [])];
    const viewBtns = views.map(([id, label]) => h('button', {
      class: 'subtab small',
      'data-view': id,
      onclick: () => {
        this.bodyView = id;
        const next = this.bodyArea(r, kind, fileUrl);
        area.replaceWith(next);
        area = next;
        markViews();
      },
    }, label));
    const markViews = () => { for (const b of viewBtns) b.classList.toggle('active', b.dataset.view === this.bodyView); };
    markViews();
    const wrapBtn = h('button', { class: 'tbtn' + (this.app.prefs.wrap ? ' active' : ''), title: 'Wrap long lines', html: icon('wrap') });
    wrapBtn.addEventListener('click', () => {
      this.app.prefs.wrap = !this.app.prefs.wrap;
      this.app.saveStateSoon();
      wrapBtn.classList.toggle('active', this.app.prefs.wrap);
      area.classList.toggle('wrap', this.app.prefs.wrap);
    });
    const bar = h('div', { class: 'resp-bodybar' }, h('div', { class: 'subtabs' }, viewBtns), h('span', { class: 'muted kind' }, kind.toUpperCase()), h('span', { class: 'grow' }),
      r.textTruncated ? h('span', { class: 'resp-warn' }, 'Showing the first 10 MB') : null, wrapBtn);
    let area = this.bodyArea(r, kind, fileUrl);
    return h('div', { class: 'resp-body' }, bar, area);
  }

  bodyArea(r, kind, fileUrl) {
    const text = r.text ?? '';
    const wrap = this.app.prefs.wrap ? ' wrap' : '';
    if (this.bodyView === 'preview') {
      if ((r.contentType || '').includes('svg')) return h('div', { class: 'resp-preview center' }, h('img', { src: fileUrl, alt: 'SVG preview' }));
      // No scripts, forms or navigation: the page is shown, never run.
      return h('iframe', { class: 'resp-frame', sandbox: '', srcdoc: text });
    }
    const pre = h('pre', { class: 'resp-pre mono' + wrap, tabIndex: 0 });
    if (this.bodyView === 'pretty' && kind === 'json') {
      const big = text.length > HIGHLIGHT_LIMIT;
      const out = formatJson(text, { html: !big });
      if (out == null) pre.textContent = text;
      else if (big) pre.textContent = out;
      else pre.innerHTML = out;
    } else if (this.bodyView === 'pretty' && kind === 'xml') {
      pre.textContent = formatXml(text);
    } else {
      pre.textContent = text;
    }
    return pre;
  }

  headersEl(r) {
    return h('div', { class: 'resp-scroll' }, h('table', { class: 'edit-table info-table' },
      h('thead', null, h('tr', null, h('th', null, 'Name'), h('th', null, 'Value'))),
      h('tbody', null, r.headers.map(x => h('tr', null, h('td', { class: 'mono hname' }, x.name), h('td', { class: 'mono sel-text' }, x.value))))));
  }

  cookiesEl(r) {
    if (!r.cookies.length) return h('div', { class: 'placeholder' }, 'The response did not set any cookies.');
    return h('div', { class: 'resp-scroll' }, h('table', { class: 'edit-table info-table' },
      h('thead', null, h('tr', null, ['Name', 'Value', 'Domain', 'Path', 'Expires', 'HttpOnly', 'Secure', 'SameSite'].map(t => h('th', null, t)))),
      h('tbody', null, r.cookies.map(c => h('tr', null,
        h('td', { class: 'mono' }, c.name), h('td', { class: 'mono sel-text' }, c.value), h('td', null, c.domain ?? ''), h('td', null, c.path ?? ''),
        h('td', null, c.expires ?? 'Session'), h('td', null, c.httpOnly ? '✓' : ''), h('td', null, c.secure ? '✓' : ''), h('td', null, c.sameSite ?? ''))))),
      h('div', { class: 'hint pad' }, 'Cookies are kept in the cookie jar and sent with later requests to the same site (Tools › Cookies).'));
  }

  timelineEl(r) {
    const s = r.sent;
    const lines = (list) => list.map(x => h('div', null, h('span', { class: 'tl-h' }, x.name + ':'), ' ', x.value));
    return h('div', { class: 'resp-scroll timeline mono sel-text' },
      h('div', { class: 'tl-sec' }, h('div', { class: 'tl-title' }, 'Request'),
        h('div', { class: 'tl-line' }, h('b', { class: methodClass(s.method) }, s.method), ' ', s.url), lines(s.headers),
        s.bodySize ? h('div', { class: 'tl-body' }, s.bodyPreview ?? `(${fmtBytes(s.bodySize)} of binary data)`) : null),
      r.redirects.length ? h('div', { class: 'tl-sec' }, h('div', { class: 'tl-title' }, 'Redirects'),
        r.redirects.map(x => h('div', null, h('span', { class: 'status-pill small ' + statusClass(x.status) }, x.status), ' ', x.method, ' ', x.url, ' → ', x.location))) : null,
      h('div', { class: 'tl-sec' }, h('div', { class: 'tl-title' }, 'Response'),
        h('div', { class: 'tl-line' }, `${r.httpVersion} `, h('span', { class: 'status-pill small ' + statusClass(r.status) }, `${r.status} ${r.reason}`)), lines(r.headers)),
      h('div', { class: 'tl-sec' }, h('div', { class: 'tl-title' }, 'Timing'),
        h('div', null, `Waiting (TTFB)  ${fmtMs(r.ttfbMs)}`), h('div', null, `Download        ${fmtMs(r.totalMs - r.ttfbMs)}`), h('div', null, h('b', null, `Total           ${fmtMs(r.totalMs)}`))));
  }
}
