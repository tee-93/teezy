using Shouldly;
using Teezy.Core.Abstractions;
using Xunit;

namespace Teezy.Core.Tests;

public class SecretStoreTests
{
    // A real Anthropic key's shape: a constant label, then random. 108 characters.
    private const string Key = "sk-ant-api03-" + "aBcDeFgH1234" + "kLmNoPqRsTuV5678" + "wXyZ0123456789" + "AbCdEfGhIjKl9gAA";

    // ---- the mask ----

    [Fact]
    public void MaskKeepsTheLabelAndTheLastFour() =>
        // Both halves earn their place: the label says what kind of key it is, and the last
        // four are what let someone confirm the key they just pasted is the one that landed.
        SecretMask.For(Key).ShouldBe("sk-ant-…9gAA");

    [Fact]
    public void MaskNeverRevealsMostOfTheSecret() =>
        SecretMask.For(Key).ShouldNotContain(Key[10..^10]);

    [Fact]
    public void MaskHidesShortSecretsEntirely() =>
        // "First seven and last four" would be almost all of a short secret, so it gets a
        // fixed row of dots instead - the length is not worth leaking either.
        SecretMask.For("short-secret").ShouldBe("••••••••••");

    [Fact]
    public void MaskOfNothingIsNothing() =>
        SecretMask.For("").ShouldBe("");

    // ---- Describe ----

    [Fact]
    public void DescribeReturnsNullWhenNothingIsStored()
    {
        // Typed as the interface throughout: Describe is a default interface method, so it
        // resolves through ISecretStore and not through the concrete store.
        ISecretStore store = new InMemorySecretStore();

        // The distinction the settings page depends on: nothing stored reads as no key, not
        // as an empty mask that would still count as "a key is saved".
        store.Describe("absent").ShouldBeNull();
    }

    [Fact]
    public void DescribeMasksWhatWasStored()
    {
        ISecretStore store = new InMemorySecretStore();
        store.Write("k", Key);

        store.Describe("k").ShouldBe("sk-ant-…9gAA");
    }

    [Fact]
    public void DescribeGoesBackToNullAfterDelete()
    {
        ISecretStore store = new InMemorySecretStore();
        store.Write("k", Key);
        store.Delete("k");

        store.Describe("k").ShouldBeNull();
    }

    [Fact]
    public void WriteReplacesRatherThanAccumulates()
    {
        ISecretStore store = new InMemorySecretStore();
        store.Write("k", Key);
        store.Write("k", "sk-ant-api03-second-key-that-is-definitely-long-enough-XYZW");

        store.Describe("k").ShouldBe("sk-ant-…XYZW");
    }
}
