namespace Wisper.Speech;

/// <summary>Finds the Parakeet model files and reports precisely what is missing.</summary>
/// <remarks>
/// Worth more care than it looks. sherpa-onnx does <b>not</b> throw when handed a bad model
/// path — it prints a message to stderr and returns a recogniser that fails later, in a
/// place that has nothing to do with the real cause. Everything here exists so that a
/// missing or truncated file is reported once, up front, in the user's own terms.
/// </remarks>
public sealed record ModelPaths(string Directory, string Encoder, string Decoder, string Joiner, string Tokens)
{
    /// <summary>Approximate expected sizes. A truncated download is the single most common
    /// failure and it does not announce itself — a partial encoder surfaces as an opaque
    /// protobuf parse error at load time.</summary>
    private static readonly (string Name, long Bytes)[] Expected =
    [
        ("encoder.int8.onnx", 652_183_000),
        ("decoder.int8.onnx", 7_257_753),
        ("joiner.int8.onnx",  1_739_080),
        ("tokens.txt",        9_384),
    ];

    private const long SizeTolerance = 2 * 1024 * 1024;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wisper", "models", "parakeet-v2");

    public static ModelPaths For(string directory) => new(
        directory,
        Path.Combine(directory, "encoder.int8.onnx"),
        Path.Combine(directory, "decoder.int8.onnx"),
        Path.Combine(directory, "joiner.int8.onnx"),
        Path.Combine(directory, "tokens.txt"));

    /// <summary>Where to look, in order: the user's override, the default location, then
    /// next to the executable for a self-contained install.</summary>
    public static IEnumerable<string> SearchPath(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        yield return DefaultDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "models", "parakeet-v2");
    }

    public static ModelPaths? Resolve(string? configured) =>
        SearchPath(configured).Select(For).FirstOrDefault(p => p.Validate().Count == 0);

    /// <summary>Every problem with this directory, phrased for a person.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (!System.IO.Directory.Exists(Directory))
            return [$"Model folder not found: {Directory}"];

        foreach (var (name, expected) in Expected)
        {
            var path = Path.Combine(Directory, name);
            if (!File.Exists(path))
            {
                problems.Add($"Missing: {name}");
                continue;
            }

            var actual = new FileInfo(path).Length;
            if (Math.Abs(actual - expected) > SizeTolerance)
            {
                problems.Add(
                    $"{name} is {actual / 1024.0 / 1024.0:F1} MB, expected about "
                    + $"{expected / 1024.0 / 1024.0:F1} MB — the download was probably truncated.");
            }
        }

        return problems;
    }
}
