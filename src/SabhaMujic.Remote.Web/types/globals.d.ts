// Type-checking only (not served): the vendored UMD build of @microsoft/signalr defines the global `signalR`.
import type * as SignalR from '@microsoft/signalr';

declare global {
  const signalR: typeof SignalR;
}

export {};
