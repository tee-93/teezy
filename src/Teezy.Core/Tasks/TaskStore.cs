using System.Text.Json;
using System.Text.Json.Serialization;

namespace Teezy.Core.Tasks;

/// <summary>The task list, kept as one JSON file, and the only thing that changes it.</summary>
/// <remarks>
/// <para>
/// Every change stamps <see cref="TaskItem.Modified"/>, which is what lets two computers that both
/// changed the list while apart be merged task by task (<see cref="Merge"/>) rather than one
/// computer's list replacing the other's. A deletion is kept as a marker for the same reason: a
/// task simply missing from one copy is indistinguishable from one that copy has not heard of.
/// </para>
/// <para>
/// Written atomically — beside the real file, then swapped in — so a crash mid-write leaves the
/// previous list rather than half of one.
/// </para>
/// </remarks>
public sealed class TaskStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private List<TaskItem> _tasks;

    /// <summary>Raised after any change, from whichever thread made it.</summary>
    public event Action? Changed;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Teezy", "tasks.json");

    public TaskStore(string? path = null, Func<DateTimeOffset>? now = null)
    {
        _path = path ?? DefaultPath;
        _now = now ?? (() => DateTimeOffset.Now);
        _tasks = Load(_path);
    }

    /// <summary>Everything, including closed and deleted, for sync.</summary>
    public IReadOnlyList<TaskItem> All
    {
        get { lock (_gate) return [.. _tasks]; }
    }

    /// <summary>Everything not deleted.</summary>
    public IReadOnlyList<TaskItem> Visible
    {
        get { lock (_gate) return [.. _tasks.Where(t => !t.Deleted)]; }
    }

    public TaskItem? Find(string id)
    {
        lock (_gate) return _tasks.FirstOrDefault(t => t.Id == id && !t.Deleted);
    }

    public TaskItem Add(string title, string? category = null, DateOnly? start = null,
        DateOnly? due = null, TimeOnly? dueTime = null, string? followUpOf = null)
    {
        var task = TaskItem.New(title, _now(), category, start, due, dueTime, followUpOf);
        Change(list => list.Add(task));
        return task;
    }

    /// <summary>Replaces a task with an edited copy of itself.</summary>
    public TaskItem Update(TaskItem edited)
    {
        var stamped = edited with { Category = TaskItem.Clean(edited.Category), Modified = _now() };
        Change(list =>
        {
            var at = list.FindIndex(t => t.Id == edited.Id);
            if (at >= 0) list[at] = stamped;
        });
        return stamped;
    }

    public void AddNote(string id, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Find(id) is not { } task) return;
        Update(task with { Notes = [.. task.Notes, new TaskNote(_now(), text.Trim())] });
    }

    public void Close(string id)
    {
        if (Find(id) is { IsOpen: true } task) Update(task with { Closed = _now() });
    }

    public void Reopen(string id)
    {
        if (Find(id) is { } task) Update(task with { Closed = null, Reminded = null });
    }

    /// <summary>
    /// Closes a task and books the next one in its chain.
    /// </summary>
    /// <remarks>
    /// The follow-up keeps the category and points back at the task it follows, which is what
    /// lets a quote be read as one story — sent, chased, chased again — rather than three
    /// unrelated lines. A note on the closed task records that it was followed up, and when for.
    /// </remarks>
    public TaskItem CloseAndFollowUp(string id, DateOnly due, string? title = null, TimeOnly? dueTime = null)
    {
        var task = Find(id) ?? throw new InvalidOperationException("That task no longer exists.");
        var followUp = TaskItem.New(
            string.IsNullOrWhiteSpace(title) ? FollowUpTitle(task.Title) : title,
            _now(), task.Category, due: due, dueTime: dueTime, followUpOf: task.Id);

        var closed = task with
        {
            Closed = _now(),
            Modified = _now(),
            Notes = [.. task.Notes, new TaskNote(_now(), $"Closed, and followed up for {due:ddd d MMM}.")],
        };

        Change(list =>
        {
            var at = list.FindIndex(t => t.Id == id);
            if (at >= 0) list[at] = closed;
            list.Add(followUp);
        });

        return followUp;
    }

    /// <summary>Takes back a close-and-follow-up: the follow-up goes, and the task reopens as it was.</summary>
    public void UndoFollowUp(string closedId, string followUpId)
    {
        Delete(followUpId);
        if (Find(closedId) is not { } task) return;

        var notes = task.Notes.Count > 0 && task.Notes[^1].Text.StartsWith("Closed, and followed up", StringComparison.Ordinal)
            ? task.Notes.Take(task.Notes.Count - 1).ToList()
            : task.Notes;
        Update(task with { Closed = null, Reminded = null, Notes = notes });
    }

    /// <summary>"Follow up: …" once, not "Follow up: Follow up: …" down a long chain.</summary>
    public static string FollowUpTitle(string title) =>
        title.StartsWith("Follow up: ", StringComparison.OrdinalIgnoreCase) ? title : $"Follow up: {title}";

    public void Delete(string id)
    {
        if (Find(id) is { } task) Update(task with { Deleted = true });
    }

    /// <summary>Marks a reminder as shown, without it counting as an edit for sync.</summary>
    public void MarkReminded(string id)
    {
        Change(list =>
        {
            var at = list.FindIndex(t => t.Id == id);
            if (at >= 0) list[at] = list[at] with { Reminded = _now() };
        });
    }

    /// <summary>
    /// Folds another computer's list into this one, task by task: the newer copy of each wins.
    /// </summary>
    /// <returns>Whether anything here changed.</returns>
    public bool Merge(IEnumerable<TaskItem> incoming)
    {
        var changed = false;
        Change(list =>
        {
            foreach (var theirs in incoming)
            {
                var at = list.FindIndex(t => t.Id == theirs.Id);
                if (at < 0)
                {
                    list.Add(theirs);
                    changed = true;
                }
                else if (theirs.Modified > list[at].Modified)
                {
                    // A reminder shown here stays shown here.
                    list[at] = theirs with { Reminded = list[at].Reminded ?? theirs.Reminded };
                    changed = true;
                }
            }
        }, raise: false);

        if (changed) Changed?.Invoke();
        return changed;
    }

    /// <summary>The list as JSON, for the sync file.</summary>
    public string ToJson()
    {
        lock (_gate) return JsonSerializer.Serialize(_tasks, Json);
    }

    /// <summary>
    /// The list as the sync file carries it: in a fixed order, and without when each reminder was
    /// shown here — so the same tasks always read the same, and showing a reminder is not a change.
    /// </summary>
    public string ToSyncJson()
    {
        lock (_gate)
        {
            return JsonSerializer.Serialize(
                _tasks.OrderBy(t => t.Id, StringComparer.Ordinal).Select(t => t with { Reminded = null }).ToList(), Json);
        }
    }

    public static IReadOnlyList<TaskItem> FromJson(string json) =>
        JsonSerializer.Deserialize<List<TaskItem>>(json, Json) ?? [];

    private void Change(Action<List<TaskItem>> edit, bool raise = true)
    {
        lock (_gate)
        {
            edit(_tasks);
            Save();
        }

        if (raise) Changed?.Invoke();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".writing";
        File.WriteAllText(temp, JsonSerializer.Serialize(_tasks, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private static List<TaskItem> Load(string path)
    {
        try
        {
            return File.Exists(path) ? [.. FromJson(File.ReadAllText(path))] : [];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // Keep the unreadable file rather than overwrite it on the next save.
            try { File.Copy(path, path + ".unreadable", overwrite: true); } catch (IOException) { }
            return [];
        }
    }
}
