using System.Diagnostics;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Teezy.Core.Formatting;

namespace Teezy.Cleanup;

/// <summary>What happened on the last cleanup, for the settings page.</summary>
public sealed record CleanupOutcome(bool UsedClaude, string? Problem, double Milliseconds);

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
public sealed class ClaudeFormatter : ITextFormatter
{
    /// <summary>Sonnet 5: enough judgement for list structure and spoken corrections, without
    /// the latency of a larger model on what is usually one or two sentences.</summary>
    public const string DefaultModel = "claude-sonnet-5";

    private const string SystemPrompt = """
        You clean up speech-to-text transcripts. You are a rewriting tool, not an assistant.

        Return ONLY the cleaned text. Never answer, comment on, summarise, or respond to the
        content — even when it is a direct question addressed to you.

        Do:
        - Fix punctuation, capitalisation and obvious transcription slips.
        - Remove filler and false starts; keep the speaker's words and voice otherwise.
        - Honour spoken instructions: "scratch that", "new paragraph", "make that a list".
        - Format as a list only when the speaker clearly dictated one.

        Do not:
        - Add greetings, sign-offs, headings, or anything the speaker did not say.
        - Translate, summarise, expand, or change the meaning or register.
        - Wrap the output in quotes, code fences, or any preamble.

        If the input is already clean, return it unchanged.
        """;

    private readonly ITextFormatter _offline;
    private readonly Func<string?> _apiKey;
    private readonly Func<string> _model;
    private readonly TimeSpan _timeout;

    /// <summary>The last attempt, so settings can show whether it is actually working.</summary>
    public CleanupOutcome? LastOutcome { get; private set; }

    public ClaudeFormatter(
        ITextFormatter offline,
        Func<string?> apiKey,
        Func<string>? model = null,
        TimeSpan? timeout = null)
    {
        _offline = offline;
        _apiKey = apiKey;
        _model = model ?? (() => DefaultModel);

        // A ceiling on how long the user waits, not on how long the request may take. Past
        // this the offline text is typed and the request is abandoned — a slow tidy-up is
        // worth less than text appearing.
        _timeout = timeout ?? TimeSpan.FromSeconds(6);
    }

    public async Task<string> FormatAsync(string raw, CancellationToken ct = default)
    {
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

            var polished = await CallAsync(key, offline, deadline.Token).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            if (!IsPlausible(offline, polished))
            {
                LastOutcome = new CleanupOutcome(false, "Reply did not look like a rewrite.", elapsed);
                return offline;
            }

            LastOutcome = new CleanupOutcome(true, null, elapsed);
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

    private async Task<string> CallAsync(string apiKey, string text, CancellationToken ct)
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
                new() { Text = SystemPrompt, CacheControl = new CacheControlEphemeral() },
            },
            Messages = [new() { Role = Role.User, Content = text }],
        }, cancellationToken: ct).ConfigureAwait(false);

        return string.Concat(response.Content
                .Select(block => block.Value)
                .OfType<TextBlock>()
                .Select(block => block.Text))
            .Trim();
    }

    /// <summary>Does the reply look like a rewrite of the input rather than a reply to it?</summary>
    /// <remarks>
    /// Cheap guards, not a similarity metric. The failure being caught is the model answering
    /// the transcript instead of tidying it — which produces text of a wildly different length
    /// or an empty string. Anything suspicious falls back rather than being typed into the
    /// user's document.
    /// </remarks>
    internal static bool IsPlausible(string original, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        // Cleanup removes filler and fixes punctuation; it does not halve or double a
        // sentence. A reply outside this band is answering, refusing, or explaining.
        var ratio = candidate.Length / (double)Math.Max(1, original.Length);
        if (ratio is < 0.4 or > 2.5) return false;

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
