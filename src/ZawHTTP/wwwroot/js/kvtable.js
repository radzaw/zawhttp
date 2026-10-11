// Editable key/value table (query parameters, headers, form fields, variables) with a bulk-edit mode.
import { h } from './util.js';
import { icon } from './icons.js';

export const COMMON_HEADERS = [
  'Accept', 'Accept-Charset', 'Accept-Encoding', 'Accept-Language', 'Authorization', 'Cache-Control', 'Connection', 'Content-Disposition',
  'Content-Encoding', 'Content-Language', 'Content-Length', 'Content-Type', 'Cookie', 'DNT', 'Forwarded', 'Host', 'If-Match',
  'If-Modified-Since', 'If-None-Match', 'If-Unmodified-Since', 'Origin', 'Pragma', 'Range', 'Referer', 'User-Agent',
  'X-API-Key', 'X-Correlation-ID', 'X-Forwarded-For', 'X-Forwarded-Host', 'X-Forwarded-Proto', 'X-Request-ID', 'X-Requested-With',
];

let datalistMade = false;
function ensureHeaderList() {
  if (datalistMade) return;
  datalistMade = true;
  document.body.append(h('datalist', { id: 'dl-headers' }, COMMON_HEADERS.map(n => h('option', { value: n }))));
}

/** Rows in bulk-edit form: "key: value" per line, disabled rows commented out with "//". */
export function rowsToText(rows) {
  return rows.filter(r => r.key || r.value).map(r => `${r.enabled ? '' : '//'}${r.key}: ${r.value}`).join('\n');
}

export function textToRows(text) {
  return text.split(/\r?\n/).filter(l => l.trim()).map(l => {
    let enabled = true;
    let line = l.trim();
    if (line.startsWith('//')) { enabled = false; line = line.slice(2).trim(); }
    const i = line.indexOf(':');
    return i < 0 ? { key: line, value: '', enabled } : { key: line.slice(0, i).trim(), value: line.slice(i + 1).trim(), enabled };
  });
}

export class KvTable {
  /**
   * rows: the array to edit (mutated in place). onChange(rows) after every edit.
   * files: form-data mode with a Text/File type per row. headers: suggest header names.
   */
  constructor({ rows = [], onChange, keyPlaceholder = 'Key', valuePlaceholder = 'Value', files = false, headers = false, bulk = true, secretValues = false }) {
    this.rows = rows;
    this.onChange = onChange || (() => {});
    this.opts = { keyPlaceholder, valuePlaceholder, files, headers, bulk, secretValues };
    this.bulkMode = false;
    if (headers) ensureHeaderList();
    this.tbody = h('tbody');
    this.table = h('table', { class: 'edit-table kv-table' },
      h('thead', null, h('tr', null,
        h('th', { class: 'kv-chk' }), h('th', null, 'Key'), files ? h('th', { class: 'kv-type' }, 'Type') : null, h('th', null, 'Value'), h('th', { class: 'kv-del' }))),
      this.tbody);
    this.bulkArea = h('textarea', { class: 'inp kv-bulk mono', spellcheck: false, placeholder: 'key: value\n// disabled: value' });
    this.bulkArea.addEventListener('input', () => {
      this.rows.splice(0, this.rows.length, ...textToRows(this.bulkArea.value));
      this.onChange(this.rows);
    });
    this.bulkBtn = bulk && !files ? h('button', { class: 'linkbtn', onclick: () => this.toggleBulk() }, 'Bulk edit') : null;
    this.el = h('div', { class: 'kv' }, this.bulkBtn ? h('div', { class: 'kv-bar' }, this.bulkBtn) : null, this.table, this.bulkArea);
    this.bulkArea.style.display = 'none';
    this.render();
  }

  setRows(rows) {
    this.rows = rows;
    if (this.bulkMode) this.bulkArea.value = rowsToText(rows);
    else this.render();
  }

  toggleBulk() {
    this.bulkMode = !this.bulkMode;
    this.bulkBtn.textContent = this.bulkMode ? 'Key-value edit' : 'Bulk edit';
    this.table.style.display = this.bulkMode ? 'none' : '';
    this.bulkArea.style.display = this.bulkMode ? '' : 'none';
    if (this.bulkMode) {
      this.bulkArea.value = rowsToText(this.rows);
      this.bulkArea.focus();
    } else {
      this.render();
    }
  }

  render() {
    this.tbody.replaceChildren(...this.rows.map(r => this.rowEl(r)), this.rowEl(null));
  }

  rowEl(row) {
    const ghost = !row;
    const r = row || { key: '', value: '', enabled: true, ...(this.opts.files ? { type: 'text' } : {}) };
    const tr = h('tr', { class: ghost ? 'ghost' : r.enabled ? '' : 'off' });
    const chk = h('input', { type: 'checkbox', checked: r.enabled, disabled: ghost, title: 'Include this row' });
    const keyIn = h('input', { class: 'cell-inp mono', value: r.key, placeholder: this.opts.keyPlaceholder, spellcheck: false, list: this.opts.headers ? 'dl-headers' : null });
    const valIn = h('input', { class: 'cell-inp mono', value: r.value, spellcheck: false, type: this.opts.secretValues && r.secret ? 'password' : 'text',
      placeholder: r.type === 'file' ? 'Path of a local file, e.g. C:\\data\\photo.jpg' : this.opts.valuePlaceholder });
    const typeSel = this.opts.files
      ? h('select', { class: 'cell-inp' }, h('option', { value: 'text', selected: r.type !== 'file' }, 'Text'), h('option', { value: 'file', selected: r.type === 'file' }, 'File'))
      : null;
    const delBtn = h('button', { class: 'kv-x', title: 'Delete row', html: icon('close'), tabIndex: -1 });
    if (ghost) delBtn.style.visibility = 'hidden';

    // Typing into the empty last row turns it into a real row and adds a new empty one below.
    const promote = () => {
      if (!tr.classList.contains('ghost')) return;
      this.rows.push(r);
      tr.classList.remove('ghost');
      chk.disabled = false;
      delBtn.style.visibility = '';
      this.tbody.append(this.rowEl(null));
    };
    keyIn.addEventListener('input', () => { promote(); r.key = keyIn.value; this.onChange(this.rows); });
    valIn.addEventListener('input', () => { promote(); r.value = valIn.value; this.onChange(this.rows); });
    chk.addEventListener('change', () => { r.enabled = chk.checked; tr.classList.toggle('off', !r.enabled); this.onChange(this.rows); });
    typeSel?.addEventListener('change', () => {
      promote();
      r.type = typeSel.value;
      valIn.placeholder = r.type === 'file' ? 'Path of a local file, e.g. C:\\data\\photo.jpg' : this.opts.valuePlaceholder;
      this.onChange(this.rows);
    });
    delBtn.addEventListener('click', () => {
      const i = this.rows.indexOf(r);
      if (i >= 0) this.rows.splice(i, 1);
      tr.remove();
      this.onChange(this.rows);
    });
    tr.append(
      h('td', { class: 'kv-chk' }, chk),
      h('td', null, keyIn),
      ...(typeSel ? [h('td', { class: 'kv-type' }, typeSel)] : []),
      h('td', null, valIn),
      h('td', { class: 'kv-del' }, delBtn));
    return tr;
  }
}
