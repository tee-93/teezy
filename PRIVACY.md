# TeezyFlow privacy policy

_Last updated: 13 September 2026_

TeezyFlow is a push-to-talk dictation and voice assistant app that runs on your own Windows PC.
There is no TeezyFlow account, no TeezyFlow server, and no TeezyFlow company collecting anything. Nothing
in this document is a promise about a service — it is a description of what the program on your
machine does with what it can see.

## The short version

- **TeezyFlow operates no servers.** Nothing is ever sent to us, because there is no "us" to send it
  to. We cannot see your dictation, your diary or your mail, and could not hand them over if
  asked.
- **Everything is local until you switch something on.** Speech recognition, the command
  vocabulary, your dictionary, your history, meeting transcription and the built-in Windows voice
  all run on-device.
- **Five optional tiers leave the machine**, each off by default, each on your own API key or
  your own account, and each listed below.
- **No analytics, no telemetry, no crash reporting, no advertising, no tracking of any kind.**
  TeezyFlow does not phone home, and there is no build of it that does.

## What stays on your computer, always

Your voice is transcribed on-device by a speech model that ships with the app. **Audio is never
uploaded anywhere, by any tier, ever.** Dictation audio is held in memory while you hold the key
and discarded once it has been transcribed.

**Meetings are recorded only when you press Start recording**, and record your microphone and
whatever your speakers play. Nothing joins the call, so nobody in it can tell it is happening —
letting them know is your responsibility, and in some places the law requires their consent. The
recording is written to `Meetings\` while it is transcribed on this computer after the call, and
**the audio is deleted as soon as its transcript has been written**.

These live in `%LOCALAPPDATA%\Teezy` and are never transmitted:

| File | What it holds |
| --- | --- |
| `history.jsonl` | Everything you have dictated, so you can find it again |
| `Meetings\` | Meeting transcripts and the notes you asked for; recordings only until transcribed |
| `settings.json` | Your preferences, and which accounts you have connected |
| `dictionary.txt` | Words and names you have taught it |
| `secrets\` | API keys, account tokens, the Gmail app password and calendar links, encrypted (see below) |

## What leaves your computer, and only if you turn it on

Each of these is **off by default**. Switching one on is the whole of the consent — TeezyFlow does
not enable them for you, and turning one off stops it immediately.

**1. Smarter cleanup (Anthropic).** Sends the transcript of what you just dictated, so it can be
punctuated and tidied. Billed to your own Anthropic API key.

**2. The assistant's smarter tier (Anthropic).** When the local command vocabulary does not
recognise what you said, your words are sent so Claude can interpret them. It is sent **your
words and nothing else** — never your screen, your clipboard, your files or your history.

**3. Calendar and mail answers (Anthropic).** Questions like "what's on today" are answered on
your machine and send nothing. Only an unusual phrasing that TeezyFlow cannot compose an answer to
sends the relevant events or message summaries so Claude can answer in a sentence.

**4. Spoken replies (ElevenLabs).** If you choose the paid voice, the text to be spoken is sent
to ElevenLabs on your own key. The free Windows voice sends nothing.

**5. Meeting summaries (Anthropic).** When you press **Summarise** on a meeting, that meeting's
transcript is sent so Claude can write a summary and the follow-up tasks. It is never automatic:
it happens for that meeting, when you ask, and at no other time. The recording itself is never
sent, and the PDF is made on your computer. Like calendar and mail answers, the request is given
no tools, because a transcript is other people's words.

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
- **A calendar link** is a calendar you published from Outlook. The link itself is the access, so
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
