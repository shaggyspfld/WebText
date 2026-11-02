using System.Text.RegularExpressions;

namespace WebText.Web.Services;

/// <summary>
/// The name given to this installation of WebText (e.g. "AcmeText", "BillWatchText", "RepairText"), chosen when the
/// installer runs and stored in appsettings.json as Instance:Name.
///
/// It names the IIS site, app pool, install folder, database and backup files, and it's the default name shown in the
/// top bar. It's also the Data Protection application name and the prefix of every encryption purpose, so two
/// instances on one server (or sharing a database server) can never read each other's secrets.
/// Changing it on an existing install makes its saved API keys and authenticator keys unreadable, so move an instance
/// to a new name with Backup &amp; restore instead.
/// </summary>
public sealed partial class AppInstance
{
    public const string DefaultName = "WebText";

    public AppInstance(string? name)
    {
        var n = (name ?? "").Trim();
        Name = ValidName().IsMatch(n) ? n : DefaultName;
        Slug = Name.ToLowerInvariant();
    }

    /// <summary>e.g. "AcmeText".</summary>
    public string Name { get; }

    /// <summary>Lower-case form for file names, e.g. "acmetext".</summary>
    public string Slug { get; }

    /// <summary>A Data Protection purpose unique to this instance, e.g. "AcmeText.Settings.v1".</summary>
    public string Purpose(string what) => $"{Name}.{what}";

    public static bool IsValid(string? name) => name != null && ValidName().IsMatch(name);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{1,39}$")]
    private static partial Regex ValidName();

    public static AppInstance FromConfiguration(IConfiguration config) => new(config["Instance:Name"]);
}
