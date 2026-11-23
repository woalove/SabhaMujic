namespace SabhaMujic.Core.Security;

/// <summary>Every client-invokable operation. One entry per RPC method / HTTP endpoint.</summary>
public enum Operation
{
    // Anonymous (on the remote only Login and ResumeSession; see Authorizer)
    Hello,
    Login,
    ResumeSession,
    /// <summary>First-run admin creation. Anonymous at the matrix level; the service additionally requires
    /// an empty user table and a local Windows administrator on the desktop pipe.</summary>
    InitialSetup,
    /// <summary>Admin password recovery. Anonymous at the matrix level; the service additionally requires an elevated local
    /// Windows administrator on the desktop pipe (the same gate as <see cref="InitialSetup"/>).</summary>
    LocalRecovery,

    // Volunteer level: anyone on the desktop (signed in or not); on the remote, administrators only
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
    /// <summary>Which tracks have a category (the category side of tagging; the track side is <see cref="ManageTracks"/>).</summary>
    ManageCategoryMembership,
    ManageTracks,
    ManageLoopPoints,
    ManageUsers,
    ManageDevices,
}

/// <summary>The level an operation needs. How a caller meets it depends on its transport (see <see cref="Authorizer"/>).</summary>
public enum AccessLevel
{
    /// <summary>No sign-in (hello, sign-in itself, first-run setup, recovery).</summary>
    Anonymous = 0,

    /// <summary>
    /// Volunteer use of the player (library, playback, queue, playlists). On the desktop pipe anyone qualifies, signed
    /// in or not; on the remote only administrators do.
    /// </summary>
    Authenticated = 1,

    /// <summary>Administration: an administrator session on either transport.</summary>
    Admin = 2,
}
