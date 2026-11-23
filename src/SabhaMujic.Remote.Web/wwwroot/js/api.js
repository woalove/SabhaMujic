// @ts-check
// HTTP API client. The session token is an HttpOnly cookie: this code never sees it. State-changing requests carry the
// X-SabhaMujic header that the server requires as CSRF protection.

/** @typedef {import('./types.js').Me} Me */
/** @typedef {import('./types.js').ImportResult} ImportResult */

export class ApiError extends Error {
  /**
   * @param {string} code ErrorCode name (Unauthenticated, Forbidden, NotFound, …) or 'Unavailable' for transport failures.
   * @param {string} message
   * @param {number} [status]
   */
  constructor(code, message, status = 0) {
    super(message);
    this.name = 'ApiError';
    this.code = code;
    this.status = status;
  }
}

/**
 * @param {string} method
 * @param {string} url
 * @param {{ json?: unknown, body?: Blob, signal?: AbortSignal }} [options]
 * @returns {Promise<any>}
 */
async function request(method, url, options = {}) {
  /** @type {Record<string, string>} */
  const headers = { Accept: 'application/json' };
  if (method !== 'GET') headers['X-SabhaMujic'] = '1';
  /** @type {BodyInit | undefined} */
  let body;
  if (options.json !== undefined) {
    headers['Content-Type'] = 'application/json';
    body = JSON.stringify(options.json);
  } else if (options.body) {
    headers['Content-Type'] = 'application/octet-stream';
    body = options.body;
  }

  let response;
  try {
    response = await fetch(url, { method, headers, body, credentials: 'same-origin', cache: 'no-store', signal: options.signal });
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error;
    throw new ApiError('Unavailable', 'Cannot reach the SabhaMujic PC. Check the Wi-Fi connection.');
  }

  if (response.status === 204) return undefined;
  /** @type {any} */
  let data;
  try {
    data = await response.json();
  } catch {
    data = undefined;
  }
  if (!response.ok) {
    const code = data && typeof data.code === 'string' ? data.code : 'Internal';
    const message = data && typeof data.message === 'string' ? data.message : `The request failed (${response.status}).`;
    throw new ApiError(code, message, response.status);
  }
  return data;
}

export const api = {
  /** @returns {Promise<Me>} */
  me: () => request('GET', '/api/me'),

  /**
   * Administrators only (others get Forbidden).
   * @param {string} userName
   * @param {string} password
   * @param {boolean} rememberDevice
   * @param {string} deviceName
   * @returns {Promise<Me>}
   */
  login: (userName, password, rememberDevice, deviceName) =>
    request('POST', '/api/login', { json: { userName, password, rememberDevice, deviceName } }),

  /** @returns {Promise<void>} */
  logout: () => request('POST', '/api/logout'),

  /**
   * @param {string} fileName
   * @param {number} totalBytes
   * @returns {Promise<string>} the upload id
   */
  beginUpload: async (fileName, totalBytes) => (await request('POST', '/api/uploads', { json: { fileName, totalBytes } })).uploadId,

  /**
   * @param {string} uploadId
   * @param {number} offset
   * @param {Blob} chunk
   * @param {AbortSignal} [signal]
   * @returns {Promise<void>}
   */
  uploadChunk: (uploadId, offset, chunk, signal) =>
    request('PUT', `/api/uploads/${encodeURIComponent(uploadId)}?offset=${offset}`, { body: chunk, signal }),

  /**
   * @param {string} uploadId
   * @param {{ title: string, artist: string | null, categoryIds: string[] }} metadata
   * @returns {Promise<ImportResult>}
   */
  completeUpload: (uploadId, metadata) => request('POST', `/api/uploads/${encodeURIComponent(uploadId)}/complete`, { json: metadata }),

  /**
   * @param {string} uploadId
   * @returns {Promise<void>}
   */
  cancelUpload: (uploadId) => request('DELETE', `/api/uploads/${encodeURIComponent(uploadId)}`),
};
