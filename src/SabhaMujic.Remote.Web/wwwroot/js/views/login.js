// @ts-check
// Sign-in (administrators only): user name and password. "Remember this phone" keeps it signed in (it then appears in
// the PC's list of phones, where an administrator can forget it); otherwise the sign-in lasts at most 12 hours.

import { api, ApiError } from '../api.js';
import { byId } from '../dom.js';

/** @typedef {import('../types.js').Me} Me */

/**
 * @param {(me: Me) => void} onSignedIn
 */
export function mountLogin(onSignedIn) {
  const form = byId('login-form', HTMLFormElement);
  const user = byId('login-user', HTMLInputElement);
  const password = byId('login-password', HTMLInputElement);
  const remember = byId('login-remember', HTMLInputElement);
  const submit = byId('login-submit', HTMLButtonElement);
  const error = byId('login-error', HTMLElement);

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const userName = user.value.trim();
    if (!userName) return showError('Enter your administrator user name.');
    if (!password.value) return showError('Enter your password.');
    submit.disabled = true;
    error.hidden = true;
    try {
      const me = await api.login(userName, password.value, remember.checked, guessDeviceName());
      password.value = '';
      onSignedIn(me);
    } catch (e) {
      password.value = '';
      showError(e instanceof ApiError ? e.message : 'Signing in failed. Try again.');
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
    show() {
      (user.value ? password : user).focus();
    },
  };
}

/** The name a remembered phone gets in the PC's list. */
function guessDeviceName() {
  const ua = navigator.userAgent;
  if (/iPhone/.test(ua)) return 'iPhone';
  if (/iPad/.test(ua)) return 'iPad';
  if (/Android/.test(ua)) return 'Android phone';
  return 'Phone';
}
