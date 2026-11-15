namespace SabhaMujic.Core.Security;

/// <summary>Every client-invokable operation. One entry per RPC method / HTTP endpoint.</summary>
public enum Operation
{
    // Anonymous
    Hello,
    Login,
    ResumeSession,
    Pair,

    // Any authenticated user (Volunteer or Admin)
    Logout,
    BrowseLibrary,
    ReadPlayback,
    ControlPlayback,
    EditQueue,
    ReadPlaylists,
    EditPlaylists,

    // Admin only
    ImportTracks,
    ManageCategories,
    ManageTracks,
    ManageLoopPoints,
    ManageUsers,
    ManageDevices,
}

public enum AccessLevel
{
    Anonymous = 0,
    Authenticated = 1,
    Admin = 2,
}
