using Wisper.Core.Abstractions;
namespace Wisper.Speech;

/// <summary>How far along the model download is.</summary>
public sealed record ModelDownloadProgress(
    string FileName,
    int FileIndex,
    int FileCount,
    long BytesReceived,
    long BytesExpected,
    double OverallFraction)
{
    public string Describe() =>
        $"{FileName} — {BytesReceived / 1024.0 / 1024.0:F0} of {BytesExpected / 1024.0 / 1024.0:F0} MB";
}

/// <summary>Fetches the Parakeet model on first run.</summary>
/// <remarks>
/// <para>
/// The one failure worth engineering against is the <b>truncated download</b>: it is the most
/// common way this goes wrong and it does not announce itself. A partially-written encoder is
/// a valid file on disk that fails much later, at model load, with an opaque protobuf parse
/// error naming nothing useful.
/// </para>
/// <para>
/// Two things prevent that here. Each file is written to <c>.part</c> and only renamed into
/// place once its length has been checked, so an interrupted run leaves no file rather than a
/// half one. And the rename is the last step, which makes the whole thing safely re-runnable:
/// a retry re-fetches exactly what is missing.
/// </para>
/// </remarks>
public sealed class ModelDownloader
{
    private const string BaseUrl =
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8/resolve/main";

    /// <summary>Files to fetch, largest last so the progress bar spends its time where the
    /// bytes actually are rather than jumping at the end.</summary>
    internal static readonly (string Name, long Bytes)[] Files =
    [
        ("tokens.txt",        9_384),
        ("joiner.int8.onnx",  1_739_080),
        ("decoder.int8.onnx", 7_257_753),
        ("encoder.int8.onnx", 652_183_000),
    ];

    /// <summary>Sizes differ slightly between builds; this is a truncation check, not a hash.</summary>
    private const long SizeTolerance = 2 * 1024 * 1024;

    internal static long TotalBytes => Files.Sum(f => f.Bytes);

    private readonly HttpClient _http;

    public ModelDownloader(HttpClient? http = null)
    {
        // No overall timeout: 661 MB on a slow connection legitimately exceeds any sensible
        // one, and HttpClient's default 100 s would abort a working download partway. The
        // CancellationToken is the way out.
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Downloads every missing file into <paramref name="directory"/>.</summary>
    public async Task DownloadAsync(
        string directory,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);

        long completedBytes = 0;

        for (var i = 0; i < Files.Length; i++)
        {
            var (name, expected) = Files[i];
            var final = Path.Combine(directory, name);

            // Skip what is already present and the right size, so a retry after a failure
            // does not re-fetch 650 MB that already landed.
            if (File.Exists(final) && Math.Abs(new FileInfo(final).Length - expected) < SizeTolerance)
            {
                completedBytes += expected;
                progress?.Report(new ModelDownloadProgress(
                    name, i + 1, Files.Length, expected, expected, (double)completedBytes / TotalBytes));
                continue;
            }

            var part = final + ".part";
            var received = await DownloadFileAsync(
                $"{BaseUrl}/{name}", part, expected, completedBytes,
                p => progress?.Report(p with { FileName = name, FileIndex = i + 1, FileCount = Files.Length }),
                ct).ConfigureAwait(false);

            if (Math.Abs(received - expected) >= SizeTolerance)
            {
                File.Delete(part);
                throw new TranscriberException(
                    $"{name} downloaded as {received / 1024.0 / 1024.0:F1} MB but should be about "
                    + $"{expected / 1024.0 / 1024.0:F1} MB. The download was cut short — try again.");
            }

            // Rename last. Until this line there is no file by the real name, so a crash or a
            // pulled network cable can never leave a plausible-looking partial model behind.
            File.Move(part, final, overwrite: true);
            completedBytes += received;
        }
    }

    private async Task<long> DownloadFileAsync(
        string url,
        string destination,
        long expected,
        long alreadyDone,
        Action<ModelDownloadProgress> report,
        CancellationToken ct)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? expected;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);

        var buffer = new byte[128 * 1024];
        long received = 0;
        var lastReport = 0L;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;

            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;

            // Throttle to ~1 MB: reporting every 128 KB chunk floods the UI thread with
            // thousands of dispatcher callbacks and visibly slows the download.
            if (received - lastReport < 1024 * 1024 && received != total) continue;
            lastReport = received;

            report(new ModelDownloadProgress(
                string.Empty, 0, 0, received, total,
                Math.Clamp((alreadyDone + received) / (double)TotalBytes, 0, 1)));
        }

        return received;
    }
}
