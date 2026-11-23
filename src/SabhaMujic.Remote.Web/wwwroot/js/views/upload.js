// @ts-check
// Upload: add audio files (chunked over HTTPS, progress + cancel; duplicates reported) with any number of categories,
// chosen as pills you tap on and off, and create categories. The remote is admin-only; the server enforces it.

import { api, ApiError } from '../api.js';
import { byId, h } from '../dom.js';
import { formatBytes } from '../format.js';
import { icon } from '../icons.js';
import { categoryTone, dot } from '../ui.js';

/** @typedef {import('../context.js').AppContext} AppContext */
/** @typedef {import('../types.js').ImportProgress} ImportProgress */

/** Chunk size (the server accepts at most 1 MiB per chunk). */
export const CHUNK_BYTES = 512 * 1024;

const STAGES = /** @type {Record<string, string>} */ ({
  validating: 'Checking the audio…',
  storing: 'Saving…',
  analyzing: 'Analysing…',
});

/**
 * @typedef {object} UploadEntry
 * @property {File} file
 * @property {HTMLLIElement} element
 * @property {HTMLInputElement} title
 * @property {HTMLInputElement} artist
 * @property {HTMLElement} categories group of toggle pills (aria-pressed), one per category
 * @property {Set<string>} selected chosen category ids
 * @property {HTMLProgressElement} progress
 * @property {HTMLElement} status
 * @property {HTMLButtonElement} cancel
 * @property {'ready' | 'uploading' | 'done' | 'failed' | 'cancelled'} state
 * @property {AbortController | null} controller
 * @property {string | null} uploadId
 */

/** @param {AppContext} ctx */
export function mountUpload(ctx) {
  const { store, hub, run, toast } = ctx;
  const input = byId('up-files', HTMLInputElement);
  const list = byId('up-list', HTMLUListElement);
  const start = byId('up-start', HTMLButtonElement);
  const categoryForm = byId('cat-form', HTMLFormElement);
  const categoryName = byId('cat-name', HTMLInputElement);

  /** @type {UploadEntry[]} */
  let entries = [];
  let busy = false;

  input.addEventListener('change', () => {
    const files = [...(input.files ?? [])];
    input.value = '';
    for (const file of files) entries.push(createEntry(file));
    renderStart();
  });

  start.addEventListener('click', async () => {
    if (busy) return;
    busy = true;
    renderStart();
    try {
      for (const entry of entries) {
        if (entry.state === 'ready') await upload(entry);
      }
    } finally {
      busy = false;
      renderStart();
    }
  });

  categoryForm.addEventListener('submit', async (event) => {
    event.preventDefault();
    const name = categoryName.value.trim();
    if (!name) return;
    const created = await run(() => hub.invoke('CreateCategory', name), `Category “${name}” added`);
    if (created) categoryName.value = '';
  });

  store.addEventListener('categories', () => {
    for (const entry of entries) fillCategories(entry);
  });

  function renderStart() {
    const ready = entries.filter((e) => e.state === 'ready').length;
    start.hidden = ready === 0 && !busy;
    start.disabled = busy || ready === 0;
    start.textContent = busy ? 'Uploading…' : `Upload ${ready} file${ready === 1 ? '' : 's'}`;
    ctx.setTabValue('upload', ready > 0 ? String(ready) : '');
  }

  /**
   * Category pills (multi-select: tap to toggle). Keeps the selection across category refreshes and drops ids of
   * categories that no longer exist.
   * @param {UploadEntry} entry
   */
  function fillCategories(entry) {
    for (const id of [...entry.selected]) if (!store.categories.some((c) => c.id === id)) entry.selected.delete(id);
    const editable = entry.state === 'ready';
    entry.categories.replaceChildren(
      ...store.categories.map((c) => {
        const pill = h(
          'button',
          { class: 'pill-btn', attrs: { type: 'button', 'aria-pressed': String(entry.selected.has(c.id)), 'data-category': c.id, disabled: !editable } },
          dot(categoryTone(store, c.id)),
          c.name,
        );
        pill.addEventListener('click', () => {
          if (entry.selected.has(c.id)) entry.selected.delete(c.id);
          else entry.selected.add(c.id);
          pill.setAttribute('aria-pressed', String(entry.selected.has(c.id)));
        });
        return pill;
      }),
    );
    if (store.categories.length === 0) entry.categories.append(h('span', { class: 'muted', text: 'No categories yet — add one below.' }));
  }

  /** @param {File} file @returns {UploadEntry} */
  function createEntry(file) {
    const title = h('input', { attrs: { value: file.name.replace(/\.[^.]+$/, ''), maxlength: 200, 'aria-label': 'Title' } });
    const artist = h('input', { attrs: { placeholder: 'Artist (optional)', maxlength: 200, 'aria-label': 'Artist' } });
    const categories = h('div', { class: 'pill-choice', attrs: { role: 'group', 'aria-label': 'Categories' } });
    const progress = h('progress', { attrs: { max: file.size || 1, value: 0 } });
    const status = h('div', { class: 'up-status', text: `Ready · ${formatBytes(file.size)}` });
    const cancel = h('button', { class: 'btn secondary danger-text', text: 'Remove', attrs: { type: 'button' } });
    const element = h(
      'li',
      { attrs: { 'data-upload': file.name } },
      h('div', { class: 'row up-name' }, icon('library', 18), h('span', { class: 'grow', text: file.name })),
      title,
      artist,
      h('div', { class: 'up-cats-label', text: 'Categories (tap to choose any number)' }),
      categories,
      progress,
      status,
      h('div', { class: 'row end' }, cancel),
    );
    /** @type {UploadEntry} */
    const entry = {
      file, element, title, artist, categories, selected: new Set(), progress, status, cancel, state: 'ready', controller: null, uploadId: null,
    };
    fillCategories(entry);
    cancel.addEventListener('click', () => void cancelEntry(entry));
    list.append(element);
    return entry;
  }

  /**
   * @param {UploadEntry} entry
   * @param {string} text
   * @param {'' | 'ok' | 'info' | 'bad'} [kind]
   */
  function setStatus(entry, text, kind = '') {
    entry.status.textContent = text;
    entry.status.className = `up-status ${kind}`.trim();
  }

  /** @param {UploadEntry} entry @param {boolean} editable */
  function setEditable(entry, editable) {
    entry.title.disabled = !editable;
    entry.artist.disabled = !editable;
    for (const pill of entry.categories.querySelectorAll('button')) pill.disabled = !editable;
  }

  /** @param {UploadEntry} entry */
  async function upload(entry) {
    const { file } = entry;
    entry.state = 'uploading';
    entry.controller = new AbortController();
    const signal = entry.controller.signal;
    entry.cancel.textContent = 'Cancel';
    setEditable(entry, false);
    renderStart();
    try {
      entry.uploadId = await api.beginUpload(file.name, file.size);
      for (let offset = 0; offset < file.size; offset += CHUNK_BYTES) {
        if (signal.aborted) return;
        const end = Math.min(offset + CHUNK_BYTES, file.size);
        await api.uploadChunk(entry.uploadId, offset, file.slice(offset, end), signal);
        entry.progress.value = end;
        setStatus(entry, `Uploading ${Math.floor((end / file.size) * 100)}% · ${formatBytes(end)} of ${formatBytes(file.size)}`);
      }
      if (signal.aborted) return;
      setStatus(entry, STAGES.validating);
      entry.cancel.disabled = true;
      const result = await api.completeUpload(entry.uploadId, {
        title: entry.title.value.trim(),
        artist: entry.artist.value.trim() || null,
        categoryIds: store.categories.filter((c) => entry.selected.has(c.id)).map((c) => c.id),
      });
      entry.state = 'done';
      entry.cancel.hidden = true;
      if (result.wasDuplicate) {
        setStatus(entry, 'Already in the library — not added again.', 'info');
      } else {
        setStatus(entry, 'Added to the library.', 'ok');
        toast(`Added “${entry.title.value.trim() || file.name}”`);
      }
    } catch (error) {
      if (signal.aborted || (error instanceof DOMException && error.name === 'AbortError')) return;
      entry.state = 'failed';
      entry.cancel.disabled = false;
      entry.cancel.textContent = 'Remove';
      setStatus(entry, error instanceof ApiError ? error.message : 'The upload failed.', 'bad');
    } finally {
      entry.controller = null;
      renderStart();
    }
  }

  /** @param {UploadEntry} entry */
  async function cancelEntry(entry) {
    if (entry.state === 'uploading') {
      entry.state = 'cancelled';
      entry.controller?.abort();
      setStatus(entry, 'Cancelled.', 'info');
      entry.cancel.textContent = 'Remove';
      if (entry.uploadId) {
        try {
          await api.cancelUpload(entry.uploadId);
        } catch {
          // already gone (completed, expired or never started)
        }
      }
      renderStart();
      return;
    }
    entries = entries.filter((e) => e !== entry);
    entry.element.remove();
    renderStart();
  }

  return {
    /** @param {ImportProgress} progress */
    onProgress(progress) {
      const entry = entries.find((e) => e.uploadId === progress.uploadId);
      if (entry && entry.state === 'uploading' && STAGES[progress.stage]) setStatus(entry, STAGES[progress.stage]);
    },
  };
}
