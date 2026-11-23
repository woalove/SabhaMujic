// @ts-check
// Classic (non-module) script loaded synchronously in <head> so the saved theme applies before first paint (no flash).
// A stored choice ('dark' | 'light') wins; without one the CSS follows prefers-color-scheme. Storage can be missing or
// throw (private mode, blocked site data), so every access is guarded. app.js owns the toggle button.
(function applySavedTheme() {
  try {
    const saved = window.localStorage.getItem('sabhamujic.theme');
    if (saved === 'dark' || saved === 'light') document.documentElement.dataset.theme = saved;
  } catch {
    // no storage: follow the system setting
  }
})();
