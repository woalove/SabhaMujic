// @ts-check
// Client-side state: the latest playback state and queue (stale revisions ignored, reset when the service restarts,
// i.e. when ServerEpoch changes), a track title cache and the categories.

/** @typedef {import('./types.js').PlaybackState} PlaybackState */
/** @typedef {import('./types.js').Queue} Queue */
/** @typedef {import('./types.js').Track} Track */
/** @typedef {import('./types.js').Category} Category */
/** @typedef {import('./types.js').Me} Me */

const MAX_BATCH = 500;
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

export class Store extends EventTarget {
  constructor() {
    super();
    /** @type {Me | null} */
    this.me = null;
    /** @type {PlaybackState | null} */
    this.state = null;
    /** Local time (ms) when `state` arrived. */
    this.stateReceivedAt = 0;
    /** @type {Queue | null} */
    this.queue = null;
    /** @type {string | null} */
    this.epoch = null;
    this.stateRevision = -Infinity;
    this.queueRevision = -Infinity;
    /** @type {Map<string, Track>} */
    this.tracks = new Map();
    /** @type {Category[]} */
    this.categories = [];
  }

  /**
   * A new ServerEpoch means the service restarted and its revision counters started over.
   * @param {string | undefined} epoch
   */
  checkEpoch(epoch) {
    if (epoch && epoch !== EMPTY_GUID && epoch !== this.epoch) {
      this.epoch = epoch;
      this.stateRevision = -Infinity;
      this.queueRevision = -Infinity;
    }
  }

  /**
   * @param {PlaybackState} state
   * @returns {boolean} whether it was applied (false for stale revisions)
   */
  applyState(state) {
    this.checkEpoch(state.serverEpoch);
    if (state.revision <= this.stateRevision) return false;
    this.stateRevision = state.revision;
    this.state = state;
    this.stateReceivedAt = Date.now();
    this.dispatchEvent(new Event('state'));
    return true;
  }

  /**
   * @param {Queue} queue
   * @returns {boolean}
   */
  applyQueue(queue) {
    this.checkEpoch(queue.serverEpoch);
    if (queue.revision <= this.queueRevision) return false;
    this.queueRevision = queue.revision;
    this.queue = queue;
    this.dispatchEvent(new Event('queue'));
    return true;
  }

  /** @param {Category[]} categories */
  setCategories(categories) {
    this.categories = categories;
    this.dispatchEvent(new Event('categories'));
  }

  /** @param {Track[]} tracks */
  addTracks(tracks) {
    for (const track of tracks) this.tracks.set(track.id, track);
    if (tracks.length > 0) this.dispatchEvent(new Event('tracks'));
  }

  /** @param {string | null | undefined} id */
  track(id) {
    return id ? this.tracks.get(id) : undefined;
  }

  /**
   * Loads titles for ids not yet cached.
   * @param {string[]} ids
   * @param {(ids: string[]) => Promise<Track[]>} fetchByIds
   */
  async ensureTracks(ids, fetchByIds) {
    const missing = [...new Set(ids.filter((id) => id && !this.tracks.has(id)))];
    for (let i = 0; i < missing.length; i += MAX_BATCH) {
      this.addTracks(await fetchByIds(missing.slice(i, i + MAX_BATCH)));
    }
  }

  /**
   * Position now, interpolated from the last state. The server Timestamp is used when the phone's clock agrees with
   * the PC's (age under 2 s); otherwise the local receive time, so a skewed phone clock cannot run the bar off.
   */
  positionNow() {
    const state = this.state;
    if (!state) return 0;
    if (state.status !== 'Playing' && state.status !== 'Crossfading') return state.positionSeconds;
    const now = Date.now();
    const sent = Date.parse(state.timestamp);
    const age = this.stateReceivedAt - sent;
    const base = Number.isFinite(sent) && age >= 0 && age < 2000 ? sent : this.stateReceivedAt;
    const position = state.positionSeconds + (now - base) / 1000;
    return state.durationSeconds > 0 ? Math.min(position, state.durationSeconds) : position;
  }
}
