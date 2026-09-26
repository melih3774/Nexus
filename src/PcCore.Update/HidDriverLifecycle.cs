namespace PcCore.Update;

/// <summary>What Windows reports about the Nexus devnode right now.</summary>
/// <param name="Stack">Driver services from the top of the stack down, without the <c>\Driver\</c> prefix.</param>
public sealed record HidDeviceState(
    bool Present,
    uint ProblemCode,
    string? DriverInf,
    string? DriverVersion,
    IReadOnlyList<string> Stack)
{
    public static HidDeviceState Absent { get; } = new(false, 0, null, null, []);
}

/// <summary><c>WUDF\Services\NexusHidDriver</c> as the UMDF reflector will read it on the next start.</summary>
/// <param name="ImagePath">Expanded; null when the value is missing.</param>
public sealed record UmdfRegistration(
    string? ImagePath,
    int? WdfMajorVersion,
    int? WdfMinorVersion,
    IReadOnlyList<string> Extensions);

/// <summary>One package in the Driver Store that matches the Nexus hardware id.</summary>
public sealed record DriverPackage(
    string PublishedName,
    string OriginalName,
    string Provider,
    string? Version,
    bool Installed);

/// <summary>A hash-verified driver package on disk: the INF to hand Windows, and what its DLL must hash to.</summary>
public sealed record DriverPayload(string Version, string InfPath, string DriverDllSha256);

/// <summary>
/// The handful of things the lifecycle asks Windows. Each is a supported, documented path; none of them
/// writes the UMDF registration itself.
/// </summary>
public interface IHidDriverHost
{
    /// <summary><c>pnputil /add-driver &lt;inf&gt; /install</c>.</summary>
    (int Code, string Output) AddAndInstall(string infPath);

    /// <summary>
    /// <c>UpdateDriverForPlugAndPlayDevices(INSTALLFLAG_FORCE)</c> for the Nexus hardware id: binds exactly
    /// this package, even when it is the one already bound or an older one, and makes Windows run its
    /// install again - which is what writes the UMDF registration.
    /// </summary>
    (bool Succeeded, string Detail) Reapply(string infPath);

    HidDeviceState ReadDevice();

    /// <summary>Null when the service key does not exist at all.</summary>
    UmdfRegistration? ReadUmdfRegistration();

    /// <summary>Every Driver Store package matching the Nexus hardware id; null when Windows could not be asked.</summary>
    IReadOnlyList<DriverPackage>? MatchingPackages();

    /// <summary><c>pnputil /delete-driver &lt;oem#.inf&gt;</c>, without <c>/uninstall</c> or <c>/force</c>.</summary>
    (bool Deleted, string Detail) DeletePackage(string publishedName);

    /// <summary>Lower-case hex, or null when the file cannot be read.</summary>
    string? Sha256OfFile(string path);
}

public enum HidDriverOutcomeKind
{
    /// <summary>The new driver is bound, healthy, and the only Nexus package left.</summary>
    Updated,

    /// <summary>The new driver failed its checks; the previous one is bound and healthy again.</summary>
    RolledBack,

    /// <summary>Neither the new driver nor the previous one could be made healthy.</summary>
    Failed,
}

public sealed record HidDriverOutcome(HidDriverOutcomeKind Kind, string Detail, IReadOnlyList<string> StalePackagesLeft);

/// <summary>
/// Replaces the Nexus HID driver package and leaves exactly one, healthy, behind.
/// <para>
/// <b>Why cleanup is part of an update.</b> Every driver update used to leave the previous package in
/// the Driver Store, where Storage Sense deletes outranked packages on its own schedule. When 0.9.2.1
/// and 0.9.2.2 were not configurable, deleting the last older package deleted the UMDF registration
/// the active driver needed, and the device came back without its driver on the next boot. So the
/// update removes the stale packages itself, at a moment it chose, and checks what Windows did.
/// </para>
/// <para>
/// <b>Why the checks come after the cleanup.</b> Deleting a package is where Windows can remove the
/// shared registration, so "the new driver installed" proves nothing until the old ones are gone. The
/// device is healthy when it is bound to the expected version, has no problem code, has exactly the
/// stack <c>WUDFRd -> vhf -> PnpManager</c>, and its UMDF registration points at a DLL whose hash is the
/// release manifest's.
/// </para>
/// <para>
/// <b>The only repair is reapplying the package.</b> If cleanup damaged the registration, Windows is
/// asked to install the already-active package again, which rewrites it. The registration is never
/// written by hand. If that does not make the device healthy, the previous release's payload - from the
/// release store, hash-verified - is bound the same way. Rollback never relies on an old package still
/// being in the Driver Store.
/// </para>
/// </summary>
public sealed class HidDriverLifecycle(IHidDriverHost host, Action<string> log)
{
    /// <summary>The instance id the Nexus install created (devgen, instance id NexusHid).</summary>
    public const string DeviceInstance = @"ROOT\DEVGEN\NEXUSHID";

    public const string HardwareId = @"Root\NexusHid";

    public const string PackageInf = "nexushid.inf";

    public const string Provider = "Nexus";

    public const string DriverDll = "NexusHidDriver.dll";

    /// <summary>The one stack that is right: the UMDF reflector over vhf as function driver, once.</summary>
    public static readonly IReadOnlyList<string> ExpectedStack = ["WUDFRd", "vhf", "PnpManager"];

    /// <summary>Installs <paramref name="target"/>; on failure puts <paramref name="rollback"/> back.</summary>
    public HidDriverOutcome Update(DriverPayload target, DriverPayload? rollback)
    {
        var failure = Establish(target, install: true);

        if (failure is null)
        {
            return new HidDriverOutcome(HidDriverOutcomeKind.Updated, $"HID driver {target.Version} is installed and healthy.", StaleLeft());
        }

        log($"HID driver {target.Version} failed: {failure}");

        if (rollback is null)
        {
            return new HidDriverOutcome(HidDriverOutcomeKind.Failed, $"{failure}; there is no previous driver payload to roll back to.", StaleLeft());
        }

        log($"Rolling back to HID driver {rollback.Version} from the release store.");
        var rollbackFailure = Establish(rollback, install: false);

        return rollbackFailure is null
            ? new HidDriverOutcome(HidDriverOutcomeKind.RolledBack, $"HID driver {target.Version} {failure}; {rollback.Version} was restored.", StaleLeft())
            : new HidDriverOutcome(HidDriverOutcomeKind.Failed, $"HID driver {target.Version} {failure}; restoring {rollback.Version} also failed: {rollbackFailure}", StaleLeft());
    }

    /// <summary>
    /// Makes <paramref name="payload"/> the bound driver, removes every other Nexus package, and proves
    /// the device healthy afterwards. Null on success, otherwise why not.
    /// </summary>
    /// <param name="install">
    /// True for an ordinary upgrade (<c>pnputil /add-driver /install</c>, which only binds a better
    /// driver); false to force this exact package, as a rollback to an older version must.
    /// </param>
    public string? Establish(DriverPayload payload, bool install)
    {
        if (install)
        {
            var (code, output) = host.AddAndInstall(payload.InfPath);
            log($"pnputil /add-driver /install {payload.Version}: exit {code}. {output}");
        }
        else if (!TryReapply(payload, "bind"))
        {
            return $"Windows did not install {payload.Version}";
        }

        if (!BoundTo(payload))
        {
            // pnputil only binds a better-ranked driver. The forced path binds exactly this one.
            if (!install || !TryReapply(payload, "bind") || !BoundTo(payload))
            {
                return $"the device is not using {payload.Version} (it reports {host.ReadDevice().DriverVersion ?? "no driver"})";
            }
        }

        // Healthy before anything is deleted: a package that never worked is rolled back, not cleaned up after.
        if (Check(payload, "after install").Count > 0
            && Repair(payload, "after install") is { Count: > 0 } afterInstall)
        {
            return "did not become healthy after install: " + string.Join("; ", afterInstall);
        }

        RemoveStalePackages(payload);

        if (Check(payload, "after cleanup").Count > 0)
        {
            log("The active driver is not healthy after the stale packages were removed; reapplying it.");

            if (Repair(payload, "after cleanup") is { Count: > 0 } afterCleanup)
            {
                return "did not stay healthy after stale packages were removed: " + string.Join("; ", afterCleanup);
            }
        }

        return null;
    }

    /// <summary>
    /// The payload for a release's driver component, laid out (and already hash-verified) under
    /// <paramref name="directory"/>.
    /// </summary>
    public static DriverPayload PayloadFrom(FileSetComponent component, string directory)
    {
        var inf = component.Files.FirstOrDefault(f => f.Path.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
            ?? throw new UpdateIntegrityException($"HID driver {component.Version} has no INF.");

        var dll = component.Files.FirstOrDefault(f => string.Equals(Path.GetFileName(f.Path), DriverDll, StringComparison.OrdinalIgnoreCase))
            ?? throw new UpdateIntegrityException($"HID driver {component.Version} has no {DriverDll}.");

        return new DriverPayload(
            component.Version,
            ReleaseStore.Contained(directory, inf.Path) ?? throw new UpdateIntegrityException($"{inf.Path} is not a safe path."),
            dll.Sha256);
    }

    /// <summary>Packages other than the one bound to the device, from Nexus, that Windows lists for our hardware id.</summary>
    public static IReadOnlyList<DriverPackage> StalePackages(IEnumerable<DriverPackage> matching, string? boundInf) =>
        matching
            .Where(p => string.Equals(p.OriginalName, PackageInf, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Provider, Provider, StringComparison.OrdinalIgnoreCase)
                && !p.Installed
                && !string.Equals(p.PublishedName, boundInf, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.PublishedName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Everything wrong with the device for <paramref name="expected"/>; empty when it is healthy.</summary>
    public static IReadOnlyList<string> Problems(
        HidDeviceState device,
        UmdfRegistration? registration,
        string? registeredDllSha256,
        DriverPayload expected)
    {
        var problems = new List<string>();

        if (!device.Present)
        {
            problems.Add($"{DeviceInstance} is not present");
            return problems;
        }

        if (device.ProblemCode != 0)
        {
            problems.Add($"the device has problem code {device.ProblemCode}");
        }

        if (!string.Equals(device.DriverVersion, expected.Version, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"the device uses driver {device.DriverVersion ?? "none"}, not {expected.Version}");
        }

        if (!device.Stack.SequenceEqual(ExpectedStack, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"the device stack is {Describe(device.Stack)}, not {Describe(ExpectedStack)}");
        }

        if (registration?.ImagePath is not { Length: > 0 } imagePath)
        {
            problems.Add("the UMDF registration for NexusHidDriver has no ImagePath");
            return problems;
        }

        if (!string.Equals(Path.GetFileName(imagePath), DriverDll, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"the UMDF registration points at {imagePath}, not {DriverDll}");
        }

        if (registeredDllSha256 is null)
        {
            problems.Add($"the registered driver {imagePath} cannot be read");
        }
        else if (!string.Equals(registeredDllSha256, expected.DriverDllSha256, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"the registered driver {imagePath} is not the one the release manifest lists for {expected.Version}");
        }

        if (registration.WdfMajorVersion != 2)
        {
            problems.Add("the UMDF registration has no WDF 2 version");
        }

        if (!registration.Extensions.Contains("HidVhf", StringComparer.OrdinalIgnoreCase))
        {
            problems.Add("the UMDF registration does not load the HidVhf extension");
        }

        return problems;
    }

    public static string Describe(IEnumerable<string> stack) =>
        stack.Any() ? string.Join(" -> ", stack) : "empty";

    private bool BoundTo(DriverPayload payload) =>
        string.Equals(host.ReadDevice().DriverVersion, payload.Version, StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<string> Check(DriverPayload payload, string when)
    {
        var device = host.ReadDevice();
        var registration = host.ReadUmdfRegistration();
        var sha = registration?.ImagePath is { Length: > 0 } path ? host.Sha256OfFile(path) : null;
        var problems = Problems(device, registration, sha, payload);

        log($"Check {when}: {device.DriverInf ?? "no inf"} {device.DriverVersion ?? "no driver"}, problem {device.ProblemCode}, " +
            $"stack {Describe(device.Stack)}, UMDF ImagePath {registration?.ImagePath ?? "missing"}" +
            (problems.Count == 0 ? " - healthy." : " - " + string.Join("; ", problems) + "."));

        return problems;
    }

    /// <summary>One forced reinstall of the payload, then a check. What is still wrong; empty when that fixed it.</summary>
    private IReadOnlyList<string> Repair(DriverPayload payload, string when)
    {
        TryReapply(payload, "repair " + when);
        return Check(payload, "after repair");
    }

    private bool TryReapply(DriverPayload payload, string why)
    {
        var (succeeded, detail) = host.Reapply(payload.InfPath);
        log($"Reapplied {payload.Version} ({why}) through UpdateDriverForPlugAndPlayDevices: {(succeeded ? "ok" : "failed")}. {detail}");
        return succeeded;
    }

    private void RemoveStalePackages(DriverPayload payload)
    {
        var matching = host.MatchingPackages();

        if (matching is null)
        {
            log("Windows could not list the Nexus driver packages; none were removed.");
            return;
        }

        var bound = host.ReadDevice().DriverInf;
        var stale = StalePackages(matching, bound);

        log(stale.Count == 0
            ? $"No stale Nexus driver packages; {bound} is the only one."
            : $"Removing {stale.Count} stale Nexus driver package(s): " + string.Join(", ", stale.Select(p => $"{p.PublishedName} ({p.Version})")));

        foreach (var package in stale)
        {
            var (deleted, detail) = host.DeletePackage(package.PublishedName);
            log($"pnputil /delete-driver {package.PublishedName} ({package.Version}): {(deleted ? "removed" : "not removed")}. {detail}");
        }
    }

    private IReadOnlyList<string> StaleLeft()
    {
        var matching = host.MatchingPackages();
        return matching is null
            ? []
            : StalePackages(matching, host.ReadDevice().DriverInf).Select(p => p.PublishedName).ToList();
    }
}
