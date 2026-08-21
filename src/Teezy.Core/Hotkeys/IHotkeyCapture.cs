namespace Teezy.Core.Hotkeys;

/// <summary>Records the next combination the user holds, for the settings picker.</summary>
/// <remarks>
/// Separate from <c>IHotkeySource</c> because it is a settings-time concern, not part of
/// dictation. While a capture is active the source stops reporting presses, so the hotkey
/// cannot fire dictation while the user is busy choosing it.
/// </remarks>
public interface IHotkeyCapture
{
    bool IsCapturing { get; }

    /// <summary>Invokes <paramref name="onCaptured"/> once the user releases everything.</summary>
    void BeginCapture(Action<Hotkey> onCaptured);

    void CancelCapture();
}
