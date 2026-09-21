using System;
using Teezy.Core.Abstractions;

namespace Teezy.App;

/// <summary>A secret store that says when something in it changes.</summary>
/// <remarks>
/// Keys are saved from many places — Settings, the account sign-in flow, the calendar link
/// box — and sync needs to hear about all of them without each one remembering to tell it.
/// Wrapping the store is the one place that cannot be forgotten.
/// </remarks>
internal sealed class NotifyingSecretStore(ISecretStore inner) : ISecretStore
{
    /// <summary>Raised after a write or delete, with the secret's name. Never its value.</summary>
    public event Action<string>? Changed;

    public string? Read(string name) => inner.Read(name);

    public void Write(string name, string secret)
    {
        inner.Write(name, secret);
        Changed?.Invoke(name);
    }

    public void Delete(string name)
    {
        inner.Delete(name);
        Changed?.Invoke(name);
    }

    public string? Describe(string name) => inner.Describe(name);
}
