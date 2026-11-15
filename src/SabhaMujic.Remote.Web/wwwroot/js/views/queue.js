// @ts-check
// Queue: current item highlighted; play, move up/down, remove; clear with confirmation.

import { byId, h } from '../dom.js';
import { formatTime } from '../format.js';

/** @typedef {import('../context.js').AppContext} AppContext */

/** @param {AppContext} ctx */
export function mountQueue(ctx) {
  const { store, hub, run, confirm } = ctx;
  const list = byId('queue-list', HTMLOListElement);
  const empty = byId('queue-empty', HTMLElement);
  const clearButton = byId('queue-clear', HTMLButtonElement);

  clearButton.addEventListener('click', async () => {
    if (await confirm('Remove every item from the queue except the one playing?', 'Clear')) {
      await run(async () => store.applyQueue(await hub.invoke('ClearQueue')), 'Queue cleared');
    }
  });

  function render() {
    const queue = store.queue;
    const items = queue?.items ?? [];
    empty.hidden = items.length > 0;
    clearButton.disabled = items.length === 0;
    list.replaceChildren(
      ...items.map((item, index) => {
        const track = store.track(item.trackId);
        const current = index === queue?.currentIndex;
        return h(
          'li',
          { attrs: { 'aria-current': current ? 'true' : null, 'data-queue-item': item.queueItemId } },
          h('div', { class: 'item-title', text: `${current ? '▶ ' : ''}${track?.title ?? '…'}` }),
          h('div', { class: 'item-sub', text: [track?.artist, track ? formatTime(track.durationSeconds) : null].filter(Boolean).join(' · ') }),
          h(
            'div',
            { class: 'actions' },
            h('button', {
              class: 'btn',
              text: 'Play',
              attrs: { type: 'button' },
              on: { click: () => void run(() => hub.invoke('PlayQueueItem', item.queueItemId)) },
            }),
            h('button', {
              class: 'btn icon-btn',
              text: '↑',
              attrs: { type: 'button', 'aria-label': 'Move up', disabled: index === 0 },
              on: { click: () => void run(async () => store.applyQueue(await hub.invoke('MoveQueueItem', item.queueItemId, index - 1))) },
            }),
            h('button', {
              class: 'btn icon-btn',
              text: '↓',
              attrs: { type: 'button', 'aria-label': 'Move down', disabled: index === items.length - 1 },
              on: { click: () => void run(async () => store.applyQueue(await hub.invoke('MoveQueueItem', item.queueItemId, index + 1))) },
            }),
            h('button', {
              class: 'btn danger',
              text: 'Remove',
              attrs: { type: 'button' },
              on: { click: () => void run(async () => store.applyQueue(await hub.invoke('RemoveFromQueue', item.queueItemId))) },
            }),
          ),
        );
      }),
    );
  }

  store.addEventListener('queue', render);
  store.addEventListener('tracks', render);
  render();
}
