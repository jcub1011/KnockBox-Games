namespace KnockBox.Server.Tests;

/// <summary>
/// Locates files in the repo checkout, for the tests that assert repo FILES stay consistent with
/// each other (port lists, version numbers) rather than asserting anything about a running host.
/// </summary>
/// <remarks>
/// Such tests have to tolerate not finding the repo at all: the same assemblies run from a publish
/// output or a NuGet-restored test run where no checkout exists above them. Every accessor returns
/// null in that case and the caller skips, rather than failing a test for a reason that has nothing
/// to do with the code under test.
/// </remarks>
internal static class RepoFile
{
    /// <summary>The repo root (the directory holding KnockBox-Games.slnx), or null outside a checkout.</summary>
    public static string? Root { get; } = FindRoot();

    private static string? FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "KnockBox-Games.slnx")))
                return dir.FullName;
        return null;
    }

    /// <summary>Absolute path for a repo-relative path, or null outside a checkout.</summary>
    public static string? Path_(string relativePath) =>
        Root is null ? null : Path.Combine(Root, relativePath);

    /// <summary>File contents, or null outside a checkout or when the file does not exist.</summary>
    public static string? Read(string relativePath)
    {
        var full = Path_(relativePath);
        return full is not null && File.Exists(full) ? File.ReadAllText(full) : null;
    }

    /// <summary>True when the path exists in the checkout. False outside a checkout.</summary>
    public static bool Exists(string relativePath)
    {
        var full = Path_(relativePath);
        return full is not null && (File.Exists(full) || Directory.Exists(full));
    }
}
