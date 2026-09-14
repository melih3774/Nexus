using Xunit;

namespace PcAssistant.Tests;

/// <summary>
/// Everything that touches the one real desktop runs one at a time.
///
/// These tests share a single machine: one foreground window, one volume, one set of
/// processes. Run in parallel they interfere in ways that look like product bugs and are not
/// — the failure that produced this was the Stop test asserting no stray `claude.exe`
/// survived cancellation, while a workflow test in another class had just started one of its
/// own. It reported a real process id and a real leak that had never happened.
///
/// The cost is a few seconds of wall clock. The alternative is a suite that fails at random
/// and teaches you to re-run it instead of reading it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DesktopCollection
{
    public const string Name = "Real desktop";
}
