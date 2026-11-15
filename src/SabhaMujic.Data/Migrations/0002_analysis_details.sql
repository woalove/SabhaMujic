-- SabhaMujic schema v2: full-fidelity analysis and the playback position.
--
-- track_analysis.details_json holds the complete analysis as produced by the audio engine (System.Text.Json of
-- SabhaMujic.Audio.Analysis.TrackAnalysis: seam correlation, fallback flag, tempo, gaps, exact frames). It is opaque to
-- the data layer; the columns from v1 stay authoritative for TrackDto and queries. NULL for rows written before v2 or
-- for non-Ready rows; readers then rebuild what they can from the columns.
ALTER TABLE track_analysis ADD COLUMN details_json TEXT NULL CHECK (details_json IS NULL OR json_valid(details_json));

-- Position (seconds) inside the current queue item, saved on shutdown so playback can resume there after a restart.
ALTER TABLE playback_state ADD COLUMN position_seconds REAL NOT NULL DEFAULT 0 CHECK (position_seconds >= 0);
