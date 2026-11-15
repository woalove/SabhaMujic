// @ts-check
// Pairing: the code arrives in the URL fragment (#code=123456, never sent to the server) or is typed in.

import { api, ApiError } from '../api.js';
import { byId } from '../dom.js';

/** @typedef {import('../types.js').Me} Me */

/**
 * @param {(me: Me) => void} onPaired
 */
export function mountPair(onPaired) {
  const form = byId('pair-form', HTMLFormElement);
  const code = byId('pair-code', HTMLInputElement);
  const name = byId('pair-name', HTMLInputElement);
  const submit = byId('pair-submit', HTMLButtonElement);
  const error = byId('pair-error', HTMLElement);

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const value = code.value.trim();
    const deviceName = name.value.trim();
    if (!/^[0-9]{6}$/.test(value)) return showError('Enter the 6-digit code shown on the PC.');
    if (!deviceName) return showError('Give this phone a name, e.g. “Sevak iPhone”.');
    submit.disabled = true;
    error.hidden = true;
    try {
      const me = await api.pair(value, deviceName);
      code.value = '';
      onPaired(me);
    } catch (e) {
      showError(e instanceof ApiError ? e.message : 'Pairing failed. Try again.');
    } finally {
      submit.disabled = false;
    }
  });

  /** @param {string} message */
  function showError(message) {
    error.textContent = message;
    error.hidden = false;
  }

  return {
    /** Prefills the code from the fragment and removes it from the address bar. */
    show() {
      const match = /^#code=([0-9]{6})$/.exec(location.hash);
      if (match) code.value = match[1];
      if (location.hash) history.replaceState(null, '', location.pathname);
      if (!name.value) name.value = guessDeviceName();
      (code.value ? name : code).focus();
    },
  };
}

function guessDeviceName() {
  const ua = navigator.userAgent;
  if (/iPhone/.test(ua)) return 'iPhone';
  if (/iPad/.test(ua)) return 'iPad';
  if (/Android/.test(ua)) return 'Android phone';
  return 'Phone';
}
