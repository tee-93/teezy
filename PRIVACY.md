# TeezyFlow privacy policy

_Last updated: 22 September 2026 (1.19)_

TeezyFlow is a push-to-talk dictation and voice assistant app that runs on your own Windows PC.
There is no TeezyFlow account, no TeezyFlow server, and no TeezyFlow company collecting anything. Nothing
in this document is a promise about a service — it is a description of what the program on your
machine does with what it can see.

## The short version

- **TeezyFlow operates no servers.** Nothing is ever sent to us, because there is no "us" to send it
  to. We cannot see your dictation, your diary or your mail, and could not hand them over if
  asked.
- **Everything is local until you switch something on.** Speech recognition, the command
  vocabulary, your dictionary, your history, your tasks, meeting transcription and the voices —
  Windows' own and the natural (Kokoro) ones — all run on-device.
- **Eight optional tiers leave the machine**, each off by default, each on your own API key, your
  own account or your own storage, and each listed below.
- **No analytics, no telemetry, no crash reporting, no advertising, no tracking of any kind.**
  TeezyFlow does not phone home, and there is no build of it that does. The one request it
  makes by itself is **a check for updates**: shortly after it starts and every four hours, it
  asks GitHub for this repository's latest release. That tells GitHub your IP address and the
  version you run — as any download does — and sends nothing about you or your use of it.

## What stays on your computer, always

Your voice is transcribed on-device by a speech model that ships with the app. **Audio is never
uploaded anywhere, by any tier, ever.** Dictation audio is held in memory while you hold the key
and discarded once it has been transcribed.

**Meetings are recorded only when you press Start recording**, and record your microphone and
whatever your speakers play. Nothing joins the call, so nobody in it can tell it is happening —
letting them know is your responsibility, and in some places the law requires their consent. The
recording is written to `Meetings\` while it is transcribed on this computer after the call, and
**the audio is deleted as soon as its transcript has been written** — unless you switch on
**Keep the recording** (Settings ▸ Audio ▸ Meetings, off by default), which leaves it on this
computer for the day, week or month you choose so a transcript can be made again, and deletes it
when that time is up. Turning the setting back off deletes what is still there.

**Telling the voices apart** — labelling the far end Speaker 1, Speaker 2 and so on — is done on
this computer by two models you download once from Hugging Face if you switch it on. Like the
speech model's download, that request carries nothing about you, and no audio ever leaves the
machine.

These live in `%LOCALAPPDATA%\Teezy` and are never transmitted:

| File | What it holds |
| --- | --- |
| `history.jsonl` | Everything you have dictated, so you can find it again |
| `Meetings\` | Meeting transcripts and the notes you asked for; recordings only until transcribed, or for as long as you chose to keep them |
| `settings.json` | Your preferences, and which accounts you have connected |
| `dictionary.txt` | Words and names you have taught it |
| `tasks.json` | Your task list: titles, dates, categories, notes (with who wrote them), any email you dropped or pasted into a task, and the last AI suggestion you kept |
| `models\` | The speech model, and the natural voices if you downloaded them |
| `secrets\` | API keys, account tokens, the Gmail app password and calendar links, encrypted (see below) |

## What leaves your computer, and only if you turn it on

Each of these is **off by default**. Switching one on is the whole of the consent — TeezyFlow does
not enable them for you, and turning one off stops it immediately.

**1. Smarter cleanup (Anthropic).** Sends the transcript of what you just dictated, so it can be
punctuated and tidied. Billed to your own Anthropic API key.

**2. The assistant's smarter tier (Anthropic).** When the local command vocabulary does not
recognise what you said, your words are sent so Claude can interpret them. It is sent **your
words and nothing else** — never your screen, your clipboard, your files or your history.

**3. Calendar, mail and task answers (Anthropic).** Questions like "what's on today" or "what
tasks do I have today" are answered on your machine and send nothing, and adding or closing a
task by voice never leaves it. Only an unusual phrasing that TeezyFlow cannot compose an answer
to sends the relevant events, message summaries or open tasks (titles, due dates and categories —
never notes or attached emails) so Claude can answer in a sentence. If the smarter assistant tier
is on, a request TeezyFlow's own patterns do not recognise — "jot down that I should ring the
builder" — is sent as before, and Claude may answer by adding a task.

**4. Spoken replies (ElevenLabs).** If you choose the paid voice, the text to be spoken is sent
to ElevenLabs on your own key. The free Windows and natural voices send nothing.

**5. Meeting summaries (Anthropic).** When you press **Summarise** on a meeting, that meeting's
transcript is sent so Claude can write a summary and the follow-up tasks. It is never automatic:
it happens for that meeting, when you ask, and at no other time. The recording itself is never
sent, and the PDF is made on your computer. Like calendar and mail answers, the request is given
no tools, because a transcript is other people's words.

**6. Sync between your computers (your own folder).** If you turn on Settings ▸ Sync, your API
keys, the Google client secret, the Gmail app password, calendar links, your preferences, your
dictionary and your tasks (with their notes and attached emails) are written to one file in a folder you choose —
usually your own Google Drive or OneDrive, so it is stored by Google or Microsoft under your
account. The file is **encrypted with a passphrase only you know** (PBKDF2-SHA256, 600,000
iterations, then AES-256-GCM), so the storage provider, and anyone who obtains the file, sees
only scrambled data. Your dictation history, meetings, signed-in account tokens and audio are
never in it. Turning sync off stops it; delete the file to remove it.

**7. Next steps and draft replies for a task (Anthropic).** On the Tasks page, pressing **Next
steps** or **Draft reply** sends the email you pasted or dropped into that task, with the task's
title, category and due date and any steer you typed, so Claude can suggest what to do or draft a
reply. Only that one email, only when you press the button, and never in the background. The
request has no tools: the reply is text on the page for you to edit and copy — kept with the task
on your computer — and nothing is sent or acted on. If it is a work email, check your employer
allows it to go to an outside AI service.

**8. The AI plan in the morning briefing (Anthropic).** The morning briefing itself — what is late,
the day in order, the week's follow-ups — is made on your computer and sends nothing. Only if you
switch on **Add an AI plan for the day** (Settings ▸ Tasks) are your open tasks (titles, due
dates, categories and each one's latest note) and today's meeting names sent, once a morning, so
Claude can write two or three sentences on how to tackle the day. Attached emails are never
sent. The request has no tools — meeting names are written by whoever sent the invitation — and
the answer is only shown to you.

**Emails you drag in stay local.** Dropping an email from Outlook onto the Tasks page copies its
text into the task on this computer. TeezyFlow never connects to Outlook or your mailbox to do
it, reads nothing beyond what you dropped, and writes nothing back.

**The natural voices are downloaded once, when you ask.** Pressing **Download** in Settings fetches
the Kokoro voice files from Hugging Face; like the speech model's download, that request carries
nothing about you. After that the voice runs on your computer and the text it reads never leaves.

Requests to Anthropic and ElevenLabs are subject to those companies' own terms and privacy
policies, under your own account with them.

## Connected accounts

TeezyFlow can read your **calendar** (Microsoft, Google, or a published calendar link) and your
**mail** (Microsoft, Gmail), if you connect them.

- **Signed-in accounts are read-only, enforced at the provider.** TeezyFlow requests
  `Calendars.Read`, `Mail.Read` and `calendar.readonly`. It holds no permission to send, delete,
  move, or mark anything as read — not to your calendar, and not to your mailbox.
- **Gmail is read over IMAP with an app password**, because Google restricts its mail API. An app
  password is not limited by Google: it is full access to the mailbox. TeezyFlow opens your inbox
  read-only and never changes anything, but that is TeezyFlow's own restraint, not a limit Google
  enforces.
- **A calendar link** is a calendar you published (from Outlook.com, Google or another calendar). The link itself is the access, so
  TeezyFlow stores it encrypted like a token, fetches it only over HTTPS, and has no way to change the
  calendar behind it.
- **Microsoft and Google accounts sign in on the provider's own page**, in your own browser. No
  account password is ever typed into TeezyFlow, and TeezyFlow never sees one. A Gmail app password is a
  separate password Google issues for one app; it is typed into TeezyFlow, stored encrypted, and can
  be revoked on its own without touching your Google password.
- **Reading mail is a separate switch** from connecting the account, and asked for separately at
  sign-in. Connecting a calendar never grants access to mail.
- **TeezyFlow never opens a link, loads a remote image, or fetches anything a message points at.**
- Your calendar and mail are read when you ask a question and are **not copied, indexed, or
  stored** — they are held in memory long enough to answer, and discarded.

**To revoke access at any time**, without involving TeezyFlow: at
[myaccount.google.com/permissions](https://myaccount.google.com/permissions) or
[microsoft.com/consent](https://account.live.com/consent/Manage); delete a Gmail app password at
[myaccount.google.com/apppasswords](https://myaccount.google.com/apppasswords); or stop
publishing a calendar in Outlook's calendar settings. Disconnecting an account inside TeezyFlow
deletes its stored tokens, password or link from your machine.

### Google API Services user data

TeezyFlow's use of information received from Google APIs adheres to the
[Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy),
including the Limited Use requirements. Specifically: calendar data obtained through Google APIs
is used only to answer the questions you ask TeezyFlow, is never transferred to anyone, is never
used for advertising, and is never used to train any model.

## How credentials are stored

API keys and account tokens are encrypted with the Windows Data Protection API, bound to your
Windows user account on that machine. Copying the files to another computer, or reading them as
another user, yields nothing.

This is not a claim of protection against someone already running programs as you — such a
person can decrypt anything you can. What it does mean is that your keys are never written in
plain text, never appear in `settings.json`, and cannot be leaked by sending someone your
settings file.

## Children

TeezyFlow is not directed at children and collects nothing from anyone, of any age.

## Changes

This file is versioned in the repository, so every change to it is a public commit with a date
and a diff. Material changes will be noted in the release notes.

## Contact

TeezyFlow is an open-source project at [github.com/tee-93/teezy](https://github.com/tee-93/teezy).
Questions and concerns are welcome as GitHub issues.
