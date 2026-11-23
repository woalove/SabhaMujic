// @ts-check
// Shared UI services handed to every view.

/**
 * @typedef {object} AppContext
 * @property {import('./store.js').Store} store
 * @property {import('./hub.js').HubClient} hub
 * @property {(message: string) => void} toast
 * @property {(error: unknown) => void} showError
 * @property {(text: string, okLabel?: string) => Promise<boolean>} confirm
 * @property {<T>(action: () => Promise<T>, success?: string) => Promise<T | undefined>} run
 *   Runs a hub/API action, showing `success` as a toast or the error in the banner.
 * @property {(view: 'now' | 'queue' | 'playlists' | 'upload', value: string) => void} setTabValue
 *   Sets the bold value shown next to the screen name in the tab row (e.g. the queue length).
 */

export {};
