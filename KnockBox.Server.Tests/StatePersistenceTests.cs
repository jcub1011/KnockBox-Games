using KnockBox.Server.Hosting;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The mount-point parsing and containment rules behind the "this directory will not survive an image
/// update" warning. Pure input/output, so the precedence is pinned here rather than in a container test:
/// getting "is /app/data covered by a mount?" wrong in either direction is costly — a false negative is
/// the silent data loss this whole check exists to prevent, and a false positive is a scary warning on a
/// correctly configured server, which teaches operators to ignore the real one.
/// </summary>
public class StatePersistenceTests
{
    // Shape copied from a real container's /proc/self/mountinfo: id, parent, major:minor, root,
    // MOUNT POINT, options, then optional fields, a lone "-", and the filesystem details.
    private const string MountInfo = """
        21 1 0:20 / / rw,relatime - overlay overlay rw
        22 21 0:24 / /proc rw,nosuid,nodev,noexec,relatime - proc proc rw
        23 21 0:25 / /sys ro,nosuid,nodev,noexec,relatime - sysfs sysfs ro
        30 21 8:1 /var/lib/docker/volumes/knockbox-admin/_data /app/data rw,relatime - ext4 /dev/sda1 rw
        31 21 8:1 /srv/knockbox/games /games ro,relatime - ext4 /dev/sda1 ro
        """;

    [Fact]
    public void Mount_points_are_the_fifth_field_and_come_back_longest_first()
    {
        var points = StatePersistence.MountPoints(MountInfo);

        // The fifth field, not the fourth (which is the source subtree inside the volume) — reading the
        // wrong one would report a mount at /var/lib/docker/volumes/... and match nothing.
        Assert.Contains("/app/data", points);
        Assert.Contains("/games", points);
        Assert.Contains("/", points);
        Assert.DoesNotContain("/var/lib/docker/volumes/knockbox-admin/_data", points);

        // Longest first, so a caller walking the list hits the most specific mount before "/".
        Assert.Equal(points.OrderByDescending(p => p.Length), points);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("21 1 0:20 /")] // truncated before the mount point
    public void Unparseable_mountinfo_yields_no_mount_points_rather_than_throwing(string text) =>
        // Read from /proc at startup, so a shape we didn't anticipate must not take the server down.
        Assert.Empty(StatePersistence.MountPoints(text));

    [Fact]
    public void Octal_escapes_in_a_mount_point_are_decoded()
    {
        // The kernel escapes spaces as \040. Left encoded, a mount under a directory with a space in it
        // would never match the path we compare against, so the state would be reported ephemeral while
        // sitting safely on a volume.
        var points = StatePersistence.MountPoints("30 21 8:1 / /mnt/my\\040pool/data rw - ext4 /dev/sda1 rw");
        Assert.Equal(["/mnt/my pool/data"], points);
    }

    [Fact]
    public void The_nearest_mount_is_the_most_specific_one()
    {
        var points = StatePersistence.MountPoints(MountInfo);

        Assert.Equal("/app/data", StatePersistence.NearestMount(points, "/app/data"));
        Assert.Equal("/app/data", StatePersistence.NearestMount(points, "/app/data/nested/deeper"));
        // Nothing is mounted at /app itself, so it falls back to the root filesystem.
        Assert.Equal("/", StatePersistence.NearestMount(points, "/app/games-managed"));
    }

    [Fact]
    public void The_most_specific_mount_wins_regardless_of_input_order()
    {
        // NearestMount must not depend on MountPoints' sort — a hand-built list is a legitimate caller,
        // and "first match wins" over an unsorted list would answer "/app" for a path under "/app/data".
        string[] shortestFirst = ["/", "/app", "/app/data"];
        Assert.Equal("/app/data", StatePersistence.NearestMount(shortestFirst, "/app/data/admin.secret"));
    }

    [Fact]
    public void Containment_is_compared_on_segment_boundaries()
    {
        // A prefix match would have "/app" contain "/application" and report a mounted directory as
        // ephemeral (or the reverse). The same trap GameAssetPath exists to avoid.
        string[] points = ["/", "/app"];
        Assert.Equal("/app", StatePersistence.NearestMount(points, "/app/data"));
        Assert.Equal("/", StatePersistence.NearestMount(points, "/application/data"));
    }

    [Fact]
    public void Trailing_separators_do_not_change_the_answer()
    {
        string[] points = ["/", "/app/data/"];
        Assert.Equal("/app/data", StatePersistence.NearestMount(points, "/app/data"));
        Assert.Equal("/app/data", StatePersistence.NearestMount(points, "/app/data/"));
    }

    [Fact]
    public void A_path_on_its_own_mount_is_persisted_and_one_on_the_root_filesystem_is_not()
    {
        var points = StatePersistence.MountPoints(MountInfo);

        // Mounted: survives the container being replaced.
        Assert.False(StatePersistence.IsEphemeral(points, "/app/data"));
        Assert.False(StatePersistence.IsEphemeral(points, "/games/tictactoe"));

        // Not mounted: lives in the container's writable layer and dies with it. This is the case the
        // whole warning exists for — every published compose block once omitted this exact directory.
        Assert.True(StatePersistence.IsEphemeral(points, "/app/games-managed"));
        Assert.True(StatePersistence.IsEphemeral(points, "/app/data-other"));
    }

    [Fact]
    public void An_empty_mount_list_reads_as_ephemeral()
    {
        // Callers only reach IsEphemeral once CurrentMountPoints returned a list, so "no mounts at all"
        // means nothing is held outside the container. Answering "persisted" here would make an
        // unexpected /proc shape silently suppress the warning.
        Assert.True(StatePersistence.IsEphemeral([], "/app/data"));
    }
}
