using System.Security.Cryptography;
using SabhaMujic.Contracts;
using SabhaMujic.Data.Storage;

namespace SabhaMujic.Data.Tests;

public sealed class ProtectedStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly RecordingAcl _acl = new();
    private readonly ProtectedStore _store;

    public ProtectedStoreTests() => _store = new ProtectedStore(Path.Combine(_dir.Path, "Library"), _acl);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Acl_is_applied_to_the_root_when_the_store_is_created()
    {
        Assert.Equal([_store.RootDirectory], _acl.Secured);
        Assert.True(Directory.Exists(_store.StagingDirectory));
    }

    [Fact]
    public async Task Stores_content_addressed_in_two_level_layout()
    {
        var bytes = RandomNumberGenerator.GetBytes(300_000);
        var stored = await _store.StoreAsync(new MemoryStream(bytes), ".MP3");

        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal(new StoredFile(hash + ".mp3", hash, bytes.Length, AlreadyExisted: false), stored);
        var path = _store.GetPhysicalPath(stored.FileKey);
        Assert.Equal(Path.Combine(_store.RootDirectory, hash[..2], hash + ".mp3"), path);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.True(_store.Exists(stored.FileKey));
        Assert.Empty(Directory.EnumerateFiles(_store.StagingDirectory));
    }

    [Fact]
    public async Task Identical_content_stored_twice_gives_one_file_and_the_same_key()
    {
        var bytes = RandomNumberGenerator.GetBytes(10_000);
        var first = await _store.StoreAsync(new MemoryStream(bytes), "mp3");
        var second = await _store.StoreAsync(new MemoryStream(bytes), ".mp3");

        Assert.Equal(first.FileKey, second.FileKey);
        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
        var files = Directory.EnumerateFiles(_store.RootDirectory, "*", SearchOption.AllDirectories).ToArray();
        Assert.Single(files);
        Assert.Empty(Directory.EnumerateFiles(_store.StagingDirectory));
    }

    [Fact]
    public async Task Concurrent_stores_of_identical_content_leave_one_file()
    {
        var bytes = RandomNumberGenerator.GetBytes(50_000);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _store.StoreAsync(new MemoryStream(bytes), ".wav"))));
        Assert.Single(results.Select(r => r.FileKey).Distinct());
        Assert.Single(Directory.EnumerateFiles(_store.RootDirectory, "*", SearchOption.AllDirectories));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(_store.GetPhysicalPath(results[0].FileKey)));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("\\\\server\\share\\x.mp3")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde.mp3")] // 63 hex chars
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0.mp3")] // 65 hex chars
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF.mp3")] // uppercase
    [InlineData("0123456789abcdeg0123456789abcdef0123456789abcdef0123456789abcdef.mp3")] // non-hex
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")] // no extension
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.MP3")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.mp3\n")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.mp3/../../x")]
    [InlineData("01/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.mp3")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.toolongext")]
    public async Task Malformed_or_traversal_keys_are_rejected(string key)
    {
        Assert.False(ProtectedStore.IsValidFileKey(key));
        AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _store.GetPhysicalPath(key));
        AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _store.Exists(key));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _store.DeleteAsync(key));
    }

    [Fact]
    public void Null_key_is_rejected()
    {
        Assert.False(ProtectedStore.IsValidFileKey(null));
        AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _store.GetPhysicalPath(null!));
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData("")]
    [InlineData(".mp3/../x")]
    [InlineData(".ogg")]
    public async Task Disallowed_extensions_are_rejected(string extension)
    {
        await AssertEx.ThrowsCode(ErrorCode.UnsupportedFormat, () => _store.StoreAsync(new MemoryStream([1, 2, 3]), extension));
        Assert.Empty(Directory.EnumerateFiles(_store.RootDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task An_interrupted_store_leaves_nothing_in_place()
    {
        var failing = new FailingStream(RandomNumberGenerator.GetBytes(500_000), failAfter: 200_000);
        await Assert.ThrowsAsync<IOException>(() => _store.StoreAsync(failing, ".mp3"));

        Assert.Empty(Directory.EnumerateFiles(_store.RootDirectory, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(_store.StagingDirectory));
    }

    [Fact]
    public async Task A_cancelled_store_leaves_nothing_in_place()
    {
        using var cts = new CancellationTokenSource();
        var stream = new FailingStream(RandomNumberGenerator.GetBytes(500_000), failAfter: int.MaxValue, onRead: cts.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.StoreAsync(stream, ".mp3", cts.Token));
        Assert.Empty(Directory.EnumerateFiles(_store.RootDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Stale_staging_files_are_removed_at_startup()
    {
        File.WriteAllBytes(Path.Combine(_store.StagingDirectory, "crashed.tmp"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_store.StagingDirectory, "other.tmp"), [4]);

        var acl = new RecordingAcl();
        var restarted = new ProtectedStore(_store.RootDirectory, acl);

        Assert.Empty(Directory.EnumerateFiles(restarted.StagingDirectory));
        Assert.Single(acl.Secured);
    }

    [Fact]
    public async Task Delete_removes_the_file()
    {
        var stored = await _store.StoreAsync(new MemoryStream([9, 8, 7]), ".flac");
        Assert.True(await _store.DeleteAsync(stored.FileKey));
        Assert.False(_store.Exists(stored.FileKey));
        Assert.False(await _store.DeleteAsync(stored.FileKey));
    }

    [Fact]
    public void A_failing_acl_prevents_the_store_from_opening()
    {
        Assert.Throws<UnauthorizedAccessException>(() => new ProtectedStore(Path.Combine(_dir.Path, "Other"), new ThrowingAcl()));
    }

    private sealed class RecordingAcl : IStoreAcl
    {
        public List<string> Secured { get; } = [];

        public void Secure(string rootDirectory) => Secured.Add(rootDirectory);
    }

    private sealed class ThrowingAcl : IStoreAcl
    {
        public void Secure(string rootDirectory) => throw new UnauthorizedAccessException("cannot set ACL");
    }

    /// <summary>Returns data in small reads and throws (or runs a callback) part-way through.</summary>
    private sealed class FailingStream(byte[] data, int failAfter, Action? onRead = null) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= failAfter)
            {
                throw new IOException("Connection lost mid-upload.");
            }

            onRead?.Invoke();
            var n = Math.Min(Math.Min(count, 16_384), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= failAfter)
            {
                throw new IOException("Connection lost mid-upload.");
            }

            onRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var n = Math.Min(Math.Min(buffer.Length, 16_384), data.Length - _position);
            data.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return ValueTask.FromResult(n);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class UploadStagingTests : IDisposable
{
    private const string Owner = "session-a";
    private readonly TempDirectory _dir = new();
    private readonly FakeClock _clock = new();
    private readonly UploadStaging _staging;

    public UploadStagingTests() => _staging = new UploadStaging(Path.Combine(_dir.Path, "uploads"), NoOpStoreAcl.Instance, _clock);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Happy_path_produces_the_exact_bytes()
    {
        var bytes = RandomNumberGenerator.GetBytes(AdminLimits.MaxChunkBytes * 2 + 12_345);
        var id = _staging.Begin("C:\\Users\\ram\\Music\\Jay Swaminarayan.MP3", bytes.Length, Owner);

        long offset = 0;
        while (offset < bytes.Length)
        {
            var size = (int)Math.Min(AdminLimits.MaxChunkBytes, bytes.Length - offset);
            var status = await _staging.AppendChunkAsync(id, Owner, offset, bytes.AsMemory((int)offset, size));
            offset += size;
            Assert.Equal(offset, status.BytesReceived);
        }

        using var done = _staging.Complete(id, Owner);
        Assert.Equal("Jay Swaminarayan.mp3", done.FileName);
        Assert.Equal(".mp3", done.Extension);
        Assert.Equal(bytes.Length, done.Length);
        await using (var read = done.OpenRead())
        using (var copy = new MemoryStream())
        {
            await read.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
        }

        Assert.Equal(0, _staging.ActiveCount);
        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => _staging.AppendChunkAsync(id, Owner, bytes.Length, new byte[1]));
        var path = done.TempPath;
        done.Dispose();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Completed_upload_can_be_stored_in_the_protected_store()
    {
        var bytes = RandomNumberGenerator.GetBytes(4096);
        var id = _staging.Begin("a.wav", bytes.Length, Owner);
        await _staging.AppendChunkAsync(id, Owner, 0, bytes);
        using var done = _staging.Complete(id, Owner);
        var store = new ProtectedStore(Path.Combine(_dir.Path, "Library"), NoOpStoreAcl.Instance);
        await using var content = done.OpenRead();
        var stored = await store.StoreAsync(content, done.Extension);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(store.GetPhysicalPath(stored.FileKey)));
    }

    [Fact]
    public async Task Out_of_order_offsets_are_rejected()
    {
        var id = _staging.Begin("a.mp3", 100, Owner);
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _staging.AppendChunkAsync(id, Owner, 10, new byte[10]));
        await _staging.AppendChunkAsync(id, Owner, 0, new byte[10]);
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _staging.AppendChunkAsync(id, Owner, 0, new byte[10])); // replay
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _staging.AppendChunkAsync(id, Owner, 20, new byte[10])); // gap
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _staging.AppendChunkAsync(id, Owner, -1, new byte[10]));
        await AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _staging.AppendChunkAsync(id, Owner, 10, Array.Empty<byte>()));
        Assert.Equal(10, _staging.GetStatus(id, Owner).BytesReceived);
    }

    [Fact]
    public async Task Oversized_chunk_is_rejected()
    {
        var id = _staging.Begin("a.mp3", AdminLimits.MaxChunkBytes * 3L, Owner);
        await AssertEx.ThrowsCode(ErrorCode.TooLarge, () => _staging.AppendChunkAsync(id, Owner, 0, new byte[AdminLimits.MaxChunkBytes + 1]));
        Assert.Equal(0, _staging.GetStatus(id, Owner).BytesReceived);
    }

    [Fact]
    public async Task Total_overflow_is_rejected()
    {
        var id = _staging.Begin("a.mp3", 100, Owner);
        await _staging.AppendChunkAsync(id, Owner, 0, new byte[60]);
        await AssertEx.ThrowsCode(ErrorCode.TooLarge, () => _staging.AppendChunkAsync(id, Owner, 60, new byte[41]));
        await _staging.AppendChunkAsync(id, Owner, 60, new byte[40]);
        await AssertEx.ThrowsCode(ErrorCode.TooLarge, () => _staging.AppendChunkAsync(id, Owner, 100, new byte[1]));
        using var done = _staging.Complete(id, Owner);
        Assert.Equal(100, new FileInfo(done.TempPath).Length);
    }

    [Fact]
    public async Task Another_session_cannot_touch_the_upload()
    {
        var id = _staging.Begin("a.mp3", 10, Owner);
        await AssertEx.ThrowsCode(ErrorCode.Forbidden, () => _staging.AppendChunkAsync(id, "session-b", 0, new byte[10]));
        AssertEx.ThrowsCode(ErrorCode.Forbidden, () => _staging.Complete(id, "session-b"));
        AssertEx.ThrowsCode(ErrorCode.Forbidden, () => _staging.Cancel(id, "session-b"));
        AssertEx.ThrowsCode(ErrorCode.Forbidden, () => _staging.GetStatus(id, "session-b"));
        await _staging.AppendChunkAsync(id, Owner, 0, new byte[10]);
        using var done = _staging.Complete(id, Owner);
    }

    [Theory]
    [InlineData("song.exe")]
    [InlineData("song.mp3.exe")]
    [InlineData("song")]
    [InlineData("song.ogg")]
    [InlineData("song.mp3\u202Eexe.mp3x")]
    public void Disallowed_extensions_are_rejected(string fileName)
    {
        AssertEx.ThrowsCode(ErrorCode.UnsupportedFormat, () => _staging.Begin(fileName, 10, Owner));
        Assert.Equal(0, _staging.ActiveCount);
    }

    [Fact]
    public void Size_limits_on_begin()
    {
        AssertEx.ThrowsCode(ErrorCode.TooLarge, () => _staging.Begin("a.mp3", AdminLimits.MaxUploadBytes + 1, Owner));
        AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _staging.Begin("a.mp3", 0, Owner));
        AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _staging.Begin("a.mp3", 10, ""));
        AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _staging.Begin("  ", 10, Owner));
        _staging.Begin("a.mp3", AdminLimits.MaxUploadBytes, Owner);
    }

    [Fact]
    public void Too_many_concurrent_uploads_per_session_are_rejected()
    {
        var ids = Enumerable.Range(0, AdminLimits.MaxConcurrentUploadsPerSession).Select(i => _staging.Begin($"{i}.mp3", 10, Owner)).ToList();
        AssertEx.ThrowsCode(ErrorCode.RateLimited, () => _staging.Begin("x.mp3", 10, Owner));
        _staging.Begin("other.mp3", 10, "session-b"); // other sessions are independent

        _staging.Cancel(ids[0], Owner);
        _staging.Begin("x.mp3", 10, Owner);
    }

    [Fact]
    public void Total_concurrent_uploads_are_capped()
    {
        var staging = new UploadStaging(Path.Combine(_dir.Path, "capped"), NoOpStoreAcl.Instance, _clock, maxConcurrentUploads: 3);
        staging.Begin("a.mp3", 10, "s1");
        staging.Begin("b.mp3", 10, "s2");
        staging.Begin("c.mp3", 10, "s3");
        AssertEx.ThrowsCode(ErrorCode.RateLimited, () => staging.Begin("d.mp3", 10, "s4"));
    }

    [Fact]
    public async Task Incomplete_upload_cannot_be_completed()
    {
        var id = _staging.Begin("a.mp3", 100, Owner);
        AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _staging.Complete(id, Owner));
        await _staging.AppendChunkAsync(id, Owner, 0, new byte[99]);
        AssertEx.ThrowsCode(ErrorCode.InvalidArgument, () => _staging.Complete(id, Owner));
        await _staging.AppendChunkAsync(id, Owner, 99, new byte[1]);
        using var done = _staging.Complete(id, Owner);
        AssertEx.ThrowsCode(ErrorCode.NotFound, () => _staging.Complete(id, Owner));
    }

    [Fact]
    public async Task Unknown_and_cancelled_uploads_are_not_found()
    {
        AssertEx.ThrowsCode(ErrorCode.NotFound, () => _staging.Complete(Guid.NewGuid(), Owner));
        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => _staging.AppendChunkAsync(Guid.NewGuid(), Owner, 0, new byte[1]));

        var id = _staging.Begin("a.mp3", 100, Owner);
        await _staging.AppendChunkAsync(id, Owner, 0, new byte[50]);
        _staging.Cancel(id, Owner);
        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => _staging.AppendChunkAsync(id, Owner, 50, new byte[1]));
        AssertEx.ThrowsCode(ErrorCode.NotFound, () => _staging.Cancel(id, Owner));
        Assert.Empty(Directory.EnumerateFiles(_staging.StagingDirectory));
    }

    [Fact]
    public async Task Abandoned_uploads_expire_after_the_idle_timeout()
    {
        var idle = _staging.Begin("idle.mp3", 100, Owner);
        var active = _staging.Begin("active.mp3", 100, Owner);
        _clock.Advance(TimeSpan.FromMinutes(20));
        await _staging.AppendChunkAsync(active, Owner, 0, new byte[10]);
        _clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(1, _staging.ExpireAbandoned());
        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => _staging.AppendChunkAsync(idle, Owner, 0, new byte[10]));
        await _staging.AppendChunkAsync(active, Owner, 10, new byte[10]);
        Assert.Single(Directory.EnumerateFiles(_staging.StagingDirectory));

        _clock.Advance(TimeSpan.FromMinutes(31));
        await AssertEx.ThrowsCode(ErrorCode.NotFound, () => _staging.AppendChunkAsync(active, Owner, 20, new byte[10]));
        Assert.Equal(0, _staging.ActiveCount);
        Assert.Empty(Directory.EnumerateFiles(_staging.StagingDirectory));
    }

    [Fact]
    public void Cancel_all_for_owner()
    {
        _staging.Begin("a.mp3", 10, Owner);
        _staging.Begin("b.mp3", 10, Owner);
        _staging.Begin("c.mp3", 10, "session-b");
        Assert.Equal(2, _staging.CancelAllForOwner(Owner));
        Assert.Equal(1, _staging.ActiveCount);
    }

    [Fact]
    public void Leftover_files_are_removed_at_startup_and_acl_is_applied()
    {
        var dir = Path.Combine(_dir.Path, "restart");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "old.part"), [1]);
        var secured = new List<string>();
        var staging = new UploadStaging(dir, new LambdaAcl(secured.Add), _clock);
        Assert.Empty(Directory.EnumerateFiles(dir));
        Assert.Equal([staging.StagingDirectory], secured);
    }

    [Fact]
    public async Task Concurrent_appends_to_one_upload_accept_each_offset_once()
    {
        var id = _staging.Begin("a.mp3", 10, Owner);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            try
            {
                await _staging.AppendChunkAsync(id, Owner, 0, new byte[5]);
                return true;
            }
            catch (SabhaMujicException ex) when (ex.Code == ErrorCode.InvalidArgument)
            {
                return false;
            }
        })));
        Assert.Single(attempts, a => a);
        Assert.Equal(5, _staging.GetStatus(id, Owner).BytesReceived);
    }

    [Theory]
    [InlineData("../../etc/passwd.mp3", "passwd.mp3")]
    [InlineData("..\\..\\Windows\\evil.wav", "evil.wav")]
    [InlineData("a<b>c:d\"e|f?g*h.flac", "abcdefgh.flac")]
    [InlineData("tab\there\u0000.m4a", "tabhere.m4a")]
    [InlineData(".mp3", "upload.mp3")]
    [InlineData("  spaced  .MP3 ", "spaced.mp3")]
    [InlineData("trailing.mp3...", "trailing.mp3")]
    [InlineData("bidi\u202Egnp.mp3", "bidignp.mp3")]
    public void File_names_are_sanitized(string input, string expected)
    {
        Assert.Equal(expected, UploadStaging.SanitizeFileName(input).Name);
    }

    [Fact]
    public void Long_file_names_are_truncated_keeping_the_extension()
    {
        var (name, ext) = UploadStaging.SanitizeFileName(new string('a', 500) + ".wma");
        Assert.Equal(UploadStaging.MaxFileNameLength, name.Length);
        Assert.EndsWith(".wma", name);
        Assert.Equal(".wma", ext);
    }

    private sealed class LambdaAcl(Action<string> secure) : IStoreAcl
    {
        public void Secure(string rootDirectory) => secure(rootDirectory);
    }
}
