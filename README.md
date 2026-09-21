# TeezyFlow

*A Teezy Labs project, called Teezy until 1.11. Only what you see was renamed: the exe is still
`Teezy.exe`, and settings, history, keys and the model still live in `%LOCALAPPDATA%\Teezy`,
so an existing install upgrades in place with nothing to move.*

Push-to-talk for Windows. Hold a key, talk, release — cleaned-up text is typed into whatever
had focus. Hold a *different* key and it does what you said instead: opens an app, changes the
volume, skips a track, locks the PC, and can answer you out loud. It also records a meeting and
transcribes it once the call is over. **Local by default** — after the one-time model download,
dictation, commands, meeting transcription, your dictionary and the built-in voice all run
on-device and stay there. Six optional tiers are the exception — four Claude, one ElevenLabs,
and an encrypted sync file in your own OneDrive — every one of them off until you switch it on.
The only request TeezyFlow makes by itself is a check for updates against this repository.

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

TeezyFlow lives in the system tray — click the `^` arrow next to the clock if you cannot see it.
**Hold Ctrl + Win together, speak, release.** Double-click the tray icon to open the window.

For development, `dotnet run --project src\Teezy.App -c Release` still works.

### Installing on another machine

Three routes, in the order you should reach for them. All three are per-user: no
administrator rights, no services, nothing under Program Files or `HKLM`. That is what makes
TeezyFlow installable on a managed work machine at all.

**1 · The installer.** `tools\build-installer.ps1` compiles `dist\TeezyFlow-Setup.exe` (~147 MB):

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

**2 · The offline package, for when it does.** `tools\package.ps1` stages `dist\TeezyFlow-Setup\`
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

**Installing over an older copy re-points the sign-in entry.** TeezyFlow heals its own `Run` value
at launch — but only if it launches, and a value aimed at a path that no longer exists never
does. `install.ps1` fixes it from outside, where both halves are still known.

### Starting at sign-in

Settings ▸ **Start TeezyFlow when I sign in**. It registers under `HKCU\…\CurrentVersion\Run` —
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
  entry that failed to resolve. TeezyFlow re-points it at launch — comparing the whole command
  line, not just the path, so an entry written by an older version is brought up to date
  rather than left half-right.
- **The sign-in launch is told apart from every other one.** The `Run` value ends in
  `--startup`, and that flag is the only thing that distinguishes "Windows started me" from
  "someone double-clicked me". Without it, the two would have to behave the same, and both
  answers are wrong: a window at every sign-in is a nuisance, and no window at all when you
  have just installed the thing looks broken. So TeezyFlow opens its window on launch **unless**
  the flag is present.

Starting at sign-in loads the model — about 1.6 s and ~900 MB resident, once.

### Updates

TeezyFlow updates itself the way Fivebar does. It checks this repository's releases 45 seconds
after launch and every four hours, downloads a newer `TeezyFlow-Setup.exe` in the background,
and then says so: a bar under the tabs — *"TeezyFlow 1.13.0 is ready to install… Restart now"* —
a line in the tray menu, and one tray notification. **Restart now** installs silently and starts
TeezyFlow again; if it is never pressed, the update installs the next time you quit. Settings ▸
About has **Check for updates**.

- **A download is only run if it matches.** GitHub publishes each asset's SHA-256; the
  installer is checked against it and against its size, and anything else is deleted.
- **No SmartScreen prompt on an update.** A file the app downloads itself carries no
  mark-of-the-web, unlike one from a browser.
- **Only the installed copy updates.** A build run from the repo or `dist\` never replaces
  the installed app.
- **No switch to turn it off,** as in Fivebar: an app that is always running is exactly the one
  that never gets updated by hand.

The first version with this (1.13.0) has to be installed by hand once; every later one arrives
by itself.

### Your other computers

**Settings ▸ Sync** keeps your keys, settings and dictionary the same on every computer,
through one encrypted file in a folder they can all see — a `TeezyFlow` folder in OneDrive is
ideal. Choose the folder and a passphrase on the first computer; on each of the others, choose
the same folder and type the same passphrase, and that computer takes the setup.

- **What travels:** the Anthropic and ElevenLabs keys, the Google client secret, the Gmail app
  password, calendar links, hotkeys, the cleanup and assistant choices, writing styles and
  per-app rules, the voice, and the dictionary.
- **What stays:** the microphone, the thread count tuned to that processor, where the model
  lives, and signed-in accounts — sign in to Microsoft or Google once on each computer (the
  app ids are built in, so there is nothing to paste). A calendar file from Power Automate
  stays on the computer it was added on.
- **The file is unreadable without the passphrase:** PBKDF2-SHA256 at 600,000 iterations,
  then AES-256-GCM. The passphrase is kept on each computer under DPAPI, so it is typed once
  per machine — and it cannot be recovered. Forget it and the answer is to turn sync off
  everywhere and start again.
- **The newest file wins, whole.** Two computers changing different things while both are
  offline would lose one set of changes. For one person with a few machines that is simpler to
  trust than a merge.

**Why the keys are not simply built in:** the repository and its releases are public, and a
key compiled into a public installer is a key anyone can pull out and spend. The Microsoft and
Google *app ids* are built in (`BuiltInApps.cs`) because they are identifiers shown on every
sign-in page, not secrets.

### A work calendar that allows nothing else

When a work calendar can be neither signed in to (the organisation will not consent to an app
it has not approved) nor published (IT has switched publishing off), there are two routes left.

**1 · Read New Outlook on this computer — works when everything else is blocked.** Settings ▸
Accounts ▸ *Read my calendar from Outlook*. New Outlook labels every meeting for screen readers
with its subject, times, date and location; TeezyFlow reads those labels every three minutes,
the way a screen reader does, and keeps a copy on this computer. No API, no sign-in, no flow,
nothing installed in Outlook, and nothing for IT to approve. The copy never leaves the laptop and
never syncs.

- **Leave Outlook open on the Calendar, in Week or Month view.** Behind other windows is fine —
  measured: fully covered for 25 seconds, still read in full. **Minimised is not:** Outlook
  discards its view, and TeezyFlow keeps the last copy and says how old it is.
- **It sees what is on screen.** A month with more meetings on a day than fit in its box shows
  "+2", and those two are not read — Week view shows everything.
- Each read replaces what was stored for the days it saw and keeps the rest, so switching views
  loses nothing, and a meeting deleted from a day on screen is gone at the next read.
- Proven against real New Outlook labels, US and Australian date formats, and subjects with
  commas in them (`OutlookLabel`, `OutlookWatcher`).

**2 · Power Automate, where the organisation allows its Microsoft 365 connectors.** Many block
them outright (data loss prevention policy), in which case saving the flow reports it as blocked
and route 1 is the answer. Where they are allowed, a flow writes the diary to a file in your work
OneDrive every 15 minutes, and TeezyFlow reads that file — Settings ▸ Accounts ▸ *Or read a
calendar file*:

1. In the work OneDrive on the laptop, make a folder `TeezyFlow` and an empty file in it called
   `calendar.json` (New ▸ Text document, then rename it).
2. Go to **make.powerautomate.com**, signed in with the work account. **Create ▸ Scheduled cloud
   flow**, name it *TeezyFlow calendar*, repeat every **15 minutes**.
3. **New step ▸ Office 365 Outlook ▸ Get calendar view of events (V3).** Calendar id:
   *Calendar*. Start time: the expression `utcNow()`. End time: the expression
   `addDays(utcNow(), 14)`.
4. **New step ▸ OneDrive for Business ▸ Update file.** File: pick `/TeezyFlow/calendar.json`.
   File content: the expression `string(body('Get_calendar_view_of_events_(V3)'))`.
5. **Save**, then **Test ▸ Manually**. `calendar.json` should fill with your week.
6. In TeezyFlow on the work laptop: **Settings ▸ Accounts ▸ Or read a calendar file ▸ Choose…**,
   pick that `calendar.json`, **Add file**.

If saving the flow says a connector is blocked by your organisation's data policy, use route 1
instead. If the file stops changing, the flow has stopped — its run history says why.

---

## The window

TeezyFlow runs from the tray and never needs its window, so the window is built for the two
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

## The assistant

A second combination, off until you set one in Settings ▸ Assistant. Hold it, say what you
want, and TeezyFlow does it rather than typing it.

| | |
|---|---|
| Open or focus an app | "open Chrome", "switch to Outlook" |
| Volume | "volume up", "set the volume to 40", "mute" |
| Media | "play", "skip", "previous track" |
| Lock | "lock the computer" |

**It is local, like everything else.** No key, no account, no network, no per-command cost —
the same promise as dictation, which is why this came before anything cleverer.

**It is deliberately not natural language understanding.** A dozen verbs and one free argument
is covered completely by an ordered list of patterns: instant, offline, free, and predictable
about what it will refuse. An utterance that matches nothing produces nothing, and the pill
says so *and shows you the transcript* — because without that there is no way to tell a
misrecognition from an unsupported command, and those need opposite responses from you.

**Every command is a typed value, never a string that gets executed.** That is the whole
security posture in one decision, and it is what makes a smarter tier safe to add later: a
model would *choose from* this list rather than name a command of its own, so the worst a
confused one could do is set the volume wrong.

**Nothing irreversible is in the vocabulary.** No closing windows, no killing processes,
nothing that deletes. Speech recognition is wrong often enough that pairing it with an
unrecoverable action is a bad trade, and the list stays short until the rest is solid.

**Applications are found through `shell:AppsFolder`, not by reading Start menu shortcuts.**
Shortcut files miss every Store app — measured here, that meant Notepad, Calculator, Teams and
the current Outlook all resolved to nothing. Which app you meant is scored in `Teezy.Core`
where it is tested: exact beats whole word beats prefix, shorter wins ties so "chrome" is
Chrome rather than Chrome Canary, and nothing is fuzzy. Edit distance would let "teams" reach
TeamViewer, and opening the wrong application is worse than admitting nothing matched.

### When the patterns miss

Settings ▸ Assistant can hand what they could not place to Claude, which either picks from the
*same* command list or answers you in a sentence. Off by default, on your own API key, and it
reuses the cleanup tier's — it is the same account.

**The everyday vocabulary never reaches it.** Only what the local patterns decline costs a round
trip, which is the economic argument as much as the privacy one: if "volume up" went to Claude,
every command would cost money and leave the machine. There is a test pinning that.

**Claude chooses; it does not name.** It gets the same six commands as tools, and what comes
back is validated rather than trusted — an unknown tool, a missing argument or a number out of
range yields no command at all instead of a best guess. The worst a confused model can do is set
the volume wrong, because nothing destructive is on the list.

**It is sent your words and nothing else.** No screen, no clipboard, no history, no documents.
With no untrusted text in the request there is no prompt-injection surface, and that is a
property to defend as this grows rather than a happy accident of it being small.

Answers appear in a taller pill, sized from the wrapped text and left up longer the more there
is to read — it cannot be summoned back, so a missed answer is a lost one.

### Reading answers out loud

Optional, off by default, and **answers only** — never the confirmations. "Volume set to forty
percent" takes two seconds to say for something the pill shows instantly, and you would hear it
twenty times a day. It stops the moment you press the key again, because anyone starting a new
utterance has stopped listening to the last answer.

**Windows' own voices are the default tier: free, offline, instant.** TeezyFlow picks the best one
installed rather than the system default, which is usually the *worst* — measured here, the
default was "Microsoft Hazel Desktop" while four newer voices sat unused beside it. The ones
Windows marks "Desktop" are the old SAPI5 set and are labelled as older in the picker. Accent is
matched to your own where a voice exists for it.

**ElevenLabs is the paid tier**, and it costs three things: a subscription, a third API key, and
a round trip before the first word — which lands *on top of* the wait for Claude that has already
happened. Worth knowing before paying, because it is not obvious from the demos.

It is sold as a monthly allowance of characters rather than per use, so Settings counts what you
actually speak, by calendar month, and leads with last month's figure — this month is always
partial. Answers are capped at two short sentences, so one is roughly 100–150 characters;
whether that lands in a free tier or a paid one depends entirely on how often you ask questions
rather than give commands, which is exactly what the counter is there to tell you. Failed
requests are not counted, since they are not billed.

An ElevenLabs key needs only **Text to Speech** and **Voices: read**. Nothing else is touched.

**Pick a combination that does not contain your dictation one.** Holding Ctrl+Alt+Win satisfies
Alt+Win on the way, so dictation briefly starts first — you will hear its tone. Settings warns
when the two overlap. The alternative was a debounce on every press, which taxes the thing you
do fifty times a day to fix the thing you chose.

---

## Choosing a microphone

Settings ▸ Microphone picks the device TeezyFlow records from. The default is **Windows default**,
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
plugging it back in is all it takes — and TeezyFlow says once per run that it is using something
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
times longer, and until recently TeezyFlow could not say *why* — it recorded a single number for
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

TeezyFlow is a Teezy Labs product and looks like one. **It uses Fivebar's design system**,
value for value: cool blue-grey panels in four steps of elevation (`#141619` strip, `#1b1d21`
page, `#25282d` panel, `#2e3238` / `#363b42` raised and selected), one-pixel `#3a3f46` lines
instead of shadows, Segoe UI at 13 px, 4 px buttons and inputs, 8 px cards, small uppercase
section labels, tabular figures, and a 3 px accent bar for whatever is selected. Shadows are
kept for things that float — the pills and dropdowns. The source of truth for those values is
Fivebar's `src\renderer\src\styles.css`; keep them in step rather than tuning them here.

**The family shares everything but its colour.** Fivebar is orange; TeezyFlow is teal
(`#2BB3A3`), the way Word and Excel share a look but not a colour. Text on the accent is dark:
white on this teal is about 2.6:1, dark ink over 7:1. **Red is reserved** — it means recording
and appears nowhere else.

The window is Fivebar's shell: a dark strip across the top with the mark and the pages as
tabs (the active tab takes the page colour and sits over the strip's line, so it reads as the
top of its page), Settings on the right, and a status bar along the bottom that says whether
dictation will work right now and which keys start it. Outfit Bold, the Teezy Labs display
face, appears in exactly two places: the home greeting and the wordmark.

`Theme.xaml` is the whole design system — palette, type scale, and templates for every
control the app uses. **Views must not contain literal colours**, and code that draws its own
visuals reads them through `Brand`, which looks them up in the theme rather than keeping a
second copy. A second copy is exactly what went wrong before: `Brand.cs` was still the old
light palette months after the window went dark.

Dark only, for now. Every lookup is `StaticResource`; a light theme following Windows, as
Fivebar does, means moving the views to `DynamicResource` first.

**The mark is four rounded bars at different heights — a voice level.** It is built the way
Fivebar's is: flat bars, the same bar-to-gap ratio and corner, no tile, no gradient. Fivebar's
bars stand full height and are cut on a diagonal; these rise and fall, because this product is
a voice. The assistant pill animates the mark itself — the two middle bars are the voice meter
while it listens. The wordmark is lowercase `teezyflow` in Outfit Bold with `flow` in the
accent, as Fivebar writes `five` + `bar`.

**One drawing, every size, and nothing redraws it.** `MarkGeometry` in `Theme.xaml` is used at
16 px in the tray and at 256 px in Explorer; its proportions are Fivebar's small-size ones,
because at 16 px a thinner gap closes up. `TrayIcons` renders that resource at runtime, and
`tools/make-icon.ps1` loads `Theme.xaml` and renders the same one into the `.ico`. Neither
hardcodes the glyph's bounds: they take them from the geometry, so re-drawing the mark
re-centres it. The old speech-bubble artwork is still in `Logo\` for reference.

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
**Off by default, and that default is the honest one** — it is the only thing in TeezyFlow that
leaves your machine.

- **A Claude Pro or Max subscription does not cover this.** The Anthropic API is billed
  separately, pay-as-you-go from prepaid credits at `console.anthropic.com`. Roughly
  **$0.60 a month** at 400 dictations on Sonnet 5; Haiku 4.5 about a third of that, Opus 5
  about double.
- **The real price is latency.** Dictation is ~170 ms end to end today; a round trip adds
  about a second to every utterance.

### What it actually costs you

**Insights shows measured spend, not the estimate above.** Every response carries a `usage`
block, so TeezyFlow records the tokens each dictation consumed and prices them locally. There is
no endpoint that reports account spend to an ordinary API key — the Console has that — but
this is the more useful figure anyway: it is what *TeezyFlow* cost, not what the account cost.

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

TeezyFlow already knew which app it was typing into — that is what the Insights breakdown is —
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
# off: gpt -> GPT              # disabled, kept for later
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

1. **Translation.** Dictate in English, type in another language. Cheap now the Claude tier
   exists — it is one more transform on the text — but it still needs a translation engine, so
   an *offline* version remains a real project.
2. **Command mode over selected text.** Select text, hold a key, "make this more formal."
3. **Code signing.** `install.ps1` needs no elevation and clears the mark-of-the-web, but the
   executable itself is unsigned — so SmartScreen warns on first launch, Smart App Control can
   refuse it outright on a freshly installed Windows 11, and a machine running WDAC or a
   publisher-allowlist policy can block it with nothing we can do locally.
4. **Elevated-window injection.** A non-elevated process cannot type into an elevated
   window. Elevating TeezyFlow would be worse than the problem.
5. **Budget figures.** The Budget page and its dashboard widget exist; reading a Cashew export
   and the bills that arrive by email does not yet, so both say so rather than show numbers.

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

## Privacy

TeezyFlow runs no servers and collects nothing. Speech recognition, commands, meeting transcription,
your dictionary and your history stay on the machine; five optional tiers can leave it, each off
until you switch it on and each on your own account. Meeting audio is deleted once it has been
transcribed. Connected calendars and mailboxes are read, never copied or stored. The full
statement is in [PRIVACY.md](PRIVACY.md).
