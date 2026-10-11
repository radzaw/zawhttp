// Left panel: the collections tree and the request history.
import { get, del } from '../api.js';
import { h, methodClass, methodShort, statusClass, fmtTime } from '../util.js';
import { icon } from '../icons.js';
import { contextMenu, confirmDlg } from '../dialogs.js';

export class Sidebar {
  constructor(app, host) {
    this.app = app;
    this.mode = app.state.sidebarTab === 'history' ? 'history' : 'collections';
    this.filter = '';
    this.history = [];
    this.expanded = new Set(app.state.expanded || []);

    this.tabBtns = {
      collections: h('button', { class: 'subtab', onclick: () => this.setMode('collections') }, 'Collections'),
      history: h('button', { class: 'subtab', onclick: () => this.setMode('history') }, 'History'),
    };
    this.filterIn = h('input', { class: 'inp side-filter', placeholder: 'Filter', spellcheck: false, type: 'search' });
    this.filterIn.addEventListener('input', () => { this.filter = this.filterIn.value.trim().toLowerCase(); this.render(); });
    this.actions = h('div', { class: 'side-actions' });
    this.list = h('div', { class: 'side-list', tabIndex: 0 });
    this.list.addEventListener('contextmenu', e => {
      if (e.target.closest('.tn')) return;
      e.preventDefault();
      if (this.mode === 'collections') contextMenu(e.clientX, e.clientY, [
        { label: 'New collection', icon: 'collection', onClick: () => this.app.newCollection() },
        { label: 'Import…', icon: 'import', onClick: () => this.app.importDialog() },
      ]);
    });
    host.append(h('div', { class: 'side-head' }, h('div', { class: 'subtabs' }, this.tabBtns.collections, this.tabBtns.history)),
      h('div', { class: 'side-tools' }, this.filterIn, this.actions), this.list);
    this.setMode(this.mode);
    this.refreshHistory();
  }

  setMode(mode) {
    this.mode = mode;
    this.app.state.sidebarTab = mode;
    this.app.saveStateSoon();
    for (const [k, b] of Object.entries(this.tabBtns)) b.classList.toggle('active', k === mode);
    this.actions.replaceChildren(...(mode === 'collections'
      ? [h('button', { class: 'tbtn', title: 'New collection', html: icon('plus'), onclick: () => this.app.newCollection() }),
        h('button', { class: 'tbtn', title: 'Import (Ctrl+O)', html: icon('import'), onclick: () => this.app.importDialog() })]
      : [h('button', { class: 'tbtn', title: 'Clear history', html: icon('trash'), onclick: () => this.clearHistory() })]));
    this.render();
  }

  render() {
    if (this.mode === 'collections') this.renderCollections();
    else this.renderHistory();
  }

  saveExpanded() {
    this.app.state.expanded = [...this.expanded];
    this.app.saveStateSoon();
  }

  // ------------------------------------------------------------------ collections

  matches(node) {
    if (!this.filter) return true;
    if (node.name.toLowerCase().includes(this.filter)) return true;
    if (node.type === 'request') return (node.request?.url || '').toLowerCase().includes(this.filter);
    return (node.items || []).some(n => this.matches(n));
  }

  renderCollections() {
    const cols = this.app.collections;
    if (!cols.length) {
      this.list.replaceChildren(h('div', { class: 'side-empty' },
        h('p', null, 'No collections yet.'),
        h('p', null, 'Collections keep your requests organised in folders.'),
        h('button', { class: 'btn', onclick: () => this.app.newCollection() }, 'New collection'), ' ',
        h('button', { class: 'btn', onclick: () => this.app.importDialog() }, 'Import…')));
      return;
    }
    const activeNode = this.app.activeTab()?.nodeId;
    const out = [];
    const walk = (node, depth) => {
      if (!this.matches(node)) return;
      out.push(this.nodeEl(node, depth, activeNode));
      if (node.type !== 'request' && (this.expanded.has(node.id) || this.filter)) for (const c of node.items || []) walk(c, depth + 1);
    };
    for (const c of cols) walk(c, 0);
    this.list.replaceChildren(...out);
  }

  nodeEl(node, depth, activeNode) {
    const isReq = node.type === 'request';
    const open = this.expanded.has(node.id) || !!this.filter;
    const row = h('div', { class: 'tn' + (node.id === activeNode ? ' sel' : ''), style: { paddingLeft: 4 + depth * 14 + 'px' }, draggable: true, title: isReq ? node.request?.url || '' : node.name },
      h('span', { class: 'tw' }, isReq ? '' : open ? '▾' : '▸'),
      isReq
        ? h('span', { class: 'mbadge ' + methodClass(node.request?.method) }, methodShort(node.request?.method))
        : h('span', { html: icon(node.type === 'collection' ? 'collection' : 'folder') }),
      h('span', { class: 'tl' }, node.name),
      !isReq ? h('span', { class: 'ts' }, String(countRequests(node))) : null);
    row.dataset.id = node.id;
    row.addEventListener('click', () => {
      if (isReq) this.app.openNode(node);
      else {
        if (this.expanded.has(node.id)) this.expanded.delete(node.id); else this.expanded.add(node.id);
        this.saveExpanded();
        this.render();
      }
    });
    row.addEventListener('dblclick', e => { if (!isReq) return; e.preventDefault(); this.app.renameNode(node); });
    row.addEventListener('contextmenu', e => {
      e.preventDefault();
      e.stopPropagation();
      contextMenu(e.clientX, e.clientY, this.menuFor(node));
    });

    // Drag & drop: onto a collection or folder moves into it, onto a request moves before it.
    row.addEventListener('dragstart', e => {
      e.dataTransfer.setData('text/zawhttp-node', node.id);
      e.dataTransfer.effectAllowed = 'move';
      this.dragId = node.id;
    });
    row.addEventListener('dragend', () => { this.dragId = null; this.clearDrop(); });
    row.addEventListener('dragover', e => {
      if (!this.dragId || this.dragId === node.id) return;
      const dragged = this.app.findNode(this.dragId)?.node;
      if (!dragged) return;
      const into = !isReq && dragged.type !== 'collection';
      if (dragged.type === 'collection' && node.type !== 'collection') return;
      if (dragged.type !== 'collection' && isDescendant(dragged, node.id)) return;
      e.preventDefault();
      this.clearDrop();
      row.classList.add(into ? 'drop-into' : 'drop-before');
    });
    row.addEventListener('dragleave', () => row.classList.remove('drop-into', 'drop-before'));
    row.addEventListener('drop', e => {
      e.preventDefault();
      const into = row.classList.contains('drop-into');
      this.clearDrop();
      if (this.dragId) this.app.moveNode(this.dragId, node.id, into ? 'into' : 'before');
    });
    return row;
  }

  clearDrop() {
    for (const el of this.list.querySelectorAll('.drop-into, .drop-before')) el.classList.remove('drop-into', 'drop-before');
  }

  menuFor(node) {
    const app = this.app;
    if (node.type === 'request') return [
      { label: 'Open', icon: 'request', onClick: () => app.openNode(node) },
      { label: 'Open in new tab', icon: 'newtab', onClick: () => app.openNode(node, { newTab: true }) },
      '-',
      { label: 'Rename…', icon: 'edit', shortcut: 'F2', onClick: () => app.renameNode(node) },
      { label: 'Duplicate', icon: 'duplicate', onClick: () => app.duplicateNode(node) },
      { label: 'Copy as cURL', icon: 'code', onClick: () => app.copyCurl(node.request) },
      '-',
      { label: 'Delete', icon: 'trash', onClick: () => app.deleteNode(node) },
    ];
    return [
      { label: 'New request', icon: 'plus', onClick: () => app.newRequestIn(node) },
      { label: 'New folder', icon: 'newfolder', onClick: () => app.newFolderIn(node) },
      '-',
      { label: 'Rename…', icon: 'edit', onClick: () => app.renameNode(node) },
      { label: 'Duplicate', icon: 'duplicate', onClick: () => app.duplicateNode(node) },
      node.type === 'collection' ? { label: 'Export…', icon: 'export', onClick: () => app.exportCollection(node) } : null,
      '-',
      { label: 'Expand all', onClick: () => { walkAll(node, n => n.type !== 'request' && this.expanded.add(n.id)); this.saveExpanded(); this.render(); } },
      { label: 'Collapse all', onClick: () => { walkAll(node, n => this.expanded.delete(n.id)); this.saveExpanded(); this.render(); } },
      '-',
      { label: 'Delete', icon: 'trash', onClick: () => app.deleteNode(node) },
    ];
  }

  reveal(nodeId) {
    const path = this.app.findNode(nodeId)?.path || [];
    let changed = false;
    for (const n of path) if (!this.expanded.has(n.id)) { this.expanded.add(n.id); changed = true; }
    if (changed) this.saveExpanded();
    this.render();
    this.list.querySelector(`.tn[data-id="${nodeId}"]`)?.scrollIntoView({ block: 'nearest' });
  }

  // ------------------------------------------------------------------ history

  async refreshHistory() {
    try {
      this.history = await get('/history');
    } catch {
      this.history = [];
    }
    if (this.mode === 'history') this.renderHistory();
  }

  renderHistory() {
    const items = this.history.filter(e => !this.filter || e.url.toLowerCase().includes(this.filter) || (e.name || '').toLowerCase().includes(this.filter));
    if (!items.length) {
      this.list.replaceChildren(h('div', { class: 'side-empty' }, this.history.length ? 'Nothing matches the filter.' : 'Requests you send appear here.'));
      return;
    }
    const out = [];
    let lastDay = null;
    for (const e of items) {
      const day = dayLabel(e.time);
      if (day !== lastDay) { out.push(h('div', { class: 'hist-day' }, day)); lastDay = day; }
      const row = h('div', { class: 'tn hist', title: (e.error ? e.error + '\n' : '') + e.url },
        h('span', { class: 'mbadge ' + methodClass(e.method) }, methodShort(e.method)),
        h('span', { class: 'tl' }, e.name ? h('span', null, e.name, h('span', { class: 'muted' }, ' · ' + e.url)) : e.url),
        h('span', { class: 'ts' }, e.status ? h('span', { class: 'status-text ' + statusClass(e.status) }, e.status) : h('span', { class: 'status-text s-err' }, 'ERR'), ' ', fmtTime(e.time)));
      row.addEventListener('click', () => this.app.openHistory(e));
      row.addEventListener('contextmenu', ev => {
        ev.preventDefault();
        contextMenu(ev.clientX, ev.clientY, [
          { label: 'Open in new tab', icon: 'newtab', onClick: () => this.app.openHistory(e) },
          { label: 'Save to collection…', icon: 'save', onClick: () => this.app.saveHistoryEntry(e) },
          { label: 'Copy as cURL', icon: 'code', onClick: () => this.app.copyCurl(e.request) },
          { label: 'Copy URL', icon: 'copy', onClick: () => navigator.clipboard.writeText(e.url) },
          '-',
          { label: 'Delete', icon: 'trash', onClick: async () => { await del('/history/' + e.id); this.refreshHistory(); } },
        ]);
      });
      out.push(row);
    }
    this.list.replaceChildren(...out);
  }

  async clearHistory() {
    if (!this.history.length) return;
    if (!(await confirmDlg(`Delete all ${this.history.length} history entries?`, { title: 'Clear history', ok: 'Delete', danger: true }))) return;
    await del('/history');
    this.refreshHistory();
  }
}

export function countRequests(node) {
  return (node.items || []).reduce((n, c) => n + (c.type === 'request' ? 1 : countRequests(c)), 0);
}

function walkAll(node, fn) {
  fn(node);
  for (const c of node.items || []) walkAll(c, fn);
}

function isDescendant(node, id) {
  return (node.items || []).some(c => c.id === id || isDescendant(c, id));
}

function dayLabel(ms) {
  const d = new Date(ms);
  const today = new Date();
  const yesterday = new Date(Date.now() - 86400000);
  if (d.toDateString() === today.toDateString()) return 'Today';
  if (d.toDateString() === yesterday.toDateString()) return 'Yesterday';
  return d.toLocaleDateString(undefined, { weekday: 'short', year: 'numeric', month: 'short', day: 'numeric' });
}
