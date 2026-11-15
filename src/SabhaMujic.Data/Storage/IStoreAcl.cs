namespace SabhaMujic.Data.Storage;

/// <summary>
/// Restricts a storage directory so only the service (and Administrators) can read it.
/// The Windows implementation lives in SabhaMujic.Service; <see cref="NoOpStoreAcl"/> is for tests and Linux dev hosts.
/// Implementations must be idempotent and throw if the directory cannot be secured.
/// </summary>
public interface IStoreAcl
{
    void Secure(string rootDirectory);
}

/// <summary>Leaves permissions unchanged. Not for production on Windows.</summary>
public sealed class NoOpStoreAcl : IStoreAcl
{
    public static NoOpStoreAcl Instance { get; } = new();

    public void Secure(string rootDirectory)
    {
    }
}
