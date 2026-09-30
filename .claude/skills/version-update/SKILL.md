---
name: version-update
description: Ships a new TeezyFlow release, start to finish — bumps the version of record, commits and pushes, builds the Windows installer, writes patch notes in the project's own voice, and publishes a GitHub release with the installer attached. Use this whenever the user says "release and push", "push" (when they mean publish a version, not a plain git push), "publish a new version", "ship this", "cut a release", or "version update" while working in this repo. There is no CI/CD here — this skill *is* the entire release pipeline, run by hand. Always follow every step in order: skipping the installer build, or pushing commits without creating the GitHub release (or vice versa), leaves the app's auto-updater and in-app "what's new" text out of sync with what people actually have installed.
---

# TeezyFlow version update

TeezyFlow updates itself by polling `https://api.github.com/repos/tee-93/teezy/releases/latest`
(`src\Teezy.App\Updater.cs`) and reading the release body as in-app "what's new" text
(Settings ▸ About, via `UpdateState.WhatsNew()`). That means a version isn't real to anyone but
you until it exists as a **published GitHub release with the installer attached** — committing
and pushing source is necessary but not sufficient, and installed copies (including the
secondary/work computer) will keep reporting the old version as current until this whole
sequence has run.

The user has given standing authorization for this entire flow: when they say the trigger
phrases above, that *is* the go-ahead to publish. Don't pause partway through to ask "are you
sure?" — run the steps, and only stop early if something genuinely fails (build error, `gh` not
authenticated, etc.), in which case explain what broke and where it stopped.

## Before you start: check what's actually pending

```bash
cd <repo root>
git status --short
git log --oneline -5
gh release list --repo tee-93/teezy --limit 3
```

Compare the `<Version>` in `Directory.Build.props` against the most recent **published** release
tag (not just the most recent commit — commits can land on `main` without ever being released,
which is exactly the gap this skill exists to close). If the version of record is already ahead
of the last real release by more than the change you're about to ship, the release notes need to
cover *everything* since that last release, not just the newest commit — that's the actual delta
a user on the old version will experience when they update.

## 1. Bump the version

`Directory.Build.props` at the repo root is the single source of truth (its own comment says so:
"Bump it here and nowhere else"). `tools\build-installer.ps1` reads it automatically and it also
becomes the assembly version Settings ▸ About reads, so the installer and the app can't disagree.

Pick a minor bump (`X.Y.0`) for a feature or behavior change, a patch bump (`X.Y.Z`) only for a
small, contained fix — that's the mix already in the tag history (most releases are minor;
`v1.1.1`, `v1.1.3`, `v1.5.1`–`v1.5.3` are the patch-sized exceptions). If the version was already
bumped for the pending work (check `git diff` on `Directory.Build.props`), don't bump it again.

## 2. Commit and push

Stage only the files that actually changed — never `git add -A` or `git add .`, this repo has
untracked scratch/probe folders that don't belong in a commit. Write the message in the exact
house style already in `git log`:

```
TeezyFlow X.Y.Z: <one-line, present-tense, lowercase-after-colon description>
```

e.g. `TeezyFlow 1.26.0: backdate a note, on a task or a quote`,
`TeezyFlow 1.28.0: drop the top strip and the Assistant panel`. Add whatever Claude attribution
footer this session's own instructions specify for commits. Then:

```bash
git push origin main
```

## 3. Build the installer

Run this with the **PowerShell tool, not Bash** — it's a native `.ps1` script:

```powershell
cd <repo root>
.\tools\build-installer.ps1
```

This publishes both architectures and compiles `dist\TeezyFlow-Setup.exe` via Inno Setup; it can
take a few minutes. It reads the version from `Directory.Build.props` itself, so nothing needs
passing in. Treat `Build FAILED`, `error CS`, or `error MSB` anywhere in its output as a hard
stop — do not go on to publish a release built from a failed build. Check that
`dist\TeezyFlow-Setup.exe` actually exists and the script's own summary line names the version
you expect before moving on.

## 4. Write the patch notes

This is the part worth taking care over now: the release body isn't just a GitHub changelog
entry, it's what shows up in the app itself (Settings ▸ About ▸ "What's new"). Write real prose
in TeezyFlow's own voice — explain what changed and *why it matters to someone using the app*,
the way a person would describe it to a colleague, not a bullet-point changelog. If you want a
feel for the tone, `gh release view v1.22.0 --repo tee-93/teezy` and
`gh release view v1.26.0 --repo tee-93/teezy` are good real examples (short single-change
releases read as a couple of plain paragraphs; multi-part releases use `###` headers per piece).

Cover everything since the last published release (see "Before you start" above) — don't
shortchange the notes just because only the last commit is fresh in mind.

Always end with this exact footer, unchanged:

```
---

**Install:** copies on 1.13.0 or later update themselves. Otherwise download `TeezyFlow-Setup.exe` below and run it. It's unsigned, so SmartScreen warns on first run: choose More info, then Run anyway.
```

## 5. Publish the GitHub release

```bash
gh release create vX.Y.Z dist/TeezyFlow-Setup.exe \
  --repo tee-93/teezy \
  --title "TeezyFlow X.Y.Z" \
  --notes "<the notes from step 4>"
```

A few things that matter here and are easy to get wrong:

- **Tag is `vX.Y.Z`** (confirmed from every existing tag, `v1.0.0` through the present). `gh`
  creates the tag from the current `main` automatically if it doesn't exist — there's no separate
  `git tag` step.
- **Title is `TeezyFlow X.Y.Z`**, optionally with a short " — tagline" suffix for a headline
  feature (several past releases do this; plenty don't — use your judgment on whether the release
  has one obvious flagship change worth naming).
- **The asset must be uploaded as exactly `TeezyFlow-Setup.exe` — do not rename it.** The
  updater's `Release.Parse` (`src\Teezy.Core\Updates\Release.cs`) only recognizes an asset with
  that literal filename; a renamed asset is invisible to every installed copy and quietly breaks
  auto-update for everyone until caught.
- For a multi-line `--notes` value, write the notes to a temp file and pass `--notes-file
  <path>` instead of trying to get a long string with blank lines through `--notes` on the
  command line — much less error-prone than quoting/escaping it inline.

## 6. Verify

```bash
gh release view vX.Y.Z --repo tee-93/teezy
```

Confirm the asset list shows `TeezyFlow-Setup.exe` and the notes look right.

## 7. Tell the user

Report the release URL, and remind them: installed copies on 1.13.0 or later pick this up
automatically within 4 hours, or immediately via Settings ▸ About ▸ Check for updates — so if
they're about to go check a secondary or work computer, a manual check-for-updates there will be
faster than waiting on the timer.
