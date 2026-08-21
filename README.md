# Teezy

Push-to-talk dictation for Windows. Hold a key, talk, release — cleaned-up text is typed
into whatever had focus. Fully on-device: after the one-time model download, nothing leaves
the machine.

Everything dictated is kept, searchable, in an app window with usage stats — because the
text goes into *someone else's* app, and when that app eats it, mangles it, or you simply
want it again, history is the only place it still exists.

**Status:** working end to end. Built and verified on Windows 11 ARM64 (Snapdragon X Plus).

---

## Install

Build a single self-contained executable — no .NET runtime, no SDK, no loose files:

```powershell
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

That writes `dist\win-x64\Teezy.exe` (80 MB) and `dist\win-arm64\Teezy.exe` (75 MB), then
verifies the architecture actually written rather than trusting the flag — an ARM64 native
library inside an x64 exe fails at load, on someone else's machine, with no useful message.

Copy the one matching the target CPU: **ARM64** for Snapdragon and Surface Pro X, **x64** for
Intel and AMD. The x64 build runs on ARM64 through emulation, but transcribes far slower.

**The model is not bundled.** On first launch the app downloads it (~661 MB, once) behind a
progress window, verifies every file, and only then starts. Nothing touches the network
afterwards. `tools\download-model.ps1` does the same from a shell if you prefer.

Teezy lives in the system tray — click the `^` arrow next to the clock if you cannot see it.
**Hold Right Ctrl, speak, release.** Double-click the tray icon to open the window.

For development, `dotnet run --project src\Teezy.App -c Release` still works.

---

## The window

Teezy runs from the tray and never needs its window, so the window is built for the two
moments you actually want it.

**Home** is the history: every dictation, newest first, grouped by day, searchable. Hover a
row to copy or delete it. Entries are recorded **even when injection failed** — that is
precisely when you need the text back, because it did not land anywhere you can reach.

**Insights** is the aggregate: words per minute, dictionary fixes, total words, where the
text went, and a 26-week activity grid.

Two numbers are easy to compute dishonestly, so both are defined deliberately:

- **Words per minute is weighted by time, not by utterance.** A plain mean lets a two-word
  "yes" count as much as a two-minute paragraph, which flatters short bursts badly.
- **Time saved is measured against 40 wpm typing**, stated on the card rather than hidden.
  If you type quickly, read it as an upper bound.

History is JSON Lines at `%LOCALAPPDATA%\Teezy\history.jsonl` — appended one line per
utterance, so a crash mid-write can damage at most the last entry, and a torn final line is
skipped rather than failing the whole file. Stats are always recomputed from it rather than
accumulated, so deleting an entry corrects them instead of leaving them drifted.

---

## How it fits together

```
 hold Right Ctrl ──► WindowsHotkeySource ──► DictationController ◄── TeezySettings
                                                    │
                                  ┌─────────────────┼─────────────────┐
                                  ▼                 ▼                 ▼
                         WindowsAudioCapture    HudWindow    ParakeetTranscriber
                          16 kHz mono f32                    (sherpa-onnx, CPU)
                                  │                                   │
                                  └──────── AudioChunk ───────────────┘
                                                    │
                                                 (text)
                                                    ▼
                                          RuleBasedFormatter
                                                    ▼
                                          DictionaryCorrector
                                                    ▼
                                        WindowsTextInjector ──► focused app
```

| Project | Target | Contains |
|---|---|---|
| `Teezy.Core` | `net10.0` | State machine, formatter, dictionary, history, stats, settings |
| `Teezy.Speech` | `net10.0` | Parakeet via sherpa-onnx |
| `Teezy.Platform.Windows` | `net10.0-windows` | The only Win32 code in the repo |
| `Teezy.App` | `net10.0-windows` | WPF tray app, HUD, main window, settings |

`Teezy.Core` targets plain `net10.0` deliberately: `CA1416` is escalated to a build error,
so any Win32 call that drifts into the testable layer fails the build rather than quietly
becoming code the tests cannot reach.

---

## Measured on this machine

Snapdragon X Plus (8 cores, ARM64), 16 GB, everything CPU-only.

| | |
|---|---|
| Model load | **1.65 s**, once at startup |
| Transcription, 7.4 s of audio | **250 ms** — 30x realtime |
| Typical 5 s utterance | **~170 ms** |
| Text injection | **0.2–0.35 ms/char** — 1280 chars in ~270 ms |
| Resident memory, model loaded | ~800 MB |

Four inference threads measured fastest; **eight measured slower**. That is why
`NumThreads` defaults to 4 rather than to core count.

---

## Decisions worth knowing

**Right Ctrl is the default hotkey, and Right Alt is not offered at all.** Right Alt is
AltGr on German, Polish, UK, Nordic and most Latin-American layouts — it is how those users
type `@`, `€`, `\` and `|`. Right Ctrl produces no character on any layout.

**The hotkey is observed, never swallowed.** Every hook callback ends in `CallNextHookEx`.
Suppression would buy nothing and risks a much worse failure: if a key-down is consumed but
the key-up escapes, the foreground app believes Ctrl is held down forever.

**Left Ctrl must not match.** A low-level hook may report either `VK_CONTROL` or
`VK_RCONTROL` depending on the driver, so both are accepted and the *extended-key flag* is
what actually decides. Treating a bare `VK_CONTROL` as a match would arm dictation on every
copy and paste on the machine. There is a test for this.

**The HUD must never take focus.** Text is injected into whatever had keyboard focus, so if
the overlay ever became active there would be nothing left to type into. Three independent
mechanisms enforce it — `ShowActivated="False"`, `WS_EX_NOACTIVATE`, and
`IsHitTestVisible="False"` — because each alone has a gap.

**`SendInput` is the primary path, not a fallback.** UI Automation cannot do this job:
`TextPattern` is read-only and `ValuePattern` replaces a whole field rather than inserting
at the caret. Characters are sent as Unicode, so the result is independent of keyboard
layout. The clipboard is never touched.

**The cleanup pass is tuned for what Parakeet actually emits.** Unusually for an ASR model,
Parakeet TDT v2 produces punctuated, sentence-cased text. So cleanup is not adding
punctuation from scratch — it removes disfluency and honours spoken commands, and every
rule is idempotent so it cannot fight the model.

**The dictionary runs even when cleanup is off.** Engine biasing only improves the odds of a
spelling; the correction pass is what guarantees it. Making it switchable off alongside
cleanup would silently remove the guarantee.

**Audio buffers are copied, never borrowed.** NAudio reuses its buffer the instant the
handler returns. A chunk that borrowed it would be rewritten underneath the consumer — and
the symptom is a garbled transcript under load, not a crash.

**CPU-only inference, deliberately.** sherpa-onnx ships no GPU package; DirectML forbids the
variable-length tensor shapes this model needs; CUDA would force a toolkit install on every
user. At 30x realtime none of it is worth it.

---

## Personal dictionary

Plain text at `%LOCALAPPDATA%\Teezy\dictionary.txt`, reloaded automatically when saved.

```
Anthropic                    # bias the engine toward this spelling
cloud code -> Claude Code    # rewrite the left side to the right
# off: teezy -> Teezy      # disabled, kept for later
```

Corrections apply longest-trigger-first and match whole words only, so `cloud code` never
touches `Cloudflare`. Glued and hyphenated forms (`CloudCode`, `cloud-code`) still match.

---

## Building and testing

```powershell
dotnet build Teezy.slnx -c Release
dotnet test  tests\Teezy.Core.Tests
```

63 tests cover the formatter, the dictionary, the state machine, the level mapping and the
history and stats — including the
re-entrancy guard that stops a second key press during transcription from typing the
utterance twice.

Two things tests structurally cannot cover, both verified by hand instead:
**text injection into a foreground window** (round-tripped through a real text box,
including `é`, `—` and newlines) and **Right-vs-Left Ctrl discrimination** (verified with
synthesised key events).

---

## Not built yet

1. **LLM cleanup tier.** `ITextFormatter` is the seam; a Claude-backed formatter would add
   tone, list formatting and spoken corrections. Costs an API key and a network round trip,
   so the app would stop being fully offline.
2. **Command mode.** Select text, hold a second key, "make this more formal."
3. **A settings UI for the dictionary.** It opens in Notepad today.
4. **Installer and autostart.**
5. **Elevated-window injection.** A non-elevated process cannot type into an elevated
   window. Elevating Teezy would be worse than the problem.

---

## Other platforms

`Teezy.Core` and `Teezy.Speech` (~800 lines — state machine, cleanup, dictionary,
Parakeet) are plain `net10.0` and port unchanged. The remaining ~1,950 lines are the hotkey,
injection, tray and overlay, and those are Windows-specific by nature. sherpa-onnx ships
native runtimes for macOS, Linux, Android and Windows alike, so **the engine is never the
obstacle — the platform integration is.**

| Target | Engine | The app around it |
|---|---|---|
| Windows x64 / ARM64 | ✅ | ✅ shipping |
| macOS | ✅ | Rewrite: `CGEventTap`, AX insert, menu bar. Needs Accessibility permission. |
| Linux | ✅ | X11 workable; **Wayland blocks global hotkeys and synthetic input by design.** |
| Android | ✅ | Only as a custom keyboard (IME) — see below. |
| iOS / iPadOS | ❌ | Not possible. No third-party app gets system-wide text injection. |

**Android, deliberately postponed.** Three findings, worth keeping so the question does not
get re-litigated from scratch:

1. **It cannot be push-to-talk anywhere.** Android grants no app system-wide text injection.
   The only route is an IME the user switches to — a different product, not a port.
2. **The model likely does not fit.** Parakeet 0.6B int8 needs ~2 GB resident, and Android
   reclaims IME processes aggressively. The lighter 110M Parakeet variants are published
   **only as fp32 ONNX** — no int8 export exists — so there is no drop-in smaller model.
   Hosting the model in a bound `Service` would mitigate this without eliminating it.
3. A PC-backed design (phone records, this app transcribes over the LAN) sidesteps both, at
   the cost of the offline guarantee and requiring the desktop to be awake.

---

## Third-party notices

Speech model: **NVIDIA Parakeet TDT 0.6B**, exported to ONNX and quantized to int8 by
`csukuangfj`. Weights are **CC-BY-4.0** — commercial use is permitted with attribution, and
the quantization and ONNX export are modifications. Model card:
<https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2> · Licence:
<https://creativecommons.org/licenses/by/4.0/>

**sherpa-onnx** is Apache-2.0. **ONNX Runtime** is MIT. **NAudio** is MIT.

The architecture and several hard-won constants were informed by
[per-simmons/murmur-youtube](https://github.com/per-simmons/murmur-youtube), whose Windows
directory is a specification rather than an implementation. That repository carries **no
licence**, so no code was copied from it — only independently re-verified facts.
