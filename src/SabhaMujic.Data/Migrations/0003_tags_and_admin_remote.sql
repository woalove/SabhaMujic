-- SabhaMujic schema v3 (contract v2): tags, and the admin-only remote.
--
-- Tags: a track can belong to several categories. track_categories is the many-to-many link; deleting a track or a
-- category removes its links (ON DELETE CASCADE). The single tracks.category_id of v1/v2 is copied into it and then
-- cleared. The column itself stays: SQLite cannot drop a column that has a foreign key and an index without rebuilding
-- the table, so it is simply no longer read or written (always NULL from here on) and ix_tracks_category goes.
CREATE TABLE track_categories (
    track_id    TEXT NOT NULL REFERENCES tracks (id) ON DELETE CASCADE,
    category_id TEXT NOT NULL REFERENCES categories (id) ON DELETE CASCADE,
    PRIMARY KEY (track_id, category_id)
) WITHOUT ROWID;
CREATE INDEX ix_track_categories_category ON track_categories (category_id, track_id);

INSERT INTO track_categories (track_id, category_id)
SELECT id, category_id FROM tracks WHERE category_id IS NOT NULL;

UPDATE tracks SET category_id = NULL WHERE category_id IS NOT NULL;
DROP INDEX ix_tracks_category;

-- The remote no longer pairs phones with one-time codes (administrators sign in with their password; a remembered
-- phone is a row in devices), so the pairing codes go. Existing devices and their sessions are kept: the admin sees
-- them in the Devices list, and those paired as Volunteer are refused by the remote until revoked.
DROP TABLE pairing_codes;
