using Shouldly;
using Teezy.Core.Tasks;
using Xunit;

namespace Teezy.Core.Tests;

public class DroppedEmailTests
{
    [Fact]
    public void ReadsOutlooksColumnTable()
    {
        var email = DroppedEmail.FromOutlookRow(
            "From\tSubject\tReceived\tSize\tCategories\t\r\nPriya Nair\tRE: Cessnock quote Q-4411\t22/09/2026 9:14 AM\t45 KB\t\t\r\n");

        email.ShouldNotBeNull();
        email.From.ShouldBe("Priya Nair");
        email.Subject.ShouldBe("RE: Cessnock quote Q-4411");
        email.TaskTitle.ShouldBe("Cessnock quote Q-4411");
    }

    [Fact]
    public void OrdinaryTextIsNotMistakenForOutlooksTable() =>
        DroppedEmail.FromOutlookRow("Hi Zack,\nCan you send the revised quote?").ShouldBeNull();

    [Fact]
    public void EveryReplyPrefixComesOffTheTitle() =>
        new DroppedEmail("RE: FW: Re: Door schedule", null, null, "").TaskTitle.ShouldBe("Door schedule");

    [Fact]
    public void TheNoteSaysWhoAndWhatThenTheText()
    {
        var note = new DroppedEmail("Door schedule", "Sam <sam@example.com>", null, "Can you confirm by Friday?").ToNote();

        note.ShouldStartWith("Email from Sam <sam@example.com>");
        note.ShouldContain("Subject: Door schedule");
        note.ShouldEndWith("Can you confirm by Friday?");
    }

    [Fact]
    public void AVeryLongThreadIsCut() =>
        new DroppedEmail("x", null, null, new string('a', 50_000)).ToNote().Length.ShouldBeLessThan(DroppedEmail.MaxBody + 200);

    [Fact]
    public void HtmlBecomesReadableText() =>
        DroppedEmail.TextFromHtml("<html><style>p{}</style><p>Hi&nbsp;Zack,</p><p>See <b>attached</b>.</p></html>")
            .ShouldBe("Hi Zack,\nSee attached.");
}
