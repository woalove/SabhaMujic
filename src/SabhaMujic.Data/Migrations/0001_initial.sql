-- SabhaMujic schema v1.
--
-- Representation conventions (every later migration must follow them):
--   * GUIDs       TEXT, lowercase 'D' format, e.g. '3f2504e0-4f89-41d3-9a0c-0305e82c3301'.
--   * Timestamps  INTEGER, Unix epoch milliseconds, UTC.
--   * Durations   REAL seconds (positions, trims, loop points, crossfades).
--   * Booleans    INTEGER 0/1.  Enums: INTEGER values of the SabhaMujic.Contracts enums.
--   * *_key       Case-folded copy of a name (Unicode NFKC + upper-invariant, computed in C#), used
--                 for case-insensitive uniqueness and search so non-ASCII text folds correctly too.
--   * Secrets     SecretHasher strings only. Session tokens and pairing codes: only the output of
--                 Core.Security.Tokens.HashToken (64 hex chars) is stored, never the token itself.
-- Dapper type handlers (SabhaMujic.Data.DapperTypeHandlers) map Guid and DateTimeOffset to these.
-- Migrations run inside a transaction; PRAGMA user_version holds the applied version.

-- ---------------------------------------------------------------- accounts & sessions
CREATE TABLE users (
    id            TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36 AND id = lower(id)),
    user_name     TEXT    NOT NULL COLLATE NOCASE CHECK (length(user_name) BETWEEN 1 AND 64),
    user_name_key TEXT    NOT NULL,
    role          INTEGER NOT NULL CHECK (role IN (0, 1)),
    secret_hash   TEXT    NOT NULL CHECK (length(secret_hash) > 0),
    created_at    INTEGER NOT NULL,
    updated_at    INTEGER NOT NULL
);
CREATE UNIQUE INDEX ux_users_user_name_key ON users (user_name_key);
CREATE INDEX ix_users_role ON users (role);

CREATE TABLE devices (
    id           TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36 AND id = lower(id)),
    name         TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    role         INTEGER NOT NULL CHECK (role IN (0, 1)),
    paired_at    INTEGER NOT NULL,
    last_seen_at INTEGER NULL,
    revoked_at   INTEGER NULL
);

CREATE TABLE sessions (
    id           TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36 AND id = lower(id)),
    token_hash   TEXT    NOT NULL CHECK (length(token_hash) = 64),
    user_id      TEXT    NULL REFERENCES users (id) ON DELETE CASCADE,
    device_id    TEXT    NULL REFERENCES devices (id) ON DELETE CASCADE,
    role         INTEGER NOT NULL CHECK (role IN (0, 1)),
    client_kind  INTEGER NOT NULL CHECK (client_kind IN (0, 1)),
    created_at   INTEGER NOT NULL,
    expires_at   INTEGER NOT NULL,
    last_seen_at INTEGER NOT NULL,
    revoked_at   INTEGER NULL,
    CHECK (user_id IS NOT NULL OR device_id IS NOT NULL)
);
CREATE UNIQUE INDEX ux_sessions_token_hash ON sessions (token_hash);
CREATE INDEX ix_sessions_user ON sessions (user_id);
CREATE INDEX ix_sessions_device ON sessions (device_id);
CREATE INDEX ix_sessions_expires ON sessions (expires_at);

CREATE TABLE pairing_codes (
    id          TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36 AND id = lower(id)),
    code_hash   TEXT    NOT NULL CHECK (length(code_hash) = 64),
    role        INTEGER NOT NULL CHECK (role IN (0, 1)),
    created_by  TEXT    NULL REFERENCES users (id) ON DELETE SET NULL,
    created_at  INTEGER NOT NULL,
    expires_at  INTEGER NOT NULL,
    consumed_at INTEGER NULL
);
CREATE INDEX ix_pairing_codes_code_hash ON pairing_codes (code_hash);

-- ---------------------------------------------------------------- library
CREATE TABLE categories (
    id         TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36 AND id = lower(id)),
    name       TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    name_key   TEXT    NOT NULL,
    sort_order INTEGER NOT NULL,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);
CREATE UNIQUE INDEX ux_categories_name_key ON categories (name_key);
CREATE INDEX ix_categories_sort ON categories (sort_order, name_key);

CREATE TABLE tracks (
    id                 TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36 AND id = lower(id)),
    content_hash       TEXT    NOT NULL CHECK (length(content_hash) = 64 AND content_hash = lower(content_hash)),
    file_key           TEXT    NOT NULL,
    title              TEXT    NOT NULL CHECK (length(title) BETWEEN 1 AND 300),
    title_key          TEXT    NOT NULL,
    artist             TEXT    NULL CHECK (artist IS NULL OR length(artist) BETWEEN 1 AND 300),
    artist_key         TEXT    NULL,
    category_id        TEXT    NULL REFERENCES categories (id) ON DELETE SET NULL,
    duration_seconds   REAL    NOT NULL DEFAULT 0 CHECK (duration_seconds >= 0),
    size_bytes         INTEGER NOT NULL CHECK (size_bytes >= 0),
    original_file_name TEXT    NULL,
    added_by           TEXT    NULL REFERENCES users (id) ON DELETE SET NULL,
    added_at           INTEGER NOT NULL,
    updated_at         INTEGER NOT NULL
);
CREATE UNIQUE INDEX ux_tracks_content_hash ON tracks (content_hash);
CREATE UNIQUE INDEX ux_tracks_file_key ON tracks (file_key);
CREATE INDEX ix_tracks_title_key ON tracks (title_key);
CREATE INDEX ix_tracks_artist_key ON tracks (artist_key);
CREATE INDEX ix_tracks_category ON tracks (category_id, title_key);

-- Automatic analysis result. Replaced on re-analysis.
CREATE TABLE track_analysis (
    track_id               TEXT    NOT NULL PRIMARY KEY REFERENCES tracks (id) ON DELETE CASCADE,
    status                 INTEGER NOT NULL CHECK (status IN (0, 1, 2, 3)),
    analysis_version       INTEGER NOT NULL CHECK (analysis_version >= 0),
    trim_start_seconds     REAL    NULL CHECK (trim_start_seconds IS NULL OR trim_start_seconds >= 0),
    trim_end_seconds       REAL    NULL CHECK (trim_end_seconds IS NULL OR trim_end_seconds >= 0),
    loop_start_seconds     REAL    NULL CHECK (loop_start_seconds IS NULL OR loop_start_seconds >= 0),
    loop_end_seconds       REAL    NULL,
    loop_crossfade_seconds REAL    NULL CHECK (loop_crossfade_seconds IS NULL OR loop_crossfade_seconds >= 0),
    confidence             REAL    NULL CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),
    error                  TEXT    NULL,
    updated_at             INTEGER NOT NULL,
    CHECK ((loop_start_seconds IS NULL) = (loop_end_seconds IS NULL)),
    CHECK (loop_start_seconds IS NULL OR loop_end_seconds > loop_start_seconds)
);
CREATE INDEX ix_track_analysis_status ON track_analysis (status, analysis_version);

-- Admin loop override. Kept separate so re-analysis never wipes it.
CREATE TABLE track_loop_overrides (
    track_id          TEXT    NOT NULL PRIMARY KEY REFERENCES tracks (id) ON DELETE CASCADE,
    start_seconds     REAL    NOT NULL CHECK (start_seconds >= 0),
    end_seconds       REAL    NOT NULL,
    crossfade_seconds REAL    NOT NULL CHECK (crossfade_seconds >= 0),
    updated_by        TEXT    NULL REFERENCES users (id) ON DELETE SET NULL,
    updated_at        INTEGER NOT NULL,
    CHECK (end_seconds > start_seconds)
);

-- ---------------------------------------------------------------- playlists
CREATE TABLE playlists (
    id         TEXT    NOT NULL PRIMARY KEY CHECK (length(id) = 36 AND id = lower(id)),
    name       TEXT    NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    created_by TEXT    NULL REFERENCES users (id) ON DELETE SET NULL,
    created_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL
);

CREATE TABLE playlist_items (
    playlist_id TEXT    NOT NULL REFERENCES playlists (id) ON DELETE CASCADE,
    position    INTEGER NOT NULL CHECK (position >= 0),
    track_id    TEXT    NOT NULL REFERENCES tracks (id) ON DELETE CASCADE,
    PRIMARY KEY (playlist_id, position)
) WITHOUT ROWID;
CREATE INDEX ix_playlist_items_track ON playlist_items (track_id);

-- ---------------------------------------------------------------- playback (restored after a service restart)
CREATE TABLE playback_state (
    id                    INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
    volume                REAL    NOT NULL CHECK (volume >= 0 AND volume <= 1),
    muted                 INTEGER NOT NULL CHECK (muted IN (0, 1)),
    loop_mode             INTEGER NOT NULL CHECK (loop_mode IN (0, 1, 2)),
    crossfade_seconds     REAL    NOT NULL CHECK (crossfade_seconds >= 0 AND crossfade_seconds <= 12),
    current_index         INTEGER NOT NULL CHECK (current_index >= -1),
    current_queue_item_id TEXT    NULL,
    updated_at            INTEGER NOT NULL
);
INSERT INTO playback_state (id, volume, muted, loop_mode, crossfade_seconds, current_index, current_queue_item_id, updated_at)
VALUES (1, 0.8, 0, 0, 6.0, -1, NULL, 0);

CREATE TABLE playback_queue (
    position      INTEGER NOT NULL PRIMARY KEY CHECK (position >= 0),
    queue_item_id TEXT    NOT NULL CHECK (length(queue_item_id) = 36 AND queue_item_id = lower(queue_item_id)),
    track_id      TEXT    NOT NULL REFERENCES tracks (id) ON DELETE CASCADE
);
CREATE UNIQUE INDEX ux_playback_queue_item ON playback_queue (queue_item_id);
CREATE INDEX ix_playback_queue_track ON playback_queue (track_id);

-- ---------------------------------------------------------------- settings & audit
CREATE TABLE settings (
    key        TEXT    NOT NULL PRIMARY KEY CHECK (length(key) BETWEEN 1 AND 200),
    value      TEXT    NOT NULL,
    updated_at INTEGER NOT NULL
);

CREATE TABLE audit_log (
    id          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    occurred_at INTEGER NOT NULL,
    actor       TEXT    NOT NULL,
    client_kind INTEGER NULL CHECK (client_kind IS NULL OR client_kind IN (0, 1)),
    action      TEXT    NOT NULL,
    target      TEXT    NULL,
    details     TEXT    NULL
);
CREATE INDEX ix_audit_log_occurred ON audit_log (occurred_at);
