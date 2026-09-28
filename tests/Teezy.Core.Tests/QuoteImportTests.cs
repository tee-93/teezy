using Shouldly;
using Teezy.Core.Quotes;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Bringing a CRM export in, and bringing it in again without making a mess.</summary>
public sealed class QuoteImportTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 9, 22);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "teezy-import-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 9, 22, 9, 0, 0, TimeSpan.FromHours(10));

    private QuoteStore Store() => new(Path.Combine(_folder, "quotes.json"), () => _now);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private const string Export = """
        Quote Number,Client,Description,Quote Value,Date Sent,Status
        Q-1042,Hunter Builders,"Door hardware, stage 2","$4,200.00",2026-09-18,Open
        Q-1043,Orikan,Readers,"9,000",2026-09-10,Won
        Q-1044,Cessnock Hospital,Closers,1250.50,2026-08-30,Lost
        """;

    [Fact]
    public void ColumnsAreFoundByWhatTheyMeanAndReported()
    {
        var plan = QuoteImport.Read(Export, Today);

        plan.Quotes.Count.ShouldBe(3);
        plan.Skipped.ShouldBeEmpty();
        plan.Columns["Customer"].ShouldBe("Client");
        plan.Columns["Value"].ShouldBe("Quote Value");
        plan.Columns["Reference"].ShouldBe("Quote Number");
    }

    [Fact]
    public void ValuesCommasQuotesAndOutcomesAreRead()
    {
        var quotes = QuoteImport.Read(Export, Today).Quotes;

        quotes[0].Customer.ShouldBe("Hunter Builders");
        quotes[0].Name.ShouldBe("Door hardware, stage 2");
        quotes[0].AmountCents.ShouldBe(420_000);
        quotes[0].Sent.ShouldBe(new DateOnly(2026, 9, 18));
        quotes[0].Status.ShouldBe(QuoteStatus.Quoted);

        quotes[1].AmountCents.ShouldBe(900_000);
        quotes[1].Status.ShouldBe(QuoteStatus.Won);

        quotes[2].AmountCents.ShouldBe(125_050);
        quotes[2].Status.ShouldBe(QuoteStatus.Lost);
    }

    [Fact]
    public void ImportingPutsThemInWithTheirOutcomes()
    {
        var store = Store();

        var (added, updated) = QuoteImport.Apply(store, QuoteImport.Read(Export, Today), Today);

        added.ShouldBe(3);
        updated.ShouldBe(0);
        store.Visible.Count(q => q.IsOpen).ShouldBe(1);
        store.Visible.Single(q => q.Reference == "Q-1043").Status.ShouldBe(QuoteStatus.Won);
    }

    [Fact]
    public void ImportingTheSameFileAgainChangesNothing()
    {
        var store = Store();
        QuoteImport.Apply(store, QuoteImport.Read(Export, Today), Today);
        var before = store.ToSyncJson();

        _now = _now.AddHours(1);
        var (added, updated) = QuoteImport.Apply(store, QuoteImport.Read(Export, Today), Today);

        added.ShouldBe(0);
        updated.ShouldBe(0);
        store.ToSyncJson().ShouldBe(before);
    }

    [Fact]
    public void AFreshExportBringsTheOutcomesWithIt()
    {
        var store = Store();
        QuoteImport.Apply(store, QuoteImport.Read(Export, Today), Today);

        const string later = """
            Quote Number,Client,Description,Quote Value,Date Sent,Status
            Q-1042,Hunter Builders,"Door hardware, stage 2","$4,400.00",2026-09-18,Won
            """;

        _now = _now.AddHours(1);
        var (added, updated) = QuoteImport.Apply(store, QuoteImport.Read(later, Today), Today);

        added.ShouldBe(0);
        updated.ShouldBe(1);

        var quote = store.Visible.Single(q => q.Reference == "Q-1042");
        quote.Status.ShouldBe(QuoteStatus.Won);
        quote.AmountCents.ShouldBe(440_000);
    }

    [Fact]
    public void ALineWithoutAValueIsSkippedByNumberRatherThanGuessedAt()
    {
        const string messy = """
            Client,Description,Value
            Hunter Builders,Door hardware,
            Orikan,Readers,900
            """;

        var plan = QuoteImport.Read(messy, Today);

        plan.Quotes.ShouldHaveSingleItem().Customer.ShouldBe("Orikan");
        plan.Skipped.ShouldHaveSingleItem().ShouldContain("Line 2");
    }

    [Fact]
    public void AFileThatIsNotAQuoteListSaysSoRatherThanImportingRubbish()
    {
        var plan = QuoteImport.Read("Name,Phone\nPriya,0400 000 000", Today);

        plan.IsEmpty.ShouldBeTrue();
        plan.Skipped.ShouldHaveSingleItem().ShouldContain("a value");
    }

    [Fact]
    public void WithoutAReferenceAQuoteIsMatchedOnCustomerValueAndDay()
    {
        var store = Store();
        const string file = """
            Client,Description,Value,Date
            Hunter Builders,Door hardware,4200,2026-09-18
            """;

        QuoteImport.Apply(store, QuoteImport.Read(file, Today), Today).Added.ShouldBe(1);

        _now = _now.AddHours(1);
        QuoteImport.Apply(store, QuoteImport.Read(file, Today), Today).Added.ShouldBe(0);
        store.Visible.Count.ShouldBe(1);
    }

    [Fact]
    public void AColumnLiterallyCalledNameIsReadAsTheQuotesNameNotSwallowedByCustomer()
    {
        // The exact shape the CSV template downloads: Customer and Name are two different
        // columns, and "name" being one of the words a customer column can be called too must
        // not stop it also being found for the quote's own name here.
        const string file = """
            Customer,Name,Type,Value,Sent,Reference,Contact,Status
            Hunter Builders,Door hardware,Supply & Install,4200,15/08/2026,Q-1042,Priya Nair,Open
            """;

        var quote = QuoteImport.Read(file, Today).Quotes.ShouldHaveSingleItem();

        quote.Customer.ShouldBe("Hunter Builders");
        quote.Name.ShouldBe("Door hardware");
    }

    [Fact]
    public void AFileWithOnlyOneNameColumnReadsItAsTheCustomer()
    {
        // No separate customer column: "Name" must still mean the customer, as it always has,
        // rather than the quote ending up with an empty customer.
        var quote = QuoteImport.Read("Name,Value\nHunter Builders,4200", Today).Quotes.ShouldHaveSingleItem();

        quote.Customer.ShouldBe("Hunter Builders");
        quote.Name.ShouldBe(string.Empty);
    }

    [Fact]
    public void AQuoteTypeColumnIsReadAndCarriedIn()
    {
        var store = Store();
        const string file = """
            Client,Description,Value,Type
            Hunter Builders,Door hardware,4200,Supply & Install
            """;

        var plan = QuoteImport.Read(file, Today);
        plan.Columns["Type"].ShouldBe("Type");

        QuoteImport.Apply(store, plan, Today);
        store.Visible.ShouldHaveSingleItem().Type.ShouldBe("Supply & Install");
    }

    [Fact]
    public void TheTemplateReadsBackAsOneQuote()
    {
        var plan = QuoteImport.Read(QuoteImport.Template(), Today);

        plan.Quotes.ShouldHaveSingleItem();
        plan.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public void ACurrencyColumnIsReadAndAppliedWithTheStatedDefaultOtherwise()
    {
        var store = Store();
        const string file = """
            Client,Description,Value,Currency
            Hunter Builders,Door hardware,4200,USD
            Orikan,Readers,900,
            """;

        var plan = QuoteImport.Read(file, Today);
        plan.Columns["Currency"].ShouldBe("Currency");

        QuoteImport.Apply(store, plan, Today, defaultCurrency: "NZD");

        store.Visible.Single(q => q.Customer == "Hunter Builders").Currency.ShouldBe("USD");
        store.Visible.Single(q => q.Customer == "Orikan").Currency.ShouldBe("NZD");
    }
}
