using SabhaMujic.Contracts;
using SabhaMujic.Desktop.Tests.Fakes;
using SabhaMujic.Desktop.ViewModels.Admin;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.Tests;

public class ImportChunkerTests
{
    [Fact]
    public void Plans_exact_offsets_and_a_short_last_chunk()
    {
        Assert.Equal([(0L, 4), (4L, 4), (8L, 2)], ImportChunker.Plan(10, 4).ToArray());
    }

    [Fact]
    public void Exact_multiple_has_no_empty_trailing_chunk()
    {
        Assert.Equal([(0L, 5), (5L, 5)], ImportChunker.Plan(10, 5).ToArray());
    }

    [Fact]
    public void Max_chunk_size_boundaries()
    {
        const int max = AdminLimits.MaxChunkBytes;
        Assert.Equal([(0L, max)], ImportChunker.Plan(max, max).ToArray());
        Assert.Equal([(0L, max), ((long)max, 1)], ImportChunker.Plan(max + 1L, max).ToArray());
        Assert.Empty(ImportChunker.Plan(0, max));

        // 1 GiB (the upload limit) is exactly 1024 chunks; offsets stay exact in 64-bit.
        var plan = ImportChunker.Plan(AdminLimits.MaxUploadBytes, max).ToArray();
        Assert.Equal(1024, plan.Length);
        Assert.Equal((AdminLimits.MaxUploadBytes - max, max), plan[^1]);
    }

    [Theory]
    [InlineData("/music/Morning_Dhun.mp3", "Morning Dhun")]
    [InlineData("/x/Aarti.Sandhya.flac", "Aarti.Sandhya")]
    [InlineData("track.wav", "track")]
    public void Default_title_comes_from_the_file_name(string path, string title)
    {
        Assert.Equal(title, ImportChunker.DefaultTitle(path));
    }
}

public class ImportViewModelTests
{
    private readonly FakeSabhaClient _client = new();
    private readonly TestUi _ui = new();

    private ImportViewModel Create(int chunkSize = 4) => new(_client, _ui.Services) { ChunkSize = chunkSize };

    [Fact]
    public async Task Uploads_in_chunks_with_exact_offsets_and_data_then_completes_with_metadata()
    {
        var category = new CategoryDto(Guid.NewGuid(), "Dhun", 0, 0);
        var vm = Create(chunkSize: 4);
        vm.SetCategories([category]);
        vm.DefaultCategory = vm.CategoryOptions[1];
        _ui.FilePicker.Paths.Add(_ui.FileSystem.Add("/music/Morning_Dhun.mp3", 10));

        await vm.AddFilesCommand.ExecuteAsync(null);
        var file = Assert.Single(vm.Files);
        Assert.Equal("Morning Dhun", file.Title);
        file.Artist = "  Choir ";
        await vm.StartCommand.ExecuteAsync(null);

        var begin = _client.CallsTo("BeginImportAsync").Single();
        Assert.Equal(["Morning_Dhun.mp3", 10L], begin.Args);
        var chunks = _client.CallsTo("UploadChunkAsync").ToArray();
        Assert.Equal([0L, 4L, 8L], chunks.Select(c => (long)c.Args[1]!).ToArray());
        Assert.Equal([4, 4, 2], chunks.Select(c => ((byte[])c.Args[2]!).Length).ToArray());
        Assert.Equal(_ui.FileSystem.Files["/music/Morning_Dhun.mp3"], chunks.SelectMany(c => (byte[])c.Args[2]!).ToArray());
        Assert.All(chunks, c => Assert.Equal(file.UploadId, c.Args[0]));

        var meta = (ImportMetadata)_client.CallsTo("CompleteImportAsync").Single().Args[1]!;
        Assert.Equal(new ImportMetadata("Morning Dhun", "Choir", category.Id), meta);
        Assert.Equal(ImportFileStatus.Imported, file.Status);
        Assert.Equal(100, file.Percent);
        Assert.Equal(100, vm.OverallPercent);
        Assert.Equal("1 imported.", vm.StatusMessage);
        Assert.Empty(_client.CallsTo("CancelImportAsync"));
    }

    [Fact]
    public async Task Default_chunk_size_is_the_contract_maximum()
    {
        var vm = new ImportViewModel(_client, _ui.Services);
        Assert.Equal(AdminLimits.MaxChunkBytes, vm.ChunkSize);
        Assert.Equal(AdminLimits.MaxChunkBytes, new ImportViewModel(_client, _ui.Services) { ChunkSize = int.MaxValue }.ChunkSize);

        vm.AddFiles([_ui.FileSystem.Add("big.wav", (2 * AdminLimits.MaxChunkBytes) + 1)]);
        await vm.StartCommand.ExecuteAsync(null);

        var chunks = _client.CallsTo("UploadChunkAsync").ToArray();
        Assert.Equal([0L, AdminLimits.MaxChunkBytes, 2L * AdminLimits.MaxChunkBytes], chunks.Select(c => (long)c.Args[1]!).ToArray());
        Assert.Single((byte[])chunks[^1].Args[2]!);
        Assert.All(chunks, c => Assert.True(((byte[])c.Args[2]!).Length <= AdminLimits.MaxChunkBytes));
    }

    [Fact]
    public async Task Cancel_stops_the_upload_calls_cancel_import_and_skips_remaining_files()
    {
        var vm = Create(chunkSize: 4);
        vm.AddFiles([_ui.FileSystem.Add("a.mp3", 12), _ui.FileSystem.Add("b.mp3", 12)]);
        _client.Admin.OnChunk = (_, offset, _) =>
        {
            if (offset == 4)
            {
                vm.CancelCommand.Execute(null); // user clicks Cancel during the second chunk
            }

            return Task.CompletedTask;
        };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(2, _client.CallsTo("UploadChunkAsync").Count());
        var uploadId = vm.Files[0].UploadId;
        Assert.Equal(uploadId, _client.CallsTo("CancelImportAsync").Single().Args[0]);
        Assert.Empty(_client.CallsTo("CompleteImportAsync"));
        Assert.Single(_client.CallsTo("BeginImportAsync")); // the second file never started
        Assert.All(vm.Files, f => Assert.Equal(ImportFileStatus.Cancelled, f.Status));
        Assert.False(vm.IsImporting);
        Assert.Equal("2 cancelled.", vm.StatusMessage);
    }

    [Fact]
    public async Task An_error_on_one_file_does_not_stop_the_others()
    {
        var vm = Create();
        vm.AddFiles([_ui.FileSystem.Add("bad.mp3", 5), _ui.FileSystem.Add("good.mp3", 5)]);
        _client.Admin.OnComplete = (id, meta) => meta.Title == "bad"
            ? throw new SabhaMujicException(ErrorCode.UnsupportedFormat, "not audio")
            : new ImportResultDto(Guid.NewGuid(), false);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(ImportFileStatus.Failed, vm.Files[0].Status);
        Assert.True(vm.Files[0].IsError);
        Assert.Equal(ErrorMessages.For(ErrorCode.UnsupportedFormat), vm.Files[0].Message);
        Assert.Equal(ImportFileStatus.Imported, vm.Files[1].Status);
        Assert.Equal(vm.Files[0].UploadId, _client.CallsTo("CancelImportAsync").Single().Args[0]); // server cleanup
        Assert.Equal("1 imported, 1 failed.", vm.StatusMessage);
    }

    [Fact]
    public async Task Begin_failure_is_reported_without_cancel()
    {
        var vm = Create();
        vm.AddFiles([_ui.FileSystem.Add("a.mp3", 5), _ui.FileSystem.Add("b.mp3", 5)]);
        _client.Admin.OnBegin = (name, _) => name == "a.mp3"
            ? throw new SabhaMujicException(ErrorCode.TooLarge, "")
            : Guid.NewGuid();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(ImportFileStatus.Failed, vm.Files[0].Status);
        Assert.Equal(ImportFileStatus.Imported, vm.Files[1].Status);
        Assert.Empty(_client.CallsTo("CancelImportAsync"));
    }

    [Fact]
    public async Task Duplicate_is_shown_as_information()
    {
        var vm = Create();
        vm.AddFiles([_ui.FileSystem.Add("again.mp3", 5)]);
        _client.Admin.OnComplete = (_, _) => new ImportResultDto(Guid.NewGuid(), WasDuplicate: true);

        await vm.StartCommand.ExecuteAsync(null);

        var file = vm.Files.Single();
        Assert.Equal(ImportFileStatus.Duplicate, file.Status);
        Assert.True(file.IsInfo);
        Assert.False(file.IsError);
        Assert.Contains("already in the library", file.Message);
        Assert.Equal("1 already in library.", vm.StatusMessage);
    }

    [Fact]
    public async Task Files_that_cannot_be_imported_are_rejected_locally()
    {
        var vm = Create();
        _ui.FileSystem.Add("notes.txt", 5);
        _ui.FileSystem.Add("empty.mp3", 0);

        vm.AddFiles(["notes.txt", "empty.mp3", "missing.mp3"]);
        await vm.StartCommand.ExecuteAsync(null);

        Assert.All(vm.Files, f => Assert.Equal(ImportFileStatus.Failed, f.Status));
        Assert.Equal(ErrorMessages.For(ErrorCode.UnsupportedFormat), vm.Files[0].Message);
        Assert.Empty(_client.CallsTo("BeginImportAsync"));
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Blank_title_falls_back_to_file_name()
    {
        var vm = Create();
        vm.AddFiles([_ui.FileSystem.Add("Evening_Aarti.m4a", 3)]);
        vm.Files[0].Title = "   ";

        await vm.StartCommand.ExecuteAsync(null);

        var meta = (ImportMetadata)_client.CallsTo("CompleteImportAsync").Single().Args[1]!;
        Assert.Equal("Evening Aarti", meta.Title);
        Assert.Null(meta.Artist);
        Assert.Null(meta.CategoryId);
    }

    [Fact]
    public async Task Server_progress_stage_is_shown_while_processing()
    {
        var vm = Create();
        vm.AddFiles([_ui.FileSystem.Add("a.mp3", 3)]);
        _client.Admin.OnComplete = (id, _) =>
        {
            _client.Events.ImportProgress(new ImportProgressDto(id, "a.mp3", 3, 3, "Analyzing"));
            Assert.Equal("Processing: Analyzing", vm.Files[0].StatusText);
            return new ImportResultDto(Guid.NewGuid(), false);
        };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(ImportFileStatus.Imported, vm.Files[0].Status);
    }
}
