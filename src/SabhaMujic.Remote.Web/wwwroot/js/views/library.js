// @ts-check
// Library: category chips, debounced search, paged list; play now / play next / add to queue.

import { byId, h } from '../dom.js';
import { debounce, formatTime } from '../format.js';

/** @typedef {import('../context.js').AppContext} AppContext */
/** @typedef {import('../types.js').Track} Track */
/** @typedef {import('../types.js').TrackPage} TrackPage */

const PAGE_SIZE = 50;

/** @param {AppContext} ctx */
export function mountLibrary(ctx) {
  const { store, hub, run } = ctx;
  const search = byId('lib-search', HTMLInputElement);
  const chips = byId('lib-chips', HTMLElement);
  const list = byId('lib-list', HTMLUListElement);
  const count = byId('lib-count', HTMLElement);
  const more = byId('lib-more', HTMLButtonElement);

  /** @type {string | null} */
  let categoryId = null;
  /** @type {Track[]} */
  let items = [];
  let total = 0;
  let generation = 0;
  let loaded = false;

  /** @param {boolean} append */
  async function load(append) {
    const mine = ++generation;
    const query = { categoryId, search: search.value.trim() || null, skip: append ? items.length : 0, take: PAGE_SIZE };
    /** @type {TrackPage | undefined} */
    const page = await run(() => hub.invoke('GetTracks', query));
    if (!page || mine !== generation) return; // a newer query superseded this one
    store.addTracks(page.items);
    items = append ? items.concat(page.items) : page.items;
    total = page.total;
    loaded = true;
    renderList();
  }

  const searchSoon = debounce(() => void load(false), 300);
  search.addEventListener('input', searchSoon);
  search.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      search.blur();
      void load(false);
    }
  });
  more.addEventListener('click', () => void load(true));

  function renderChips() {
    /** @type {{ id: string | null, name: string }[]} */
    const all = [{ id: null, name: 'All' }, ...store.categories.map((c) => ({ id: c.id, name: c.name }))];
    chips.replaceChildren(
      ...all.map((c) =>
        h('button', {
          class: 'chip',
          text: c.name,
          attrs: { type: 'button', 'aria-pressed': String(c.id === categoryId) },
          on: {
            click: () => {
              categoryId = c.id;
              renderChips();
              void load(false);
            },
          },
        }),
      ),
    );
  }

  /** @param {Track} track */
  function row(track) {
    return h(
      'li',
      { attrs: { 'data-track': track.id } },
      h('div', { class: 'item-title', text: track.title }),
      h('div', { class: 'item-sub', text: [track.artist, formatTime(track.durationSeconds)].filter(Boolean).join(' · ') }),
      h(
        'div',
        { class: 'actions' },
        h('button', {
          class: 'btn primary',
          text: 'Play now',
          attrs: { type: 'button' },
          on: { click: () => void run(() => hub.invoke('PlayTrack', track.id), `Playing “${track.title}”`) },
        }),
        h('button', {
          class: 'btn',
          text: 'Play next',
          attrs: { type: 'button' },
          on: { click: () => void run(async () => store.applyQueue(await hub.invoke('Enqueue', [track.id], true)), 'Will play next') },
        }),
        h('button', {
          class: 'btn',
          text: 'Add to queue',
          attrs: { type: 'button' },
          on: { click: () => void run(async () => store.applyQueue(await hub.invoke('Enqueue', [track.id], false)), 'Added to queue') },
        }),
      ),
    );
  }

  function renderList() {
    list.replaceChildren(...items.map(row));
    count.textContent = total === 0 ? 'No tracks found.' : `${total} track${total === 1 ? '' : 's'}`;
    more.hidden = items.length >= total;
  }

  store.addEventListener('categories', () => {
    if (categoryId && !store.categories.some((c) => c.id === categoryId)) categoryId = null;
    renderChips();
  });
  renderChips();

  return {
    /** Called when the tab is shown. */
    show() {
      if (!loaded) void load(false);
    },
    /** Called when the library changed on the server. */
    refresh() {
      if (loaded) void load(false);
    },
  };
}
