using Teezy.Core.Abstractions;
using static Teezy.Platform.Windows.Native;

namespace Teezy.Platform.Windows;

/// <summary>Types finished text into the focused control with <c>SendInput</c>.</summary>
/// <remarks>
/// <para>
/// <b><c>SendInput</c> is the primary path, not a fallback.</b> The obvious alternative,
/// UI Automation, cannot do this job: <c>TextPattern</c> is documented read-only, and
/// <c>ValuePattern</c> replaces a whole field rather than inserting at the caret — which
/// would silently destroy whatever the user had already typed.
/// </para>
/// <para>
/// <b>Characters are sent as Unicode, not as key codes.</b> With <c>KEYEVENTF_UNICODE</c>
/// the scan code carries the character itself and the virtual key is zero, so the result is
/// independent of the user's keyboard layout — a German or Dvorak layout receives exactly
/// the text we meant. It is also immune to modifier state, which matters here: the user has
/// just released a modifier key, and a layout-dependent path could turn the first character
/// into a keyboard shortcut.
/// </para>
/// </remarks>
public sealed class WindowsTextInjector : ITextInjector
{
    /// <summary>
    /// Newlines cannot be sent as Unicode. A literal U+000A arrives as a control character
    /// that most controls ignore, so line breaks are sent as real Return keystrokes.
    /// </summary>
    private static readonly char[] LineSeparators = ['\n'];

    /// <summary>
    /// Injecting while a modifier is still physically held would let the target app read the
    /// keystrokes as a shortcut. Transcription normally takes long enough that the key is
    /// long since up; this is the guard for when it isn't.
    /// </summary>
    private const int ModifierWaitMs = 300;

    public InjectionResult Insert(string text)
    {
        if (string.IsNullOrEmpty(text)) return InjectionResult.Skipped;
        if (GetForegroundWindow() == 0) return InjectionResult.Failed;

        WaitForModifiersToClear();

        var inputs = new List<INPUT>(text.Length * 2);
        var lines = text.Split(LineSeparators);

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) AppendKey(inputs, 0x0D);   // VK_RETURN

            foreach (var unit in lines[i].Replace("\r", string.Empty, StringComparison.Ordinal))
            {
                // Surrogate pairs need no special handling: each UTF-16 code unit is sent
                // separately and Windows recombines them.
                AppendUnicode(inputs, unit);
            }
        }

        if (inputs.Count == 0) return InjectionResult.Skipped;

        // One call for the whole utterance. Sending per character would let the user's own
        // keystrokes interleave with ours mid-sentence.
        var array = inputs.ToArray();
        var sent = SendInput((uint)array.Length, array, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());

        return sent == array.Length ? InjectionResult.Typed : InjectionResult.Failed;
    }

    private static void AppendUnicode(List<INPUT> inputs, char unit)
    {
        inputs.Add(MakeInput(0, (ushort)unit, KEYEVENTF_UNICODE));
        inputs.Add(MakeInput(0, (ushort)unit, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
    }

    private static void AppendKey(List<INPUT> inputs, ushort vk)
    {
        inputs.Add(MakeInput(vk, 0, 0));
        inputs.Add(MakeInput(vk, 0, KEYEVENTF_KEYUP));
    }

    private static INPUT MakeInput(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags },
        },
    };

    private static void WaitForModifiersToClear()
    {
        var deadline = Environment.TickCount64 + ModifierWaitMs;
        while (Environment.TickCount64 < deadline && AnyModifierDown())
        {
            Thread.Sleep(10);
        }
    }

    private static bool AnyModifierDown() =>
        IsDown(VK_CONTROL) || IsDown(VK_MENU) || IsDown(VK_LWIN) || IsDown(VK_RWIN);

    /// <summary>The high bit of <c>GetAsyncKeyState</c> is the physical down state; the low
    /// bit is "pressed since last call" and must not be tested here.</summary>
    private static bool IsDown(uint vk) => (GetAsyncKeyState((int)vk) & 0x8000) != 0;
}
