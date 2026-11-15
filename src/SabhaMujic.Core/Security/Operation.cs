namespace SabhaMujic.Core.Security;

/// <summary>Every client-invokable operation. One entry per RPC method / HTTP endpoint.</summary>
public enum Operation
{
    // Anonymous
    Hello,
    Login,
    ResumeSession,
    Pair,
    /// <summary>First-run admin creation. Anonymous at the matrix level; the service additionally requires
    /// an empty user table and a local Windows administrator on the desktop pipe.</summary>
    InitialSetup,

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
