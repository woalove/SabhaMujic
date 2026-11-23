// @ts-check
// Library: the category tab row ("All" + each category with its coloured dot and track count), debounced search and a
// paged timeline of tracks with a pill per category. Tap a track for Play now / Play next / Add to queue.

import { byId, h } from '../dom.js';
import { debounce, formatTime } from '../format.js';
import { actionButton, categoryPills, categoryTone, dot, timelineRow } from '../ui.js';

/** @typedef {import('../context.js').AppContext} AppContext */
/** @typedef {import('../types.js').Track} Track */
/** @typedef {import('../types.js').TrackPage} TrackPage */

const PAGE_SIZE = 50;

/** @param {AppContext} ctx */
export function mountLibrary(ctx) {
  const { store, hub, run } = ctx;
  const search = byId('lib-search', HTMLInputElement);
  const tabs = byId('cat-tabs', HTMLElement);
  const list = byId('lib-list', HTMLOListElement);
  const count = byId('lib-count', HTMLElement);
  const more = byId('lib-more', HTMLButtonElement);

  /** @type {string | null} */
  let categoryId = null;
  /** @type {Track[]} */
  let items = [];
  let total = 0;
  /** Library size, learned from an unfiltered query (the "All" tab's count). @type {number | null} */
  let allCount = null;
  let generation = 0;
  let loaded = false;

  /** @param {boolean} append */
  async function load(append) {
    const mine = ++generation;
    const text = search.value.trim();
    const query = { categoryId, search: text || null, skip: append ? items.length : 0, take: PAGE_SIZE };
    /** @type {TrackPage | undefined} */
    const page = await run(() => hub.invoke('GetTracks', query));
    if (!page || mine !== generation) return; // a newer query superseded this one
    store.addTracks(page.items);
    items = append ? items.concat(page.items) : page.items;
    total = page.total;
    if (!categoryId && !text) allCount = page.total;
    loaded = true;
    renderTabs();
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

  function renderTabs() {
    /** @type {{ id: string | null, name: string, count: number | null }[]} */
    const all = [{ id: null, name: 'All', count: allCount }, ...store.categories.map((c) => ({ id: c.id, name: c.name, count: c.trackCount }))];
    tabs.replaceChildren(
      ...all.map((c) =>
        h(
          'button',
          {
            class: 'tab',
            attrs: { type: 'button', 'aria-pressed': String(c.id === categoryId), 'data-category': c.id ?? 'all', 'aria-label': c.name },
            on: {
              click: () => {
                if (categoryId === c.id) return;
                categoryId = c.id;
                renderTabs();
                void load(false);
              },
            },
          },
          c.id ? dot(categoryTone(store, c.id)) : null,
          h('span', { class: 'tab-label', text: c.name }),
          c.count === null ? null : h('b', { class: 'tab-value num', text: String(c.count) }),
        ),
      ),
    );
  }

  /** @param {Track} track */
  function row(track) {
    return timelineRow({
      label: track.title,
      title: track.title,
      top: track.artist ?? 'Unknown artist',
      pills: categoryPills(store, track.categoryIds),
      aside: formatTime(track.durationSeconds),
      current: track.id === store.state?.currentTrackId,
      attrs: { 'data-track': track.id },
      actions: () => [
        actionButton('Play now', () => void run(() => hub.invoke('PlayTrack', track.id), `Playing “${track.title}”`), { kind: 'primary', icon: 'play' }),
        actionButton('Play next', () => void run(async () => store.applyQueue(await hub.invoke('Enqueue', [track.id], true)), 'Will play next'),
          { icon: 'play-next' }),
        actionButton('Add to queue', () => void run(async () => store.applyQueue(await hub.invoke('Enqueue', [track.id], false)), 'Added to queue'),
          { icon: 'plus' }),
      ],
    });
  }

  function renderList() {
    list.replaceChildren(...items.map(row));
    const name = categoryId ? store.categories.find((c) => c.id === categoryId)?.name : null;
    count.textContent = total === 0 ? 'No tracks found.' : `${total} track${total === 1 ? '' : 's'}${name ? ` in ${name}` : ''}`;
    more.hidden = items.length >= total;
  }

  store.addEventListener('categories', () => {
    if (categoryId && !store.categories.some((c) => c.id === categoryId)) {
      categoryId = null;
      if (loaded) void load(false);
    }
    renderTabs();
    if (loaded) renderList(); // pill names/colours
  });
  // The playing track's row is highlighted.
  let lastCurrent = /** @type {string | null | undefined} */ (undefined);
  store.addEventListener('state', () => {
    const current = store.state?.currentTrackId;
    if (current !== lastCurrent && loaded) {
      lastCurrent = current;
      for (const li of list.children) {
        if (li instanceof HTMLElement) li.setAttribute('aria-current', String(li.dataset.track === current));
      }
    }
  });
  renderTabs();

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
