using System;

namespace PcAssistant.Windows;

/// <summary>
/// The owner, not the assistant, supplies secrets. Separate from every other refusal so it is never counted
/// as a foreground failure and never reads as "the tool is broken" — the task pauses and resumes.
/// </summary>
public sealed class CredentialEntryException(string message) : InvalidOperationException(message);
