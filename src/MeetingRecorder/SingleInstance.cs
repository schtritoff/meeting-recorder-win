using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Security.Principal;

namespace MeetingRecorder;

internal sealed class SingleInstance : IDisposable
{
    private static readonly HashSet<string> Held = [];
    private readonly string name;
    private readonly Mutex mutex;
    public bool IsOwner { get; private set; }

    // Registered messages are never freed in a session and share the atom table WPF needs for window
    // classes, so register only on first use (owner hook / duplicate launch), never per construction.
    private uint? activationMessage;
    public uint ActivationMessage => activationMessage ??= RegisterWindowMessage(name + "-Activate");

    internal static string MutexName(string root)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();
        var key = WindowsIdentity.GetCurrent().User!.Value + "|" + normalized;
        return "Global\\MeetingRecorder-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    public SingleInstance(string root)
    {
        name = MutexName(root);
        mutex = new Mutex(false, name);
        lock (Held)
        {
            // Mutexes are recursive on the owning thread; a second guard must still lose.
            if (Held.Contains(name)) return;
            try { IsOwner = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { IsOwner = true; }
            if (IsOwner) Held.Add(name);
        }
    }

    public void ActivateExisting()
    {
        if (ActivationMessage != 0) PostMessage((nint)0xffff, ActivationMessage, 0, 0);
    }

    public void Dispose()
    {
        lock (Held)
        {
            if (IsOwner)
            {
                mutex.ReleaseMutex();
                Held.Remove(name);
                IsOwner = false;
            }
            mutex.Dispose();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
