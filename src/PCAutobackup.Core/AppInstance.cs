using System.Security.Cryptography;
using System.Text;

namespace PCAutobackup.Core;

/// <summary>Kernel-owned instance lifetime; forced termination cannot leave a stale lock file.</summary>
public sealed class AppInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle activation;
    private RegisteredWaitHandle? listener;
    public bool IsPrimary { get; }
    public static string Identity(string role, string configurationPath) => "Local\\PCAutobackup-" + role + "-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(configurationPath).ToUpperInvariant())));

    public AppInstance(string role, string configurationPath)
    {
        var identity = Identity(role, configurationPath);
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, identity + "-activate");
        mutex = new Mutex(false, identity);
        try { IsPrimary = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
    }

    public void ActivatePrimary() => activation.Set();
    public void Listen(Action activate)
    {
        if (!IsPrimary) throw new InvalidOperationException("Only the primary instance can receive activation.");
        listener = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => activate(), null, Timeout.Infinite, false);
    }
    public void Dispose()
    {
        listener?.Unregister(null);
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose(); activation.Dispose();
    }
}
