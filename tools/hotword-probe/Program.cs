// Does hotword biasing actually change what the model returns, and at what strength?
//
// This exists because the answer was "no" and nothing said so. The Parakeet export ships
// tokens.txt and no bpe.model, so sherpa-onnx could not split a hint into tokens and skipped
// every one of them - a feature that was documented, shown in the UI, and inert. The only way
// to know either way is to decode real audio with and without hints and compare the strings.
//
//   curl -L -o test.wav https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8/resolve/main/test_wavs/0.wav
//   dotnet run -c Release -- test.wav "Phoebe|Anthropic" 2.5
//
// Keep it for tuning hint strength: too low does nothing, too high makes the engine hear
// hinted words in audio that never contained them.
using Teezy.Core.Abstractions;
using Teezy.Speech;

var wavPath = args.Length > 0 ? args[0] : "test.wav";
var hotwords = args.Length > 1 ? args[1].Replace("|", "\n") : null;
var score = args.Length > 2 ? float.Parse(args[2]) : 5.0f;

var wav = File.ReadAllBytes(wavPath);
var count = (wav.Length - 44) / 2;
var samples = new float[count];
for (var i = 0; i < count; i++) samples[i] = BitConverter.ToInt16(wav, 44 + i * 2) / 32768f;

Console.WriteLine($"{count / 16000.0:0.0}s of audio\n");

async Task<string> Run(string label, DecodingMethod method, string? hot, float hotScore)
{
    var transcriber = new ParakeetTranscriber(
        null,
        new SpeechOptions { Decoding = method, BeamSize = 4, HotwordScore = hotScore },
        () => hot);
    try
    {
        await transcriber.LoadAsync();
        var text = await transcriber.TranscribeAsync(samples);
        Console.WriteLine($"{label,-22}: {text}");
        return text;
    }
    catch (Exception e)
    {
        Console.WriteLine($"{label,-22}: FAILED — {e.Message}");
        return $"<failed:{e.GetType().Name}>";
    }
    finally
    {
        transcriber.Dispose();
    }
}

var greedy = await Run("greedy", DecodingMethod.Greedy, null, 1.5f);
var beam = await Run("beam, no hotwords", DecodingMethod.BeamSearch, null, 1.5f);

if (hotwords is null)
{
    Console.WriteLine("\nNo hotwords given. Re-run with a pipe-separated list to test biasing.");
    return;
}

var biased = await Run($"beam + hotwords @{score}", DecodingMethod.BeamSearch, hotwords, score);

Console.WriteLine();
Console.WriteLine(beam == biased
    ? "VERDICT: hotwords changed NOTHING — biasing is inert for this model."
    : "VERDICT: hotwords CHANGED the transcript — biasing works.");
Console.WriteLine($"greedy == beam ? {greedy == beam}");
