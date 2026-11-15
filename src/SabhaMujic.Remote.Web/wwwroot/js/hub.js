// @ts-check
// SignalR connection to the SabhaMujic hub: automatic reconnect (forever, backing off to 10 s), a status for the
// connection pill, and errors decoded from the server's HubException JSON ({code, message}).

import { ApiError, api } from './api.js';

/** @typedef {'connecting' | 'connected' | 'reconnecting' | 'disconnected'} ConnectionStatus */

const RETRY_DELAYS_MS = [0, 1000, 2000, 5000];
const MAX_RETRY_DELAY_MS = 10000;

/** @param {number} attempt */
function retryDelay(attempt) {
  return RETRY_DELAYS_MS[attempt] ?? MAX_RETRY_DELAY_MS;
}

/** @param {unknown} error */
function isUnauthorized(error) {
  const message = error instanceof Error ? error.message : String(error ?? '');
  return /Status code '401'|Unauthorized/.test(message) || (typeof error === 'object' && error !== null && 'statusCode' in error && error.statusCode === 401);
}

/**
 * Converts a hub invocation error into an ApiError. The server sends HubException messages as JSON.
 * @param {unknown} error
 * @returns {ApiError}
 */
export function toApiError(error) {
  if (error instanceof ApiError) return error;
  const message = error instanceof Error ? error.message : String(error ?? '');
  const marker = 'HubException: ';
  const at = message.indexOf(marker);
  if (at >= 0) {
    try {
      const info = JSON.parse(message.slice(at + marker.length));
      if (info && typeof info.code === 'string' && typeof info.message === 'string') return new ApiError(info.code, info.message);
    } catch {
      // fall through
    }
  }
  return new ApiError('Unavailable', 'The request did not reach the SabhaMujic PC. Try again.');
}

export class HubClient {
  /**
   * @param {{ onStatus: (status: ConnectionStatus) => void, onConnected: () => void, onUnauthorized: () => void }} callbacks
   */
  constructor(callbacks) {
    this.callbacks = callbacks;
    /** @type {ConnectionStatus} */
    this.status = 'disconnected';
    this.stopped = true;
    this.unauthorized = false;
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl('/hub', { withCredentials: true })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (context) => {
          if (isUnauthorized(context.retryReason)) {
            this.unauthorized = true;
            return null;
          }
          return retryDelay(context.previousRetryCount);
        },
      })
      .configureLogging(signalR.LogLevel.Warning)
      .build();
    this.connection.keepAliveIntervalInMilliseconds = 10000;
    this.connection.serverTimeoutInMilliseconds = 30000;
    this.connection.onreconnecting(() => this.setStatus('reconnecting'));
    this.connection.onreconnected(() => {
      this.setStatus('connected');
      this.callbacks.onConnected();
    });
    this.connection.onclose(() => {
      if (this.stopped) {
        this.setStatus('disconnected');
      } else if (this.unauthorized) {
        this.stopped = true;
        this.setStatus('disconnected');
        this.callbacks.onUnauthorized();
      } else {
        // Closed by the server (e.g. restart) after automatic reconnect gave up or never started: keep trying.
        this.setStatus('reconnecting');
        void this.connectLoop(1);
      }
    });
  }

  /**
   * Subscribes to a server event (IClientEvents method name).
   * @param {string} event
   * @param {(...args: any[]) => void} handler
   */
  on(event, handler) {
    this.connection.on(event, handler);
  }

  start() {
    this.stopped = false;
    this.unauthorized = false;
    this.setStatus('connecting');
    void this.connectLoop(0);
  }

  async stop() {
    this.stopped = true;
    await this.connection.stop();
  }

  /**
   * Invokes a hub method; rejects with ApiError.
   * @param {string} method
   * @param {...unknown} args
   * @returns {Promise<any>}
   */
  async invoke(method, ...args) {
    if (this.connection.state !== signalR.HubConnectionState.Connected) {
      throw new ApiError('Unavailable', 'Not connected to the SabhaMujic PC yet. Reconnecting…');
    }
    try {
      return await this.connection.invoke(method, ...args);
    } catch (error) {
      throw toApiError(error);
    }
  }

  /** @param {number} attempt */
  async connectLoop(attempt) {
    while (!this.stopped) {
      if (this.connection.state !== signalR.HubConnectionState.Disconnected) return;
      try {
        await this.connection.start();
        this.setStatus('connected');
        this.callbacks.onConnected();
        return;
      } catch (error) {
        if (this.stopped) return;
        if (isUnauthorized(error) || (await this.isSignedOut())) {
          this.stopped = true;
          this.setStatus('disconnected');
          this.callbacks.onUnauthorized();
          return;
        }
        this.setStatus(attempt === 0 ? 'connecting' : 'reconnecting');
      }
      await new Promise((resolve) => setTimeout(resolve, retryDelay(++attempt)));
    }
  }

  async isSignedOut() {
    try {
      await api.me();
      return false;
    } catch (error) {
      return error instanceof ApiError && error.code === 'Unauthenticated';
    }
  }

  /** @param {ConnectionStatus} status */
  setStatus(status) {
    this.status = status;
    this.callbacks.onStatus(status);
  }
}
