using System;
using System.Threading;

namespace Teezy.App;

/// <summary>Ensures only one Teezy runs per logon session.</summary>
/// <remarks>
/// <para>
/// Not a nicety. Two instances install two <c>WH_KEYBOARD_LL</c> hooks on the same key, so a
/// single hold starts two recordings, runs two transcriptions and performs two injections —
/// the utterance is typed twice, interleaved. They also load a second copy of the model,
/// roughly a gigabyte, which on a 16 GB machine turns a 1.65 s load into a minute of paging
/// and reads as a hang.
/// </para>
/// <para>
/// The mutex is session-local (no <c>Global\</c> prefix): the hook and the tray icon are
/// per-session, so a second user logged into the same machine may legitimately run their
/// own copy.
/// </para>
/// <para>
/// A second launch is not an error to report: it is someone clicking TeezyFlow on the taskbar
/// or Start menu to get at the window. It rings the show signal, which the running copy is
/// waiting on, and quietly exits — so launching the app always ends with its window in front,
/// as any other application does.
/// </para>
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = "Teezy.SingleInstance.6f1c2a";
    private const string ShowSignal = "Teezy.Show.6f1c2a";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show = new(false, EventResetMode.AutoReset, ShowSignal);
    private RegisteredWaitHandle? _listening;

    /// <summary>In the running copy: calls <paramref name="show"/> whenever another launch asks for the window.</summary>
    /// <remarks>Called on a thread-pool thread; the caller hops to the UI thread.</remarks>
    public void Listen(Action show)
    {
        if (!IsFirst || _listening is not null) return;
        _listening = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => show(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>In a second launch: asks the running copy to show its window.</summary>
    /// <remarks>
    /// Windows only lets the process the user just launched take the foreground, so this one —
    /// which has that right — passes it on first; otherwise the window would only flash on the
    /// taskbar instead of coming forward.
    /// </remarks>
    public void AskFirstToShow()
    {
        AllowSetForegroundWindow(-1); // ASFW_ANY
        _show.Set();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>False when another instance already holds the mutex.</summary>
    public bool IsFirst { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

        // A previous instance that crashed leaves the mutex abandoned rather than released.
        // WaitOne then throws AbandonedMutexException, which means "the owner died and you
        // now hold it" — that is success, not failure, and must not be treated as a
        // duplicate launch or the app becomes unstartable after any hard crash.
        if (!createdNew)
        {
            try
            {
                IsFirst = _mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                IsFirst = true;
            }
        }
        else
        {
            IsFirst = true;
        }
    }

    public void Dispose()
    {
        _listening?.Unregister(null);
        _show.Dispose();
        if (IsFirst)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* never acquired */ }
        }
        _mutex.Dispose();
    }
}
