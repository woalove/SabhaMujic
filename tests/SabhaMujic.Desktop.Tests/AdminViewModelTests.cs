using SabhaMujic.Contracts;
using SabhaMujic.Desktop.Tests.Fakes;
using SabhaMujic.Desktop.ViewModels.Admin;

namespace SabhaMujic.Desktop.Tests;

public class DevicesAdminViewModelTests
{
    private readonly FakeSabhaClient _client = new();
    private readonly TestUi _ui = new();
    private readonly DevicesAdminViewModel _vm;

    public DevicesAdminViewModelTests()
    {
        _client.Admin.OnPairing = role => new PairingCodeDto(
            "482913", "https://sabha-pc.local:8443/pair#482913", "http://sabha-pc.local:8080/", role,
            _ui.Scheduler.UtcNow.AddMinutes(5));
        _vm = new DevicesAdminViewModel(_client, _ui.Services);
    }

    [Fact]
    public async Task Pairing_shows_code_qr_and_counts_down_to_expiry()
    {
        _vm.PairingRole = Role.Admin;
        await _vm.PairNewDeviceCommand.ExecuteAsync(null);

        Assert.Equal(Role.Admin, _client.CallsTo("CreatePairingCodeAsync").Single().Args[0]);
        Assert.Equal("482913", _vm.Pairing?.Code);
        Assert.True(_vm.IsPairingVisible);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], _vm.PairingQrPng!.Take(4).ToArray());
        Assert.Equal("Expires in 5:00", _vm.CountdownText);

        _ui.Scheduler.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("Expires in 4:59", _vm.CountdownText);

        _ui.Scheduler.Advance(TimeSpan.FromSeconds(238));
        Assert.Equal("Expires in 1:01", _vm.CountdownText);

        _ui.Scheduler.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal("Expires in 0:01", _vm.CountdownText);
        Assert.False(_vm.IsPairingExpired);
        var devicesCallsBefore = _client.CallsTo("GetDevicesAsync").Count();

        _ui.Scheduler.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_vm.IsPairingExpired);
        Assert.Null(_vm.PairingQrPng);
        Assert.Equal("This code has expired. Create a new one.", _vm.CountdownText);
        Assert.Equal(0, _ui.Scheduler.ActiveCount); // countdown stopped
        Assert.Equal(devicesCallsBefore + 1, _client.CallsTo("GetDevicesAsync").Count());
    }

    [Fact]
    public async Task New_code_replaces_the_old_countdown()
    {
        await _vm.PairNewDeviceCommand.ExecuteAsync(null);
        _ui.Scheduler.Advance(TimeSpan.FromSeconds(30));
        await _vm.PairNewDeviceCommand.ExecuteAsync(null);

        Assert.Equal(1, _ui.Scheduler.ActiveCount);
        Assert.Equal("Expires in 5:00", _vm.CountdownText);
    }

    [Fact]
    public async Task Close_pairing_stops_countdown_and_refreshes_devices()
    {
        await _vm.PairNewDeviceCommand.ExecuteAsync(null);
        _client.Admin.Devices.Add(new DeviceDto(Guid.NewGuid(), "iPhone", Role.Volunteer, DateTimeOffset.UtcNow, null));

        await _vm.ClosePairingCommand.ExecuteAsync(null);

        Assert.False(_vm.IsPairingVisible);
        Assert.Equal(0, _ui.Scheduler.ActiveCount);
        Assert.Single(_vm.Devices);
    }

    [Fact]
    public async Task Revoke_requires_confirmation()
    {
        var device = new DeviceDto(Guid.NewGuid(), "iPhone", Role.Volunteer, DateTimeOffset.UtcNow, null);
        _ui.Dialogs.ConfirmResult = false;
        await _vm.RevokeCommand.ExecuteAsync(device);
        Assert.Empty(_client.CallsTo("RevokeDeviceAsync"));

        _ui.Dialogs.ConfirmResult = true;
        await _vm.RevokeCommand.ExecuteAsync(device);
        Assert.Equal(device.Id, _client.CallsTo("RevokeDeviceAsync").Single().Args[0]);
    }
}

public class CategoriesAndTracksAdminTests
{
    private readonly FakeSabhaClient _client = new();
    private readonly TestUi _ui = new();

    [Fact]
    public async Task Category_crud_and_reorder()
    {
        var a = new CategoryDto(Guid.NewGuid(), "A", 0, 3);
        var b = new CategoryDto(Guid.NewGuid(), "B", 1, 0);
        var c = new CategoryDto(Guid.NewGuid(), "C", 2, 0);
        _client.Library.Categories.AddRange([c, a, b]);
        var vm = new CategoriesAdminViewModel(_client, _ui.Services);
        await vm.LoadAsync();
        Assert.Equal(["A", "B", "C"], vm.Categories.Select(x => x.Name).ToArray());

        await vm.MoveDownCommand.ExecuteAsync(vm.Categories[0]);
        Assert.Equal([b.Id, a.Id, c.Id], (Guid[])_client.CallsTo("ReorderCategoriesAsync").Single().Args[0]!);

        await vm.MoveUpCommand.ExecuteAsync(vm.Categories[0]); // already first: no call
        Assert.Single(_client.CallsTo("ReorderCategoriesAsync"));

        _ui.Dialogs.PromptResults.Enqueue("Bhajan");
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.Equal("Bhajan", _client.CallsTo("CreateCategoryAsync").Single().Args[0]);
        Assert.Equal("Bhajan", vm.SelectedCategory?.Name);

        _ui.Dialogs.PromptResults.Enqueue("A2");
        await vm.RenameCommand.ExecuteAsync(a);
        Assert.Equal([a.Id, "A2"], _client.CallsTo("RenameCategoryAsync").Single().Args);

        await vm.DeleteCommand.ExecuteAsync(a);
        Assert.Equal(a.Id, _client.CallsTo("DeleteCategoryAsync").Single().Args[0]);
    }

    [Fact]
    public async Task Track_metadata_loop_override_and_reanalyze()
    {
        var track = _client.Library.AddTrack("Old title", duration: 100);
        var vm = new TracksAdminViewModel(_client, _ui.Services);
        await vm.LoadAsync();
        vm.SelectedTrack = vm.Tracks.Single();
        Assert.Equal("Old title", vm.EditTitle);
        Assert.Equal(100, vm.LoopEnd); // no loop yet: defaults to the whole track

        vm.EditTitle = " New title ";
        vm.EditArtist = "";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new TrackUpdateDto(track.Id, "New title", null, null), _client.CallsTo("UpdateTrackAsync").Single().Args[0]);
        Assert.Equal("New title", vm.SelectedTrack?.Title);

        vm.LoopStart = 10;
        vm.LoopEnd = 90;
        vm.LoopCrossfade = 0.3;
        Assert.Null(vm.LoopValidationError);
        await vm.SaveLoopCommand.ExecuteAsync(null);
        var args = _client.CallsTo("SetLoopOverrideAsync").Single().Args;
        Assert.Equal(new LoopRegionDto(10, 90, 0.3, 1.0, true), args[1]);

        await vm.UseAutomaticLoopCommand.ExecuteAsync(null);
        Assert.Null(_client.CallsTo("SetLoopOverrideAsync").Last().Args[1]);

        await vm.ReanalyzeCommand.ExecuteAsync(null);
        Assert.Equal(track.Id, _client.CallsTo("ReanalyzeTrackAsync").Single().Args[0]);
    }

    [Theory]
    [InlineData(-1, 50, 0.2, "Start must be 0 or more.")]
    [InlineData(50, 50, 0.2, "End must be after start.")]
    [InlineData(10, 101, 0.2, "End must be at most the track length (100 s).")]
    [InlineData(10, 20, 6, "Crossfade must be between 0 and half the loop length.")]
    public async Task Invalid_loop_override_cannot_be_saved(double start, double end, double fade, string error)
    {
        _client.Library.AddTrack("T", duration: 100);
        var vm = new TracksAdminViewModel(_client, _ui.Services);
        await vm.LoadAsync();
        vm.SelectedTrack = vm.Tracks.Single();

        vm.LoopStart = start;
        vm.LoopEnd = end;
        vm.LoopCrossfade = fade;

        Assert.Equal(error, vm.LoopValidationError);
        Assert.False(vm.SaveLoopCommand.CanExecute(null));
    }

    [Fact]
    public async Task Delete_track_requires_confirmation()
    {
        var track = _client.Library.AddTrack("T");
        var vm = new TracksAdminViewModel(_client, _ui.Services);
        await vm.LoadAsync();
        vm.SelectedTrack = vm.Tracks.Single();

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Equal(track.Id, _client.CallsTo("DeleteTrackAsync").Single().Args[0]);
        Assert.Empty(vm.Tracks);
        Assert.Null(vm.SelectedTrack);
    }
}

public class UsersAdminViewModelTests
{
    private readonly FakeSabhaClient _client = new();
    private readonly TestUi _ui = new();

    [Fact]
    public async Task Create_reset_and_delete_users()
    {
        var vm = new UsersAdminViewModel(_client, _ui.Services);
        var cleared = 0;
        vm.SecretCleared += () => cleared++;

        vm.NewUserName = " sevak2 ";
        vm.NewUserRole = Role.Admin;
        vm.NewUserSecret = "4321";
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.Equal(["sevak2", Role.Admin, "4321"], _client.CallsTo("CreateUserAsync").Single().Args);
        Assert.Equal("", vm.NewUserSecret);
        Assert.Equal(1, cleared);
        var user = Assert.Single(vm.Users);

        _ui.Dialogs.PromptResults.Enqueue("9999");
        await vm.ResetSecretCommand.ExecuteAsync(user);
        Assert.Equal([user.Id, "9999"], _client.CallsTo("SetUserSecretAsync").Single().Args);

        await vm.DeleteCommand.ExecuteAsync(user);
        Assert.Equal(user.Id, _client.CallsTo("DeleteUserAsync").Single().Args[0]);
        Assert.Empty(vm.Users);
    }

    [Fact]
    public async Task Short_pins_are_rejected_before_calling_the_service()
    {
        var vm = new UsersAdminViewModel(_client, _ui.Services) { NewUserName = "x", NewUserSecret = "12" };

        await vm.CreateCommand.ExecuteAsync(null);
        _ui.Dialogs.PromptResults.Enqueue("1");
        await vm.ResetSecretCommand.ExecuteAsync(new UserDto(Guid.NewGuid(), "y", Role.Volunteer, DateTimeOffset.UtcNow));

        Assert.Empty(_client.CallsTo("CreateUserAsync"));
        Assert.Empty(_client.CallsTo("SetUserSecretAsync"));
        Assert.Contains("at least 4", vm.ErrorMessage);
    }
}
