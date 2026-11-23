using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels.Admin;

/// <summary>One category (tag) in a multi-select list: checked when the track has (or will get) it.</summary>
public sealed partial class CategoryChoice(Guid id, string name, bool isSelected = false, TagColor color = TagColor.Accent)
    : ObservableObject
{
    public Guid Id { get; } = id;

    public string Name { get; } = name;

    /// <summary>Colour of the category's dot (by category order).</summary>
    public TagColor Color { get; } = color;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = isSelected;

    public override string ToString() => Name;
}

/// <summary>A set of <see cref="CategoryChoice"/> ticks with a one-line summary ("Dhun, Kirtan"); rebuilt when categories change.</summary>
public sealed partial class CategorySelection : ObservableObject
{
    public const string NoneText = "No category";

    public ObservableCollection<CategoryChoice> Choices { get; } = [];

    /// <summary>The ticked categories, in category order.</summary>
    public IReadOnlyList<Guid> SelectedIds => Choices.Where(c => c.IsSelected).Select(c => c.Id).ToArray();

    public string Summary
    {
        get
        {
            var names = Choices.Where(c => c.IsSelected).Select(c => c.Name).ToArray();
            return names.Length == 0 ? NoneText : string.Join(", ", names);
        }
    }

    public bool HasSelection => Choices.Any(c => c.IsSelected);

    /// <summary>Replaces the choices with <paramref name="categories"/> (sort order), ticking <paramref name="selected"/>.</summary>
    public void Reset(IReadOnlyList<CategoryDto> categories, IEnumerable<Guid> selected)
    {
        var ticked = selected.ToHashSet();
        foreach (var c in Choices)
        {
            c.PropertyChanged -= OnChoiceChanged;
        }

        Choices.Clear();
        var position = 0;
        foreach (var c in categories.OrderBy(c => c.SortOrder))
        {
            var choice = new CategoryChoice(c.Id, c.Name, ticked.Contains(c.Id), TagColors.ForPosition(position++));
            choice.PropertyChanged += OnChoiceChanged;
            Choices.Add(choice);
        }

        Changed();
    }

    /// <summary>Ticks exactly <paramref name="ids"/> (unknown ids are ignored).</summary>
    public void Select(IEnumerable<Guid> ids)
    {
        var wanted = ids.ToHashSet();
        foreach (var c in Choices)
        {
            c.IsSelected = wanted.Contains(c.Id);
        }
    }

    private void OnChoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CategoryChoice.IsSelected))
        {
            Changed();
        }
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(SelectedIds));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasSelection));
    }
}

/// <summary>Splits an upload into chunks of at most <c>chunkSize</c> bytes.</summary>
public static class ImportChunker
{
    public static IEnumerable<(long Offset, int Length)> Plan(long totalBytes, int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        for (long offset = 0; offset < totalBytes; offset += chunkSize)
        {
            yield return (offset, (int)Math.Min(chunkSize, totalBytes - offset));
        }
    }

    /// <summary>Default title for a file: its name without extension, underscores as spaces.</summary>
    public static string DefaultTitle(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Trim();
        return name.Length == 0 ? Path.GetFileName(path) : name;
    }
}

public enum ImportFileStatus
{
    Ready,
    Uploading,
    Processing,
    Imported,
    /// <summary>The service already had identical audio; nothing new was stored. Informational, not an error.</summary>
    Duplicate,
    Failed,
    Cancelled,
}

public sealed partial class ImportFileViewModel : ObservableObject
{
    public ImportFileViewModel(ImportViewModel owner, string path, long totalBytes, IReadOnlyList<CategoryDto> categories,
        IEnumerable<Guid> selectedCategories)
    {
        Owner = owner;
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        TotalBytes = totalBytes;
        Title = ImportChunker.DefaultTitle(path);
        Categories.Reset(categories, selectedCategories);
    }

    public ImportViewModel Owner { get; }

    public string Path { get; }

    public string FileName { get; }

    public long TotalBytes { get; }

    public string SizeText => Formatting.Bytes(TotalBytes);

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string? Artist { get; set; }

    /// <summary>The categories this file will get (any number).</summary>
    public CategorySelection Categories { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable), nameof(IsError), nameof(IsInfo), nameof(StatusText))]
    public partial ImportFileStatus Status { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent))]
    public partial long BytesSent { get; set; }

    /// <summary>Server-side stage from <see cref="ImportProgressDto.Stage"/> (e.g. "Analyzing").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Stage { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    public Guid? UploadId { get; internal set; }

    public Guid? TrackId { get; internal set; }

    public double Percent => TotalBytes == 0 ? 0 : 100.0 * BytesSent / TotalBytes;

    public bool IsEditable => Status == ImportFileStatus.Ready;

    public bool IsError => Status == ImportFileStatus.Failed;

    public bool IsInfo => Status == ImportFileStatus.Duplicate;

    public string StatusText => Status switch
    {
        ImportFileStatus.Ready => "Ready",
        ImportFileStatus.Uploading => "Uploading…",
        ImportFileStatus.Processing => Stage is null ? "Processing…" : $"Processing: {Stage}",
        ImportFileStatus.Imported => "Imported",
        ImportFileStatus.Duplicate => "Already in library",
        ImportFileStatus.Failed => "Failed",
        ImportFileStatus.Cancelled => "Cancelled",
        _ => Status.ToString(),
    };
}

/// <summary>
/// Admin import: pick files, edit title/artist/categories (any number), then upload each file in chunks of at most
/// <see cref="AdminLimits.MaxChunkBytes"/> (Begin → UploadChunk… → Complete). Files are uploaded one at a time;
/// a failure on one file is recorded on that row and the next file continues. Cancel stops the current upload
/// (calling <see cref="IAdminApi.CancelImportAsync"/>) and marks the remaining files cancelled.
/// </summary>
public sealed partial class ImportViewModel : ViewModelBase
{
    private readonly ISabhaClient _client;
    private readonly UiServices _ui;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<CategoryDto> _categories = [];

    public ImportViewModel(ISabhaClient client, UiServices ui)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _client.Events.ImportProgressReceived += p => _ui.Dispatcher.Post(() => OnImportProgress(p));
        Files.CollectionChanged += (_, _) => StartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Upload chunk size; never larger than <see cref="AdminLimits.MaxChunkBytes"/>.</summary>
    public int ChunkSize
    {
        get;
        init => field = Math.Clamp(value, 1, AdminLimits.MaxChunkBytes);
    } = AdminLimits.MaxChunkBytes;

    public ObservableCollection<ImportFileViewModel> Files { get; } = [];

    /// <summary>Categories ticked for newly added files (multi-select).</summary>
    public CategorySelection DefaultCategories { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand), nameof(AddFilesCommand), nameof(ClearFinishedCommand))]
    public partial bool IsImporting { get; private set; }

    [ObservableProperty]
    public partial double OverallPercent { get; private set; }

    public void SetCategories(IReadOnlyList<CategoryDto> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        _categories = categories.ToArray();
        DefaultCategories.Reset(_categories, DefaultCategories.SelectedIds);
        foreach (var file in Files.Where(f => f.IsEditable))
        {
            file.Categories.Reset(_categories, file.Categories.SelectedIds);
        }
    }

    /// <summary>Gives every file that hasn't been uploaded yet the default categories.</summary>
    [RelayCommand]
    private void ApplyDefaultCategoriesToAll()
    {
        foreach (var file in Files.Where(f => f.IsEditable))
        {
            file.Categories.Select(DefaultCategories.SelectedIds);
        }
    }

    private bool CanEditList() => !IsImporting;

    [RelayCommand(CanExecute = nameof(CanEditList))]
    private async Task AddFilesAsync()
    {
        var paths = await _ui.FilePicker.PickAudioFilesAsync().ConfigureAwait(true);
        AddFiles(paths);
    }

    /// <summary>Adds files to the list, rejecting ones that can't be imported (type, size, unreadable).</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            if (Files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase) && f.IsEditable))
            {
                continue;
            }

            long length;
            try
            {
                length = _ui.FileSystem.GetLength(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Files.Add(Rejected(path, 0, "The file could not be read."));
                continue;
            }

            if (!AdminLimits.AllowedExtensions.Contains(System.IO.Path.GetExtension(path)))
            {
                Files.Add(Rejected(path, length, ErrorMessages.For(ErrorCode.UnsupportedFormat)));
            }
            else if (length > AdminLimits.MaxUploadBytes)
            {
                Files.Add(Rejected(path, length, ErrorMessages.For(ErrorCode.TooLarge)));
            }
            else if (length == 0)
            {
                Files.Add(Rejected(path, length, "The file is empty."));
            }
            else
            {
                Files.Add(new ImportFileViewModel(this, path, length, _categories, DefaultCategories.SelectedIds));
            }
        }
    }

    [RelayCommand]
    private void RemoveFile(ImportFileViewModel? file)
    {
        if (file is not null && file.Status is not (ImportFileStatus.Uploading or ImportFileStatus.Processing))
        {
            Files.Remove(file);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditList))]
    private void ClearFinished()
    {
        foreach (var file in Files.Where(f => f.Status is not ImportFileStatus.Ready).ToArray())
        {
            Files.Remove(file);
        }
    }

    private bool CanStart() => !IsImporting && Files.Any(f => f.Status == ImportFileStatus.Ready);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var batch = Files.Where(f => f.Status == ImportFileStatus.Ready).ToArray();
        if (batch.Length == 0)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsImporting = true;
        ErrorMessage = StatusMessage = null;
        var totalBytes = Math.Max(1, batch.Sum(f => f.TotalBytes));
        long doneBytes = 0;
        try
        {
            foreach (var file in batch)
            {
                if (cts.IsCancellationRequested)
                {
                    file.Status = ImportFileStatus.Cancelled;
                    continue;
                }

                var before = doneBytes;
                await ImportOneAsync(file, cts.Token, sent => OverallPercent = 100.0 * (before + sent) / totalBytes)
                    .ConfigureAwait(true);
                doneBytes += file.TotalBytes;
                OverallPercent = 100.0 * doneBytes / totalBytes;
            }
        }
        finally
        {
            _cts = null;
            IsImporting = false;
            StatusMessage = Summarize(batch);
        }
    }

    private bool CanCancel() => IsImporting;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private async Task ImportOneAsync(ImportFileViewModel file, CancellationToken ct, Action<long> onProgress)
    {
        Guid? uploadId = null;
        file.Message = null;
        file.Stage = null;
        file.BytesSent = 0;
        try
        {
            file.Status = ImportFileStatus.Uploading;
            var total = _ui.FileSystem.GetLength(file.Path);
            if (total != file.TotalBytes)
            {
                throw new SabhaMujicException(ErrorCode.Conflict, "The file changed after it was added. Remove it and add it again.");
            }

            uploadId = await _client.Admin.BeginImportAsync(file.FileName, total, ct).ConfigureAwait(true);
            file.UploadId = uploadId;

            await using (var stream = _ui.FileSystem.OpenRead(file.Path))
            {
                foreach (var (offset, length) in ImportChunker.Plan(total, ChunkSize))
                {
                    ct.ThrowIfCancellationRequested();
                    var buffer = new byte[length];
                    await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(true);
                    await _client.Admin.UploadChunkAsync(uploadId.Value, offset, buffer, ct).ConfigureAwait(true);
                    file.BytesSent = offset + length;
                    onProgress(file.BytesSent);
                }
            }

            ct.ThrowIfCancellationRequested();
            file.Status = ImportFileStatus.Processing;
            var title = string.IsNullOrWhiteSpace(file.Title) ? ImportChunker.DefaultTitle(file.Path) : file.Title.Trim();
            var artist = string.IsNullOrWhiteSpace(file.Artist) ? null : file.Artist.Trim();
            var result = await _client.Admin
                .CompleteImportAsync(uploadId.Value, new ImportMetadata(title, artist, file.Categories.HasSelection ? file.Categories.SelectedIds : null), ct)
                .ConfigureAwait(true);
            file.TrackId = result.TrackId;
            file.Status = result.WasDuplicate ? ImportFileStatus.Duplicate : ImportFileStatus.Imported;
            file.Message = result.WasDuplicate ? "This audio is already in the library; the existing track was kept." : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            file.Status = ImportFileStatus.Cancelled;
            await CancelOnServerAsync(uploadId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            file.Status = ImportFileStatus.Failed;
            file.Message = ErrorMessages.For(ex);
            await CancelOnServerAsync(uploadId).ConfigureAwait(true);
        }
    }

    private async Task CancelOnServerAsync(Guid? uploadId)
    {
        if (uploadId is null)
        {
            return;
        }

        try
        {
            await _client.Admin.CancelImportAsync(uploadId.Value, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Best effort: the service expires abandoned uploads itself; the row already shows the real outcome.
        }
    }

    private void OnImportProgress(ImportProgressDto progress)
    {
        var file = Files.FirstOrDefault(f => f.UploadId == progress.UploadId);
        if (file is not null && file.Status == ImportFileStatus.Processing)
        {
            file.Stage = progress.Stage;
        }
    }

    private ImportFileViewModel Rejected(string path, long length, string message) =>
        new(this, path, length, _categories, []) { Status = ImportFileStatus.Failed, Message = message };

    private static string Summarize(IReadOnlyCollection<ImportFileViewModel> batch)
    {
        var parts = new List<string>();
        void Add(ImportFileStatus status, string label)
        {
            var n = batch.Count(f => f.Status == status);
            if (n > 0)
            {
                parts.Add($"{n} {label}");
            }
        }

        Add(ImportFileStatus.Imported, "imported");
        Add(ImportFileStatus.Duplicate, "already in library");
        Add(ImportFileStatus.Failed, "failed");
        Add(ImportFileStatus.Cancelled, "cancelled");
        return parts.Count == 0 ? "Nothing imported." : string.Join(", ", parts) + ".";
    }
}
