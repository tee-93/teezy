namespace Teezy.Core.Abstractions;

/// <summary>A microphone the user could dictate through.</summary>
/// <param name="Id">
/// Stable identifier, persisted in settings. Survives renames and reboots, unlike the
/// friendly name, and unlike an index — which is the trap here: device order changes the
/// moment a headset is plugged in, so an index saved on Tuesday points at the webcam on
/// Wednesday.
/// </param>
/// <param name="Name">What the user sees. Only ever a label.</param>
/// <param name="IsSystemDefault">True for the device Windows would have chosen itself.</param>
public sealed record AudioDevice(string Id, string Name, bool IsSystemDefault);
