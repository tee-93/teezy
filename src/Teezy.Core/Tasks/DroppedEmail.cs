using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Teezy.Core.Tasks;

/// <summary>An email dragged or pasted in from Outlook (or anywhere), reduced to what a task needs.</summary>
/// <remarks>
/// <para>
/// This is the only bridge between the task list and Outlook, and it is one-way and manual: the
/// user drags a message across, and TeezyFlow keeps a copy of its text. Nothing here reaches
/// back into Outlook, so there is nothing for an employer's lockdown to block.
/// </para>
/// <para>
/// The body is someone else's writing. It is kept as a note and shown as text; it is sent to
/// Claude only through <see cref="IMailAdvisor"/>, with no tools, when the user presses a button.
/// </para>
/// </remarks>
public sealed partial record DroppedEmail(string Subject, string? From, DateTimeOffset? Received, string Body)
{
    /// <summary>The most kept of a body in a note; long threads repeat everything below.</summary>
    public const int MaxBody = 8_000;

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<br\s*/?>|</p>|</div>|</tr>|</li>|</h[1-6]>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();

    /// <summary>A task title for it: the subject without the reply and forward prefixes.</summary>
    public string TaskTitle
    {
        get
        {
            var subject = Subject.Trim();
            while (true)
            {
                var stripped = Regex.Replace(subject, @"^\s*(re|fw|fwd|aw|wg)\s*:\s*", string.Empty, RegexOptions.IgnoreCase);
                if (stripped == subject) break;
                subject = stripped;
            }

            return subject.Length > 0 ? subject : "Email";
        }
    }

    /// <summary>The email as a task keeps it, cut to <see cref="MaxBody"/>.</summary>
    public TaskEmail ToTaskEmail(DateTimeOffset now)
    {
        var body = Body.Trim();
        return new TaskEmail(now, Subject.Trim(), From, Received,
            body.Length > MaxBody ? body[..MaxBody] + "\n[…the rest is omitted]" : body);
    }

    /// <summary>The note it becomes: who, when, what, then the text.</summary>
    public string ToNote()
    {
        var text = new StringBuilder("Email");
        if (From is { Length: > 0 } from) text.Append(" from ").Append(from);
        if (Received is { } at) text.Append(", ").Append(at.LocalDateTime.ToString("ddd d MMM, h:mm tt", System.Globalization.CultureInfo.GetCultureInfo("en-AU")));
        text.Append('\n').Append("Subject: ").Append(Subject.Trim());

        var body = Body.Trim();
        if (body.Length > 0)
        {
            text.Append("\n\n").Append(body.Length > MaxBody ? body[..MaxBody] + "\n[…the rest is omitted]" : body);
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads the text Outlook puts beside a dragged message: a header row of the list's columns
    /// and a row of values, tab-separated — "From⇥Subject⇥Received⇥Size…".
    /// </summary>
    /// <returns>Null when the text is not that table, e.g. ordinary dragged or pasted text.</returns>
    public static DroppedEmail? FromOutlookRow(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return null;

        var headers = lines[0].Split('\t');
        var values = lines[1].Split('\t');
        var subjectAt = Array.FindIndex(headers, h => h.Trim().Equals("Subject", StringComparison.OrdinalIgnoreCase));
        if (subjectAt < 0 || subjectAt >= values.Length || !lines[0].Contains('\t', StringComparison.Ordinal)) return null;

        string? Column(params string[] names)
        {
            var at = Array.FindIndex(headers, h => names.Any(n => h.Trim().Equals(n, StringComparison.OrdinalIgnoreCase)));
            return at >= 0 && at < values.Length && values[at].Trim().Length > 0 ? values[at].Trim() : null;
        }

        DateTimeOffset? received = DateTime.TryParse(
            Column("Received", "Sent", "Date") ?? string.Empty,
            System.Globalization.CultureInfo.CurrentCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var when)
            ? new DateTimeOffset(when)
            : null;

        return new DroppedEmail(values[subjectAt].Trim(), Column("From", "Sender"), received, string.Empty);
    }

    /// <summary>Plain text from an HTML body: blocks become lines, tags go, entities are decoded.</summary>
    public static string TextFromHtml(string html)
    {
        var text = ScriptOrStyle().Replace(html, string.Empty);
        text = LineBreakTags().Replace(text, "\n");
        text = AnyTag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace(' ', ' ');
        text = TrailingSpace().Replace(text, "\n");
        return BlankRuns().Replace(text, "\n\n").Trim();
    }
}
