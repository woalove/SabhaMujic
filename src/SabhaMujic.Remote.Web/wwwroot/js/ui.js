// @ts-check
// Shared v2 components (docs/DESIGN.md): category colours and pills, timeline rows with an action tray, range fills.

import { h } from './dom.js';
import { icon } from './icons.js';

/** @typedef {import('./store.js').Store} Store */

/** Tag colours cycle accent → violet → amber → blue. */
const TONES = /** @type {const} */ (['accent', 'violet', 'amber', 'blue']);

/**
 * The colour of a category (by its position in the category order).
 * @param {Store} store
 * @param {string} categoryId
 */
export function categoryTone(store, categoryId) {
  const index = store.categories.findIndex((c) => c.id === categoryId);
  return TONES[(index < 0 ? 0 : index) % TONES.length];
}

/** @param {string} tone */
export function dot(tone) {
  return h('i', { class: `dot dot-${tone}` });
}

/**
 * Pills for a track's categories (unknown ids are skipped).
 * @param {Store} store
 * @param {string[] | undefined} categoryIds
 * @returns {HTMLElement[]}
 */
export function categoryPills(store, categoryIds) {
  /** @type {HTMLElement[]} */
  const pills = [];
  for (const id of categoryIds ?? []) {
    const category = store.categories.find((c) => c.id === id);
    if (category) pills.push(h('span', { class: `pill tag-${categoryTone(store, id)}`, attrs: { 'data-category': id } }, category.name));
  }
  return pills;
}

/**
 * @typedef {object} RowOptions
 * @property {string} title
 * @property {string} [top] muted line above the title
 * @property {{ icon: string, text: string }} [meta] line under the title with a 16 px icon
 * @property {HTMLElement[]} [pills]
 * @property {string} [aside] muted value on the right (count, duration)
 * @property {boolean} [current] filled accent node + accent title
 * @property {Record<string, string>} [attrs] extra attributes on the <li>
 * @property {string} label accessible name of the row's toggle (e.g. the track title)
 * @property {() => HTMLElement[]} actions buttons for the action tray (built when first opened)
 */

/**
 * A timeline row: gutter node on the shared vertical rule, a tappable body that opens an action tray, and a chevron.
 * Only one row per list is open at a time.
 * @param {RowOptions} options
 * @returns {HTMLLIElement}
 */
export function timelineRow(options) {
  const tray = h('div', { class: 'row-actions', attrs: { role: 'group', 'aria-label': `Actions for ${options.label}` } });
  tray.hidden = true;
  const toggle = h(
    'button',
    { class: 'row-main', attrs: { type: 'button', 'aria-expanded': 'false', 'aria-label': options.label } },
    h(
      'span',
      { class: 'row-body' },
      options.top ? h('span', { class: 'row-top', text: options.top }) : null,
      h('span', { class: 'row-title', text: options.title }),
      options.meta ? h('span', { class: 'row-meta' }, icon(options.meta.icon, 16), options.meta.text) : null,
      options.pills && options.pills.length > 0 ? h('span', { class: 'pills' }, ...options.pills) : null,
    ),
    h('span', { class: 'row-aside num' }, options.aside ?? '', icon('chevron-right', 18)),
  );
  const row = h('li', { class: 'trow', attrs: { ...(options.attrs ?? {}), 'aria-current': options.current ? 'true' : null } },
    h('span', { class: 'node', attrs: { 'aria-hidden': 'true' } }),
    toggle,
    tray,
  );

  toggle.addEventListener('click', () => {
    const open = toggle.getAttribute('aria-expanded') !== 'true';
    const list = row.parentElement;
    if (open && list) {
      for (const other of list.querySelectorAll('.trow.open')) if (other !== row) closeRow(/** @type {HTMLElement} */ (other));
    }
    if (open) {
      if (tray.childElementCount === 0) tray.append(...options.actions());
      tray.hidden = false;
      row.classList.add('open');
      toggle.setAttribute('aria-expanded', 'true');
    } else {
      closeRow(row);
    }
  });
  return row;
}

/** @param {HTMLElement} row */
function closeRow(row) {
  row.classList.remove('open');
  row.querySelector('.row-main')?.setAttribute('aria-expanded', 'false');
  const tray = row.querySelector('.row-actions');
  if (tray instanceof HTMLElement) tray.hidden = true;
}

/**
 * A labelled action button for a row tray.
 * @param {string} text
 * @param {() => void} onClick
 * @param {{ kind?: 'primary' | 'secondary' | 'danger-text', icon?: string, disabled?: boolean }} [options]
 */
export function actionButton(text, onClick, options = {}) {
  const kind = options.kind ?? 'secondary';
  const button = h(
    'button',
    { class: `btn ${kind === 'danger-text' ? 'secondary danger-text' : kind}`, attrs: { type: 'button', disabled: options.disabled ?? false } },
    options.icon ? icon(options.icon, 18) : null,
    text,
  );
  button.addEventListener('click', onClick);
  return button;
}

/**
 * Updates the accent fill of a range input (flat two-colour track; CSSOM, allowed by the CSP).
 * @param {HTMLInputElement} range
 */
export function updateRangeFill(range) {
  const min = Number(range.min) || 0;
  const max = Number(range.max) || 0;
  const value = Number(range.value) || 0;
  const percent = max > min ? ((value - min) / (max - min)) * 100 : 0;
  range.style.setProperty('--fill', `${Math.max(0, Math.min(100, percent)).toFixed(2)}%`);
}
