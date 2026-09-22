namespace OpcUaBridge.Host;

/// <summary>
/// The settings files the gateway reads, and the order in which they override each other.
/// </summary>
/// <remarks>
/// <para>
/// Kept here rather than inline in <c>Program.cs</c> because the order is the whole
/// behaviour: a file that loads but loses to a shipped default is indistinguishable, from
/// the outside, from a file that never loaded. A test pins it.
/// </para>
/// <para>
/// Later sources win. Each layer is more specific to one machine than the one before it:
/// what the release shipped, then what this installation was configured with, then
/// whatever a developer is overriding right now, then the environment and the command line.
/// </para>
/// </remarks>
public static class BridgeConfiguration
{
    /// <summary>Settings that ship with the release, and that the next release replaces.</summary>
    public const string DefaultsFileName = "appsettings.json";

    /// <summary>
    /// Settings belonging to one installation: the plant endpoint, the ports, the
    /// credentials for this site.
    /// </summary>
    /// <remarks>
    /// Never in the repository and never in a release package, so extracting a new version
    /// over the install folder cannot overwrite it. It is created by hand on the machine
    /// the gateway runs on, and it is what makes an upgrade a file copy rather than a
    /// reconfiguration.
    /// </remarks>
    public const string InstallationFileName = "appsettings.production.json";

    /// <summary>A developer's own override, kept under <c>config\</c> so it is never mistaken for an installed file.</summary>
    public static string LocalFileRelativePath { get; } = Path.Combine("config", "appsettings.Local.json");

    /// <summary>
    /// Adds every source the gateway reads, lowest precedence first.
    /// </summary>
    /// <remarks>
    /// <c>WebApplication.CreateBuilder</c> has already added <c>appsettings.json</c> and
    /// <c>appsettings.{Environment}.json</c> against its own content root, and on a default
    /// install the environment is <c>Production</c> -- so on Windows, where file names are
    /// not case sensitive, it has already read the installation file at a precedence below
    /// the shipped defaults. Adding these explicitly against
    /// <see cref="AppContext.BaseDirectory"/> is what makes the order deterministic, and
    /// the same on Linux. Do not remove them as duplicates.
    /// </remarks>
    public static IConfigurationBuilder AddBridgeConfiguration(
        this IConfigurationBuilder builder,
        string baseDirectory,
        string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        return builder
            .SetBasePath(baseDirectory)
            .AddJsonFile(DefaultsFileName, optional: false, reloadOnChange: true)
            .AddJsonFile(InstallationFileName, optional: true, reloadOnChange: true)
            .AddJsonFile(LocalFileRelativePath, optional: true, reloadOnChange: true)
            .AddEnvironmentVariables("OPCUABRIDGE_")
            .AddCommandLine(args ?? []);
    }

    /// <summary>
    /// The settings files, lowest precedence first, and whether each one is present.
    /// </summary>
    /// <remarks>
    /// For <c>--check-config</c>. "I edited the settings and nothing changed" is nearly
    /// always a file in the wrong place or under the wrong name, and that is a question
    /// this answers in one line rather than a remote debugging session.
    /// </remarks>
    public static IReadOnlyList<BridgeConfigurationFile> DescribeFiles(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        return
        [
            Describe(baseDirectory, DefaultsFileName, "Shipped defaults", required: true),
            Describe(baseDirectory, InstallationFileName, "This installation", required: false),
            Describe(baseDirectory, LocalFileRelativePath, "Developer override", required: false)
        ];
    }

    private static BridgeConfigurationFile Describe(string baseDirectory, string relativePath, string role, bool required)
    {
        var fullPath = Path.Combine(baseDirectory, relativePath);
        return new BridgeConfigurationFile(role, fullPath, File.Exists(fullPath), required);
    }
}

/// <summary>One settings file and whether it is there.</summary>
public sealed record BridgeConfigurationFile(string Role, string Path, bool Exists, bool Required);
