using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Teezy.Core.Tasks;

namespace Teezy.App;

/// <summary>The task list over classic Outlook: flagged emails, and the flag as the tick box.</summary>
/// <remarks>
/// <para>
/// <b>It reads Outlook's own To-Do List folder</b>, which Outlook keeps as a live search of every
/// flagged item in the mailbox — so the list here is the same list as Outlook's, from every
/// folder, and a flag set on a phone or in New Outlook shows up too. Flags and categories live
/// in the mailbox itself, which is why classic and New Outlook agree on them. Pins do not: they
/// are New Outlook's alone, and classic Outlook cannot see them.
/// </para>
/// <para>
/// <b>Only the list fields are read in bulk</b> — sender name, subject, dates, categories. The
/// body and the sender's address are what Outlook's security guard watches, so the body is read
/// only for one email, when the user asks the AI about it.
/// </para>
/// <para>
/// Changes are the ones Outlook itself makes: a flag marked complete, or marked again for an
/// undo. Nothing is moved, deleted, sent or replied to.
/// </para>
/// </remarks>
internal sealed class ClassicOutlookMail : IMailTasks
{
    private const int ToDoFolder = 28;       // olFolderToDo
    private const int MailClass = 43;        // olMail
    private const int TaskClass = 48;        // olTask
    private const int FlagMarked = 2;        // olFlagMarked
    private const int FlagComplete = 1;      // olFlagComplete
    private const int MarkNoDate = 4;        // olMarkNoDate

    public bool IsAvailable => ClassicOutlook.IsRunning;

    public Task<IReadOnlyList<MailTask>> OpenAsync(CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<MailTask>>(() =>
        {
            var outlook = ClassicOutlook.Running() ?? throw new InvalidOperationException(NotRunning);
            dynamic? ns = null, folder = null, items = null;
            try
            {
                ns = outlook.GetNamespace("MAPI");
                folder = ns.GetDefaultFolder(ToDoFolder);
                items = folder.Items;

                var tasks = new List<MailTask>();
                var walked = 0;
                for (dynamic? item = items.GetFirst(); item is not null && walked < 1000; item = items.GetNext(), walked++)
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        if (Read(item) is { } task) tasks.Add(task);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }

                return tasks;
            }
            finally
            {
                ClassicOutlook.Release(items);
                ClassicOutlook.Release(folder);
                ClassicOutlook.Release(ns);
                Marshal.ReleaseComObject(outlook);
            }
        }, ct);

    public Task CompleteAsync(string id, CancellationToken ct = default) =>
        WithItem(id, item =>
        {
            if ((int)item.Class == TaskClass) item.MarkComplete();
            else item.FlagStatus = FlagComplete;
            item.Save();
            return 0;
        }, ct);

    public Task ReopenAsync(string id, CancellationToken ct = default) =>
        WithItem(id, item =>
        {
            if ((int)item.Class == TaskClass) item.Status = 0; // olTaskNotStarted
            else item.MarkAsTask(MarkNoDate);
            item.Save();
            return 0;
        }, ct);

    public Task<string?> BodyAsync(string id, CancellationToken ct = default) =>
        WithItem<string?>(id, item => (string?)item.Body, ct);

    public Task OpenInOutlookAsync(string id, CancellationToken ct = default) =>
        WithItem(id, item => { item.Display(); return 0; }, ct);

    private const string NotRunning = "Classic Outlook isn’t running. Open it — minimised is fine.";

    /// <summary>One item's fields, or null if it is not an open task.</summary>
    private static MailTask? Read(dynamic item)
    {
        int kind = item.Class;
        if (kind == MailClass)
        {
            if ((int)item.FlagStatus != FlagMarked) return null;

            DateTime due = item.TaskDueDate;
            return new MailTask(
                (string)item.EntryID,
                (string?)item.SenderName ?? string.Empty,
                (string?)item.Subject ?? "(no subject)",
                ClassicOutlook.Local((DateTime)item.ReceivedTime),
                Dated(due),
                Categories((string?)item.Categories),
                IsEmail: true);
        }

        if (kind == TaskClass)
        {
            if ((bool)item.Complete) return null;

            DateTime due = item.DueDate;
            return new MailTask(
                (string)item.EntryID,
                string.Empty,
                (string?)item.Subject ?? "(no subject)",
                ClassicOutlook.Local((DateTime)item.CreationTime),
                Dated(due),
                Categories((string?)item.Categories),
                IsEmail: false);
        }

        return null;
    }

    /// <summary>Outlook's "no date" is 1 January 4501.</summary>
    private static DateOnly? Dated(DateTime value) =>
        value.Year >= 4000 ? null : DateOnly.FromDateTime(value);

    /// <summary>Outlook stores categories as one string, separated by the list separator.</summary>
    private static IReadOnlyList<string> Categories(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Task<T> WithItem<T>(string id, Func<dynamic, T> action, CancellationToken ct) =>
        Task.Run(() =>
        {
            var outlook = ClassicOutlook.Running() ?? throw new InvalidOperationException(NotRunning);
            dynamic? ns = null, item = null;
            try
            {
                ns = outlook.GetNamespace("MAPI");
                item = ns.GetItemFromID(id);
                T result = action((object)item!);
                return result;
            }
            finally
            {
                ClassicOutlook.Release(item);
                ClassicOutlook.Release(ns);
                Marshal.ReleaseComObject(outlook);
            }
        }, ct);
}
