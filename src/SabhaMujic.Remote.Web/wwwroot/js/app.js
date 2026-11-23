// @ts-check
// SabhaMujic remote: app shell (routing between sign-in and the tabbed main UI), hub wiring and shared UI services.
// The remote is for administrators only (the server enforces it).

import { api, ApiError } from './api.js';
import { byId } from './dom.js';
import { HubClient } from './hub.js';
import { hydrateIcons, setIcon } from './icons.js';
import { Store } from './store.js';
import { mountLibrary } from './views/library.js';
import { mountNowPlaying } from './views/nowplaying.js';
import { mountLogin } from './views/login.js';
import { mountPlaylists } from './views/playlists.js';
import { mountQueue } from './views/queue.js';
import { mountUpload } from './views/upload.js';

/** @typedef {import('./types.js').Me} Me */
/** @typedef {import('./types.js').PlaybackState} PlaybackState */
/** @typedef {import('./types.js').Queue} Queue */
/** @typedef {import('./types.js').Category} Category */
/** @typedef {import('./types.js').Track} Track */
/** @typedef {import('./types.js').LibraryChange} LibraryChange */
/** @typedef {import('./types.js').ImportProgress} ImportProgress */
/** @typedef {import('./hub.js').ConnectionStatus} ConnectionStatus */
/** @typedef {import('./context.js').AppContext} AppContext */
/** @typedef {'login' | 'now' | 'queue' | 'library' | 'playlists' | 'upload'} ViewName */

/** Screen names, in the same order and wording as the desktop. */
const SCREENS = /** @type {const} */ ({
  login: 'Sign in',
  now: 'Now Playing',
  queue: 'Queue',
  library: 'Library',
  playlists: 'Playlists',
  upload: 'Upload',
});
const THEME_KEY = 'sabhamujic.theme';

hydrateIcons();

const store = new Store();
const conn = byId('conn', HTMLElement);
const connLabel = byId('conn-label', HTMLElement);
const tabrow = byId('tabrow', HTMLElement);
const catTabs = byId('cat-tabs', HTMLElement);
const viewTab = byId('view-tab', HTMLElement);
const viewTabLabel = byId('view-tab-label', HTMLElement);
const viewTabValue = byId('view-tab-value', HTMLElement);
const subtitle = byId('app-subtitle', HTMLElement);
const themeToggle = byId('theme-toggle', HTMLButtonElement);
const accountWrap = byId('account-wrap', HTMLElement);
const accountButton = byId('account', HTMLButtonElement);
const accountMenu = byId('account-menu', HTMLElement);
const banner = byId('banner', HTMLElement);
const bannerText = byId('banner-text', HTMLElement);
const toastEl = byId('toast', HTMLElement);
const tabbar = byId('tabbar', HTMLElement);
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
  onUnauthorized: () => showLogin('You have been signed out. Sign in again.'),
});

/** Bold values next to the screen name in the tab row. @type {Record<string, string>} */
const tabValues = {};
/** @type {ViewName} */
let currentView = 'login';

/** @type {AppContext['setTabValue']} */
function setTabValue(view, value) {
  tabValues[view] = value;
  if (currentView === view) viewTabValue.textContent = value;
}

/** @type {AppContext} */
const ctx = { store, hub, toast, showError, confirm: confirmAction, run, setTabValue };

/** @param {ConnectionStatus} status */
function renderStatus(status) {
  const labels = { connected: 'Live', connecting: 'Connecting…', reconnecting: 'Reconnecting…', disconnected: 'Disconnected' };
  connLabel.textContent = labels[status];
  conn.dataset.status = status;
}

// ------------------------------------------------------------------ theme (stored choice, else the system setting)

function effectiveTheme() {
  const chosen = document.documentElement.dataset.theme;
  if (chosen === 'dark' || chosen === 'light') return chosen;
  return window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
}

function renderTheme() {
  const theme = effectiveTheme();
  // The button shows what a tap switches to.
  setIcon(themeToggle, theme === 'dark' ? 'sun' : 'moon');
  themeToggle.setAttribute('aria-label', theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme');
  themeToggle.dataset.theme = theme;
  document.getElementById('theme-color')?.setAttribute('content', theme === 'dark' ? '#11151A' : '#F6F7F9');
}

themeToggle.addEventListener('click', () => {
  const next = effectiveTheme() === 'dark' ? 'light' : 'dark';
  document.documentElement.dataset.theme = next;
  try {
    window.localStorage.setItem(THEME_KEY, next);
  } catch {
    // storage unavailable: the choice lasts for this page only
  }
  renderTheme();
});
window.matchMedia('(prefers-color-scheme: light)').addEventListener('change', renderTheme);
renderTheme();

// ------------------------------------------------------------------ account menu

/** @param {boolean} open */
function setMenu(open) {
  accountMenu.hidden = !open;
  accountButton.setAttribute('aria-expanded', String(open));
}
accountButton.addEventListener('click', (event) => {
  event.stopPropagation();
  setMenu(accountMenu.hidden);
});
document.addEventListener('click', (event) => {
  if (!accountMenu.hidden && event.target instanceof Node && !accountMenu.contains(event.target)) setMenu(false);
});
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape') setMenu(false);
});

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
hub.on('ImportProgress', (/** @type {ImportProgress} */ progress) => upload.onProgress(progress));
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
  showLogin(reason || 'This phone was signed out.');
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
const upload = mountUpload(ctx);
const login = mountLogin((me) => {
  toast(`Signed in as “${me.name}”`);
  startMain(me);
});

/** @type {Record<ViewName, { show(): void } | undefined>} */
const viewHooks = { login, now: undefined, queue: undefined, library, playlists, upload: undefined };

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
  const signedOut = name === 'login';
  currentView = name;
  tabbar.hidden = signedOut;
  tabrow.hidden = signedOut;
  accountWrap.hidden = signedOut;
  document.body.classList.toggle('signed-out', signedOut);
  // DESIGN.md's example subtitle for the player is SABHA MUSIC · PLAYBACK (and it fits a phone header).
  subtitle.textContent = signedOut ? 'Sabha music · Remote' : `Sabha music · ${name === 'now' ? 'Playback' : SCREENS[name]}`;
  catTabs.hidden = name !== 'library';
  viewTab.hidden = name === 'library';
  viewTabLabel.textContent = SCREENS[name];
  viewTabValue.textContent = tabValues[name] ?? '';
  setMenu(false);
  viewHooks[name]?.show();
  window.scrollTo(0, 0);
}

for (const button of tabbar.querySelectorAll('button[data-tab]')) {
  if (button instanceof HTMLElement) {
    button.addEventListener('click', () => showView(/** @type {ViewName} */ (button.dataset.tab)));
  }
}

/** @param {string} [message] */
function showLogin(message) {
  store.me = null;
  if (message) showError(new ApiError('Unauthenticated', message));
  renderStatus('disconnected');
  showView('login');
}

/** @param {Me} me */
function startMain(me) {
  store.me = me;
  byId('account-initial', HTMLElement).textContent = (me.name.trim()[0] ?? '?').toUpperCase();
  byId('account-name', HTMLElement).textContent = me.name;
  byId('account-menu-name', HTMLElement).textContent = me.name;
  accountButton.setAttribute('aria-label', `Account: ${me.name}`);
  if (location.pathname !== '/') history.replaceState(null, '', '/');
  showView('now');
  hub.start();
}

byId('home', HTMLButtonElement).addEventListener('click', () => {
  if (store.me) showView('now');
});

signout.addEventListener('click', async () => {
  setMenu(false);
  if (!(await confirmAction('Sign out of SabhaMujic on this phone? You will need your password to sign in again.', 'Sign out'))) return;
  await run(() => api.logout());
  await hub.stop();
  showLogin();
});

async function boot() {
  renderStatus('connecting');
  try {
    startMain(await api.me());
  } catch (error) {
    if (error instanceof ApiError && (error.code === 'Unauthenticated' || error.code === 'Forbidden')) {
      showLogin(error.code === 'Forbidden' ? error.message : undefined);
    } else {
      showError(error);
      renderStatus('disconnected');
      setTimeout(() => void boot(), 3000);
    }
  }
}

void boot();
