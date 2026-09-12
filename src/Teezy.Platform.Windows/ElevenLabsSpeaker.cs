using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.Wave;
using Teezy.Core.Abstractions;
using Teezy.Core.Voice;

namespace Teezy.Platform.Windows;

/// <summary>Reads answers with an ElevenLabs voice.</summary>
/// <remarks>
/// <para>
/// The paid half of the two-tier arrangement: a voice worth listening to, in exchange for a
/// subscription, a third API key and a network round trip before the first word. That last one
/// is the cost people do not expect — the assistant has <i>already</i> waited for Claude by the
/// time it speaks, so this delay lands on top of an existing one.
/// </para>
/// <para>
/// <b>Characters are counted whether or not you hear them.</b> The provider bills for what it
/// synthesised, so the count happens at the request and not at the end of playback, which the
/// user can cut short.
/// </para>
/// </remarks>
public sealed class ElevenLabsSpeaker : ISpeaker
{
    private const string Api = "https://api.elevenlabs.io";

    private readonly Func<string?> _apiKey;
    private readonly Func<string> _model;
    private readonly VoiceUsage _usage;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly Lock _gate = new();
    private CancellationTokenSource? _playing;
    private WaveOutEvent? _device;

    public ElevenLabsSpeaker(Func<string?> apiKey, Func<string> model, VoiceUsage usage)
    {
        _apiKey = apiKey;
        _model = model;
        _usage = usage;
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey()) && PreferredVoice is { Length: > 0 };

    /// <summary>The ElevenLabs voice id. There is no sensible automatic choice.</summary>
    /// <remarks>
    /// Unlike the local voices, these are an account's own library rather than a fixed set, so
    /// there is nothing to fall back to — an unset voice means the tier simply is not ready.
    /// </remarks>
    public string? PreferredVoice { get; set; }

    public string? VoiceName { get; private set; }

    /// <summary>
    /// Why the last attempt to list voices came back empty, or null if it did not.
    /// </summary>
    /// <remarks>
    /// Exists because the first version of this swallowed every failure and returned an empty
    /// list, so a wrong endpoint was indistinguishable from an unsaved key — and the settings
    /// page confidently told the user to save the key they had just saved. An empty list is
    /// never self-explanatory; it always needs a reason attached.
    /// </remarks>
    public string? VoiceListError { get; private set; }

    /// <summary>The account's voices. Requires a key, and a network call.</summary>
    public IReadOnlyList<SpeechVoice> Voices()
    {
        if (_apiKey() is not { Length: > 0 } key)
        {
            VoiceListError = null;
            return [];
        }

        try
        {
            // v2, not v1. v1 still exists and still answers, which is exactly why getting this
            // wrong was invisible: it authenticates happily and returns nothing useful.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/v2/voices");
            request.Headers.Add("xi-api-key", key);

            using var response = _http.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                // The body says what is wrong, in words. Reporting only the status code throws
                // away the one part of the answer that is actually diagnostic.
                VoiceListError = $"ElevenLabs returned {(int)response.StatusCode}"
                                 + (Explain(response) is { Length: > 0 } why ? $": {why}" : ".");
                return [];
            }

            using var stream = response.Content.ReadAsStream();
            var list = JsonSerializer.Deserialize<VoiceList>(stream);

            VoiceListError = list?.Voices is { Count: > 0 }
                ? null
                : "That account has no voices in it.";

            return
            [
                .. (list?.Voices ?? [])
                    .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId) && !string.IsNullOrWhiteSpace(v.Name))
                    .Select(v => new SpeechVoice(
                        Id: v.VoiceId!,
                        Name: v.Name!,
                        Culture: v.Labels?.GetValueOrDefault("accent") ?? v.Category ?? "ElevenLabs",
                        Gender: v.Labels?.GetValueOrDefault("gender") ?? string.Empty,
                        IsModern: true)),
            ];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            VoiceListError = $"Couldn’t reach ElevenLabs: {e.Message}";
            return [];
        }
    }

    /// <summary>
    /// The human-readable half of an error response.
    /// </summary>
    /// <remarks>
    /// ElevenLabs answers a failure with <c>{"detail":{"message":"…"}}</c>, sometimes with
    /// <c>detail</c> as a plain string instead. Both shapes are worth reading: without them a
    /// 400 is just a number, and the number is the least useful part of what was said.
    /// </remarks>
    private static string? Explain(HttpResponseMessage response)
    {
        try
        {
            var body = new StreamReader(response.Content.ReadAsStream()).ReadToEnd();
            if (string.IsNullOrWhiteSpace(body)) return null;

            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("detail", out var detail))
            {
                return Shorten(body);
            }

            return detail.ValueKind switch
            {
                JsonValueKind.String => detail.GetString(),
                JsonValueKind.Object when detail.TryGetProperty("message", out var m)
                    => m.GetString(),
                _ => Shorten(body),
            };
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Enough to diagnose, little enough to fit in a settings hint.</summary>
    private static string Shorten(string body) =>
        body.Length <= 200 ? body.Trim() : body[..200].Trim() + "…";

    public async Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_apiKey() is not { Length: > 0 } key) return;
        if (PreferredVoice is not { Length: > 0 } voice) return;

        Stop();

        CancellationTokenSource cts;
        lock (_gate)
        {
            _playing = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts = _playing;
        }

        try
        {
            var audio = await Synthesise(key, voice, text, cts.Token).ConfigureAwait(false);
            if (audio is null) return;

            // Counted once audio has actually come back: after that the provider has billed
            // for it whether or not the user lets it finish playing, but a request that failed
            // — a rejected key, a rate limit — costs nothing and must not appear in a figure
            // someone is about to choose a subscription tier with.
            _usage.Add(text.Length);

            if (cts.IsCancellationRequested) return;

            Play(audio, cts.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // The answer is already on screen. Losing the voice is not worth an error dialog.
        }
    }

    private async Task<byte[]?> Synthesise(string key, string voice, string text, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{Api}/v1/text-to-speech/{voice}?output_format=mp3_44100_128");

        request.Headers.Add("xi-api-key", key);
        request.Content = JsonContent.Create(new { text, model_id = _model() });

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private void Play(byte[] mp3, CancellationToken ct)
    {
        var stream = new MemoryStream(mp3);

        try
        {
            var reader = new Mp3FileReader(stream);
            var device = new WaveOutEvent();
            device.Init(reader);

            device.PlaybackStopped += (_, _) =>
            {
                device.Dispose();
                reader.Dispose();
                stream.Dispose();
            };

            lock (_gate) _device = device;

            if (ct.IsCancellationRequested) { device.Dispose(); return; }
            device.Play();
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            stream.Dispose();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _playing?.Cancel();
            _playing = null;

            try { _device?.Stop(); }
            catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { }

            _device = null;
        }
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }

    private sealed class VoiceList
    {
        [JsonPropertyName("voices")] public List<Voice>? Voices { get; set; }
    }

    private sealed class Voice
    {
        [JsonPropertyName("voice_id")] public string? VoiceId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("labels")] public Dictionary<string, string>? Labels { get; set; }
    }
}
