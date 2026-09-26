using System.Text.Json;
using System.Text.Json.Serialization;
using Teezy.Core.Tasks;

namespace Teezy.Core.Quotes;

/// <summary>The quotes you have out, kept as one JSON file, and the only thing that changes them.</summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="TaskStore"/>, for the same reasons: every change stamps
/// <see cref="Quote.Modified"/> so two computers that both worked while apart merge quote by
/// quote, a deletion is kept as a marker so it reaches the other computers, and the file is
/// written beside itself and swapped in so a crash mid-write costs nothing.
/// </para>
/// <para>
/// It does not do the chasing. A chase is a task, and tasks already have reminders, the focus
/// card, Home and the briefing — so <see cref="Chasing"/> books them in the task list and this
/// store only remembers which one is current.
/// </para>
/// </remarks>
public sealed class QuoteStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private List<Quote> _quotes;

    /// <summary>Raised after any change, from whichever thread made it.</summary>
    public event Action? Changed;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Teezy", "quotes.json");

    public QuoteStore(string? path = null, Func<DateTimeOffset>? now = null)
    {
        _path = path ?? DefaultPath;
        _now = now ?? (() => DateTimeOffset.Now);
        _quotes = Read(_path);
    }

    /// <summary>Everything, including decided and deleted, for sync.</summary>
    public IReadOnlyList<Quote> All
    {
        get { lock (_gate) return [.. _quotes]; }
    }

    /// <summary>Everything not deleted.</summary>
    public IReadOnlyList<Quote> Visible
    {
        get { lock (_gate) return [.. _quotes.Where(q => !q.Deleted)]; }
    }

    public Quote? Find(string id)
    {
        lock (_gate) return _quotes.FirstOrDefault(q => q.Id == id && !q.Deleted);
    }

    /// <summary>The quote a chase task belongs to, if it is one.</summary>
    public Quote? ForTask(string taskId)
    {
        lock (_gate) return _quotes.FirstOrDefault(q => !q.Deleted && q.ChaseTaskId == taskId);
    }

    public Quote Add(
        string customer,
        string what,
        long amountCents,
        DateOnly sent,
        IReadOnlyList<int>? cadence = null,
        string? reference = null,
        string? contact = null)
    {
        var quote = Quote.New(customer, what, amountCents, sent, _now(), cadence, reference, contact);
        Change(list => list.Add(quote));
        return quote;
    }

    /// <summary>Replaces a quote with an edited copy of itself.</summary>
    public Quote Update(Quote edited)
    {
        var stamped = edited with
        {
            Customer = edited.Customer.Trim(),
            What = edited.What.Trim(),
            Reference = Quote.Clean(edited.Reference),
            Contact = Quote.Clean(edited.Contact),
            Modified = _now(),
        };

        Change(list =>
        {
            var at = list.FindIndex(q => q.Id == edited.Id);
            if (at >= 0) list[at] = stamped;
        });

        return stamped;
    }

    /// <param name="by">Who wrote it; see <see cref="TaskNote.By"/>.</param>
    public void AddNote(string id, string text, string? by = null)
    {
        if (string.IsNullOrWhiteSpace(text) || Find(id) is not { } quote) return;
        Update(quote with { Notes = [.. quote.Notes, new TaskNote(_now(), text.Trim(), by)] });
    }

    /// <summary>Attaches an email to a quote, apart from its notes.</summary>
    public void AddEmail(string id, TaskEmail email)
    {
        if (Find(id) is not { } quote) return;
        Update(quote with { Emails = [.. quote.AllEmails, email] });
    }

    /// <summary>
    /// Records that it has been chased. The link to the chase task is left alone: the task is
    /// what said the chase happened, and <see cref="QuoteChasing"/> replaces the link when it
    /// books the next one.
    /// </summary>
    public Quote? Chased(string id, DateOnly on)
    {
        if (Find(id) is not { IsOpen: true } quote) return null;
        return Update(quote with { Chased = quote.Chased + 1, LastChased = on });
    }

    /// <summary>Won or lost, on a day. <see cref="QuoteChasing"/> then closes what was chasing it.</summary>
    public Quote? Decide(string id, QuoteStatus status, DateOnly on)
    {
        if (Find(id) is not { } quote || status == QuoteStatus.Open) return null;
        return Update(quote with { Status = status, Decided = on });
    }

    /// <summary>Back to open, for a decision made too soon.</summary>
    public Quote? Reopen(string id)
    {
        if (Find(id) is not { } quote || quote.IsOpen) return null;
        return Update(quote with { Status = QuoteStatus.Open, Decided = null });
    }

    /// <summary>Points the quote at the task now chasing it.</summary>
    public void Chasing(string id, string? taskId)
    {
        if (Find(id) is not { } quote || quote.ChaseTaskId == taskId) return;
        Update(quote with { ChaseTaskId = taskId });
    }

    public void Delete(string id)
    {
        if (Find(id) is { } quote) Update(quote with { Deleted = true });
    }

    /// <summary>Folds another computer's quotes into this one: the newer copy of each wins.</summary>
    /// <returns>Whether anything here changed.</returns>
    public bool Merge(IEnumerable<Quote> incoming)
    {
        var changed = false;

        Change(list =>
        {
            foreach (var theirs in incoming)
            {
                var at = list.FindIndex(q => q.Id == theirs.Id);
                if (at < 0)
                {
                    list.Add(theirs);
                    changed = true;
                }
                else if (theirs.Modified > list[at].Modified)
                {
                    list[at] = theirs;
                    changed = true;
                }
            }
        }, raise: false);

        if (changed) Changed?.Invoke();
        return changed;
    }

    public string ToJson()
    {
        lock (_gate) return JsonSerializer.Serialize(_quotes, Json);
    }

    /// <summary>The list as it travels: ordered, so two computers that agree produce one file.</summary>
    public string ToSyncJson()
    {
        lock (_gate)
        {
            return JsonSerializer.Serialize(_quotes.OrderBy(q => q.Id, StringComparer.Ordinal), Json);
        }
    }

    public static IReadOnlyList<Quote> FromJson(string json) =>
        JsonSerializer.Deserialize<List<Quote>>(json, Json) ?? [];

    private void Change(Action<List<Quote>> edit, bool raise = true)
    {
        lock (_gate)
        {
            edit(_quotes);
            Save();
        }

        if (raise) Changed?.Invoke();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".writing";
        File.WriteAllText(temp, JsonSerializer.Serialize(_quotes, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private static List<Quote> Read(string path)
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
