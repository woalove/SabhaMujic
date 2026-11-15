// @ts-check
// Playlists: list; enqueue (append) or replace the queue (with confirmation).

import { byId, h } from '../dom.js';

/** @typedef {import('../context.js').AppContext} AppContext */
/** @typedef {import('../types.js').Playlist} Playlist */

/** @param {AppContext} ctx */
export function mountPlaylists(ctx) {
  const { store, hub, run, confirm } = ctx;
  const list = byId('pl-list', HTMLUListElement);
  const empty = byId('pl-empty', HTMLElement);
  let loaded = false;

  async function load() {
    /** @type {Playlist[] | undefined} */
    const playlists = await run(() => hub.invoke('GetPlaylists'));
    if (!playlists) return;
    loaded = true;
    empty.hidden = playlists.length > 0;
    list.replaceChildren(
      ...playlists.map((playlist) =>
        h(
          'li',
          { attrs: { 'data-playlist': playlist.id } },
          h('div', { class: 'item-title', text: playlist.name }),
          h('div', { class: 'item-sub', text: `${playlist.trackIds.length} track${playlist.trackIds.length === 1 ? '' : 's'}` }),
          h(
            'div',
            { class: 'actions' },
            h('button', {
              class: 'btn primary',
              text: 'Add to queue',
              attrs: { type: 'button', disabled: playlist.trackIds.length === 0 },
              on: {
                click: () =>
                  void run(async () => store.applyQueue(await hub.invoke('EnqueuePlaylist', playlist.id, false)), `Added “${playlist.name}”`),
              },
            }),
            h('button', {
              class: 'btn',
              text: 'Replace queue',
              attrs: { type: 'button', disabled: playlist.trackIds.length === 0 },
              on: {
                click: async () => {
                  if (await confirm(`Replace the whole queue with “${playlist.name}”?`, 'Replace')) {
                    await run(async () => store.applyQueue(await hub.invoke('EnqueuePlaylist', playlist.id, true)), 'Queue replaced');
                  }
                },
              },
            }),
          ),
        ),
      ),
    );
  }

  return {
    show() {
      if (!loaded) void load();
    },
    refresh() {
      if (loaded) void load();
    },
  };
}
