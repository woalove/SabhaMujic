using SabhaMujic.Data.Storage;
using SabhaMujic.Server.Hosting;

namespace SabhaMujic.Server.Tests.Integration;

/// <summary>Cross-platform part of the data-root hardening: real folders only, links refused and never followed.</summary>
public sealed class DataRootGuardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sabhamujic-guard-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SabhaDataPaths Paths(string name = "root") => new(Path.Combine(_dir, name));

    [Fact]
    public void Creates_every_folder()
    {
        var paths = Paths();
        new DataRootGuard().Prepare(paths);
        new DataRootGuard().Prepare(paths); // idempotent
        foreach (var folder in new[] { paths.Root, paths.Data, paths.Library, paths.Uploads, paths.Certs, paths.Logs })
        {
            Assert.True(Directory.Exists(folder), folder);
        }
    }

    [Fact]
    public void A_linked_root_is_refused()
    {
        var target = Directory.CreateDirectory(Path.Combine(_dir, "elsewhere")).FullName;
        var paths = Paths();
        Directory.CreateSymbolicLink(paths.Root, target);

        var ex = Assert.Throws<DataRootException>(() => new DataRootGuard().Prepare(paths));
        Assert.Contains("junction or symbolic link", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target)); // nothing was created through the link
    }

    [Fact]
    public void A_linked_library_is_refused_and_its_target_untouched()
    {
        var target = Directory.CreateDirectory(Path.Combine(_dir, "user-readable")).FullName;
        File.WriteAllText(Path.Combine(target, "marker.txt"), "untouched");
        var paths = Paths();
        Directory.CreateDirectory(paths.Root);
        Directory.CreateSymbolicLink(paths.Library, target);

        Assert.Throws<DataRootException>(() => new DataRootGuard().Prepare(paths));
        Assert.Equal(["marker.txt"], Directory.EnumerateFileSystemEntries(target).Select(Path.GetFileName));
    }

    [Fact]
    public void A_file_in_place_of_a_folder_is_refused()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.Logs, "not a folder");
        Assert.Throws<DataRootException>(() => new DataRootGuard().Prepare(paths));
    }

    [Fact]
    public async Task The_host_refuses_to_start_on_a_linked_folder()
    {
        var target = Directory.CreateDirectory(Path.Combine(_dir, "target")).FullName;
        var paths = Paths();
        Directory.CreateDirectory(paths.Root);
        Directory.CreateSymbolicLink(paths.Data, target);

        await Assert.ThrowsAsync<DataRootException>(() => SabhaHost.StartAsync(new SabhaHostOptions { DataRoot = paths.Root, StartPipe = false }));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target)); // no database was written through the link
    }

    [Fact]
    public async Task The_store_refuses_a_link_planted_as_a_two_level_subfolder()
    {
        var library = Directory.CreateDirectory(Path.Combine(_dir, "library")).FullName;
        var store = new ProtectedStore(library, NoOpStoreAcl.Instance);
        var bytes = new byte[] { 1, 2, 3 };
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var target = Directory.CreateDirectory(Path.Combine(_dir, "redirect")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(library, hash[..2]), target);

        await Assert.ThrowsAsync<IOException>(() => store.StoreAsync(new MemoryStream(bytes), ".mp3"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));

        var linkedRoot = Path.Combine(_dir, "linked-library");
        Directory.CreateSymbolicLink(linkedRoot, target);
        Assert.Throws<IOException>(() => new ProtectedStore(linkedRoot, NoOpStoreAcl.Instance));
        Assert.Throws<IOException>(() => new UploadStaging(linkedRoot, NoOpStoreAcl.Instance));
    }
}
