using KnockBox.Server.Hosting;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The one writability probe. It answers by WRITING, which is the only thing that tells the truth on a
/// read-only bind mount — and is also why its file has a recognisable name: the games root is watched,
/// so a probe there is otherwise indistinguishable from a game being dropped in, and the portal's
/// catalog poll spent one rescan per poll chasing its own probe.
/// </summary>
public class DirectoryProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-probe-" + Guid.NewGuid().ToString("N"));

    public DirectoryProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_writable_directory_reports_no_reason_and_keeps_no_residue()
    {
        Assert.Null(DirectoryProbe.WhyNotWritable(_root));
        // The probe must clean up after itself: a leftover file is both a watcher event and, in the games
        // root, something the catalog would try to make sense of.
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void A_missing_directory_reports_why_rather_than_throwing()
    {
        var reason = DirectoryProbe.WhyNotWritable(Path.Combine(_root, "not-there"));
        Assert.NotNull(reason);
        Assert.Contains("not-there", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_parent_form_asks_about_the_directory_an_entry_would_be_removed_from()
    {
        // Deleting a file or folder needs write access to its PARENT, not to itself.
        var child = Path.Combine(_root, "game");
        Directory.CreateDirectory(child);
        Assert.Null(DirectoryProbe.WhyParentNotWritable(child));
    }

    [Fact]
    public void Probe_files_are_recognisable_and_nothing_else_is()
    {
        Assert.True(DirectoryProbe.IsProbeFile($"{DirectoryProbe.FilePrefix}{Guid.NewGuid():N}"));
        // What GameCatalog's watcher must still act on.
        Assert.False(DirectoryProbe.IsProbeFile("GAME.json"));
        Assert.False(DirectoryProbe.IsProbeFile("word-rush.kbg"));
        Assert.False(DirectoryProbe.IsProbeFile(null));
    }
}
