// @ts-check
// Playlists as a timeline: tap one to add it to the queue or replace the queue with it (with confirmation).

import { byId } from '../dom.js';
import { actionButton, timelineRow } from '../ui.js';

/** @typedef {import('../context.js').AppContext} AppContext */
/** @typedef {import('../types.js').Playlist} Playlist */

/** @param {AppContext} ctx */
export function mountPlaylists(ctx) {
  const { store, hub, run, confirm } = ctx;
  const list = byId('pl-list', HTMLOListElement);
  const empty = byId('pl-empty', HTMLElement);
  let loaded = false;

  async function load() {
    /** @type {Playlist[] | undefined} */
    const playlists = await run(() => hub.invoke('GetPlaylists'));
    if (!playlists) return;
    loaded = true;
    empty.hidden = playlists.length > 0;
    ctx.setTabValue('playlists', String(playlists.length));
    list.replaceChildren(
      ...playlists.map((playlist) => {
        const n = playlist.trackIds.length;
        return timelineRow({
          label: playlist.name,
          title: playlist.name,
          top: 'Playlist',
          meta: { icon: 'clock', text: `Updated ${new Date(playlist.updatedAt).toLocaleDateString()}` },
          aside: `${n} track${n === 1 ? '' : 's'}`,
          attrs: { 'data-playlist': playlist.id },
          actions: () => [
            actionButton('Add to queue', () =>
              void run(async () => store.applyQueue(await hub.invoke('EnqueuePlaylist', playlist.id, false)), `Added “${playlist.name}”`),
            { kind: 'primary', icon: 'plus', disabled: n === 0 }),
            actionButton('Replace queue', async () => {
              if (await confirm(`Replace the whole queue with “${playlist.name}”?`, 'Replace')) {
                await run(async () => store.applyQueue(await hub.invoke('EnqueuePlaylist', playlist.id, true)), 'Queue replaced');
              }
            }, { icon: 'queue', disabled: n === 0 }),
          ],
        });
      }),
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
