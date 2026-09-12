using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Teezy.Core.Commands;
using Teezy.Core.Cost;
using Teezy.Core.Formatting;

namespace Teezy.Assistant;

/// <summary>
/// Asks Claude what to make of an utterance the local patterns declined.
/// </summary>
/// <remarks>
/// <para>
/// <b>Claude picks from the tool list; it never names an action.</b> Every tool here maps onto
/// a <see cref="VoiceCommand"/> that already existed, and anything it returns which does not
/// map is discarded rather than guessed at. The model's influence ends at choosing an item and
/// its parameters, both of which are then validated — so the worst a confused one can do is set
/// the volume to the wrong number.
/// </para>
/// <para>
/// <b>It is sent the user's words and nothing else.</b> No screen, no clipboard, no history.
/// There is no untrusted text in the request, so there is nothing for an injection to ride in
/// on, and that is a property to defend rather than a coincidence.
/// </para>
/// <para>
/// Reached only when <see cref="CommandMatcher"/> has declined, so the everyday vocabulary
/// never costs a round trip and never leaves the machine.
/// </para>
/// </remarks>
public sealed class ClaudeAssistant(
    Func<string?> apiKey,
    Func<string> model,
    TimeSpan timeout) : IAssistantFallback, IReportsUsage
{
    /// <summary>
    /// What Claude is told it is doing.
    /// </summary>
    /// <remarks>
    /// Short on purpose, and cached as the request prefix. The instruction to keep answers to a
    /// couple of sentences is load-bearing rather than stylistic: the reply is shown in a
    /// floating pill above the taskbar and read at a glance, not scrolled.
    /// </remarks>
    private const string SystemPrompt = """
        You are the voice assistant built into Teezy, a Windows dictation app. The user held a
        key, spoke, and this is what they said. A set of local patterns already tried and
        failed to match it, so it is either a request phrased unusually or a question.

        If they are asking the computer to do something, call exactly one tool. Prefer a tool
        over an answer whenever one plausibly fits.

        Otherwise answer them directly, in at most two short sentences. Your reply appears in a
        small floating panel and is read at a glance, so be brief and never use lists,
        markdown, or preamble. If you do not know, say so in one sentence.

        Never describe what you are about to do, and never ask a follow-up question — the user
        cannot reply without pressing the key again.
        """;

    public TokenUsage? LastTokens { get; private set; }

    public string? LastModel { get; private set; }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(apiKey());

    public async Task<AssistantReply> AskAsync(string spoken, CancellationToken ct = default)
    {
        if (apiKey() is not { Length: > 0 } key)
        {
            throw new AssistantUnavailableException("The assistant needs an API key.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        try
        {
            return await CallAsync(key, spoken, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AssistantUnavailableException("Claude took too long.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new AssistantUnavailableException("Couldn’t reach Claude.", e);
        }
    }

    private async Task<AssistantReply> CallAsync(string key, string spoken, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = key };

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = model(),

            // Two short sentences, plus room for a tool call. Anything larger is a reply that
            // would not fit in the pill anyway.
            MaxTokens = 300,

            // Identical on every call, and the larger half of a short request, so it is the
            // cacheable prefix.
            System = new List<TextBlockParam>
            {
                new() { Text = SystemPrompt, CacheControl = new CacheControlEphemeral() },
            },
            Tools = [.. AssistantTools.All],
            Messages = [new() { Role = Role.User, Content = spoken }],
        }, cancellationToken: ct).ConfigureAwait(false);

        LastModel = model();
        LastTokens = new TokenUsage(
            (int)response.Usage.InputTokens,
            (int)response.Usage.OutputTokens,
            (int)(response.Usage.CacheReadInputTokens ?? 0),
            (int)(response.Usage.CacheCreationInputTokens ?? 0));

        // A tool call wins over any text alongside it: the model sometimes narrates what it is
        // doing, and that narration is not an answer to show.
        foreach (var block in response.Content.Select(b => b.Value).OfType<ToolUseBlock>())
        {
            if (AssistantTools.ToCommand(block.Name, block.Input) is { } command)
            {
                return new AssistantReply(Command: command);
            }
        }

        var text = string.Concat(response.Content
                .Select(b => b.Value)
                .OfType<TextBlock>()
                .Select(b => b.Text))
            .Trim();

        return text.Length == 0 ? AssistantReply.Nothing : new AssistantReply(Answer: text);
    }
}
