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
**Hold Ctrl + Win together, speak, release.** Double-click the tray icon to open the window.

For development, `dotnet run --project src\Teezy.App -c Release` still works.

### Installing on another machine

Three routes, in the order you should reach for them. All three are per-user: no
administrator rights, no services, nothing under Program Files or `HKLM`. That is what makes
Teezy installable on a managed work machine at all.

**1 · The installer.** `tools\build-installer.ps1` compiles `dist\Teezy-Setup.exe` (~147 MB):

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
```

Download it, double-click, done — no wizard pages and no command line. It installs to
`%LOCALAPPDATA%\Programs\Teezy`, adds a Start Menu entry, registers a real uninstaller in
**Apps & features** and launches the app. **One download carries both architectures** and
picks by CPU at install time, so there is no wrong file to choose.

Building it needs Inno Setup 6, which `winget install -e --id JRSoftware.InnoSetup` puts
under `%LOCALAPPDATA%` without elevation. Its compiler now prints *"Non-commercial use
only"* — check the current licence before shipping this commercially.

**The installer does not carry the model**; the app downloads it on first launch. That is
what keeps the download at 147 MB rather than 800 MB, and it is the one step a managed
network can break.

**2 · The offline package, for when it does.** `tools\package.ps1` stages `dist\Teezy-Setup\`
(~790 MB) — both architectures, the model, `install.ps1`, `uninstall.ps1` and
`READ-ME-FIRST.txt`. Add `-Zip` for a single 613 MB file.

```powershell
powershell -ExecutionPolicy Bypass -File tools\package.ps1 -Zip
```

Copy the folder over and run `install.ps1 -Autostart` inside it. Proxies and TLS inspection
routinely kill a 660 MB Hugging Face transfer, and the failure lands on the far machine at
the worst moment — so `package.ps1` and `install.ps1` both verify every model file by size,
because a truncated model does not announce itself.

**3 · No installer at all.** `ModelLocator` searches next to the executable as well as in
`%LOCALAPPDATA%`, so `Teezy.exe` beside `models\parakeet-v2\` runs from any folder — a USB
stick, or a machine where Group Policy blocks scripts outright and `-ExecutionPolicy Bypass`
cannot help. `READ-ME-FIRST.txt` spells that layout out, along with SmartScreen, AppLocker
and a plain-language summary for IT.

**None of it is code-signed**, so the first launch raises SmartScreen's *"Windows protected
your PC"* — *More info*, then *Run anyway*. `install.ps1` clears the mark-of-the-web so the
prompt does not return on every launch; the installer route is unaffected, since Setup writes
the file itself. A machine running WDAC or a publisher allowlist can still refuse outright,
and nothing local fixes that.

**Installing over an older copy re-points the sign-in entry.** Teezy heals its own `Run` value
at launch — but only if it launches, and a value aimed at a path that no longer exists never
does. `install.ps1` fixes it from outside, where both halves are still known.

### Starting at sign-in

Settings ▸ **Start Teezy when I sign in**. It registers under `HKCU\…\CurrentVersion\Run` —
per-user, no administrator rights, and visible in **Task Manager ▸ Startup** where people
already expect to manage startup apps. A scheduled task would have hidden it from the place
users actually look.

Three details that are easy to get wrong, and are not:

- **The OS is the source of truth, not a saved setting.** There is deliberately no
  `Autostart` field in `settings.json`. Windows lets you switch a startup entry off in Task
  Manager, and a mirrored setting would keep showing a tick next to something that no longer
  happens.
- **Writing the `Run` value alone is not enough.** If the entry was ever disabled in Task
  Manager, Windows records that veto in a separate `StartupApproved` key which wins — so
  enabling would appear to succeed and nothing would happen at sign-in. Ticking the box
  clears the veto; unticking it leaves the veto alone, because that was your choice and
  should outlive us.
- **The registered path self-heals.** Republishing to a different folder would otherwise
  leave a `Run` value aimed at a file that no longer exists, and nothing reports a startup
  entry that failed to resolve. Teezy re-points it at launch — comparing the whole command
  line, not just the path, so an entry written by an older version is brought up to date
  rather than left half-right.
- **The sign-in launch is told apart from every other one.** The `Run` value ends in
  `--startup`, and that flag is the only thing that distinguishes "Windows started me" from
  "someone double-clicked me". Without it, the two would have to behave the same, and both
  answers are wrong: a window at every sign-in is a nuisance, and no window at all when you
  have just installed the thing looks broken. So Teezy opens its window on launch **unless**
  the flag is present.

Starting at sign-in loads the model — about 1.6 s and ~900 MB resident, once.

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

## Choosing a microphone

Settings ▸ Microphone picks the device Teezy records from. The default is **Windows default**,
which follows the communications endpoint Windows has chosen — so plugging in a headset
switches to it automatically, and for most people that is the right answer permanently.

It exists for the case Windows gets wrong, and that case does not announce itself. A laptop
that keeps choosing its built-in far-field array over the headset you are speaking into does
not fail: it records the room along with you, and the transcript comes back subtly wrong in a
way that reads as a bad recogniser rather than a bad input. Nothing in the pipeline can
recover words the microphone did not capture cleanly.

**"Check it is hearing you"** opens the selected device and shows the live level, because a
picker alone cannot tell you whether you chose correctly — device names are not descriptions.
The verdict distinguishes *quiet* from *nothing*, which is the distinction that matters:

- **Nothing at all** is almost always the Windows privacy setting. When "Let desktop apps
  access your microphone" is off, WASAPI opens the device and returns digital zeroes forever.
  Nothing throws, nothing logs, and the meter simply never moves — which reads as a broken
  app, so the test names the real cause instead of leaving you to guess.
- **Quiet but present** is a placement or gain problem, and speaking up genuinely fixes it.

The test releases the device when you leave the page, close the window, or after thirty
seconds, so a forgotten test never leaves the recording indicator lit in the tray.

**A device is stored by its endpoint id, not its position in the list.** Device order changes
the moment anything is plugged in, so an index saved on Tuesday points at the webcam on
Wednesday. The friendly name is stored alongside it, but only so an absent device can be named
in Settings rather than shown as an opaque id.

**An unplugged microphone falls back to the Windows default rather than failing.** A chosen
headset that is in another bag must not turn the hotkey into a dead key. It stays selected —
plugging it back in is all it takes — and Teezy says once per run that it is using something
else, because falling back *silently* would recreate the exact problem the picker exists to
solve.

---

## How it fits together

```
    hold Ctrl+Win ──► WindowsHotkeySource ──► DictationController ◄── TeezySettings
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
| `Teezy.Cleanup` | `net10.0` | Optional Claude cleanup tier |
| `Teezy.Platform.Windows` | `net10.0-windows` | The only Win32 code in the repo |
| `Teezy.App` | `net10.0-windows` | WPF tray app, HUD, and the four-page window |

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

### When it is slow somewhere else

Those numbers are one machine. On a throttled corporate laptop the same work can take several
times longer, and until recently Teezy could not say *why* — it recorded a single number for
everything between releasing the key and seeing text, so the model, the network and the target
app were indistinguishable.

**Insights now breaks the wait into transcribe, cleanup and type-it-in**, as medians with the
worst case called out separately. Medians because one dictation that hits the six-second
cleanup timeout drags a mean somewhere no individual dictation ever was. The realtime factor
is the figure worth comparing between machines, since it divides out how long you talked for.

**Settings ▸ Speech model ▸ Check this machine** sweeps thread counts and keeps the fastest.
Two things it is careful about:

- **It compares thread counts against each other, not absolute speed.** The audio is
  synthesised, so the encoder — which dominates and costs the same whatever the audio
  contains — is exercised honestly, while the decoder emits fewer tokens than real speech
  and finishes early. The ratios mean something; the milliseconds are a floor. The real
  figure comes from Insights, measured on actual dictations.
- **It refuses to change the setting for a margin it cannot stand behind.** A 5% floor, on a
  best-of-two benchmark, is about where a real difference stops being another process
  borrowing the CPU. A run of this sweep produced 4 threads at 172 ms and 6 at 173 ms; an
  earlier run had called 6 a 7.3% win. Same machine, pure noise — and the threshold is what
  stops that becoming a settings change.

The sweep always tries the machine's own core count. The first version trimmed its ladder from
the top and never tested 8 threads on an 8-core machine, which is the one value the table
above says is worth knowing about.

**It also always tries 2 threads, which is less obvious.** The ladder used to drop 1 and 2
first, on the reasoning that low counts are never the answer on a machine with more cores.
That holds on a homogeneous CPU and is wrong on a hybrid one. An Intel Core Ultra 5 135U
reports 14 processors but has **two** performance cores, the rest being E-cores and low-power
E-cores — so asking for 4 threads either sets hyperthread siblings fighting over the same
vector units or spills the graph onto cores several times slower, and a parallel region
finishes at the speed of its slowest thread. On that machine the sweep ran 4, 6, 8 and 14, and
never tried the count most likely to win. On any hybrid chip the performance-core count is a
live candidate, and it is usually 2.

**Thread count is the only part of a slow machine a setting can fix.** A throttled CPU, a
corporate proxy in front of the Claude tier, and endpoint security sitting on the microphone
all look identical from inside the app, so the check reports what it cannot help with rather
than quietly changing a number and leaving you no faster.

---

## Look and feel

Warm paper and ink against a cool, saturated accent: a serif for display type, a humanist
sans for everything else. Quiet enough to live in the tray all day without competing with
whatever the user is actually working on.

`Theme.xaml` is the whole design system — palette, type scale, and templates for every
control the app uses. **Views must not contain literal colours**; there are none left. That
is not tidiness for its own sake: it is what lets the tray icon, the floating meter and four
pages be recoloured together and stay one product.

**The accent is taken from the mark, not chosen next to it.** `Accent` `#014AFD` is the blue
the sound waves are drawn in and `AccentInk` `#041945` is the navy of the speech bubble, so
the app cannot drift away from its own logo. Both pairings were checked rather than eyeballed
— white on `Accent` is 6.2:1 and `AccentInk` on `AccentSoft` is 14.6:1, clearing AA for body
text and not merely for the large type they mostly carry.

The mark's amber sparkle is deliberately **not** in the general palette. It lives as
`MarkSparkle`, for the logo alone: promoting the one warm note in the brand to a UI accent
would set it beside the recording red, which is precisely what the palette is arranged to
prevent. The brand art itself is in `Logo\`.

**Red is reserved.** It means recording and appears nowhere else, which is why the accent is
a blue — a warm accent would compete with the one signal the user must read instantly.

WPF ships dated chrome, so the controls are re-templated: switches instead of tick boxes for
preferences, a segmented control where two options are two shapes of one thing, a slim
overlay scrollbar, and a custom combo box. Stock WPF controls were the single biggest thing
making this look like a tool rather than a product.

The mark is speech flowing into a text bubble and coming out cleaned up. It is authored in
`Logo\teezy-icon.svg`, and `Theme.xaml` carries the same coordinates so the artwork and the
app can be diffed by eye.

**There are two of it, and that is not a compromise.** `MarkImage` is the logo as drawn,
used where there is room for it: the first-run window and the empty state. `MarkGeometry` is
a silhouette of the bubble with the text lines knocked out, used for the nav rail, the tray
and the `.ico`. At 16 px the bubble's stroke lands on two thirds of a pixel, the three waves
collapse into each other, and the sparkle and cursor vanish — a shrunken logo is a grey
smudge, so the small sizes get a drawing that was designed to be small.

**Nothing redraws the mark a second time.** The tray used to be hand-written GDI+ rectangles
that happened to match the XAML, and they matched only while someone remembered to change
both. `TrayIcons` now renders the real resource out of the dictionary, and
`tools/make-icon.ps1` loads `Theme.xaml` and renders the same one — including the tile inset,
corner radius and glyph scale, which live in the dictionary rather than in either renderer.
Neither hardcodes the glyph's bounds either: they take them from the geometry, so re-drawing
the mark re-centres it.

**`Teezy.ico` is the one binary asset, and it is generated rather than drawn.** Windows reads
the icon from the PE file, not from the running process — so Explorer, the Start Menu,
Alt-Tab and taskbar pinning all show the generic executable icon unless `ApplicationIcon` is
set, no matter what the app renders at runtime. Nine sizes from 16 to 256; regenerate with
`tools/make-icon.ps1` after changing the mark or the accent colour.

Window icons are deliberately **not** assigned in code. WPF falls back to the executable icon
resource, which carries every size, so Windows picks the right one per context; assigning a
single rendered bitmap would leave the taskbar one size to scale from.

---

## Decisions worth knowing

**Ctrl + Win is the default, and it is a chord for a reason.** The Windows key alone opens
the Start menu when released; held together with Ctrl, Windows treats the pair as a chord
and does not. Verified against the real hook — the foreground window is unchanged across a
full press and release. Neither key produces a character, and neither alone fires dictation.

**No Shift-only combination is offered.** Holding either Shift for eight seconds raises the
Windows Filter Keys prompt, and a push-to-talk hold routinely runs longer than that. Shift
can still be recorded as part of a custom combination, with a warning — it is the user's
keyboard, and someone who has already turned Filter Keys off should not be blocked.

**Right Alt is warned about, not banned.** It is AltGr on German, Polish, UK, Nordic and most
Latin-American layouts — how those keyboards type `@`, `€`, `\` and `|`.

**All the combination logic lives in `Teezy.Core`.** The keyboard hook is untestable, but
press order, auto-repeat, partial release and a key held on both sides of the keyboard at
once are exactly where the bugs are — so `HotkeyMatcher` is platform-neutral and covered by
tests, and the Win32 layer only translates key events into `HotkeyKey` values.

**Each slot tracks *which* physical keys satisfy it, not merely whether one does.** Hold both
Ctrl keys, release one, and a Ctrl is still physically down — the hold must not end
mid-sentence.

**The hotkey is observed, never swallowed.** Every hook callback ends in `CallNextHookEx`.
Suppression would buy nothing and risks a much worse failure: if a key-down is consumed but
the key-up escapes, the foreground app believes Ctrl is held down forever.

**Left and right are separated by the extended-key flag, not the virtual key code.** A
low-level hook may report either `VK_CONTROL` or `VK_RCONTROL` depending on the keyboard
driver, so both are accepted and the flag decides. Right Shift is the exception — it is not
an extended key and is identified by its scan code instead.

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

## Smarter cleanup with Claude (optional, off by default)

A second cleanup pass that fixes grammar, formats lists and honours spoken corrections.
**Off by default, and that default is the honest one** — it is the only thing in Teezy that
leaves your machine.

- **A Claude Pro or Max subscription does not cover this.** The Anthropic API is billed
  separately, pay-as-you-go from prepaid credits at `console.anthropic.com`. Roughly
  **$0.60 a month** at 400 dictations on Sonnet 5; Haiku 4.5 about a third of that, Opus 5
  about double.
- **The real price is latency.** Dictation is ~170 ms end to end today; a round trip adds
  about a second to every utterance.

### What it actually costs you

**Insights shows measured spend, not the estimate above.** Every response carries a `usage`
block, so Teezy records the tokens each dictation consumed and prices them locally. There is
no endpoint that reports account spend to an ordinary API key — the Console has that — but
this is the more useful figure anyway: it is what *Teezy* cost, not what the account cost.

**Prices are dated, because one of them changes.** Sonnet 5 runs an introductory rate until
2026-08-31 and goes up by half after it. A single hardcoded number would misreport every
entry on one side of that date or the other, so each dictation is priced against the rate
that applied on the day it was spoken, and the cost is derived rather than stored — fixing
the table fixes history instead of leaving a stale figure baked into the log.

**It refuses to guess.** A model the table does not know prices to `null`, not zero, and the
card says how many calls it could not price rather than dropping them from a total that would
then look complete.

### Writing style

How much licence the pass has to change your words: **Faithful** (fix the transcript, leave
the writing alone), **Polished** (tighten waffle, keep the voice), **Formal**, **Casual** —
plus one instruction of your own appended to every request. None of them may add content,
answer the text, or change the meaning; only the register and the tightening move.

**The plausibility guard moves with the style, and has to.** A style told to cut waffle
legitimately returns text at 40% of the input length, which the fixed floor rejected — and
the fallback is silent, so the setting would have looked like it did nothing. The floor is
now per-style. The ceiling is not: text that doubled is the model answering, whatever was
asked for.

Your own instruction goes **last** and is explicitly subordinate to the rules above it. It is
your text on your machine, not untrusted input — but "rewrite my dictation" and "answer my
dictation" are one careless sentence apart, and the ordering plus the guard mean a badly
worded line degrades to a fallback rather than typing an answer into whatever had focus.

### Per-app rules

A style that applies only where the text is going: Outlook formal, Teams casual, the editor
faithful with no trailing full stop. An email and a chat message should not have to share one
setting, and remembering to change a global one before each is worse than not having it.

Teezy already knew which app it was typing into — that is what the Insights breakdown is —
so the rules list offers those apps to pick from rather than asking you to know that Outlook
reports itself as `OUTLOOK`.

Three decisions in the matching, each the less clever option on purpose:

- **Exact process name, case-insensitive**, `.exe` ignored. Substring matching would make a
  rule for `code` quietly capture `vscode`, which is unpredictable from the list you are
  looking at.
- **First match wins, in the order shown.** A rule can shadow one below it — visibly, rather
  than by some hidden precedence.
- **A rule replaces the global instruction rather than adding to it.** Two instructions
  arriving together is how you get contradictory ones.

**The foreground app is now read before cleanup, not after.** It has to be, for a rule to
have anything to act on — and it is the more truthful moment anyway: what had focus when the
words were spoken, rather than wherever focus drifted during a second of network round trip.
The old comment worried about reading it *after injection*, which is a different and genuinely
too-late moment.

**The offline rules always run first and their output is the floor.** Claude is asked to
improve an already-cleaned string, and every failure path — no key, no network, rate limit,
timeout, refusal — returns the offline result. Dictation is a foreground interaction: it must
never fail, and it must never be worse than with the tier switched off.

**The reply is validated before it is typed.** An LLM handed a transcript will sometimes
*answer* it — "should we ship Friday?" comes back as advice about Fridays — and typing that
into your document is a far worse failure than leaving an "um" in. Replies wildly shorter or
longer than the input, empty, or fenced are discarded in favour of the offline text.

**The API key is not in `settings.json`.** It is encrypted with DPAPI for your Windows account
under `%LOCALAPPDATA%\Teezy\secrets`, which makes it useless on another machine or to another
user. Not a vault — anything running as you can decrypt it — but the key never appears in
plain text on disk or in a settings file someone opens in an editor.

**Settings shows the key's last four characters, and that is load-bearing.** Saving clears the
box and never pre-fills it, so with nothing to show, a save that worked and a save that did
nothing look identical — an empty box either way. Printing `sk-ant-…9gAA` makes the
confirmation about the key you just pasted rather than a reassuring sentence.

**"A key is saved" means it was decrypted, not that a file exists.** `ISecretStore.Describe`
reads and masks the secret, which is what produces that hint; the check it replaced only asked
`File.Exists`, so a file that would not decrypt still counted as a saved key and the page
happily said so while cleanup fell back to the offline rules. Saving also reads the key
straight back, because `Write` is void and a store can accept bytes it cannot return.

---

## Personal dictionary

Edited in the app, on its own page. Two kinds of entry:

- **A correction** — "when you hear X, write Y". Applied *after* transcription, so the
  spelling is guaranteed.
- **A hint** — a word the engine should know exists. Biasing only, so it improves the odds
  and promises nothing.

That distinction is the whole reason corrections exist, and it is why they run even when
cleanup is switched off.

**Hints need beam search, and they did nothing at all until they got it.** They were written
to the file, listed on the dictionary page and documented here as biasing the engine — and
read by no code whatsoever. Making them real took three things, each of which failed
silently on its own:

1. **Beam search.** Greedy decoding keeps no alternative transcripts, so there is nothing for
   a bias to re-rank. Hints are ignored under it, without a word of complaint. Settings ▸
   **Decoding**.
2. **Tokenised hints.** The Parakeet export ships `tokens.txt` and no `bpe.model`, so
   sherpa-onnx cannot split a word itself — it looks up each piece of a hint directly in the
   vocabulary, fails on `Phoebe`, and skips it. `HotwordEncoder` does the splitting: greedy
   longest-match against the 1025-piece SentencePiece vocabulary, `▁ph oe be`.
3. **A rebuild.** Hints are compiled into the recogniser, and the C# binding has no
   per-stream override for offline models. Saving the dictionary rebuilds it — but only when
   the *hints* changed, since editing a correction should not cost a model load.

**Hint strength is a real trade, measured rather than guessed.** On the model's own test
clip, `1.5` changed the transcript not at all and `2.5` changed it for the worse — stray
apostrophes around the biased word. Bias hard enough and the engine hears your hinted words
in audio that never contained them, which is a worse failure than the misspelling it was
meant to fix: a name in the wrong place is harder to spot than one spelled wrong.
`tools\hotword-probe` decodes a clip with and without hints so the setting can be tuned
against evidence instead of feel.

**Entries are checked as you type them**, before they are added. A correction rewrites text
silently and after the fact, which makes a bad rule genuinely hard to notice — the
transcript is simply wrong in a plausible way. So a single-word trigger that is an ordinary
English word (`code`, `like`, `state`) is flagged, as is one that rewrites a word to itself.

The self-rewrite check is **case-sensitive on purpose**: `kubernetes → Kubernetes` changes
the output and is one of the most common reasons to add an entry at all. Comparing
case-insensitively would have called it useless, which a test now prevents.

Corrections apply longest-trigger-first and match whole words only, so `cloud code` never
touches `Cloudflare`. Glued and hyphenated forms (`CloudCode`, `cloud-code`) still match.

The file stays plain text at `%LOCALAPPDATA%\Teezy\dictionary.txt`, hand-editable, and is
reloaded automatically when changed on disk. "Open as text file" is still in the corner for
bulk edits, which a row-at-a-time UI is genuinely worse at.

```
Anthropic                    # a hint: bias the engine toward this spelling
cloud code -> Claude Code    # a correction: rewrite the left side to the right
# off: teezy -> Teezy        # disabled, kept for later
```

---

## Building and testing

```powershell
dotnet build Teezy.slnx -c Release
dotnet test  tests\Teezy.Core.Tests
```

114 tests cover the formatter, the dictionary and its warnings, hotkey combinations and
settings migration, the state machine, the level
mapping and the
history and stats — including the
re-entrancy guard that stops a second key press during transcription from typing the
utterance twice.

Two things tests structurally cannot cover, both verified by hand instead:
**text injection into a foreground window** (round-tripped through a real text box,
including `é`, `—` and newlines) and **Right-vs-Left Ctrl discrimination** (verified with
synthesised key events).

---

## Not built yet

1. **Command mode.** Select text, hold a second key, "make this more formal."
2. **Code signing.** `install.ps1` needs no elevation and clears the mark-of-the-web, but the
   executable itself is unsigned — so SmartScreen warns on first launch, and a machine running
   WDAC or a publisher-allowlist policy can refuse it outright with nothing we can do locally.
3. **Elevated-window injection.** A non-elevated process cannot type into an elevated
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
