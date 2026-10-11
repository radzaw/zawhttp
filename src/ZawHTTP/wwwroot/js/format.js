// Pretty-printing of response and body text: JSON (with highlighting) and XML.
import { esc } from './util.js';

const WS = /\s/;

/**
 * Re-indents JSON text without parsing it into JavaScript values, so big integers and number formats
 * stay exactly as the server sent them. Returns null when the text isn't valid JSON.
 * With html: true the result is escaped HTML with highlighting spans.
 */
export function formatJson(text, { html = false, indent = 2 } = {}) {
  try { JSON.parse(text); } catch { return null; }
  const span = html ? (cls, s) => `<span class="${cls}">${esc(s)}</span>` : (_, s) => s;
  const pad = d => '\n' + ' '.repeat(d * indent);
  const n = text.length;
  let out = '', depth = 0, i = 0;
  while (i < n) {
    const c = text[i];
    if (c === '"') {
      let j = i + 1;
      while (j < n && text[j] !== '"') j += text[j] === '\\' ? 2 : 1;
      const s = text.slice(i, j + 1);
      let k = j + 1;
      while (k < n && WS.test(text[k])) k++;
      out += span(text[k] === ':' ? 'j-key' : 'j-str', s);
      i = j + 1;
    } else if (c === '{' || c === '[') {
      let k = i + 1;
      while (k < n && WS.test(text[k])) k++;
      if (text[k] === (c === '{' ? '}' : ']')) { out += c + text[k]; i = k + 1; continue; }
      depth++;
      out += c + pad(depth);
      i++;
    } else if (c === '}' || c === ']') {
      depth--;
      out += pad(depth) + c;
      i++;
    } else if (c === ',') {
      out += ',' + pad(depth);
      i++;
    } else if (c === ':') {
      out += ': ';
      i++;
    } else if (WS.test(c)) {
      i++;
    } else {
      let j = i;
      while (j < n && !/[\s,:\]}]/.test(text[j])) j++;
      const w = text.slice(i, j);
      out += span(w === 'true' || w === 'false' ? 'j-bool' : w === 'null' ? 'j-null' : 'j-num', w);
      i = j;
    }
  }
  return out;
}

/** Indents XML/HTML-like markup one element per line. Returns the text unchanged when it doesn't look like markup. */
export function formatXml(text, indent = 2) {
  const src = text.trim();
  if (!src.startsWith('<')) return text;
  const tokens = src.replace(/>\s+</g, '><').split(/(?=<)|(?<=>)/);
  let depth = 0, out = '';
  for (const t of tokens) {
    if (!t.trim()) continue;
    const closing = /^<\//.test(t);
    const selfClosing = /\/>$/.test(t) || /^<[!?]/.test(t);
    const opening = /^<[^/!?]/.test(t) && !selfClosing;
    if (closing) depth = Math.max(0, depth - 1);
    // Text and the closing tag after it stay on the opening tag's line: "<a>text</a>".
    if (!t.startsWith('<') || (closing && !out.endsWith('>'))) out += t.trim();
    else out += (out ? '\n' : '') + ' '.repeat(depth * indent) + t;
    if (opening) depth++;
  }
  return out;
}

/** Response kind for choosing a viewer. */
export function bodyKind(contentType, text) {
  const ct = (contentType || '').toLowerCase();
  if (ct.includes('json')) return 'json';
  if (ct.includes('html')) return 'html';
  if (ct.includes('xml')) return 'xml';
  if (ct.startsWith('image/')) return 'image';
  if (ct.includes('pdf')) return 'pdf';
  const t = (text || '').trimStart();
  if (t.startsWith('{') || t.startsWith('[')) return 'json';
  if (t.startsWith('<')) return /^<(!doctype html|html)/i.test(t) ? 'html' : 'xml';
  return 'text';
}
