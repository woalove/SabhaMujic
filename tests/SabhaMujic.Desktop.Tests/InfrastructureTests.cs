using SabhaMujic.Contracts;
using SabhaMujic.Desktop.Tests.Fakes;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.Tests;

public class ErrorMessagesTests
{
    [Theory]
    [InlineData(ErrorCode.Unauthenticated, "You are not signed in, or your session has ended. Please sign in again.")]
    [InlineData(ErrorCode.Forbidden, "You don't have permission to do that. Please ask an administrator.")]
    [InlineData(ErrorCode.NotFound, "That item no longer exists. It may have been removed by someone else.")]
    [InlineData(ErrorCode.InvalidArgument, "Not valid: Name is too long")]
    [InlineData(ErrorCode.Conflict, "Conflict: Name is too long")]
    [InlineData(ErrorCode.UnsupportedFormat, "This file type isn't supported. Use .aac, .flac, .m4a, .mp3, .wav, .wma files.")]
    [InlineData(ErrorCode.TooLarge, "The file is too large (the limit is 1024 MB).")]
    [InlineData(ErrorCode.RateLimited, "Too many attempts. Please wait a minute and try again.")]
    [InlineData(ErrorCode.IncompatibleClient, "This version of the app does not match the SabhaMujic service. Please reinstall or update SabhaMujic.")]
    [InlineData(ErrorCode.Internal, "The SabhaMujic service hit a problem. Please try again.")]
    public void Every_error_code_has_a_friendly_message(ErrorCode code, string expected)
    {
        Assert.Equal(expected, ErrorMessages.For(new SabhaMujicException(code, "Name is too long")));
    }

    [Fact]
    public void All_error_codes_are_covered()
    {
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ErrorMessages.For(code)));
        }
    }

    [Fact]
    public void Transport_and_unknown_errors_are_mapped()
    {
        Assert.Equal(ErrorMessages.ConnectionLost, ErrorMessages.For(new IOException("pipe broken")));
        Assert.Equal(ErrorMessages.ConnectionLost, ErrorMessages.For(new ObjectDisposedException("rpc")));
        Assert.StartsWith("The SabhaMujic service did not respond", ErrorMessages.For(new TimeoutException()));
        Assert.Equal("Cancelled.", ErrorMessages.For(new OperationCanceledException()));
        Assert.Equal("Something unexpected went wrong: boom", ErrorMessages.For(new InvalidOperationException("boom")));
        Assert.Equal(
            ErrorMessages.For(ErrorCode.Forbidden),
            ErrorMessages.For(new AggregateException(new SabhaMujicException(ErrorCode.Forbidden, ""))));
    }

    [Fact]
    public void Invalid_argument_without_detail_has_generic_text()
    {
        Assert.Equal("Some of the values entered are not valid.", ErrorMessages.For(ErrorCode.InvalidArgument, " "));
    }
}

public class DebouncerTests
{
    [Fact]
    public void Runs_only_the_last_action_after_quiet_period()
    {
        var scheduler = new ManualScheduler();
        var debouncer = new Debouncer(scheduler, TimeSpan.FromMilliseconds(300));
        var runs = new List<int>();

        debouncer.Trigger(() => runs.Add(1));
        scheduler.Advance(TimeSpan.FromMilliseconds(200));
        debouncer.Trigger(() => runs.Add(2));
        scheduler.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Empty(runs);
        Assert.True(debouncer.IsPending);

        scheduler.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal([2], runs);
        Assert.False(debouncer.IsPending);

        debouncer.Trigger(() => runs.Add(3));
        debouncer.Cancel();
        scheduler.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([2], runs);
    }
}

public class FormattingTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59.9, "0:59")]
    [InlineData(125, "2:05")]
    [InlineData(3725, "1:02:05")]
    [InlineData(-3, "0:00")]
    [InlineData(double.NaN, "0:00")]
    public void Time(double seconds, string expected) => Assert.Equal(expected, Formatting.Time(seconds));

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5 * 1024 * 1024, "5.0 MB")]
    public void Bytes(long bytes, string expected) => Assert.Equal(expected, Formatting.Bytes(bytes));
}

public sealed class UiSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sabhamujic-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Round_trips_and_creates_the_folder()
    {
        var store = new UiSettingsStore(Path.Combine(_dir, "sub", "desktop-settings.json"));
        var settings = new UiSettings { Left = 10, Top = 20, Width = 900, Height = 600, IsMaximized = true, IsQueuePanelOpen = false, MiniLeft = 5, MiniTop = 6, MiniAlwaysOnTop = false };

        Assert.True(store.Save(settings));

        Assert.Equal(settings, store.Load());
    }

    [Fact]
    public void Missing_or_corrupt_files_give_defaults_and_tiny_sizes_are_clamped()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "s.json");
        var store = new UiSettingsStore(path);
        Assert.Equal(new UiSettings(), store.Load());

        File.WriteAllText(path, "{ not json");
        Assert.Equal(new UiSettings(), store.Load());

        File.WriteAllText(path, """{ "Width": 100, "Height": 50, "IsQueuePanelOpen": false }""");
        var loaded = store.Load();
        Assert.Equal(UiSettings.MinWidth, loaded.Width);
        Assert.Equal(UiSettings.MinHeight, loaded.Height);
        Assert.False(loaded.IsQueuePanelOpen);
    }
}
