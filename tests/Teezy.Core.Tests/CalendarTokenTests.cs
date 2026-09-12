using Shouldly;
using Teezy.Connectors;
using Teezy.Core.Abstractions;
using Teezy.Core.Calendar;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Storing and renewing the credentials that keep an account connected.</summary>
public class CalendarTokenTests
{
    private static OAuthTokens Good(string refresh = "refresh-1") =>
        new("access-1", refresh, DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public void TokensSurviveARoundTrip()
    {
        var store = new TokenStore(new InMemorySecretStore());
        store.Save("abc", Good());

        var read = store.Read("abc");

        read.ShouldNotBeNull();
        read.AccessToken.ShouldBe("access-1");
        read.RefreshToken.ShouldBe("refresh-1");
    }

    [Fact]
    public void AccountsDoNotReadEachOthersTokens()
    {
        var store = new TokenStore(new InMemorySecretStore());
        store.Save("personal", Good("personal-refresh"));
        store.Save("work", Good("work-refresh"));

        store.Read("personal")!.RefreshToken.ShouldBe("personal-refresh");
        store.Read("work")!.RefreshToken.ShouldBe("work-refresh");
    }

    [Fact]
    public void DisconnectingRemovesTheTokens()
    {
        var secrets = new InMemorySecretStore();
        var store = new TokenStore(secrets);
        store.Save("abc", Good());

        new AccountSession(GraphCalendar.Provider("id"), "abc", store).Disconnect();

        store.Read("abc").ShouldBeNull();
    }

    [Fact]
    public void ARecordWithoutARefreshTokenCountsAsNotConnected()
    {
        var secrets = new InMemorySecretStore();
        var store = new TokenStore(secrets);

        // Would otherwise work for an hour and then fail somewhere far from the cause.
        store.Save("abc", new OAuthTokens("access-1", null, DateTimeOffset.UtcNow.AddHours(1)));

        store.Read("abc").ShouldBeNull();
        new AccountSession(GraphCalendar.Provider("id"), "abc", store).IsConnected.ShouldBeFalse();
    }

    [Fact]
    public void ACorruptRecordCountsAsNotConnected()
    {
        var secrets = new InMemorySecretStore();
        secrets.Write("calendar-abc", "{ not json");

        new TokenStore(secrets).Read("abc").ShouldBeNull();
    }

    [Fact]
    public async Task ALiveTokenIsReturnedWithoutAskingTheProvider()
    {
        var store = new TokenStore(new InMemorySecretStore());
        store.Save("abc", Good());

        var session = new AccountSession(GraphCalendar.Provider("id"), "abc", store);

        // The provider is a real URL and this test has no network: reaching it would either
        // hang or throw, so a plain answer proves nothing was asked.
        (await session.AccessTokenAsync()).ShouldBe("access-1");
    }

    [Fact]
    public async Task AnAccountThatWasNeverConnectedSaysSoPlainly()
    {
        var session = new AccountSession(
            GraphCalendar.Provider("id"), "nobody", new TokenStore(new InMemorySecretStore()));

        var failure = await Should.ThrowAsync<CalendarUnavailableException>(
            () => session.AccessTokenAsync());

        // The distinction Settings needs: signing in again fixes this, waiting does not.
        failure.NeedsReconnect.ShouldBeTrue();
    }

    [Fact]
    public void ATokenIsTreatedAsExpiredAMinuteEarly()
    {
        new OAuthTokens("a", "r", DateTimeOffset.UtcNow.AddSeconds(30)).IsExpired.ShouldBeTrue();
        new OAuthTokens("a", "r", DateTimeOffset.UtcNow.AddMinutes(5)).IsExpired.ShouldBeFalse();
    }
}
