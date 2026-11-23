// @ts-check
// Queue as a timeline: the current item has a filled accent node. Tap a row for Play, Move up/down and Remove;
// Clear (with confirmation) keeps only the item playing.

import { byId } from '../dom.js';
import { formatTime } from '../format.js';
import { actionButton, categoryPills, timelineRow } from '../ui.js';

/** @typedef {import('../context.js').AppContext} AppContext */

/** @param {AppContext} ctx */
export function mountQueue(ctx) {
  const { store, hub, run, confirm } = ctx;
  const list = byId('queue-list', HTMLOListElement);
  const empty = byId('queue-empty', HTMLElement);
  const summary = byId('queue-summary', HTMLElement);
  const clearButton = byId('queue-clear', HTMLButtonElement);

  clearButton.addEventListener('click', async () => {
    if (await confirm('Remove every item from the queue except the one playing?', 'Clear')) {
      await run(async () => store.applyQueue(await hub.invoke('ClearQueue')), 'Queue cleared');
    }
  });

  function render() {
    const queue = store.queue;
    const items = queue?.items ?? [];
    const current = queue?.currentIndex ?? -1;
    empty.hidden = items.length > 0;
    clearButton.disabled = items.length === 0;
    summary.textContent = items.length === 0 ? '' : `${items.length} track${items.length === 1 ? '' : 's'}`;
    list.replaceChildren(
      ...items.map((item, index) => {
        const track = store.track(item.trackId);
        const title = track?.title ?? '…';
        return timelineRow({
          label: title,
          title,
          top: index === current ? 'Now playing' : index === current + 1 ? 'Up next' : `#${index + 1}`,
          meta: track?.artist ? { icon: 'user', text: track.artist } : undefined,
          pills: categoryPills(store, track?.categoryIds),
          aside: track ? formatTime(track.durationSeconds) : '',
          current: index === current,
          attrs: { 'data-queue-item': item.queueItemId },
          actions: () => [
            actionButton('Play', () => void run(() => hub.invoke('PlayQueueItem', item.queueItemId)), { kind: 'primary', icon: 'play' }),
            actionButton('Move up', () => void run(async () => store.applyQueue(await hub.invoke('MoveQueueItem', item.queueItemId, index - 1))),
              { icon: 'arrow-up', disabled: index === 0 }),
            actionButton('Move down', () => void run(async () => store.applyQueue(await hub.invoke('MoveQueueItem', item.queueItemId, index + 1))),
              { icon: 'arrow-down', disabled: index === items.length - 1 }),
            actionButton('Remove', () => void run(async () => store.applyQueue(await hub.invoke('RemoveFromQueue', item.queueItemId))),
              { kind: 'danger-text', icon: 'trash' }),
          ],
        });
      }),
    );
    ctx.setTabValue('queue', String(items.length));
  }

  store.addEventListener('queue', render);
  store.addEventListener('tracks', render);
  store.addEventListener('categories', render);
  render();
}
