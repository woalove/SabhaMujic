// @ts-check
// SabhaMujic remote: app shell (routing between pair and the tabbed main UI), hub wiring and shared UI services.

import { api, ApiError } from './api.js';
import { byId } from './dom.js';
import { HubClient } from './hub.js';
import { Store } from './store.js';
import { mountAdmin } from './views/admin.js';
import { mountLibrary } from './views/library.js';
import { mountNowPlaying } from './views/nowplaying.js';
import { mountPair } from './views/pair.js';
import { mountPlaylists } from './views/playlists.js';
import { mountQueue } from './views/queue.js';

/** @typedef {import('./types.js').Me} Me */
/** @typedef {import('./types.js').PlaybackState} PlaybackState */
/** @typedef {import('./types.js').Queue} Queue */
/** @typedef {import('./types.js').Category} Category */
/** @typedef {import('./types.js').Track} Track */
/** @typedef {import('./types.js').LibraryChange} LibraryChange */
/** @typedef {import('./types.js').ImportProgress} ImportProgress */
/** @typedef {import('./hub.js').ConnectionStatus} ConnectionStatus */
/** @typedef {import('./context.js').AppContext} AppContext */
/** @typedef {'pair' | 'now' | 'queue' | 'library' | 'playlists' | 'admin'} ViewName */

const store = new Store();
const pill = byId('conn', HTMLElement);
const banner = byId('banner', HTMLElement);
const bannerText = byId('banner-text', HTMLElement);
const toastEl = byId('toast', HTMLElement);
const tabbar = byId('tabbar', HTMLElement);
const adminTab = byId('tab-admin', HTMLButtonElement);
const signout = byId('signout', HTMLButtonElement);
const confirmDialog = byId('confirm', HTMLDialogElement);

// ------------------------------------------------------------------ shared UI services

/** @type {ReturnType<typeof setTimeout> | undefined} */
let toastTimer;
/** @param {string} message */
function toast(message) {
  toastEl.textContent = message;
  toastEl.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (toastEl.hidden = true), 2500);
}

/** @param {unknown} error */
function showError(error) {
  bannerText.textContent = error instanceof ApiError ? error.message : 'Something went wrong. Try again.';
  banner.hidden = false;
}
byId('banner-close', HTMLButtonElement).addEventListener('click', () => (banner.hidden = true));

/**
 * @param {string} text
 * @param {string} [okLabel]
 * @returns {Promise<boolean>}
 */
function confirmAction(text, okLabel = 'OK') {
  byId('confirm-text', HTMLElement).textContent = text;
  byId('confirm-ok', HTMLButtonElement).textContent = okLabel;
  confirmDialog.returnValue = '';
  confirmDialog.showModal();
  return new Promise((resolve) => {
    confirmDialog.addEventListener('close', () => resolve(confirmDialog.returnValue === 'ok'), { once: true });
  });
}

/**
 * @template T
 * @param {() => Promise<T>} action
 * @param {string} [success]
 * @returns {Promise<T | undefined>}
 */
async function run(action, success) {
  try {
    const result = await action();
    if (success) toast(success);
    return result;
  } catch (error) {
    showError(error);
    return undefined;
  }
}

// ------------------------------------------------------------------ hub

const hub = new HubClient({
  onStatus: renderStatus,
  onConnected: () => void refreshAll(),
  onUnauthorized: () => showPair('This phone is no longer paired. Ask an administrator for a new pairing code.'),
});

/** @type {AppContext} */
const ctx = { store, hub, toast, showError, confirm: confirmAction, run };

/** @param {ConnectionStatus} status */
function renderStatus(status) {
  const labels = { connected: 'Connected', connecting: 'Connecting…', reconnecting: 'Reconnecting…', disconnected: 'Offline' };
  pill.textContent = labels[status];
  pill.dataset.status = status;
  pill.className = `pill ${status === 'connected' ? 'pill-ok' : status === 'disconnected' ? 'pill-bad' : 'pill-wait'}`;
}

/** @param {string[]} ids */
const fetchTracks = (ids) => /** @type {Promise<Track[]>} */ (hub.invoke('GetTracksByIds', ids));

async function loadCategories() {
  /** @type {Category[] | undefined} */
  const categories = await run(() => hub.invoke('GetCategories'));
  if (categories) store.setCategories(categories);
}

/** After (re)connecting: the authoritative state, queue and categories (honouring a new ServerEpoch). */
async function refreshAll() {
  try {
    /** @type {[PlaybackState, Queue]} */
    const [state, queue] = await Promise.all([hub.invoke('GetState'), hub.invoke('GetQueue')]);
    store.applyState(state);
    store.applyQueue(queue);
    banner.hidden = true;
  } catch (error) {
    showError(error);
  }
  await loadCategories();
  library.refresh();
  playlists.refresh();
}

hub.on('PlaybackStateChanged', (/** @type {PlaybackState} */ state) => store.applyState(state));
hub.on('QueueChanged', (/** @type {Queue} */ queue) => store.applyQueue(queue));
hub.on('ImportProgress', (/** @type {ImportProgress} */ progress) => admin.onProgress(progress));
hub.on('LibraryChanged', (/** @type {LibraryChange} */ change) => {
  if (change.kind === 'PlaylistsChanged') {
    playlists.refresh();
    return;
  }
  if (change.kind === 'UsersChanged' || change.kind === 'DevicesChanged') return;
  if (change.entityId && (change.kind === 'TrackUpdated' || change.kind === 'TrackRemoved' || change.kind === 'AnalysisUpdated')) {
    store.tracks.delete(change.entityId);
    void store.ensureTracks(referencedTrackIds(), fetchTracks).catch(() => {});
  }
  void loadCategories(); // track counts
  library.refresh();
});
hub.on('SessionEnded', (/** @type {string} */ reason) => {
  void hub.stop();
  showPair(reason || 'This phone was signed out.');
});

function referencedTrackIds() {
  const ids = (store.queue?.items ?? []).map((i) => i.trackId);
  for (const id of [store.state?.currentTrackId, store.state?.nextTrackId]) if (id) ids.push(id);
  return ids;
}

store.addEventListener('state', () => void store.ensureTracks(referencedTrackIds(), fetchTracks).catch(() => {}));
store.addEventListener('queue', () => void store.ensureTracks(referencedTrackIds(), fetchTracks).catch(() => {}));

// ------------------------------------------------------------------ views and routing

mountNowPlaying(ctx);
mountQueue(ctx);
const library = mountLibrary(ctx);
const playlists = mountPlaylists(ctx);
const admin = mountAdmin(ctx);
const pair = mountPair((me) => {
  toast(`Paired as “${me.name}”`);
  startMain(me);
});

/** @type {Record<ViewName, { show(): void } | undefined>} */
const viewHooks = { pair, now: undefined, queue: undefined, library, playlists, admin: undefined };

/** @param {ViewName} name */
function showView(name) {
  for (const section of document.querySelectorAll('main > section')) {
    if (section instanceof HTMLElement) section.hidden = section.id !== `view-${name}`;
  }
  for (const button of tabbar.querySelectorAll('button[data-tab]')) {
    if (button instanceof HTMLElement) {
      if (button.dataset.tab === name) button.setAttribute('aria-current', 'page');
      else button.removeAttribute('aria-current');
    }
  }
  tabbar.hidden = name === 'pair';
  viewHooks[name]?.show();
  window.scrollTo(0, 0);
}

for (const button of tabbar.querySelectorAll('button[data-tab]')) {
  if (button instanceof HTMLElement) {
    button.addEventListener('click', () => showView(/** @type {ViewName} */ (button.dataset.tab)));
  }
}

/** @param {string} [message] */
function showPair(message) {
  store.me = null;
  signout.hidden = true;
  if (message) showError(new ApiError('Unauthenticated', message));
  renderStatus('disconnected');
  showView('pair');
}

/** @param {Me} me */
function startMain(me) {
  store.me = me;
  signout.hidden = false;
  signout.title = `Signed in as ${me.name}`;
  adminTab.hidden = me.role !== 'Admin';
  if (location.pathname !== '/') history.replaceState(null, '', '/');
  showView('now');
  hub.start();
}

signout.addEventListener('click', async () => {
  if (!(await confirmAction('Sign this phone out? You will need a new pairing code to sign in again.', 'Sign out'))) return;
  await run(() => api.logout());
  await hub.stop();
  showPair();
});

// A new pairing link opened while the app is already loaded only changes the fragment.
window.addEventListener('hashchange', () => {
  if (/^#code=[0-9]{6}$/.test(location.hash)) {
    void hub.stop();
    showPair();
  }
});

async function boot() {
  if (location.pathname === '/pair') {
    showPair();
    return;
  }
  renderStatus('connecting');
  try {
    startMain(await api.me());
  } catch (error) {
    if (error instanceof ApiError && error.code === 'Unauthenticated') {
      showPair();
    } else {
      showError(error);
      renderStatus('disconnected');
      setTimeout(() => void boot(), 3000);
    }
  }
}

void boot();
