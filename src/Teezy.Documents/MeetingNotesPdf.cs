using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using Teezy.Core.Meetings;

namespace Teezy.Documents;

/// <summary>A meeting's notes as a PDF: summary and follow-ups first, the transcript behind them.</summary>
/// <remarks>
/// <para>
/// Laid out for a printed page, not the app window, so it carries its own print palette — dark
/// ink on white, with the app's accent kept only for the label and "Me". The app's dark theme
/// on paper would be a toner bill.
/// </para>
/// <para>
/// <b>Follow-ups come before decisions</b> because they are the part someone acts on, and they
/// are a table with a tick box because the likeliest thing to happen to this PDF is that it gets
/// printed and worked through with a pen.
/// </para>
/// </remarks>
public static class MeetingNotesPdf
{
    private const string Font = "Segoe UI";
    private const string SymbolFont = "Segoe UI Symbol";
    private const string SectionHeading = "SectionHeading";

    private static readonly Color Ink = new(0x1B, 0x1A, 0x18);
    private static readonly Color Body = new(0x3A, 0x38, 0x34);
    private static readonly Color Muted = new(0x8A, 0x84, 0x7B);
    private static readonly Color Accent = new(0x2E, 0x7B, 0xFF);
    private static readonly Color Rule = new(0xE4, 0xE1, 0xDC);
    private static readonly Color HeaderFill = new(0xF3, 0xF6, 0xFD);

    private static readonly object FontGate = new();

    /// <summary>Writes the PDF, replacing any earlier one whole. Returns the page count.</summary>
    /// <exception cref="InvalidOperationException">No usable font was found on this machine.</exception>
    public static int Write(string path, MeetingInfo meeting, SavedNotes notes, IReadOnlyList<MeetingLine> transcript)
    {
        UseWindowsFonts();

        var renderer = new PdfDocumentRenderer { Document = Build(meeting, notes, transcript) };
        renderer.RenderDocument();

        // Read before saving: a saved PdfDocument is sealed, and asking it anything throws.
        var pages = renderer.PdfDocument.PageCount;

        var temporary = path + ".tmp";
        renderer.PdfDocument.Save(temporary);
        File.Move(temporary, path, overwrite: true);

        return pages;
    }

    /// <summary>Points PDFsharp at the fonts Windows already has.</summary>
    /// <remarks>
    /// PDFsharp's platform-neutral build ships no fonts and finds none on its own — it fails with
    /// "No appropriate font found" rather than falling back. Segoe UI is on every Windows 10 and
    /// 11 machine; Arial is the fallback for anything older or stripped down.
    /// </remarks>
    private static void UseWindowsFonts()
    {
        lock (FontGate)
        {
            GlobalFontSettings.FontResolver ??= new WindowsFontResolver(
                Environment.GetFolderPath(Environment.SpecialFolder.Fonts));
        }
    }

    internal static Document Build(MeetingInfo meeting, SavedNotes saved, IReadOnlyList<MeetingLine> transcript)
    {
        var notes = saved.Notes;
        var document = new Document();
        document.Info.Title = notes.Title;
        document.Info.Author = "Teezy";
        Styles(document);

        var section = document.AddSection();
        var setup = document.DefaultPageSetup.Clone();
        PageSetup.GetPageSize(PageFormat.A4, out var width, out var height);
        setup.PageWidth = width;
        setup.PageHeight = height;
        setup.TopMargin = Unit.FromCentimeter(2);
        setup.BottomMargin = Unit.FromCentimeter(2.2);
        setup.LeftMargin = Unit.FromCentimeter(2);
        setup.RightMargin = Unit.FromCentimeter(2);
        section.PageSetup = setup;

        Header(section, meeting, notes);

        Heading(section, "Summary");
        if (notes.Summary.Count == 0) Quiet(section, "The transcript did not hold enough to summarise.");
        foreach (var paragraph in notes.Summary) section.AddParagraph(paragraph);

        Heading(section, "Follow-up tasks");
        if (notes.FollowUps.Count == 0) Quiet(section, "No follow-up tasks came out of this meeting.");
        else Tasks(section, notes.FollowUps);

        if (notes.Decisions.Count > 0)
        {
            Heading(section, "Decisions");
            Bullets(section, notes.Decisions);
        }

        if (notes.OpenQuestions.Count > 0)
        {
            Heading(section, "Open questions");
            Bullets(section, notes.OpenQuestions);
        }

        if (transcript.Count > 0) Transcript(section, transcript);

        Footer(section, saved);
        return document;
    }

    private static void Styles(Document document)
    {
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = Font;
        normal.Font.Size = 10;
        normal.Font.Color = Body;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
        normal.ParagraphFormat.LineSpacingRule = LineSpacingRule.Multiple;
        normal.ParagraphFormat.LineSpacing = 1.15;

        var heading = document.Styles.AddStyle(SectionHeading, StyleNames.Normal);
        heading.Font.Size = 12.5;
        heading.Font.Bold = true;
        heading.Font.Color = Ink;
        heading.ParagraphFormat.SpaceBefore = Unit.FromPoint(18);
        heading.ParagraphFormat.SpaceAfter = Unit.FromPoint(7);
        heading.ParagraphFormat.KeepWithNext = true;
    }

    private static void Header(Section section, MeetingInfo meeting, MeetingNotes notes)
    {
        var label = section.AddParagraph("MEETING NOTES");
        label.Format.Font.Size = 8;
        label.Format.Font.Bold = true;
        label.Format.Font.Color = Accent;
        label.Format.SpaceAfter = Unit.FromPoint(3);

        var title = section.AddParagraph(notes.Title);
        title.Format.Font.Size = 22;
        title.Format.Font.Bold = true;
        title.Format.Font.Color = Ink;
        title.Format.LineSpacingRule = LineSpacingRule.Single;
        title.Format.SpaceAfter = Unit.FromPoint(5);

        var started = meeting.Started.ToLocalTime();
        var length = meeting.Recorded > TimeSpan.Zero ? $" · {MeetingTranscript.Clock(meeting.Recorded)}" : "";
        var meta = section.AddParagraph(
            started.ToString("dddd d MMMM yyyy · h:mm tt", CultureInfo.CurrentCulture) + length);
        meta.Format.Font.Size = 9;
        meta.Format.Font.Color = Muted;
        meta.Format.SpaceAfter = Unit.FromPoint(4);
        meta.Format.Borders.Bottom.Width = 0.75;
        meta.Format.Borders.Bottom.Color = Rule;
        meta.Format.Borders.DistanceFromBottom = Unit.FromPoint(12);
    }

    private static Paragraph Heading(Section section, string text) => section.AddParagraph(text, SectionHeading);

    private static Paragraph Quiet(Section section, string text)
    {
        var paragraph = section.AddParagraph(text);
        paragraph.Format.Font.Color = Muted;
        paragraph.Format.Font.Italic = true;
        return paragraph;
    }

    private static void Tasks(Section section, IReadOnlyList<FollowUp> followUps)
    {
        var table = section.AddTable();
        table.Borders.Width = 0;
        table.LeftPadding = Unit.FromPoint(5);
        table.RightPadding = Unit.FromPoint(6);
        table.TopPadding = Unit.FromPoint(5);
        table.BottomPadding = Unit.FromPoint(5);
        table.Format.SpaceAfter = Unit.Zero;

        // 17 cm: the A4 width less both margins.
        table.AddColumn(Unit.FromCentimeter(0.8));
        table.AddColumn(Unit.FromCentimeter(9.6));
        table.AddColumn(Unit.FromCentimeter(3.4));
        table.AddColumn(Unit.FromCentimeter(3.2));

        var header = table.AddRow();
        header.HeadingFormat = true;
        header.Shading.Color = HeaderFill;
        header.Format.Font.Size = 7.5;
        header.Format.Font.Bold = true;
        header.Format.Font.Color = Muted;
        header.Cells[1].AddParagraph("TASK");
        header.Cells[2].AddParagraph("OWNER");
        header.Cells[3].AddParagraph("DUE");

        foreach (var task in followUps)
        {
            var row = table.AddRow();
            row.VerticalAlignment = VerticalAlignment.Center;
            row.Borders.Bottom.Width = 0.5;
            row.Borders.Bottom.Color = Rule;

            var box = row.Cells[0].AddParagraph("☐");
            box.Format.Font.Name = SymbolFont;
            box.Format.Font.Size = 12;
            box.Format.Font.Color = Muted;

            row.Cells[1].AddParagraph(task.Task).Format.Font.Color = Ink;
            row.Cells[2].AddParagraph(task.Owner);
            row.Cells[3].AddParagraph(task.Due.Length > 0 ? task.Due : "—");
        }
    }

    private static void Bullets(Section section, IEnumerable<string> items)
    {
        foreach (var item in items)
        {
            var paragraph = section.AddParagraph();
            paragraph.Format.LeftIndent = Unit.FromCentimeter(0.55);
            paragraph.Format.FirstLineIndent = Unit.FromCentimeter(-0.55);
            paragraph.Format.TabStops.AddTabStop(Unit.FromCentimeter(0.55));
            paragraph.Format.SpaceAfter = Unit.FromPoint(4);
            paragraph.AddFormattedText("•").Color = Accent;
            paragraph.AddTab();
            paragraph.AddText(item);
        }
    }

    private static void Transcript(Section section, IReadOnlyList<MeetingLine> transcript)
    {
        var heading = Heading(section, "Transcript");
        heading.Format.PageBreakBefore = true;
        heading.Format.SpaceBefore = Unit.Zero;

        Quiet(section, "Made automatically on this computer. \"Me\" is the microphone; \"Them\" is everyone else on the call.");

        foreach (var line in transcript)
        {
            var paragraph = section.AddParagraph();
            paragraph.Format.Font.Size = 9;
            paragraph.Format.SpaceAfter = Unit.FromPoint(4);
            paragraph.Format.LeftIndent = Unit.FromCentimeter(2.7);
            paragraph.Format.FirstLineIndent = Unit.FromCentimeter(-2.7);
            paragraph.Format.TabStops.AddTabStop(Unit.FromCentimeter(1.55));
            paragraph.Format.TabStops.AddTabStop(Unit.FromCentimeter(2.7));

            paragraph.AddFormattedText(MeetingTranscript.Stamp(line.At)).Color = Muted;
            paragraph.AddTab();

            var who = paragraph.AddFormattedText(line.Side == Side.Me ? "Me" : "Them");
            who.Bold = true;
            who.Color = line.Side == Side.Me ? Accent : Ink;

            paragraph.AddTab();
            paragraph.AddText(line.Text);
        }
    }

    private static void Footer(Section section, SavedNotes saved)
    {
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 7.5;
        footer.Format.Font.Color = Muted;
        footer.Format.TabStops.AddTabStop(Unit.FromCentimeter(17), TabAlignment.Right);

        footer.AddText($"Summary and tasks written by Claude ({saved.Model}) from an automatic transcript — check before relying on them.");
        footer.AddTab();
        footer.AddText("Page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();
    }
}

/// <summary>Serves PDFsharp the TrueType files in the Windows fonts folder.</summary>
internal sealed class WindowsFontResolver(string directory) : IFontResolver
{
    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        if (familyName.Equals("Segoe UI Symbol", StringComparison.OrdinalIgnoreCase) && Exists("seguisym"))
        {
            return new FontResolverInfo("seguisym");
        }

        var segoe = (bold, italic) switch
        {
            (true, true) => "segoeuiz",
            (true, false) => "segoeuib",
            (false, true) => "segoeuii",
            _ => "segoeui",
        };
        if (Exists(segoe)) return new FontResolverInfo(segoe);

        var arial = (bold, italic) switch
        {
            (true, true) => "arialbi",
            (true, false) => "arialbd",
            (false, true) => "ariali",
            _ => "arial",
        };
        return Exists(arial) ? new FontResolverInfo(arial) : null;
    }

    public byte[]? GetFont(string faceName) =>
        Exists(faceName) ? File.ReadAllBytes(PathOf(faceName)) : null;

    private bool Exists(string face) => File.Exists(PathOf(face));

    private string PathOf(string face) => Path.Combine(directory, face + ".ttf");
}
