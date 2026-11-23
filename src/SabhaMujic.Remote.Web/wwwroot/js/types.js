// @ts-check
// Wire types of the SabhaMujic remote (camelCase JSON, enums as names). Mirrors SabhaMujic.Contracts.

/** @typedef {'Volunteer' | 'Admin'} Role */
/** @typedef {'Idle' | 'Loading' | 'Playing' | 'Paused' | 'Crossfading' | 'Stopped' | 'Error'} PlaybackStatus */
/** @typedef {'Off' | 'SmartLoop' | 'RepeatQueue'} LoopMode */

/**
 * @typedef {object} PlaybackState
 * @property {PlaybackStatus} status
 * @property {string | null} currentTrackId
 * @property {string | null} currentQueueItemId
 * @property {string | null} nextTrackId
 * @property {number} positionSeconds
 * @property {number} durationSeconds
 * @property {number} volume
 * @property {boolean} muted
 * @property {LoopMode} loopMode
 * @property {number} crossfadeSeconds
 * @property {string | null} errorMessage
 * @property {number} revision
 * @property {string} timestamp
 * @property {string} serverEpoch
 */

/** @typedef {{ queueItemId: string, trackId: string }} QueueItem */

/** @typedef {{ items: QueueItem[], currentIndex: number, revision: number, serverEpoch: string }} Queue */

/**
 * @typedef {object} Track
 * @property {string} id
 * @property {string} title
 * @property {string | null} artist
 * @property {string[]} categoryIds
 * @property {number} durationSeconds
 * @property {string} addedAt
 */

/** @typedef {{ id: string, name: string, sortOrder: number, trackCount: number }} Category */

/** @typedef {{ id: string, name: string, trackIds: string[], updatedAt: string }} Playlist */

/** @typedef {{ items: Track[], total: number }} TrackPage */

/** @typedef {{ kind: string, entityId: string | null }} LibraryChange */

/** @typedef {{ uploadId: string, fileName: string, bytesReceived: number, totalBytes: number, stage: string }} ImportProgress */

/** @typedef {{ trackId: string, wasDuplicate: boolean }} ImportResult */

/** @typedef {{ name: string, role: Role }} Me */

export {};
