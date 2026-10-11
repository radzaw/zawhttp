// ZawHTTP main module: layout, menus, toolbar, tabs, workspace persistence and the glue between sidebar and request tabs.
import { get, post, put, startHeartbeat, token } from './api.js';
import { h, debounce, makeSplitter, clone, uid, copyText, saveTextFile, methodClass, methodShort } from './util.js';
import { icon } from './icons.js';
import { contextMenu, closeMenus, alertError, alertInfo, confirmDlg, promptDlg, modal } from './dialogs.js';
import { newRequest, normalizeRequest, requestLabel } from './model.js';
import { RequestView } from './views/request.js';
import { Sidebar, countRequests } from './views/sidebar.js';
import {
  environmentsDialog, saveRequestDialog, importDialog, codeDialog, cookiesDialog, preferencesDialog, shortcutsDialog, aboutDialog,
} from './views/tools.js';

const DEFAULT_PREFS = { theme: 'system', editorFontSize: 13, sidebarWidth: 290, reqHeight: 250, wrap: true, defaultTimeoutMs: 30000, defaultVerifySsl: true };

// ---------------------------------------------------------------- tabs

class Tabs {
  constructor(app, bar, content) {
    this.app = app;
    this.bar = bar;
    this.content = content;
    this.tabs = new Map();
    this.active = null;
    this.addBtn = h('button', { class: 'tab-add', title: 'New request (Ctrl+T)', html: icon('plus'), onclick: () => app.newTab() });
    bar.append(this.addBtn);
    bar.addEventListener('wheel', e => { bar.scrollLeft += e.deltaY; }, { passive: true });
  }

  add(tab, view) {
    const t = { tab, view };
    t.badge = h('span', { class: 'mbadge' });
    t.titleEl = h('span', { class: 'tab-title' });
    t.el = h('div', { class: 'tab' }, t.badge, t.titleEl, h('span', { class: 'tab-x', title: 'Close (Ctrl+W, middle click)', html: '×' }));
    t.el.addEventListener('mousedown', e => {
      if (e.button === 1) { e.preventDefault(); this.app.closeTab(tab.id); }
      else if (e.button === 0 && !e.target.closest('.tab-x')) this.activate(tab.id);
    });
    t.el.querySelector('.tab-x').addEventListener('click', e => { e.stopPropagation(); this.app.closeTab(tab.id); });
    t.el.addEventListener('dblclick', () => this.app.renameTab(tab));
    t.el.addEventListener('contextmenu', e => {
      e.preventDefault();
      contextMenu(e.clientX, e.clientY, [
        { label: 'Close', shortcut: 'Ctrl+W', onClick: () => this.app.closeTab(tab.id) },
        { label: 'Close other tabs', onClick: () => this.app.closeTabs([...this.tabs.keys()].filter(id => id !== tab.id)) },
        { label: 'Close all tabs', onClick: () => this.app.closeTabs([...this.tabs.keys()]) },
        '-',
        { label: 'Duplicate', icon: 'duplicate', onClick: () => this.app.openRequest(clone(tab.request), { name: tab.name + ' copy' }) },
        { label: 'Rename…', icon: 'edit', shortcut: 'F2', onClick: () => this.app.renameTab(tab) },
        tab.nodeId ? { label: 'Show in collection', icon: 'collection', onClick: () => { this.app.sidebar.setMode('collections'); this.app.sidebar.reveal(tab.nodeId); } } : null,
      ]);
    });
    t.pane = h('div', { class: 'tab-pane' }, view.el);
    this.bar.insertBefore(t.el, this.addBtn);
    this.content.append(t.pane);
    this.tabs.set(tab.id, t);
    this.update(tab);
    return t;
  }

  update(tab) {
    const t = this.tabs.get(tab.id);
    if (!t) return;
    const m = tab.request.method;
    t.badge.className = 'mbadge ' + methodClass(m);
    t.badge.textContent = methodShort(m);
    t.titleEl.textContent = this.app.tabTitle(tab);
    t.el.title = `${this.app.tabTitle(tab)}\n${tab.request.url}`;
    t.el.classList.toggle('modified', this.app.isModified(tab));
    t.el.classList.toggle('unsaved', !tab.nodeId);
  }

  activate(id) {
    const t = this.tabs.get(id);
    if (!t) return;
    if (this.active && this.active !== id) {
      const p = this.tabs.get(this.active);
      p?.el.classList.remove('active');
      p?.pane.classList.remove('active');
    }
    this.active = id;
    t.el.classList.add('active');
    t.pane.classList.add('active');
    t.el.scrollIntoView({ inline: 'nearest', block: 'nearest' });
    this.app.tabActivated(t.tab);
  }

  remove(id) {
    const t = this.tabs.get(id);
    if (!t) return;
    const ids = [...this.tabs.keys()];
    const i = ids.indexOf(id);
    t.el.remove();
    t.pane.remove();
    this.tabs.delete(id);
    if (this.active === id) {
      this.active = null;
      const next = ids[i + 1] ?? ids[i - 1];
      if (next && this.tabs.has(next)) this.activate(next);
    }
  }

  setBusy(id, on) { this.tabs.get(id)?.el.classList.toggle('busy', !!on); }
  view(id) { return this.tabs.get(id)?.view; }
  ids() { return [...this.tabs.keys()]; }
}

// ---------------------------------------------------------------- app

class App {
  constructor() {
    this.saveStateSoon = debounce(() => this.saveState(), 800);
    this.saveCollections = debounce(() => put('/doc/collections', { collections: this.collections }).catch(e => this.status('Could not save collections: ' + e.message)), 300);
    this.saveEnvs = debounce(() => put('/doc/environments', this.envs).catch(e => this.status('Could not save environments: ' + e.message)), 300);
    this.tabList = [];
  }

  async init() {
    try {
      const [state, cols, envs] = await Promise.all([get('/doc/state'), get('/doc/collections'), get('/doc/environments')]);
      this.state = state || {};
      this.collections = Array.isArray(cols?.collections) ? cols.collections : [];
      this.envs = { globals: envs?.globals || [], environments: envs?.environments || [] };
    } catch (e) {
      document.body.textContent = e.message;
      return;
    }
    this.prefs = Object.assign({}, DEFAULT_PREFS, this.state.prefs);
    this.state.prefs = this.prefs;
    this.activeEnvId = this.envs.environments.some(e => e.id === this.state.activeEnv) ? this.state.activeEnv : null;
    this.applyPrefs();
    matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => this.applyPrefs());
    try { this.version = await get('/version'); } catch { this.version = null; }

    this.buildMenu();
    this.buildToolbar();
    this.buildLayout();
    this.buildStatusbar();
    this.sidebar = new Sidebar(this, document.getElementById('sidebar'));
    this.tabs = new Tabs(this, document.getElementById('tabbar'), document.getElementById('tabcontent'));

    const saved = Array.isArray(this.state.tabs) ? this.state.tabs : [];
    for (const t of saved) {
      const nodeId = t.nodeId && this.findNode(t.nodeId) ? t.nodeId : null;
      this.addTab({ id: t.id || uid(), name: t.name || 'New request', nodeId, request: normalizeRequest(t.request), saved: nodeId ? t.saved : null }, { activate: false });
    }
    if (!this.tabList.length) this.newTab();
    else this.tabs.activate(this.tabList.some(t => t.id === this.state.activeTab) ? this.state.activeTab : this.tabList[0].id);

    document.addEventListener('keydown', e => this.globalKey(e));
    window.addEventListener('beforeunload', () => this.flush());
    startHeartbeat();
    this.status('Ready.');
  }

  applyPrefs() {
    const p = this.prefs;
    const dark = p.theme === 'dark' || (p.theme === 'system' && matchMedia('(prefers-color-scheme: dark)').matches);
    document.documentElement.dataset.theme = dark ? 'dark' : 'light';
    document.documentElement.style.setProperty('--ed-font-size', p.editorFontSize + 'px');
    document.querySelector('meta[name=theme-color]').content = dark ? '#14161b' : '#f3f4f7';
    try { sessionStorage.setItem('zawhttp-theme', p.theme); } catch { /* storage unavailable */ }
    if (this.themeBtn) this.themeBtn.innerHTML = icon(dark ? 'sun' : 'moon');
  }

  // ------------------------------------------------------------------ chrome

  buildMenu() {
    const bar = document.getElementById('menubar');
    const menus = [
      ['File', () => [
        { label: 'New request', icon: 'newtab', shortcut: 'Ctrl+T', onClick: () => this.newTab() },
        { label: 'New collection…', icon: 'collection', onClick: () => this.newCollection() },
        '-',
        { label: 'Import…', icon: 'import', shortcut: 'Ctrl+O', onClick: () => this.importDialog() },
        { label: 'Export all collections…', icon: 'export', disabled: !this.collections.length, onClick: () => this.exportAll() },
        '-',
        { label: 'Save', icon: 'save', shortcut: 'Ctrl+S', onClick: () => this.saveTab(this.activeTab()) },
        { label: 'Save as…', shortcut: 'Ctrl+Shift+S', onClick: () => this.saveTab(this.activeTab(), { as: true }) },
        '-',
        { label: 'Close tab', shortcut: 'Ctrl+W', onClick: () => this.closeTab(this.tabs.active) },
        { label: 'Exit', onClick: () => this.exit() },
      ]],
      ['Request', () => [
        { label: 'Send', icon: 'send', shortcut: 'Ctrl+Enter', onClick: () => this.activeView()?.send() },
        { label: 'Cancel', icon: 'stop', shortcut: 'Esc', disabled: !this.activeView()?.running, onClick: () => this.activeView()?.cancel() },
        '-',
        { label: 'Generate code…', icon: 'code', onClick: () => this.codeDialog(this.activeTab()) },
        { label: 'Copy as cURL', icon: 'copy', onClick: () => this.copyCurl(this.activeTab().request) },
        '-',
        { label: 'Rename…', icon: 'edit', shortcut: 'F2', onClick: () => this.renameTab(this.activeTab()) },
        { label: 'Duplicate', icon: 'duplicate', onClick: () => { const t = this.activeTab(); this.openRequest(clone(t.request), { name: t.name + ' copy' }); } },
      ]],
      ['Tools', () => [
        { label: 'Environments…', icon: 'environment', shortcut: 'Ctrl+E', onClick: () => this.manageEnvironments() },
        { label: 'Cookies…', icon: 'cookie', onClick: () => cookiesDialog() },
        '-',
        { label: 'Preferences…', icon: 'settings', onClick: () => preferencesDialog(this) },
      ]],
      ['Help', () => [
        { label: 'Keyboard shortcuts', onClick: () => shortcutsDialog() },
        { label: 'About ZawHTTP', icon: 'info', onClick: () => aboutDialog(this) },
      ]],
    ];
    let openEl = null;
    for (const [label, items] of menus) {
      const el = h('div', { class: 'menubar-item' }, label);
      const open = () => {
        const r = el.getBoundingClientRect();
        el.classList.add('open');
        openEl = el;
        contextMenu(r.left, r.bottom, items(), { onClose: () => { el.classList.remove('open'); if (openEl === el) openEl = null; } });
      };
      el.addEventListener('mousedown', e => {
        e.preventDefault();
        if (el.classList.contains('open')) closeMenus(); else open();
      });
      el.addEventListener('mouseenter', () => { if (openEl && openEl !== el) { closeMenus(); open(); } });
      bar.append(el);
    }
  }

  buildToolbar() {
    const tb = document.getElementById('toolbar');
    const btn = (ic, title, onclick, label) => h('button', { class: 'tbtn', title, onclick, html: icon(ic) + (label ? `<span>${label}</span>` : '') });
    this.envSel = h('select', { class: 'inp env-sel', title: 'Active environment' });
    this.envSel.addEventListener('change', () => this.setActiveEnv(this.envSel.value || null));
    this.themeBtn = btn('moon', 'Switch between light and dark', () => {
      this.prefs.theme = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
      this.applyPrefs();
      this.saveStateSoon();
    });
    tb.append(
      h('button', { class: 'tbtn new-btn', title: 'New request (Ctrl+T)', onclick: () => this.newTab(), html: icon('plus') + '<span>New request</span>' }),
      btn('import', 'Import (Ctrl+O)', () => this.importDialog(), 'Import'),
      h('span', { class: 'grow' }),
      h('div', { class: 'env-picker' },
        h('button', { class: 'env-manage', title: 'Manage environments (Ctrl+E)', onclick: () => this.manageEnvironments(), html: icon('environment') }),
        this.envSel),
      btn('cookie', 'Cookies', () => cookiesDialog()),
      this.themeBtn);
    this.fillEnvSelect();
    this.applyPrefs();
  }

  buildLayout() {
    const left = document.getElementById('sidebar');
    left.style.width = this.prefs.sidebarWidth + 'px';
    makeSplitter(document.getElementById('split-left'), {
      axis: 'x',
      get: () => left.offsetWidth,
      set: v => { left.style.width = Math.max(180, Math.min(v, innerWidth - 400)) + 'px'; },
      onEnd: () => { this.prefs.sidebarWidth = left.offsetWidth; this.saveStateSoon(); },
    });
  }

  buildStatusbar() {
    this.sbMsg = h('div', { class: 'sb-cell grow' });
    this.sbEnv = h('div', { class: 'sb-cell sb-env', title: 'Active environment — click to manage', onclick: () => this.manageEnvironments() });
    document.getElementById('statusbar').append(this.sbMsg, this.sbEnv, h('div', { class: 'sb-cell' }, 'ZawHTTP ' + (this.version?.version ?? '')));
    this.updateEnvStatus();
  }

  status(msg) {
    if (this.sbMsg) this.sbMsg.textContent = msg;
  }

  // ------------------------------------------------------------------ environments

  activeEnv() { return this.envs.environments.find(e => e.id === this.activeEnvId) || null; }

  /** Globals, then the active environment, which wins for names defined in both. */
  activeVariables() { return [...this.envs.globals, ...(this.activeEnv()?.variables || [])]; }

  fillEnvSelect() {
    this.envSel.replaceChildren(h('option', { value: '' }, 'No environment'),
      ...this.envs.environments.map(e => h('option', { value: e.id, selected: e.id === this.activeEnvId }, e.name)));
    this.envSel.value = this.activeEnvId || '';
  }

  updateEnvStatus() {
    if (!this.sbEnv) return;
    const env = this.activeEnv();
    this.sbEnv.textContent = env ? `Environment: ${env.name}` : 'No environment';
  }

  setActiveEnv(id) {
    this.activeEnvId = id;
    this.state.activeEnv = id;
    this.fillEnvSelect();
    this.updateEnvStatus();
    for (const t of this.tabList) this.tabs.view(t.id)?.highlightUrl();
    this.saveStateSoon();
  }

  async manageEnvironments() {
    const r = await environmentsDialog(this);
    if (!r) return;
    this.envs.globals = r.globals;
    this.envs.environments = r.environments;
    if (!this.activeEnvId && r.selected !== 'globals') this.activeEnvId = r.selected;
    if (!r.environments.some(e => e.id === this.activeEnvId)) this.activeEnvId = null;
    this.setActiveEnv(this.activeEnvId);
    this.saveEnvs();
  }

  // ------------------------------------------------------------------ tabs

  activeTab() { return this.tabList.find(t => t.id === this.tabs?.active); }
  activeView() { return this.tabs?.view(this.tabs.active); }

  isModified(tab) { return !!tab.nodeId && JSON.stringify(tab.request) !== tab.saved; }

  /** An unsaved tab nobody named is titled after its URL. */
  tabTitle(tab) { return !tab.nodeId && tab.name === 'New request' && tab.request.url ? requestLabel(tab.request) : tab.name; }

  addTab(tab, { activate = true } = {}) {
    this.tabList.push(tab);
    const view = new RequestView(this, tab);
    this.tabs.add(tab, view);
    if (activate) this.tabs.activate(tab.id);
    this.saveStateSoon();
    return tab;
  }

  newTab() {
    const r = newRequest();
    r.settings.timeoutMs = this.prefs.defaultTimeoutMs;
    r.settings.verifySsl = this.prefs.defaultVerifySsl;
    const tab = this.addTab({ id: uid(), name: 'New request', nodeId: null, request: r, saved: null });
    this.tabs.view(tab.id).focusUrl();
    return tab;
  }

  openRequest(request, { name } = {}) {
    const r = normalizeRequest(request);
    return this.addTab({ id: uid(), name: name || requestLabel(r), nodeId: null, request: r, saved: null });
  }

  /** A blank, unsaved tab that can be reused instead of opening yet another one. */
  pristineTab() {
    const t = this.activeTab();
    return t && !t.nodeId && !t.request.url && t.request.body.mode === 'none' && !this.tabs.view(t.id)?.response ? t : null;
  }

  openNode(node, { newTab = false } = {}) {
    const existing = !newTab && this.tabList.find(t => t.nodeId === node.id);
    if (existing) return this.tabs.activate(existing.id);
    const reuse = this.pristineTab();
    const request = normalizeRequest(clone(node.request));
    const tab = this.addTab({ id: uid(), name: node.name, nodeId: newTab && this.tabList.some(t => t.nodeId === node.id) ? null : node.id, request, saved: JSON.stringify(request) });
    if (reuse) this.removeTab(reuse.id);
    return tab;
  }

  openHistory(e) {
    this.openRequest(clone(e.request), { name: e.name || requestLabel(e.request) });
  }

  tabActivated(tab) {
    document.title = `${this.tabTitle(tab)} — ZawHTTP`;
    this.state.activeTab = tab.id;
    this.saveStateSoon();
    if (this.sidebar?.mode === 'collections') this.sidebar.render();
  }

  requestChanged(tab) {
    this.tabs.update(tab);
    this.saveStateSoon();
  }

  async closeTab(id) {
    const tab = this.tabList.find(t => t.id === id);
    if (!tab) return;
    if (this.isModified(tab)) {
      const r = await modal({
        title: 'Unsaved changes',
        body: `Save the changes to "${tab.name}"?`,
        width: 420,
        buttons: [
          { label: "Don't save", value: 'discard', align: 'left' },
          { label: 'Save', value: 'save', primary: true },
          { label: 'Cancel', value: null },
        ],
      });
      if (!r) return false;
      if (r === 'save' && !(await this.saveTab(tab))) return false;
    }
    this.tabs.view(id)?.cancel();
    this.removeTab(id);
    if (!this.tabList.length) this.newTab();
    return true;
  }

  async closeTabs(ids) {
    for (const id of ids) if ((await this.closeTab(id)) === false) break;
  }

  removeTab(id) {
    this.tabList = this.tabList.filter(t => t.id !== id);
    this.tabs.remove(id);
    this.saveStateSoon();
  }

  async renameTab(tab) {
    if (!tab) return;
    if (tab.nodeId) {
      const n = this.findNode(tab.nodeId);
      if (n) return this.renameNode(n.node);
    }
    const name = await promptDlg('Rename', 'Name:', tab.name);
    if (!name?.trim()) return;
    tab.name = name.trim();
    this.tabs.update(tab);
    this.tabActivated(this.activeTab());
  }

  /** Saves the tab into its collection request, or asks where to save it. Resolves to true once saved. */
  async saveTab(tab, { as = false } = {}) {
    if (!tab) return false;
    const found = !as && tab.nodeId ? this.findNode(tab.nodeId) : null;
    if (found) {
      found.node.request = clone(tab.request);
    } else {
      const r = await saveRequestDialog(this, { name: tab.nodeId || tab.name !== 'New request' ? tab.name : requestLabel(tab.request) });
      if (!r) return false;
      const parent = this.findNode(r.parentId)?.node;
      if (!parent) return false;
      const node = { id: uid(), type: 'request', name: r.name, request: clone(tab.request) };
      (parent.items ||= []).push(node);
      tab.nodeId = node.id;
      tab.name = r.name;
      this.sidebar.reveal(node.id);
    }
    tab.saved = JSON.stringify(tab.request);
    this.tabs.update(tab);
    this.tabActivated(tab.id === this.tabs.active ? tab : this.activeTab());
    this.saveCollections();
    this.sidebar.render();
    this.status(`Saved "${tab.name}".`);
    return true;
  }

  async saveHistoryEntry(e) {
    const tab = this.openRequest(clone(e.request), { name: e.name || requestLabel(e.request) });
    await this.saveTab(tab);
  }

  async codeDialog(tab) {
    if (tab) await codeDialog(this, tab.request);
  }

  async copyCurl(request) {
    try {
      const r = await post('/export/curl', { request, variables: null });
      await copyText(r.text);
      this.status('cURL command copied to the clipboard.');
    } catch (e) {
      alertError(e.message);
    }
  }

  // ------------------------------------------------------------------ collections

  /** Finds a node: {node, list (the array holding it), parent, path (ancestors, outermost first)}. */
  findNode(id, list = this.collections, parent = null, path = []) {
    for (const n of list) {
      if (n.id === id) return { node: n, list, parent, path };
      if (n.items) {
        const r = this.findNode(id, n.items, n, [...path, n]);
        if (r) return r;
      }
    }
    return null;
  }

  async newCollection() {
    const name = await promptDlg('New collection', 'Collection name:', '', { placeholder: 'e.g. My API' });
    if (!name?.trim()) return null;
    const c = { id: uid(), type: 'collection', name: name.trim(), items: [] };
    this.collections.push(c);
    this.saveCollections();
    this.sidebar.setMode('collections');
    this.sidebar.reveal(c.id);
    return c;
  }

  async newFolderIn(parent) {
    const name = await promptDlg('New folder', 'Folder name:', '');
    if (!name?.trim()) return;
    const f = { id: uid(), type: 'folder', name: name.trim(), items: [] };
    (parent.items ||= []).push(f);
    this.saveCollections();
    this.sidebar.reveal(f.id);
  }

  newRequestIn(parent) {
    const r = newRequest();
    r.settings.timeoutMs = this.prefs.defaultTimeoutMs;
    r.settings.verifySsl = this.prefs.defaultVerifySsl;
    const node = { id: uid(), type: 'request', name: 'New request', request: r };
    (parent.items ||= []).push(node);
    this.saveCollections();
    this.sidebar.reveal(node.id);
    this.openNode(node);
    this.activeView()?.focusUrl();
  }

  async renameNode(node) {
    const name = await promptDlg('Rename', 'Name:', node.name);
    if (!name?.trim() || name.trim() === node.name) return;
    node.name = name.trim();
    for (const t of this.tabList) if (t.nodeId === node.id) { t.name = node.name; this.tabs.update(t); }
    if (this.activeTab()) this.tabActivated(this.activeTab());
    this.saveCollections();
    this.sidebar.render();
  }

  duplicateNode(node) {
    const found = this.findNode(node.id);
    if (!found) return;
    const copy = clone(node);
    const renew = n => { n.id = uid(); for (const c of n.items || []) renew(c); };
    renew(copy);
    copy.name = node.name + ' copy';
    found.list.splice(found.list.indexOf(node) + 1, 0, copy);
    this.saveCollections();
    this.sidebar.reveal(copy.id);
  }

  async deleteNode(node) {
    const found = this.findNode(node.id);
    if (!found) return;
    const n = node.type === 'request' ? 0 : countRequests(node);
    const what = node.type === 'request' ? `the request "${node.name}"` : `the ${node.type} "${node.name}"${n ? ` and its ${n} request${n > 1 ? 's' : ''}` : ''}`;
    if (!(await confirmDlg(`Delete ${what}?`, { title: 'Delete', ok: 'Delete', danger: true }))) return;
    found.list.splice(found.list.indexOf(node), 1);
    // Open tabs of deleted requests stay open as unsaved requests.
    const gone = new Set();
    const walk = x => { gone.add(x.id); for (const c of x.items || []) walk(c); };
    walk(node);
    for (const t of this.tabList) if (gone.has(t.nodeId)) { t.nodeId = null; t.saved = null; this.tabs.update(t); }
    this.saveCollections();
    this.sidebar.render();
    this.saveStateSoon();
  }

  moveNode(dragId, targetId, mode) {
    if (dragId === targetId) return;
    const src = this.findNode(dragId);
    const dst = this.findNode(targetId);
    if (!src || !dst) return;
    src.list.splice(src.list.indexOf(src.node), 1);
    if (mode === 'into') {
      (dst.node.items ||= []).push(src.node);
      this.sidebar.expanded.add(dst.node.id);
      this.sidebar.saveExpanded();
    } else {
      dst.list.splice(dst.list.indexOf(dst.node), 0, src.node);
    }
    this.saveCollections();
    this.sidebar.render();
  }

  async exportCollection(node) {
    const name = await saveTextFile(`${node.name.replace(/[\\/:*?"<>|]/g, '_')}.zawhttp.json`, JSON.stringify({ zawhttp: 1, collections: [node] }, null, 2));
    if (name) this.status(`Exported "${node.name}" to ${name}.`);
  }

  async exportAll() {
    const name = await saveTextFile('zawhttp-workspace.json', JSON.stringify({ zawhttp: 1, collections: this.collections, environments: this.envs.environments }, null, 2));
    if (name) this.status(`Exported the workspace to ${name}.`);
  }

  importDialog() { return importDialog(this); }

  /** Adds what /import returned. Resolves to true when something was imported. */
  async applyImport(r) {
    if (r.kind === 'request') {
      this.openRequest(r.request);
      this.status('Imported the cURL command into a new tab.');
      return true;
    }
    const cols = r.collections || [];
    const envs = r.environments || [];
    for (const c of cols) { c.type = 'collection'; this.collections.push(c); }
    for (const e of envs) this.envs.environments.push(e);
    if (cols.length) { this.saveCollections(); this.sidebar.setMode('collections'); for (const c of cols) this.sidebar.reveal(c.id); }
    if (envs.length) { this.saveEnvs(); this.fillEnvSelect(); }
    const parts = [];
    if (cols.length) parts.push(`${cols.length} collection${cols.length > 1 ? 's' : ''} with ${cols.reduce((n, c) => n + countRequests(c), 0)} requests`);
    if (envs.length) parts.push(`${envs.length} environment${envs.length > 1 ? 's' : ''} (${envs.map(e => e.name).join(', ')})`);
    if (!parts.length) { alertInfo('The file contained nothing to import.'); return false; }
    this.status('Imported ' + parts.join(' and ') + '.');
    alertInfo('Imported ' + parts.join(' and ') + '.', 'Import');
    return true;
  }

  // ------------------------------------------------------------------ persistence

  stateDoc() {
    return {
      prefs: this.prefs,
      tabs: this.tabList.map(t => ({ id: t.id, name: t.name, nodeId: t.nodeId, request: t.request, saved: t.saved })),
      activeTab: this.tabs?.active,
      activeEnv: this.activeEnvId,
      expanded: this.state.expanded || [],
      sidebarTab: this.state.sidebarTab,
    };
  }

  saveState() {
    return put('/doc/state', this.stateDoc()).catch(() => {});
  }

  /** On close: pending saves go out with keepalive so they survive the window. */
  flush() {
    const send = (name, doc) => fetch('/api/doc/' + name, { method: 'PUT', keepalive: true, headers: { 'X-Token': token, 'Content-Type': 'application/json' }, body: JSON.stringify(doc) }).catch(() => {});
    send('state', this.stateDoc());
  }

  async exit() {
    this.saveStateSoon.flush();
    await post('/exit').catch(() => {});
    window.close();
  }

  // ------------------------------------------------------------------ keyboard

  globalKey(e) {
    const ctrl = e.ctrlKey || e.metaKey;
    const k = e.key.toLowerCase();
    if (document.querySelector('.modal-bg')) return;
    if (ctrl && k === 'enter') { e.preventDefault(); this.activeView()?.send(); }
    else if (ctrl && k === 's') { e.preventDefault(); this.saveTab(this.activeTab(), { as: e.shiftKey }); }
    else if (ctrl && k === 't') { e.preventDefault(); this.newTab(); }
    else if (ctrl && k === 'w') { e.preventDefault(); this.closeTab(this.tabs.active); }
    else if (ctrl && k === 'l') { e.preventDefault(); this.activeView()?.focusUrl(); }
    else if (ctrl && k === 'o') { e.preventDefault(); this.importDialog(); }
    else if (ctrl && k === 'e') { e.preventDefault(); this.manageEnvironments(); }
    else if (ctrl && k === 'tab') {
      e.preventDefault();
      const ids = this.tabs.ids();
      const i = ids.indexOf(this.tabs.active);
      this.tabs.activate(ids[(i + (e.shiftKey ? -1 : 1) + ids.length) % ids.length]);
    } else if (k === 'f2') { e.preventDefault(); this.renameTab(this.activeTab()); }
    else if (k === 'escape' && this.activeView()?.running) { e.preventDefault(); this.activeView().cancel(); }
  }
}

new App().init();
