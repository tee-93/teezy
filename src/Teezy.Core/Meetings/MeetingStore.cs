using System.Globalization;
using System.Text.Json;

namespace Teezy.Core.Meetings;

/// <summary>What is known about one recorded meeting.</summary>
/// <param name="Started">When recording began.</param>
/// <param name="Recorded">How long it ran. Zero until recording stops cleanly.</param>
/// <param name="MeDevice">The microphone that was recorded.</param>
/// <param name="ThemDevice">The speakers that were recorded, if they could be.</param>
/// <param name="ThemProblem">Why the speakers could not be recorded, in Windows' words.</param>
/// <param name="MeHeard">Whether the microphone ever delivered anything but digital silence.</param>
/// <param name="ThemHeard">Whether the speakers ever did.</param>
/// <param name="Stats">Set once transcribed.</param>
public sealed record MeetingInfo(
    DateTimeOffset Started,
    TimeSpan Recorded = default,
    string? MeDevice = null,
    string? ThemDevice = null,
    string? ThemProblem = null,
    bool MeHeard = false,
    bool ThemHeard = false,
    TranscriptionStats? Stats = null);

/// <summary>One meeting's folder on disk.</summary>
public sealed record MeetingRecord(string Folder, MeetingInfo Info)
{
    public string MePath => Path.Combine(Folder, "me.wav");

    public string ThemPath => Path.Combine(Folder, "them.wav");

    /// <summary>
    /// A .txt, not .md: every Windows machine opens a text file, and a managed work laptop may
    /// have nothing registered for Markdown at all.
    /// </summary>
    public string TranscriptPath => Path.Combine(Folder, "transcript.txt");

    internal string InfoPath => Path.Combine(Folder, "meeting.json");

    /// <summary>The summary and follow-ups, as data, so the PDF can always be made again.</summary>
    public string NotesPath => Path.Combine(Folder, "notes.json");

    public string PdfPath => Path.Combine(Folder, "notes.pdf");

    public bool HasTranscript => File.Exists(TranscriptPath);

    public bool HasNotes => File.Exists(NotesPath);

    public bool HasAudio => File.Exists(MePath) || File.Exists(ThemPath);
}

/// <summary>Recorded meetings, one folder each, kept on this machine.</summary>
/// <remarks>
/// <para>
/// A folder per meeting rather than one index file: the audio, the transcript and what is
/// known about them live and die together, deleting a meeting is deleting a folder, and a
/// half-written index cannot lose the list.
/// </para>
/// <para>
/// <b>The audio is deleted once it has been transcribed.</b> An hour of a work meeting in a
/// WAV file is a liability sitting in a folder; the transcript is what was wanted.
/// </para>
/// </remarks>
public sealed class MeetingStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public MeetingStore(string? root = null) => Root = root ?? DefaultRoot;

    public string Root { get; }

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Teezy", "Meetings");

    /// <summary>Makes the folder for a meeting starting now.</summary>
    public MeetingRecord Create(DateTimeOffset started)
    {
        Directory.CreateDirectory(Root);

        var name = started.ToLocalTime().ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        var folder = Path.Combine(Root, name);
        for (var n = 2; Directory.Exists(folder); n++)
        {
            folder = Path.Combine(Root, string.Create(CultureInfo.InvariantCulture, $"{name} ({n})"));
        }

        Directory.CreateDirectory(folder);

        var record = new MeetingRecord(folder, new MeetingInfo(started));
        Save(record);
        return record;
    }

    /// <summary>Writes what is known, replacing the previous version whole.</summary>
    public void Save(MeetingRecord record)
    {
        var temporary = record.InfoPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record.Info, Json));
        File.Move(temporary, record.InfoPath, overwrite: true);
    }

    public void SaveNotes(MeetingRecord record, SavedNotes notes)
    {
        var temporary = record.NotesPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(notes, Json));
        File.Move(temporary, record.NotesPath, overwrite: true);
    }

    /// <summary>The meeting's notes, or null if it has none or they cannot be read.</summary>
    public SavedNotes? LoadNotes(MeetingRecord record)
    {
        try
        {
            return JsonSerializer.Deserialize<SavedNotes>(File.ReadAllText(record.NotesPath), Json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Every meeting, newest first. Folders that cannot be read are skipped.</summary>
    public IReadOnlyList<MeetingRecord> List()
    {
        if (!Directory.Exists(Root)) return [];

        var records = new List<MeetingRecord>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            try
            {
                var json = File.ReadAllText(Path.Combine(folder, "meeting.json"));
                if (JsonSerializer.Deserialize<MeetingInfo>(json, Json) is { } info)
                {
                    records.Add(new MeetingRecord(folder, info));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // Someone else's folder, or one torn by a crash before its first save.
            }
        }

        return [.. records.OrderByDescending(r => r.Info.Started)];
    }

    public void Delete(MeetingRecord record)
    {
        if (Directory.Exists(record.Folder)) Directory.Delete(record.Folder, recursive: true);
    }

    public void DeleteAudio(MeetingRecord record)
    {
        File.Delete(record.MePath);
        File.Delete(record.ThemPath);
    }
}
