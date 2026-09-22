using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Teezy.Speech;

/// <summary>Where the Kokoro voice lives, and fetching it the first time it is chosen.</summary>
/// <remarks>
/// <para>
/// Kokoro v1.0, int8, as packaged for sherpa-onnx: the model, the voice table, the token list,
/// the English pronunciation lexicons, and espeak-ng's data for words the lexicons lack. About
/// 170 MB, fetched on request — never bundled in the installer, since most people never switch
/// the voice on.
/// </para>
/// <para>
/// Downloaded the way the speech model is (see <see cref="ModelDownloader"/>): each file to
/// <c>.part</c>, renamed only once complete, so an interrupted download leaves nothing half
/// written and a retry fetches only what is missing. The espeak-ng folder is several hundred
/// small files, so its listing comes from Hugging Face's API rather than being written out here.
/// </para>
/// </remarks>
public sealed class KokoroModel
{
    private const string Repo = "csukuangfj/kokoro-int8-multi-lang-v1_0";
    private const string Files = $"https://huggingface.co/{Repo}/resolve/main/";
    private const string Tree = $"https://huggingface.co/api/models/{Repo}/tree/main/";

    public const string ModelFile = "model.int8.onnx";
    public const string VoicesFile = "voices.bin";
    public const string TokensFile = "tokens.txt";
    public const string BritishLexicon = "lexicon-gb-en.txt";
    public const string AmericanLexicon = "lexicon-us-en.txt";
    public const string DataFolder = "espeak-ng-data";

    /// <summary>The files other than espeak-ng's, with the sizes that show a download is whole.</summary>
    internal static readonly (string Name, long Bytes)[] Main =
    [
        (TokensFile, 687),
        (AmericanLexicon, 5_956_885),
        (BritishLexicon, 6_366_635),
        (VoicesFile, 28_200_960),
        (ModelFile, 114_203_756),
    ];

    /// <summary>What a finished download leaves in the folder, so it is not fetched again.</summary>
    private const string CompleteMarker = ".complete";

    private readonly HttpClient _http;

    public KokoroModel(string? directory = null, HttpClient? http = null)
    {
        Directory = directory ?? DefaultDirectory;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Teezy", "models", "kokoro-v1.0");

    public string Directory { get; }

    public static long ApproximateBytes => Main.Sum(f => f.Bytes) + 18_000_000;

    public bool IsInstalled =>
        File.Exists(Path.Combine(Directory, CompleteMarker))
        && Main.All(f => File.Exists(Path.Combine(Directory, f.Name)))
        && System.IO.Directory.Exists(Path.Combine(Directory, DataFolder));

    /// <summary>Fetches whatever is missing. Safe to run again after a failure or a cancel.</summary>
    public async Task DownloadAsync(IProgress<ModelDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(Directory);

        var data = await ListDataAsync(ct).ConfigureAwait(false);
        List<(string Name, long Bytes)> all = [.. data, .. Main];
        var total = all.Sum(f => f.Bytes);
        long done = 0;

        for (var i = 0; i < all.Count; i++)
        {
            var (name, bytes) = all[i];
            var final = Path.Combine(Directory, name.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(final) || new FileInfo(final).Length != bytes)
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                var part = final + ".part";
                var fileStart = done;

                using (var response = await _http.GetAsync(Url(name), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var target = File.Create(part);

                    var buffer = new byte[128 * 1024];
                    long received = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        received += read;

                        // Reported for the big files only; hundreds of tiny ones would flood the UI.
                        if (bytes > 1_000_000)
                        {
                            progress?.Report(new ModelDownloadProgress(
                                name, i + 1, all.Count, received, bytes, (fileStart + received) / (double)total));
                        }
                    }
                }

                if (new FileInfo(part).Length != bytes)
                {
                    File.Delete(part);
                    throw new IOException($"{name} arrived incomplete. Try the download again.");
                }

                File.Move(part, final, overwrite: true);
            }

            done += bytes;
            progress?.Report(new ModelDownloadProgress(name, i + 1, all.Count, bytes, bytes, done / (double)total));
        }

        await File.WriteAllTextAsync(Path.Combine(Directory, CompleteMarker), DateTimeOffset.Now.ToString("O"), ct).ConfigureAwait(false);
    }

    /// <summary>Removes the voice, e.g. to switch it off for good and get the space back.</summary>
    public void Delete()
    {
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
    }

    /// <summary>Every file under espeak-ng-data, with its size.</summary>
    private async Task<List<(string Name, long Bytes)>> ListDataAsync(CancellationToken ct)
    {
        var entries = await _http.GetFromJsonAsync<List<TreeEntry>>(
            Tree + DataFolder + "?recursive=true", ct).ConfigureAwait(false) ?? [];

        return [.. entries
            .Where(e => e.Type == "file" && e.Path is { Length: > 0 })
            .Select(e => (e.Path!, e.Size))];
    }

    /// <summary>Each path segment escaped: espeak-ng has files named like "!v/Mr serious".</summary>
    internal static string Url(string path) =>
        Files + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private sealed class TreeEntry
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}
