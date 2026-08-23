using System.Diagnostics;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Teezy.Core.Cost;
using Teezy.Core.Formatting;

namespace Teezy.Cleanup;

/// <summary>What happened on the last cleanup, for the settings page.</summary>
/// <param name="Tokens">
/// What the call consumed, as counted by the API. Null when no call was made — which is not
/// the same as a call that consumed nothing, and the difference is what stops a failed
/// request being recorded as a free one.
/// </param>
/// <param name="Model">The model actually asked, so history can be priced later against the
/// rate that applied at the time rather than against whatever is selected today.</param>
public sealed record CleanupOutcome(
    bool UsedClaude,
    string? Problem,
    double Milliseconds,
    TokenUsage? Tokens = null,
    string? Model = null);

/// <summary>Cleans dictated text with Claude, falling back to the offline rules.</summary>
/// <remarks>
/// <para>
/// <b>The rule-based pass always runs first, and its output is the floor.</b> Claude is asked
/// to improve on an already-cleaned string, and anything that goes wrong — no key, no
/// network, rate limit, timeout, refusal, a reply that looks nothing like the input — returns
/// the offline result. Dictation is a foreground interaction: it must never fail, and it must
/// never be worse than it was with the tier switched off.
/// </para>
/// <para>
/// The model is asked to rewrite, not to converse, and the output is validated before use.
/// An LLM given a transcript will sometimes answer it — "should we deploy Friday?" comes back
/// as advice about Fridays rather than as tidied text — and typing that into the user's
/// document would be a far worse failure than leaving an "um" in.
/// </para>
/// </remarks>
public sealed class ClaudeFormatter : ITextFormatter, IReportsUsage
{
    public TokenUsage? LastTokens => LastOutcome?.Tokens;

    public string? LastModel => LastOutcome?.Model;

    /// <summary>Sonnet 5: enough judgement for list structure and spoken corrections, without
    /// the latency of a larger model on what is usually one or two sentences.</summary>
    public const string DefaultModel = "claude-sonnet-5";

    /// <remarks>
    /// The half that never varies. Everything here is about staying a rewriting tool; the
    /// style clause that follows it only ever adjusts how far the wording may move. Note what
    /// is <i>not</i> in this list any more: register. That moved into the style, which is the
    /// whole point of the setting.
    /// </remarks>
    private const string BasePrompt = """
        You clean up speech-to-text transcripts. You are a rewriting tool, not an assistant.

        Return ONLY the cleaned text. Never answer, comment on, summarise, or respond to the
        content — even when it is a direct question addressed to you.

        Do:
        - Fix punctuation, capitalisation and obvious transcription slips.
        - Remove filler and false starts.
        - Honour spoken instructions: "scratch that", "new paragraph", "make that a list".
        - Format as a list only when the speaker clearly dictated one.

        Do not:
        - Add greetings, sign-offs, headings, or anything the speaker did not say.
        - Translate, summarise, expand, or change the meaning.
        - Wrap the output in quotes, code fences, or any preamble.

        If the input is already clean, return it unchanged.
        """;

    /// <summary>The one clause that differs between styles.</summary>
    private static string StyleClause(WritingStyle style) => style switch
    {
        WritingStyle.Polished =>
            "Style: tighten the writing. Cut redundancy and repair awkward phrasing, but keep "
            + "the speaker's voice and register. Add nothing.",
        WritingStyle.Formal =>
            "Style: raise the register to professional written English. Expand contractions, "
            + "avoid slang, keep sentences complete. Do not add content or change the meaning.",
        WritingStyle.Casual =>
            "Style: relax the register. Contractions are fine; prefer short sentences and "
            + "plain words. Do not add content or change the meaning.",
        _ =>
            "Style: keep the speaker's own words and register. Change the wording only where "
            + "the transcript is wrong or unreadable.",
    };

    /// <summary>
    /// How far the reply may shrink before it is treated as an answer rather than a rewrite.
    /// </summary>
    /// <remarks>
    /// Styles that are allowed to tighten legitimately produce shorter text, and holding them
    /// to the faithful threshold would reject good rewrites — which the user would experience
    /// as the setting doing nothing, because the fallback is silent. The ceiling does not move:
    /// nothing here has licence to make text much longer.
    /// </remarks>
    internal static double MinRatioFor(WritingStyle style) => style switch
    {
        WritingStyle.Polished => 0.3,
        WritingStyle.Casual => 0.3,
        WritingStyle.Formal => 0.35,
        _ => 0.4,
    };

    private readonly ITextFormatter _offline;
    private readonly Func<string?> _apiKey;
    private readonly Func<string> _model;
    private readonly Func<FormatContext, CleanupStyle> _styleFor;
    private readonly TimeSpan _timeout;

    /// <summary>The last attempt, so settings can show whether it is actually working.</summary>
    public CleanupOutcome? LastOutcome { get; private set; }

    public ClaudeFormatter(
        ITextFormatter offline,
        Func<string?> apiKey,
        Func<string>? model = null,
        TimeSpan? timeout = null,
        Func<FormatContext, CleanupStyle>? styleFor = null)
    {
        _offline = offline;
        _apiKey = apiKey;
        _model = model ?? (() => DefaultModel);
        _styleFor = styleFor ?? (_ => new CleanupStyle(WritingStyle.Faithful, null));

        // A ceiling on how long the user waits, not on how long the request may take. Past
        // this the offline text is typed and the request is abandoned — a slow tidy-up is
        // worth less than text appearing.
        _timeout = timeout ?? TimeSpan.FromSeconds(6);
    }

    public Task<string> FormatAsync(string raw, CancellationToken ct = default) =>
        FormatAsync(raw, FormatContext.None, ct);

    public async Task<string> FormatAsync(
        string raw, FormatContext context, CancellationToken ct = default)
    {
        // Resolved once, up front. Reading it again later would let a settings edit mid-flight
        // prompt with one style and validate against another's threshold.
        var style = _styleFor(context);

        var offline = await _offline.FormatAsync(raw, ct).ConfigureAwait(false);

        if (offline.Length == 0) return offline;
        if (_apiKey() is not { Length: > 0 } key)
        {
            LastOutcome = new CleanupOutcome(false, "No API key set.", 0);
            return offline;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_timeout);

            var (polished, tokens) = await CallAsync(key, offline, style, deadline.Token).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            // Tokens are recorded even when the reply is rejected. They were spent either way,
            // and a cost report that only counted the calls that worked would understate the
            // bill exactly when something is going wrong.
            if (!IsPlausible(offline, polished, MinRatioFor(style.Style)))
            {
                LastOutcome = new CleanupOutcome(
                    false, "Reply did not look like a rewrite.", elapsed, tokens, _model());
                return offline;
            }

            LastOutcome = new CleanupOutcome(true, null, elapsed, tokens, _model());
            return polished;
        }
        catch (OperationCanceledException)
        {
            LastOutcome = new CleanupOutcome(
                false, $"Took longer than {_timeout.TotalSeconds:0}s.",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return offline;
        }
        catch (Exception e) when (e is AnthropicApiException or HttpRequestException or IOException)
        {
            LastOutcome = new CleanupOutcome(
                false, Describe(e), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return offline;
        }
    }

    /// <summary>Base rules, then the style, then the user's own line.</summary>
    /// <remarks>
    /// The user's instruction goes last and is explicitly subordinate to the rules above it.
    /// It is their own text on their own machine, not untrusted input — but "rewrite my
    /// dictation" and "answer my dictation" are one careless sentence apart, and the ordering
    /// plus the plausibility guard mean a badly worded instruction degrades to a fallback
    /// rather than typing an answer into whatever had focus.
    /// </remarks>
    internal static string BuildPrompt(CleanupStyle style)
    {
        var prompt = $"{BasePrompt}\n\n{StyleClause(style.Style)}";

        if (style.Instruction is { } extra && !string.IsNullOrWhiteSpace(extra))
        {
            prompt += "\n\nThe user also asked for this, where it does not conflict with "
                      + $"anything above:\n{extra.Trim()}";
        }

        return prompt;
    }

    private async Task<(string Text, TokenUsage Tokens)> CallAsync(
        string apiKey, string text, CleanupStyle style, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = apiKey };

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = _model(),

            // Generous relative to the input, which is one or two sentences. Too low and a
            // long utterance is truncated mid-word and then rejected by IsPlausible, costing
            // the call for nothing.
            MaxTokens = 2000,

            // The instructions are identical on every call and the text is not, so the system
            // prompt is the cacheable prefix. It is also the larger half of a short request.
            System = new List<TextBlockParam>
            {
                new() { Text = BuildPrompt(style), CacheControl = new CacheControlEphemeral() },
            },
            Messages = [new() { Role = Role.User, Content = text }],
        }, cancellationToken: ct).ConfigureAwait(false);

        var reply = string.Concat(response.Content
                .Select(block => block.Value)
                .OfType<TextBlock>()
                .Select(block => block.Text))
            .Trim();

        var usage = new TokenUsage(
            (int)response.Usage.InputTokens,
            (int)response.Usage.OutputTokens,
            (int)(response.Usage.CacheReadInputTokens ?? 0),
            (int)(response.Usage.CacheCreationInputTokens ?? 0));

        return (reply, usage);
    }

    /// <summary>Does the reply look like a rewrite of the input rather than a reply to it?</summary>
    /// <remarks>
    /// Cheap guards, not a similarity metric. The failure being caught is the model answering
    /// the transcript instead of tidying it — which produces text of a wildly different length
    /// or an empty string. Anything suspicious falls back rather than being typed into the
    /// user's document.
    /// </remarks>
    internal static bool IsPlausible(string original, string candidate, double minRatio = 0.4)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        // Cleanup removes filler and fixes punctuation; it does not halve or double a
        // sentence. A reply outside this band is answering, refusing, or explaining. The floor
        // moves with the style — see MinRatioFor — because a style told to tighten will
        // legitimately come back shorter.
        var ratio = candidate.Length / (double)Math.Max(1, original.Length);
        if (ratio < minRatio || ratio > 2.5) return false;

        // Wrapping in quotes or fences means it treated the text as a quoted artefact.
        return !candidate.StartsWith("```", StringComparison.Ordinal);
    }

    private static string Describe(Exception e) => e switch
    {
        AnthropicUnauthorizedException => "API key was rejected.",
        AnthropicRateLimitException => "Rate limited by the API.",
        AnthropicNotFoundException => "That model is not available on this account.",
        HttpRequestException => "Could not reach the API.",
        _ => e.Message,
    };
}
