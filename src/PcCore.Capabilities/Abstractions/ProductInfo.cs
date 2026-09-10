using System.Reflection;

namespace PcCore.Capabilities.Abstractions;

/// <summary>
/// What this build calls itself, and which version it is.
/// <para>
/// <b>One source, because there were three and they disagreed.</b> The control panel's title said
/// "PC Core v0.4" for six releases, a test restated "0.9.0" as a literal, and the real number lived
/// in <c>Directory.Build.props</c>. Every one of those was edited by hand, so the displayed version
/// was not wrong by accident — it was wrong by construction, and would drift again the moment
/// somebody fixed only the visible one.
/// </para>
/// <para>
/// The value comes from the assembly attribute MSBuild stamps from <c>VersionPrefix</c>, so the
/// build is the only place a version is written down. Anything that shows a version to the owner
/// reads it from here.
/// </para>
/// </summary>
public static class ProductInfo
{
    /// <summary>The product name, as the owner sees it.</summary>
    public const string Name = "Nexus";

    /// <summary>The process at the centre of it, named where the distinction matters.</summary>
    public const string CoreName = "PC Core";

    /// <summary>
    /// The three-part version, without any build metadata.
    /// <para>
    /// <c>InformationalVersion</c> rather than <c>AssemblyVersion</c>: the SDK appends the source
    /// revision after a <c>+</c>, and the part before it is what <c>VersionPrefix</c> actually said.
    /// </para>
    /// </summary>
    public static string Version { get; } =
        typeof(ProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary><c>Nexus 1.1.0</c>. What a window title or a page heading should show.</summary>
    public static string Display => $"{Name} {Version}";

    /// <summary><c>Nexus 1.1.0 · PC Core</c>. For places that name the process as well.</summary>
    public static string DisplayWithCore => $"{Name} {Version} · {CoreName}";
}
