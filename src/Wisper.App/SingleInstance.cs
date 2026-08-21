using System;
using System.Threading;

namespace Wisper.App;

/// <summary>Ensures only one Wisper runs per logon session.</summary>
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
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = "Wisper.SingleInstance.6f1c2a";

    private readonly Mutex _mutex;

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
        if (IsFirst)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* never acquired */ }
        }
        _mutex.Dispose();
    }
}
