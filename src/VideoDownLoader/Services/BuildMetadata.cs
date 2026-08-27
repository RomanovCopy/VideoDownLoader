using System.Reflection;
using System.Text.RegularExpressions;

namespace VideoDownLoader.Services;

internal static partial class BuildMetadata
{
    public static string CommitSha { get; } = ReadCommitSha();

    public static bool CanCheckForUpdates => CommitRegex().IsMatch(CommitSha);

    public static string ShortCommit => CanCheckForUpdates ? CommitSha[..7] : "локальная";

    private static string ReadCommitSha()
    {
        return typeof(BuildMetadata).Assembly
                   .GetCustomAttributes<AssemblyMetadataAttribute>()
                   .FirstOrDefault(attribute => attribute.Key == "CommitSha")
                   ?.Value
               ?? "development";
    }

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommitRegex();
}
