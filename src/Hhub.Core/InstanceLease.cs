using System.Security.Cryptography;
using System.Text;

namespace Hhub.Core;

public sealed class InstanceLease : IDisposable
{
    private readonly Mutex _mutex;
    private InstanceLease(Mutex mutex) => _mutex = mutex;

    public static string NameFor(string dataRoot)
    {
        string path = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        return "Local\\Hhub-" + key;
    }

    public static InstanceLease? TryAcquire(string dataRoot)
    {
        var mutex = new Mutex(false, NameFor(dataRoot), out bool created);
        if (created) return new InstanceLease(mutex);
        mutex.Dispose();
        return null;
    }

    public void Dispose() => _mutex.Dispose();
}
