// @ts-check
// Now Playing: big title, artist, category pills, interpolated position + seek, transport (filled accent play/pause
// circle), volume/mute, loop mode and crossfade, status pills (Crossfading, Smart Loop), up next.

import { byId } from '../dom.js';
import { formatTime } from '../format.js';
import { setIcon } from '../icons.js';
import { categoryPills, updateRangeFill } from '../ui.js';

/** @typedef {import('../context.js').AppContext} AppContext */
/** @typedef {import('../types.js').LoopMode} LoopMode */

const LABELS = /** @type {const} */ ({
  Idle: 'Idle',
  Loading: 'Loading',
  Playing: 'Playing',
  Paused: 'Paused',
  Crossfading: 'Playing',
  Stopped: 'Stopped',
  Error: 'Error',
});

/** @param {AppContext} ctx */
export function mountNowPlaying(ctx) {
  const { store, hub, run } = ctx;
  const title = byId('np-title', HTMLElement);
  const artist = byId('np-artist', HTMLElement);
  const tags = byId('np-tags', HTMLElement);
  const status = byId('np-status', HTMLElement);
  const xfadeBadge = byId('np-xfade', HTMLElement);
  const smartBadge = byId('np-smart', HTMLElement);
  const error = byId('np-error', HTMLElement);
  const seek = byId('np-seek', HTMLInputElement);
  const pos = byId('np-pos', HTMLElement);
  const dur = byId('np-dur', HTMLElement);
  const play = byId('np-play', HTMLButtonElement);
  const mute = byId('np-mute', HTMLButtonElement);
  const volume = byId('np-volume', HTMLInputElement);
  const volumeLabel = byId('np-volume-label', HTMLElement);
  const crossfade = byId('np-crossfade', HTMLInputElement);
  const crossfadeLabel = byId('np-xfade-label', HTMLElement);
  const nextTitle = byId('np-next-title', HTMLElement);
  const loopButtons = /** @type {HTMLButtonElement[]} */ ([...document.querySelectorAll('[data-loop]')]);

  let seeking = false;
  let volumeDragging = false;
  let crossfadeDragging = false;
  /** @type {boolean | null} */
  let shownPlaying = null;
  /** @type {boolean | null} */
  let shownMuted = null;

  const isPlaying = () => store.state?.status === 'Playing' || store.state?.status === 'Crossfading';

  play.addEventListener('click', () => void run(() => hub.invoke(isPlaying() ? 'Pause' : 'Resume')));
  byId('np-prev', HTMLButtonElement).addEventListener('click', () => void run(() => hub.invoke('Previous')));
  byId('np-next', HTMLButtonElement).addEventListener('click', () => void run(() => hub.invoke('Next')));
  byId('np-stop', HTMLButtonElement).addEventListener('click', () => void run(() => hub.invoke('Stop')));
  mute.addEventListener('click', () => void run(() => hub.invoke('SetMuted', !store.state?.muted)));

  seek.addEventListener('input', () => {
    seeking = true;
    pos.textContent = formatTime(Number(seek.value));
    updateRangeFill(seek);
  });
  seek.addEventListener('change', () => {
    seeking = false;
    void run(() => hub.invoke('Seek', Number(seek.value)));
  });

  volume.addEventListener('input', () => {
    volumeDragging = true;
    volumeLabel.textContent = `${Math.round(Number(volume.value) * 100)}%`;
    updateRangeFill(volume);
  });
  volume.addEventListener('change', () => {
    volumeDragging = false;
    void run(() => hub.invoke('SetVolume', Number(volume.value)));
  });

  crossfade.addEventListener('input', () => {
    crossfadeDragging = true;
    crossfadeLabel.textContent = `${crossfade.value} s`;
    updateRangeFill(crossfade);
  });
  crossfade.addEventListener('change', () => {
    crossfadeDragging = false;
    void run(() => hub.invoke('SetCrossfade', Number(crossfade.value)));
  });

  for (const button of loopButtons) {
    button.addEventListener('click', () => {
      const mode = /** @type {LoopMode} */ (button.dataset.loop);
      void run(() => hub.invoke('SetLoopMode', mode));
    });
  }

  function render() {
    const state = store.state;
    const track = store.track(state?.currentTrackId);
    title.textContent = track?.title ?? (state?.currentTrackId ? 'Loading…' : 'Nothing playing');
    artist.textContent = track?.artist ?? (state?.currentTrackId ? '' : 'Choose something from the Library.');
    tags.replaceChildren(...categoryPills(store, track?.categoryIds));
    status.textContent = state ? LABELS[state.status] : '—';
    status.className = `pill ${state?.status === 'Error' ? 'tag-danger' : isPlaying() ? 'tag-accent' : ''}`.trim();
    xfadeBadge.hidden = state?.status !== 'Crossfading';
    smartBadge.hidden = state?.loopMode !== 'SmartLoop';
    const message = state?.status === 'Error' ? state.errorMessage || 'Playback error.' : state?.errorMessage ?? null;
    error.hidden = !message;
    error.textContent = message ?? '';

    const playing = isPlaying();
    if (playing !== shownPlaying) {
      setIcon(play, playing ? 'pause' : 'play', 34);
      play.setAttribute('aria-label', playing ? 'Pause' : 'Play');
      shownPlaying = playing;
    }

    if (state) {
      const muted = state.muted;
      mute.setAttribute('aria-pressed', String(muted));
      mute.setAttribute('aria-label', muted ? 'Unmute' : 'Mute');
      if (muted !== shownMuted) {
        setIcon(mute, muted ? 'muted' : 'volume');
        shownMuted = muted;
      }
      if (!volumeDragging) {
        volume.value = String(state.volume);
        volumeLabel.textContent = muted ? 'Muted' : `${Math.round(state.volume * 100)}%`;
        updateRangeFill(volume);
      }
      if (!crossfadeDragging) {
        crossfade.value = String(Math.round(state.crossfadeSeconds));
        crossfadeLabel.textContent = `${Math.round(state.crossfadeSeconds)} s`;
        updateRangeFill(crossfade);
      }
      for (const button of loopButtons) button.setAttribute('aria-pressed', String(button.dataset.loop === state.loopMode));
    }

    const next = store.track(state?.nextTrackId);
    nextTitle.textContent = next ? next.title : state?.nextTrackId ? '…' : '—';
    tick();
  }

  function tick() {
    const state = store.state;
    const duration = state?.durationSeconds ?? 0;
    seek.max = String(Math.max(duration, 0));
    seek.disabled = !state?.currentTrackId || duration <= 0;
    dur.textContent = formatTime(duration);
    if (!seeking) {
      const now = store.positionNow();
      seek.value = String(now);
      pos.textContent = formatTime(now);
      updateRangeFill(seek);
    }
  }

  store.addEventListener('state', render);
  store.addEventListener('tracks', render);
  store.addEventListener('categories', render);
  setInterval(() => {
    if (isPlaying()) tick();
  }, 250);
  render();
}
