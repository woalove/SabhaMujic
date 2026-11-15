-- SabhaMujic schema v2: full-fidelity analysis.
--
-- track_analysis.details_json holds the complete analysis as produced by the audio engine (System.Text.Json of
-- SabhaMujic.Audio.Analysis.TrackAnalysis: seam correlation, fallback flag, tempo, gaps, exact frames). It is opaque to
-- the data layer; the columns from v1 stay authoritative for TrackDto and queries. NULL for rows written before v2 or
-- for non-Ready rows; readers then rebuild what they can from the columns.
ALTER TABLE track_analysis ADD COLUMN details_json TEXT NULL CHECK (details_json IS NULL OR json_valid(details_json));
