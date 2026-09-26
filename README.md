# TeezyFlow

*A Teezy Labs project, called Teezy until 1.11. Only what you see was renamed: the exe is still
`Teezy.exe`, and settings, history, keys and the model still live in `%LOCALAPPDATA%\Teezy`,
so an existing install upgrades in place with nothing to move.*

Push-to-talk for Windows. Hold a key, talk, release — cleaned-up text is typed into whatever
had focus. Hold a *different* key and it does what you said instead: opens an app, changes the
volume, skips a track, locks the PC, and can answer you out loud. It also records a meeting and
transcribes it once the call is over, and keeps a task list for the follow-ups. **Local by
default** — after the one-time model download, dictation, commands, meeting transcription, tasks,
your dictionary and the voices all run on-device and stay there. Eight optional tiers are the
exception — six Claude, one ElevenLabs, and an encrypted sync file in your own Google Drive or
OneDrive — every one of them off until you switch it on.
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
- **The window opens at sign-in too** (since 1.17): Home is the day's update, and the morning
  is when it is wanted. The `Run` value still ends in `--startup`, so the sign-in launch can be
  told apart again if that ever needs a setting.

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
through one encrypted file in a folder they can all see — a `TeezyFlow` folder in Google Drive
is ideal, and the folder picker opens there when Google Drive is installed. OneDrive works too,
but a personal and a work OneDrive cannot share a folder, which is why Google Drive is the
suggestion. Choose the folder and a passphrase on the first computer; on each of the others,
choose the same folder and type the same passphrase, and that computer takes the setup.

- **What travels:** the Anthropic and ElevenLabs keys, the Google client secret, the Gmail app
  password, calendar links, hotkeys, the cleanup and assistant choices, writing styles and
  per-app rules, the voice, the dictionary, and tasks.
- **What stays:** the microphone, the thread count tuned to that processor, where the models
  live (the natural voices are downloaded on each computer that uses them), and signed-in
  accounts — sign in to Microsoft or Google once on each computer (the app ids are built in,
  so there is nothing to paste).
- **The file is unreadable without the passphrase:** PBKDF2-SHA256 at 600,000 iterations,
  then AES-256-GCM. The passphrase is kept on each computer under DPAPI, so it is typed once
  per machine — and it cannot be recovered. Forget it and the answer is to turn sync off
  everywhere and start again.
- **The newest file wins, whole — except tasks.** Two computers changing different settings
  while both are offline would lose one set of changes; for one person with a few machines that
  is simpler to trust than a merge. Tasks are edited everywhere, so they merge task by task
  instead (see Tasks below).

**Why the keys are not simply built in:** the repository and its releases are public, and a
key compiled into a public installer is a key anyone can pull out and spend. The Microsoft and
Google *app ids* are built in (`BuiltInApps.cs`) because they are identifiers shown on every
sign-in page, not secrets.

### Tasks

The **Tasks** tab is TeezyFlow's own task list, for quote follow-ups and the day's work. It is
not connected to Outlook or anything else: 1.13–1.14 tried reading a work calendar and flagged
email out of Outlook, every route ran into the employer's lockdown, and all of it was removed in
1.15. The one bridge left is the one nothing can block — you drag an email across, or paste it.

- **Quick add:** type a task with a day, a time and a category on the end —
  *Chase Cessnock quote fri 2pm #Quotes*. Days read the way people write them: *today*,
  *tomorrow*, *fri*, *next week*, *in 3 days*, *25/9*, *3 Oct*. A word like "Friday" in the
  middle of a title stays part of the title.
- **Grouped by when:** Overdue, Today, Upcoming and No date. A task starts the moment it is made;
  the panel shows when that was. Filter by category along the top.
- **Due and Remind me are separate date-and-time pickers** — a small calendar (weeks from Monday)
  with *Today*, *Tomorrow*, *Next Mon* and *In a week*, and a time in quarter hours. A reminder
  can be on another day from the due date: due Friday, remind me Wednesday at 9.
- **Categories** are a drop-down, managed in **Settings ▸ Tasks**: add, rename (every task with
  it is renamed too), reorder, remove. A `#category` typed in quick add is added to the list.
- **Close, or close and follow up.** Following up closes the task and makes the next one for a
  day you pick (*tomorrow*, *in 3 days*, *in a week*, *in 2 weeks*, or any day; weekends move to
  Monday), linked back, with the reminder and the email carried over. The panel shows the whole
  chain — quote sent, chased, chased again. Undo takes either back.
- **Notes are saved with Save on the notes header** (or Ctrl+Enter), and anything typed but not
  saved is kept anyway when the panel moves to another task or the page is left.
- **Notes are a timeline**, newest first, each with who wrote it and when — the record to read
  back in a review. The name is set in Settings ▸ Tasks; notes TeezyFlow writes itself (closed,
  followed up, email attached) are shown quieter.
- **Reminders:** a card pops up above the tray when one comes due, with *Done*, *In 1 hour*,
  *Tomorrow* and *Open*. It never takes the keyboard from what you are typing, and it stays
  until dealt with.
- **Home** shows today's and late tasks, the week ahead, and a box to add one.
- **Syncs task by task.** With Settings ▸ Sync on, tasks travel in the same encrypted file, but
  merged: each task keeps when it last changed and the newer copy wins, so tasks added on two
  computers while apart both survive. Deletions travel too.

**Emails in.** Drag a message straight out of Outlook — classic or New — onto a task to attach it,
or anywhere else on the page to make a new task from it, named after its subject. A dashed box
under quick add (and on Home's Today panel) shows where, and lights up with what the drop will do
while an email is over the page. The email is kept beside the notes, not in them: a folded
**Email** section shows who and what in one line, and **Open** reads it in full in its own
window. Classic Outlook's `.msg`, New Outlook's `.eml`, saved message files and plain text all
work — New Outlook only hands an email over to an *asynchronous* drop, which WPF does not speak,
so TeezyFlow does that handshake itself — and an email can be pasted in instead. Nothing is read from Outlook beyond what
you drop, and nothing is written back.

**Next steps and draft replies.** In the folded **Next steps and reply** section, press **Next
steps** or **Draft reply** — an optional steer like *"yes, but not before Friday"* shapes it. Only
that email and the task's title go to Claude, on your key, when you press the button. The
request carries no tools, so an email written to manipulate an AI has nothing to act with. The
answer is kept on the task and can be edited before you copy it or save it to the notes. If it
is a work mailbox, check your employer is happy with work email going to an outside AI service
first.

### Quotes

The **Quotes** tab is the pipeline: every quote you send, what it is worth, and the chasing that
follows it. Three figures across the top — what is out there, what you have won this month, and
your win rate — then the list, grouped by what needs doing: **chase today**, **gone quiet**,
**out there**, **decided**.

**A quote is the job; a chase is a task.** Adding a quote books its first chase in your ordinary
task list, in the Quotes category, due on the day with a reminder. Tick that task off and the
quote counts one more chase and books the next. Mark the quote won or lost and the chasing
stops. Nothing new to keep on top of: reminders, the focus list, Home and the morning briefing
already work on tasks.

Four ways in, because quotes are sent from four different situations:

- **Typed:** `Hunter Builders $4,200 door hardware sent friday`. The customer comes before the
  figure, the job after it, and a day at the end means the day just gone — a quote is sent
  before it is recorded. `4.2k`, `$4,207.35` and `#Q1042` all read.
- **Dragged in:** drop the quote email you sent onto the page. The customer comes from who it
  went to and the job from the subject; you add the value and press Enter, and the email is kept
  on the quote.
- **By voice:** hold the assistant key and say *"quoted Hunter Builders four thousand two hundred
  for door hardware"*. Understood on this computer, free, with no key and no signal — and it
  tells you when it will be chased.
- **From the CRM:** **Import a CRM export** takes a spreadsheet saved as CSV. Columns are matched
  by what they mean, and it says which column it read each field from before importing anything.
  Re-importing the same file updates rather than duplicates, matching on your quote reference, so
  a fresh export brings the outcomes with it.

**Chasing** is 3 days, a week, then a fortnight by default, then monthly — changeable in
Settings ▸ Tasks. A quote entered late is chased today rather than on a day already gone.
Anything with no movement for three weeks is marked **gone quiet**. Quotes travel with Sync like
tasks, merged one at a time.

### The focus list

A small card of the tasks you are working through right now — a call list, say — that stays on
top of everything else. Open it from **Focus list** on Home or the Tasks page, or from the tray.
It has no title bar: drag it by its header, resize it from any edge, and it stays where you put
it, and comes back at start if it was open.

- **Pin** a task from its panel on the Tasks page (**Pin to focus**); pinned tasks wear a
  *Focus* tag in the list. Anything typed into the card's own box is added and pinned at once,
  with the same *fri 2pm #Quotes* quick add understands.
- **Tick** one to close it, with Undo for a slip.
- **Click** one to open a notes box right in the card. What you type is saved to that task's
  notes, under your name, with Save or Ctrl+Enter — and kept if you open another task or close
  the card. Clicking into the CRM mid-call does not split a note into pieces.
- **Unpin** takes it off the card and leaves the task open. A pinned task's follow-up stays
  pinned.

Pins travel with the tasks in Sync; where the card sits is per computer.

### The morning briefing

On working days a briefing opens at **8:30** — or the first time TeezyFlow is open after that, if
the computer was off — once a day per computer. It says the day in one line, then lists what is
**late**, **today** in order (timed tasks, reminders and meetings, then anything due at any
time), the **follow-ups this week**, the **quotes** wanting a chase or gone quiet, unread email
where a mailbox is connected, and what you closed the working day before. Tasks can be ticked off or opened from it; **Read it to me** reads
it aloud in your chosen voice. It opens without taking the keyboard, in case you are already
typing. **Briefing** on Home and **Morning briefing** in the tray show it any time.

Settings ▸ Tasks ▸ Morning briefing sets the time (6 to 11 am), weekends, and an optional **AI
plan for the day**: two or three sentences from Claude on how to tackle it, sent your open
tasks, their latest notes and today's meeting names — never an attached email — with no tools,
since meeting names are strangers' writing. The list itself is always made on the computer.

### Meetings

Press **Start recording** on the Meetings page when a call begins. TeezyFlow records **two
tracks**: your microphone, and whatever your speakers are playing. Nothing joins the call and
nobody in it can tell — telling them is your job, and in some places the law requires it. When
you stop, the meeting is transcribed on this computer, and the recording is deleted as soon as
the transcript is written.

- **It no longer hears the other people twice.** Without a headset your microphone overhears the
  call coming out of your own speakers, so every voice arrived once from the speakers and again,
  worse, from the microphone. TeezyFlow now measures the echo — how far behind it arrives and how
  much of the sound comes back — and leaves those moments out of your side before anything is
  transcribed. Measured here on a laptop with no headset: 26 seconds of a 37-second call removed
  as echo, a quarter less audio sent to the model, and no duplicated lines. Talking over someone
  is kept: your own voice into your own microphone is far louder than the room's return. With a
  headset there is no echo to find, nothing is removed, and no setting had to be changed.
- **Who said what.** Turn on *Tell the voices apart* in Settings ▸ Audio ▸ Meetings (a one-off
  35 MB download) and the far end is labelled Speaker 1, Speaker 2 and so on, worked out on this
  computer. **Name the voices** on the Meetings page turns them into Priya and Dale, in the
  transcript itself. Two labels given the same name become one person, which is how to mend a
  voice that was split in two. Your microphone is always "Me" and needs no working out.
- **The far end has to be recorded from the right output.** TeezyFlow follows whatever Windows is
  playing to. If your call goes to a headset while Windows still plays to the speakers, pick the
  headset under *Record the other people from* — otherwise the other people are only ever heard
  as echo on your microphone.
- **Keeping the recording** (off by default) leaves the audio on this computer for a day, a week
  or a month, so **Transcribe again** can have another go at a transcript that came out wrong.
- **Summarise** sends that meeting's transcript to Claude on your own key, and writes notes,
  follow-ups and a PDF. It happens when you press the button and at no other time.

---

## The window

TeezyFlow is an ordinary application on the taskbar, with its tray icon kept for status and a
quick menu. **Opening it again while it runs brings its window forward** — a second copy signals
the first and exits, since two would install two keyboard hooks and type everything twice.
**The window's X asks once** whether to keep TeezyFlow running (minimised to the taskbar, so
dictation, the assistant and reminders carry on) or quit it, with a box to remember the answer;
Settings ▸ Advanced ▸ *When I close the window* changes it later.

**Home is the day's update**, built to be full on a computer with no accounts at all:

- A greeting, the date, and the day in one line — "3 tasks today, 1 late · 2 follow-ups this
  week", with meetings and unread mail added where a calendar or mailbox is connected.
- **Tiles** across the top, after Pursiva's widget bar: up to five of Due today, Quotes out, Won
  this month, Follow-ups this week, Next reminder, Done this week, Dictated this week, Overdue,
  Time saved, Streak, Meetings recorded, and — with accounts — Next meeting and Unread. Each
  opens what it counts.
- **Two columns, 65 / 35**, stacking on a narrow window. Left: **Today** (late tasks, then the
  day's timed tasks, reminders and meetings on one timeline, then "any time today", with a
  quick-add box that also takes a dropped email) and **This week** (Monday to Sunday). Right:
  **Quotes to chase**, **Coming up**, **Recent notes** (the latest notes across your tasks, who
  and when), **Meetings**, and the **Inbox** where mail is connected. Each panel folds away.
- **Customise** ticks and orders the tiles and panels; the choice travels with Sync. Parts that
  need an account are hidden where it is not connected — never shown empty — and kept in the
  saved choice, so the work laptop's edits do not remove the personal laptop's Inbox.

**Transcripts** is the history: every dictation, newest first, grouped by day, searchable. Hover
a row to copy or delete it. Entries are recorded **even when injection failed** — that is
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
| Ask about tasks | "what tasks do I have today", "what's overdue", "any follow-ups this week", "what's due tomorrow" |
| Add a task | "add a task to chase the Cessnock quote Friday at 2 p.m.", "remind me tomorrow to send the PO", "put the Newcastle PO on my list for Thursday" |
| Record a quote | "quoted Hunter Builders four thousand two hundred for door hardware", "sent a quote to Orikan for nine hundred dollars" |
| Close a task | "mark the Cessnock quote as done", "tick off the expense report" |

**Tasks are answered on this computer, instantly**, so they work on the work laptop too. There,
with no calendar to read, "what's on today" is answered from the task list; where a calendar is
connected, the day's answer adds the tasks due ("…You also have three tasks due, one late").
Spoken times are understood as said — "2 p.m.", "two o'clock", "noon", "remind me at 3 to…".
**Closing is careful:** it needs a clear verb and exactly one open task that matches; two
equally good matches get "Which one — …?", and none at all lets the words carry on to the rest
of the assistant, so "close Chrome" is not mistaken for a task. With the smarter tier on, Claude
can also add a task when the wording is unusual; it is handed the day in your own words and
TeezyFlow works out the date, since the model is not told the date. Unusual questions about
tasks ("when is the Cessnock quote due?") go to the narrator with the task list's titles, dates
and categories — never an attached email.

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

**Natural voices (Kokoro) are the free middle tier**: a neural voice made on this computer, far
less robotic than Windows' and close to the paid tier to listen to. Settings ▸ Assistant ▸ Voice
by ▸ *Natural — free*, then **Download** once (about 172 MB from Hugging Face: Kokoro v1.0,
int8, as packaged for sherpa-onnx — the same engine dictation already uses). After that it is
offline and nothing leaves the machine. Eighteen English voices, British first — the nearest
Kokoro has to Australian — then American.

- **It speaks a sentence at a time**, so the first words come about 1.2 s after the answer is
  ready and the rest is made while the first plays (measured on the Snapdragon X Plus: about
  0.8 s of work per second of speech, four threads; six were no faster and eight were slower).
  A slower processor may leave a short gap between sentences.
- Until the download finishes, answers use the Windows voice rather than nothing.

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

Natural voices: **Kokoro-82M** v1.0 by hexgrad, **Apache-2.0**, in the int8 sherpa-onnx
packaging by `csukuangfj`, with **espeak-ng** data (GPL-3.0) for pronunciation. Downloaded
separately on request, never bundled. Model card: <https://huggingface.co/hexgrad/Kokoro-82M>

**sherpa-onnx** is Apache-2.0. **ONNX Runtime** is MIT. **NAudio** is MIT. **MsgReader**, which
reads Outlook `.msg` files dropped on the Tasks page, is MIT.

The architecture and several hard-won constants were informed by
[per-simmons/murmur-youtube](https://github.com/per-simmons/murmur-youtube), whose Windows
directory is a specification rather than an implementation. That repository carries **no
licence**, so no code was copied from it — only independently re-verified facts.

## Privacy

TeezyFlow runs no servers and collects nothing. Speech recognition, commands, meeting transcription,
tasks, the voices, your dictionary and your history stay on the machine; eight optional tiers can leave it, each off
until you switch it on and each on your own account. Meeting audio is deleted once it has been
transcribed. Connected calendars and mailboxes are read, never copied or stored. The full
statement is in [PRIVACY.md](PRIVACY.md).
