using Dapper;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Repositories;

namespace SabhaMujic.Data.Tests;

/// <summary>Migration 0003: tags (track_categories) from the single category_id, and no more pairing codes.</summary>
public sealed class SchemaV3Tests
{
    [Fact]
    public async Task A_version_2_database_keeps_every_category_as_a_tag()
    {
        using var dir = new TempDirectory();
        var db = new SqliteDatabase(Path.Combine(dir.Path, "v2.db"), new FakeClock());
        try
        {
            Assert.Equal(2, await db.MigrateAsync(SchemaMigrator.Embedded.Take(2).ToArray(), CancellationToken.None));
            Guid bhajan = Guid.NewGuid(), kirtan = Guid.NewGuid(), tagged = Guid.NewGuid(), alsoTagged = Guid.NewGuid(), untagged = Guid.NewGuid();
            await using (var conn = await db.OpenConnectionAsync())
            {
                // Written by v2 code: one category per track in tracks.category_id, and a pending pairing code.
                await conn.ExecuteAsync(
                    """
                    INSERT INTO categories (id, name, name_key, sort_order, created_at, updated_at) VALUES
                        (@Bhajan, 'Bhajan', 'BHAJAN', 0, 0, 0), (@Kirtan, 'Kirtan', 'KIRTAN', 1, 0, 0);
                    INSERT INTO tracks (id, content_hash, file_key, title, title_key, category_id, duration_seconds, size_bytes, added_at, updated_at) VALUES
                        (@Tagged, @H1, @H1 || '.mp3', 'Tagged', 'TAGGED', @Bhajan, 60, 1, 0, 0),
                        (@AlsoTagged, @H2, @H2 || '.mp3', 'Also tagged', 'ALSO TAGGED', @Kirtan, 60, 1, 0, 0),
                        (@Untagged, @H3, @H3 || '.mp3', 'Untagged', 'UNTAGGED', NULL, 60, 1, 0, 0);
                    INSERT INTO pairing_codes (id, code_hash, role, created_at, expires_at) VALUES (@Code, @H1, 0, 0, 1);
                    """,
                    new
                    {
                        Bhajan = bhajan, Kirtan = kirtan, Tagged = tagged, AlsoTagged = alsoTagged, Untagged = untagged, Code = Guid.NewGuid(),
                        H1 = TestDb.NewHash(), H2 = TestDb.NewHash(), H3 = TestDb.NewHash(),
                    });
            }

            Assert.Equal(3, await db.MigrateAsync());

            var tracks = new SqliteTrackRepository(db);
            Assert.Equal([bhajan], (await tracks.GetAsync(tagged))!.CategoryIds);
            Assert.Equal([kirtan], (await tracks.GetAsync(alsoTagged))!.CategoryIds);
            Assert.Empty((await tracks.GetAsync(untagged))!.CategoryIds);
            Assert.Equal([1, 1], (await new SqliteCategoryRepository(db).ListAsync()).Select(c => c.TrackCount));
            Assert.Equal(["Tagged"], (await tracks.QueryAsync(new TrackQuery(CategoryId: bhajan))).Items.Select(t => t.Title));

            await using (var conn = await db.OpenConnectionAsync())
            {
                // The old column is cleared and unused; the pairing codes are gone.
                Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tracks WHERE category_id IS NOT NULL;"));
                Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'pairing_codes';"));
                Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'ix_tracks_category';"));
            }
        }
        finally
        {
            db.ClearPool();
        }
    }

    [Fact]
    public async Task Deleting_a_track_removes_its_tags()
    {
        var t = new TestDb();
        await t.InitializeAsync();
        try
        {
            var cat = await t.Categories.CreateAsync("Dhun");
            var track = await t.AddTrackAsync("Song", categoryId: cat.Id);
            await t.Tracks.DeleteAsync(track.Id);
            Assert.Equal(0, (await t.Categories.GetAsync(cat.Id))!.TrackCount);
        }
        finally
        {
            await t.DisposeAsync();
        }
    }
}
