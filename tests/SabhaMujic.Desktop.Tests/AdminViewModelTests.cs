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
        _client.Admin.OnPhoneSetup = () => new PhoneSetupInfoDto("http://sabha-pc.local:8080/", "https://sabha-pc.local:8443/", null, IsRemoteRunning: true);
        _vm = new DevicesAdminViewModel(_client, _ui.Services);
    }

    [Fact]
    public async Task Set_up_a_phone_shows_the_setup_page_as_text_and_qr_code_without_any_code()
    {
        await _vm.SetUpPhoneCommand.ExecuteAsync(null);

        Assert.Single(_client.CallsTo("GetPhoneSetupInfoAsync"));
        Assert.True(_vm.IsSetupVisible);
        Assert.Equal("http://sabha-pc.local:8080/", _vm.Setup?.BootstrapUrl);
        Assert.Equal("https://sabha-pc.local:8443/", _vm.Setup?.RemoteUrl);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], _vm.SetupQrPng!.Take(4).ToArray());
        Assert.False(_vm.IsRemoteOff);
        Assert.Equal(0, _ui.Scheduler.ActiveCount); // nothing expires: no countdown
    }

    [Fact]
    public async Task A_stopped_remote_is_pointed_out()
    {
        _client.Admin.OnPhoneSetup = () => new PhoneSetupInfoDto("http://pc.local:8080/", "https://pc.local:8443/", null, IsRemoteRunning: false);
        await _vm.SetUpPhoneCommand.ExecuteAsync(null);
        Assert.True(_vm.IsRemoteOff);
    }

    [Fact]
    public async Task Closing_the_setup_panel_refreshes_the_remembered_phones()
    {
        await _vm.SetUpPhoneCommand.ExecuteAsync(null);
        _client.Admin.Devices.Add(new DeviceDto(Guid.NewGuid(), "iPhone", Role.Admin, DateTimeOffset.UtcNow, null));

        await _vm.CloseSetupCommand.ExecuteAsync(null);

        Assert.False(_vm.IsSetupVisible);
        Assert.Null(_vm.SetupQrPng);
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
    public async Task Track_categories_are_a_multi_select_that_keeps_every_tick()
    {
        var bhajan = new CategoryDto(Guid.NewGuid(), "Bhajan", 0, 1);
        var kirtan = new CategoryDto(Guid.NewGuid(), "Kirtan", 1, 0);
        var dhun = new CategoryDto(Guid.NewGuid(), "Dhun", 2, 0);
        _client.Library.Categories.AddRange([bhajan, kirtan, dhun]);
        var track = _client.Library.AddTrack("Song", bhajan.Id);
        var vm = new TracksAdminViewModel(_client, _ui.Services);
        vm.SetCategories(_client.Library.Categories);
        await vm.LoadAsync();

        vm.SelectedTrack = vm.Tracks.Single();
        Assert.Equal(["Bhajan", "Kirtan", "Dhun"], vm.EditCategories.Select(c => c.Name));
        Assert.Equal([bhajan.Id], vm.EditCategoryIds);

        vm.EditCategories.Single(c => c.Id == dhun.Id).IsSelected = true;
        vm.EditCategories.Single(c => c.Id == kirtan.Id).IsSelected = true;
        vm.SetCategories(_client.Library.Categories); // a refresh keeps unsaved ticks
        await vm.SaveCommand.ExecuteAsync(null);

        var sent = Assert.IsType<TrackUpdateDto>(_client.CallsTo("UpdateTrackAsync").Single().Args[0]);
        Assert.Equal(track.Id, sent.TrackId);
        Assert.Equal([bhajan.Id, kirtan.Id, dhun.Id], sent.CategoryIds);

        vm.EditCategories.Single(c => c.Id == bhajan.Id).IsSelected = false;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal([kirtan.Id, dhun.Id], Assert.IsType<TrackUpdateDto>(_client.CallsTo("UpdateTrackAsync").Last().Args[0]).CategoryIds);
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
        var sent = Assert.IsType<TrackUpdateDto>(_client.CallsTo("UpdateTrackAsync").Single().Args[0]);
        Assert.Equal((track.Id, "New title", (string?)null), (sent.TrackId, sent.Title, sent.Artist));
        Assert.Empty(sent.CategoryIds);
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
        vm.NewUserSecret = "pass-4321";
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.Equal(["sevak2", Role.Admin, "pass-4321"], _client.CallsTo("CreateUserAsync").Single().Args);
        Assert.Equal("", vm.NewUserSecret);
        Assert.Equal(1, cleared);
        var user = Assert.Single(vm.Users);

        _ui.Dialogs.PromptResults.Enqueue("pass-9999");
        await vm.ResetSecretCommand.ExecuteAsync(user);
        Assert.Equal([user.Id, "pass-9999"], _client.CallsTo("SetUserSecretAsync").Single().Args);

        await vm.DeleteCommand.ExecuteAsync(user);
        Assert.Equal(user.Id, _client.CallsTo("DeleteUserAsync").Single().Args[0]);
        Assert.Empty(vm.Users);
    }

    [Fact]
    public async Task Short_passwords_are_rejected_before_calling_the_service()
    {
        var vm = new UsersAdminViewModel(_client, _ui.Services) { NewUserName = "x", NewUserSecret = "1234567" };

        await vm.CreateCommand.ExecuteAsync(null);
        _ui.Dialogs.PromptResults.Enqueue("1234567");
        await vm.ResetSecretCommand.ExecuteAsync(new UserDto(Guid.NewGuid(), "y", Role.Volunteer, DateTimeOffset.UtcNow));

        Assert.Empty(_client.CallsTo("CreateUserAsync"));
        Assert.Empty(_client.CallsTo("SetUserSecretAsync"));
        Assert.Contains("at least 8", vm.ErrorMessage);
    }
}
