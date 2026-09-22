using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using Teezy.Core.Tasks;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Teezy.App;

/// <summary>Reads emails dragged onto the task list — from classic Outlook, New Outlook, or a file.</summary>
/// <remarks>
/// <para>
/// What a drag carries depends on where it came from, so every shape is tried, richest first:
/// </para>
/// <list type="bullet">
/// <item>Classic Outlook: a <b>virtual file</b> — a <c>.msg</c> that exists only inside the drag,
/// named in <c>FileGroupDescriptorW</c> and read from <c>FileContents</c> — beside a text table of
/// the list's columns. The .msg has the body; the table is the fallback.</item>
/// <item>New Outlook and mail on the web: a virtual <c>.eml</c>.</item>
/// <item>Explorer: real <c>.msg</c> or <c>.eml</c> files, saved earlier.</item>
/// <item>Anything else: plain text, which becomes a task of its own.</item>
/// </list>
/// <para>
/// Read-only, and only what was dropped: nothing here talks to Outlook, so nothing here can be
/// blocked by it.
/// </para>
/// </remarks>
internal static class EmailDrop
{
    private const string Descriptor = "FileGroupDescriptorW";
    private const string Contents = "FileContents";

    /// <summary>Whether a drag looks like something the task list can take.</summary>
    public static bool CanTake(System.Windows.IDataObject data) =>
        data.GetDataPresent(Descriptor) || data.GetDataPresent(DataFormats.FileDrop)
        || data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text);

    /// <summary>Every email in the drop; plain text as one "email" with no sender.</summary>
    public static IReadOnlyList<DroppedEmail> Read(System.Windows.IDataObject data)
    {
        var found = new List<DroppedEmail>();

        try { found.AddRange(VirtualFiles(data)); }
        catch (Exception e) when (e is COMException or IOException or InvalidOperationException or ExternalException) { }

        if (found.Count == 0 && data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var path in paths)
            {
                try
                {
                    using var file = File.OpenRead(path);
                    if (Parse(Path.GetFileName(path), file) is { } email) found.Add(email);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        var text = (data.GetData(DataFormats.UnicodeText) ?? data.GetData(DataFormats.Text)) as string;
        if (found.Count == 0 && text is { Length: > 0 })
        {
            // Outlook's own table when the message itself could not be read; otherwise text.
            if (DroppedEmail.FromOutlookRow(text) is { } row) found.Add(row);
            else found.Add(new DroppedEmail(FirstLine(text), null, null, text));
        }

        return found;
    }

    private static string FirstLine(string text)
    {
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    // ---- a message file ----

    private static DroppedEmail? Parse(string name, Stream stream)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension switch
        {
            ".msg" => FromMsg(stream, name),
            ".eml" => FromEml(stream),
            ".txt" => Text(stream, name),
            _ => null,
        };
    }

    private static DroppedEmail FromMsg(Stream stream, string name)
    {
        using var message = new MsgReader.Outlook.Storage.Message(stream);
        var body = message.BodyText is { Length: > 0 } text ? text
            : message.BodyHtml is { Length: > 0 } html ? DroppedEmail.TextFromHtml(html)
            : string.Empty;

        var sender = message.Sender is { } s
            ? (s.DisplayName is { Length: > 0 } d ? (s.Email is { Length: > 0 } e && e != d ? $"{d} <{e}>" : d) : s.Email)
            : null;

        DateTimeOffset? when = message.SentOn;
        var subject = message.Subject is { Length: > 0 } subj ? subj : Path.GetFileNameWithoutExtension(name);
        return new DroppedEmail(subject, sender, when, body.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static DroppedEmail FromEml(Stream stream)
    {
        var message = MimeKit.MimeMessage.Load(stream);
        var body = message.TextBody is { Length: > 0 } text ? text
            : message.HtmlBody is { Length: > 0 } html ? DroppedEmail.TextFromHtml(html)
            : string.Empty;

        DateTimeOffset? when = message.Date == DateTimeOffset.MinValue ? null : message.Date;
        return new DroppedEmail(message.Subject ?? "Email", message.From.ToString(), when,
            body.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static DroppedEmail Text(Stream stream, string name)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        return new DroppedEmail(Path.GetFileNameWithoutExtension(name), null, null, text);
    }

    // ---- virtual files ----

    /// <summary>The files that exist only inside the drag, as Outlook offers a dragged message.</summary>
    private static IEnumerable<DroppedEmail> VirtualFiles(System.Windows.IDataObject data)
    {
        if (!data.GetDataPresent(Descriptor) || data is not ComDataObject com) yield break;
        if (data.GetData(Descriptor) is not MemoryStream descriptor) yield break;

        var names = FileNames(descriptor.ToArray());
        for (var i = 0; i < names.Count; i++)
        {
            if (ReadContents(com, i) is not { } bytes) continue;
            using var stream = new MemoryStream(bytes);
            DroppedEmail? email = null;
            try { email = Parse(names[i], stream); }
            catch (Exception e) when (e is IOException or InvalidDataException or FormatException
                                          or MimeKit.ParseException or ArgumentException) { }
            if (email is not null) yield return email;
        }
    }

    /// <summary>The names in a FILEGROUPDESCRIPTORW: a count, then 592-byte records.</summary>
    private static List<string> FileNames(byte[] block)
    {
        const int record = 592, nameAt = 72, nameBytes = 520;
        var names = new List<string>();
        if (block.Length < 4) return names;

        var count = BitConverter.ToInt32(block, 0);
        for (var i = 0; i < count && 4 + (i + 1) * record <= block.Length; i++)
        {
            var name = Encoding.Unicode.GetString(block, 4 + i * record + nameAt, nameBytes);
            var end = name.IndexOf('\0', StringComparison.Ordinal);
            names.Add(end >= 0 ? name[..end] : name);
        }

        return names;
    }

    /// <summary>One virtual file's bytes. WPF's own GetData cannot ask for an index, so this goes to COM.</summary>
    private static byte[]? ReadContents(ComDataObject com, int index)
    {
        var format = new FORMATETC
        {
            cfFormat = unchecked((short)DataFormats.GetDataFormat(Contents).Id),
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            ptd = IntPtr.Zero,
            tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL,
        };

        com.GetData(ref format, out var medium);
        try
        {
            if (medium.tymed == TYMED.TYMED_ISTREAM)
            {
                var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                try { return ReadAll(stream); }
                finally { Marshal.ReleaseComObject(stream); }
            }

            if (medium.tymed == TYMED.TYMED_HGLOBAL)
            {
                var size = (int)GlobalSize(medium.unionmember);
                var pointer = GlobalLock(medium.unionmember);
                try
                {
                    var bytes = new byte[size];
                    Marshal.Copy(pointer, bytes, 0, size);
                    return bytes;
                }
                finally { GlobalUnlock(medium.unionmember); }
            }

            return null;
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static byte[] ReadAll(IStream stream)
    {
        stream.Stat(out var stat, 1); // STATFLAG_NONAME
        var output = new MemoryStream((int)Math.Min(stat.cbSize, 64L * 1024 * 1024));
        var buffer = new byte[64 * 1024];
        var read = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            while (true)
            {
                stream.Read(buffer, buffer.Length, read);
                var count = Marshal.ReadInt32(read);
                if (count <= 0) break;
                output.Write(buffer, 0, count);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(read);
        }

        return output.ToArray();
    }

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr handle);
}
