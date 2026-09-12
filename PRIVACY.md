# Teezy privacy policy

_Last updated: 12 September 2026_

Teezy is a push-to-talk dictation and voice assistant app that runs on your own Windows PC.
There is no Teezy account, no Teezy server, and no Teezy company collecting anything. Nothing
in this document is a promise about a service — it is a description of what the program on your
machine does with what it can see.

## The short version

- **Teezy operates no servers.** Nothing is ever sent to us, because there is no "us" to send it
  to. We cannot see your dictation, your diary or your mail, and could not hand them over if
  asked.
- **Everything is local until you switch something on.** Speech recognition, the command
  vocabulary, your dictionary, your history and the built-in Windows voice all run on-device.
- **Four optional tiers leave the machine**, each off by default, each on your own API key or
  your own account, and each listed below.
- **No analytics, no telemetry, no crash reporting, no advertising, no tracking of any kind.**
  Teezy does not phone home, and there is no build of it that does.

## What stays on your computer, always

Your voice is transcribed on-device by a speech model that ships with the app. **Audio is never
uploaded anywhere, by any tier, ever** — it is held in memory while you hold the key and
discarded once it has been transcribed.

These live in `%LOCALAPPDATA%\Teezy` and are never transmitted:

| File | What it holds |
| --- | --- |
| `history.jsonl` | Everything you have dictated, so you can find it again |
| `settings.json` | Your preferences, and which accounts you have connected |
| `dictionary.txt` | Words and names you have taught it |
| `secrets\` | API keys and account tokens, encrypted (see below) |

## What leaves your computer, and only if you turn it on

Each of these is **off by default**. Switching one on is the whole of the consent — Teezy does
not enable them for you, and turning one off stops it immediately.

**1. Smarter cleanup (Anthropic).** Sends the transcript of what you just dictated, so it can be
punctuated and tidied. Billed to your own Anthropic API key.

**2. The assistant's smarter tier (Anthropic).** When the local command vocabulary does not
recognise what you said, your words are sent so Claude can interpret them. It is sent **your
words and nothing else** — never your screen, your clipboard, your files or your history.

**3. Calendar and mail answers (Anthropic).** Questions like "what's on today" are answered on
your machine and send nothing. Only an unusual phrasing that Teezy cannot compose an answer to
sends the relevant events or message summaries so Claude can answer in a sentence.

**4. Spoken replies (ElevenLabs).** If you choose the paid voice, the text to be spoken is sent
to ElevenLabs on your own key. The free Windows voice sends nothing.

Requests to Anthropic and ElevenLabs are subject to those companies' own terms and privacy
policies, under your own account with them.

## Connected accounts

Teezy can read your **calendar** (Microsoft, Google) and your **mail** (Microsoft), if you
connect an account.

- **Read-only, enforced at the provider.** Teezy requests `Calendars.Read`, `Mail.Read` and
  `calendar.readonly`. It holds no permission to send, delete, move, or mark anything as read —
  not to your calendar, and not to your mailbox.
- **You sign in on the provider's own page**, in your own browser. No password is ever typed
  into Teezy, and Teezy never sees one.
- **Reading mail is a separate switch** from connecting the account, and asked for separately at
  sign-in. Connecting a calendar never grants access to mail.
- **Teezy never opens a link, loads a remote image, or fetches anything a message points at.**
- Your calendar and mail are read when you ask a question and are **not copied, indexed, or
  stored** — they are held in memory long enough to answer, and discarded.

**To revoke access at any time**, without involving Teezy: at
[myaccount.google.com/permissions](https://myaccount.google.com/permissions) or
[microsoft.com/consent](https://account.live.com/consent/Manage). Disconnecting an account
inside Teezy deletes its stored tokens from your machine.

### Google API Services user data

Teezy's use of information received from Google APIs adheres to the
[Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy),
including the Limited Use requirements. Specifically: calendar data obtained through Google APIs
is used only to answer the questions you ask Teezy, is never transferred to anyone, is never
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

Teezy is not directed at children and collects nothing from anyone, of any age.

## Changes

This file is versioned in the repository, so every change to it is a public commit with a date
and a diff. Material changes will be noted in the release notes.

## Contact

Teezy is an open-source project at [github.com/tee-93/teezy](https://github.com/tee-93/teezy).
Questions and concerns are welcome as GitHub issues.
