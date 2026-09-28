using LancerNexus.Cluster;
using Xunit;
using LancerNexus.Protocol;

namespace LancerNexus.Cluster.Tests;

public sealed class PermissionServiceTests
{
    private static readonly Guid UserId = Guid.Parse("a9c23553-ef7f-4d12-8730-6e2cd42d9711");

    [Fact]
    public void FirstMatchingUserNodeWinsBeforeOrderedInheritedGroups()
    {
        var snapshot = new PermissionSnapshot(4,
            [new(UserId, [new("admin")], [new("cluster.stop", false, PermissionPatternKind.Exact)])],
            [new("admin", ["staff"], 100, "staff", "[Admin]", null,
                [new("cluster.*", true)]),
             new("staff", [], 50, "staff", "[Staff]", null,
                [new("cluster.stop", true, PermissionPatternKind.Exact)])]);
        var service = new PermissionService(snapshot);
        Assert.False(service.HasPermission(UserId, "cluster.stop"));
        Assert.True(service.HasPermission(UserId, "cluster.status"));
        Assert.Equal(["admin", "staff"], service.GetGroupNames(UserId));
        Assert.Equal("[Admin]", service.GetPrefix(UserId));
        Assert.Equal(100, service.GetRank(UserId, "staff"));
    }

    [Fact]
    public void ChildWildcardIncludesDescendantNodesAndExactNodeStaysExact()
    {
        var service = new PermissionService(new PermissionSnapshot(1, [new(UserId, [new("member")], [])],
            [new("member", [], 1, null, null, null,
                [new("chat.*", true), new("ship.buy", true, PermissionPatternKind.Exact)])]));
        Assert.True(service.HasPermission(UserId, "chat.read.private"));
        Assert.True(service.HasPermission(UserId, "ship.buy"));
        Assert.False(service.HasPermission(UserId, "ship.buy.special"));
    }

    [Fact]
    public void ContextsAndExpiryAreAppliedBeforeFirstMatch()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var service = new PermissionService(new PermissionSnapshot(1,
            [new(UserId, [new("player")], [])],
            [new("player", [], 0, null, null, null,
                [new("travel.use", false, PermissionPatternKind.Exact, InstanceId: "li-01"),
                 new("travel.*", true, ExpiresUtc: now.AddSeconds(-1)),
                 new("travel.*", true)])]));
        Assert.False(service.HasPermission(UserId, "travel.use", new("li-01"), now));
        Assert.True(service.HasPermission(UserId, "travel.use", new("bw-01"), now));
    }

    [Fact]
    public void MoreSpecificMembershipWinsAndExpiredMembershipIsIgnored()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var service = new PermissionService(new PermissionSnapshot(1,
            [new(UserId, [new("global"), new("local", InstanceId: "li-01"),
                new("expired", ExpiresUtc: now.AddTicks(-1))], [])],
            [new("global", [], 1, "staff", null, null, [new("gate.use", true, PermissionPatternKind.Exact)]),
             new("local", [], 2, "staff", "[Local]", null, [new("gate.use", false, PermissionPatternKind.Exact)]),
             new("expired", [], 99, "staff", "[Expired]", null, [new("gate.use", true, PermissionPatternKind.Exact)])]));

        Assert.False(service.HasPermission(UserId, "gate.use", new("li-01"), now));
        Assert.True(service.HasPermission(UserId, "gate.use", new("bw-01"), now));
        Assert.Equal(["local", "global"], service.GetGroups(UserId, new("li-01")));
        Assert.Equal("[Local]", service.GetPrefix(UserId, new("li-01")));
        Assert.Equal(2, service.GetRank(UserId, "STAFF", new("li-01")));
    }

    [Fact]
    public void FirstGroupAndItsInheritedGroupsDetermineAllowDenyOrder()
    {
        var service = new PermissionService(new PermissionSnapshot(1,
            [new(UserId, [new("moderator"), new("trusted")], [])],
            [new("moderator", ["staff"], 20, null, null, null,
                [new("player.kick", false, PermissionPatternKind.Exact)]),
             new("trusted", [], 10, null, null, null,
                [new("player.kick", true, PermissionPatternKind.Exact)]),
             new("staff", [], 5, null, null, null,
                [new("player.kick", true, PermissionPatternKind.Exact)])]));

        Assert.False(service.HasPermission(UserId, "player.kick"));
        Assert.Equal(["moderator", "staff", "trusted"], service.GetGroups(UserId));
    }

    [Fact]
    public void DoubleStarMatchesZeroOrMorePermissionSegments()
    {
        var service = new PermissionService(new PermissionSnapshot(1,
            [new(UserId, [new("admin")], [])],
            [new("admin", [], 0, null, null, null,
                [new("chat.**", true)])]));

        Assert.True(service.HasPermission(UserId, "chat"));
        Assert.True(service.HasPermission(UserId, "chat.read.private"));
        Assert.False(service.HasPermission(UserId, "chatty.read"));
    }

    [Fact]
    public void RegexIsBoundedAndCanBeRejected()
    {
        _ = new PermissionSnapshot(1, [], [new("regex", [], 0, null, null, null,
            [new("^chat[.](read|write)$", true, PermissionPatternKind.Regex)])]);
        Assert.Throws<ArgumentException>(() => new PermissionSnapshot(1, [],
            [new("unsafe", [], 0, null, null, null, [new("(?=a)a", true, PermissionPatternKind.Regex)])]));
    }

    [Fact]
    public void InheritanceCyclesAreRejectedAndStaleSnapshotsFailClosed()
    {
        Assert.Throws<ArgumentException>(() => new PermissionSnapshot(1, [],
            [new("a", ["b"], 0, null, null, null, []), new("b", ["a"], 0, null, null, null, [])]));
        var service = new PermissionService(new PermissionSnapshot(1, [new(UserId, [new("admin")], [])],
            [new("admin", [], 0, null, null, null, [new("*", true)])]));
        service.RequireRevision(2);
        Assert.False(service.HasPermission(UserId, "anything"));
        service.ReplaceSnapshot(new PermissionSnapshot(2, [new(UserId, [new("admin")], [])],
            [new("admin", [], 0, null, null, null, [new("*", true)])]));
        Assert.True(service.HasPermission(UserId, "anything"));
    }

    [Fact]
    public void ClusterMetadataAlsoFailsClosedUntilSnapshotIsCurrent()
    {
        var service = new PermissionService(new PermissionSnapshot(1,
            [new(UserId, [new("admin")], [])],
            [new("admin", [], 50, "staff", "[Admin]", null, [])]));
        service.MarkUnsynchronized();
        Assert.Empty(service.GetGroups(UserId));
        Assert.Null(service.GetRank(UserId));
        Assert.Null(service.GetPrefix(UserId));
    }

    [Fact]
    public async Task RevisionNoticeLoadsSnapshotBeforeAcknowledgingAndReadiness()
    {
        var service = new PermissionService(new PermissionSnapshot(0, [], []));
        var snapshot = new PermissionSnapshotDocument(2, [new(UserId, [new("admin")], [])],
            [new("admin", [], 10, null, null, null, [new("server.stop", true)])]);
        var acknowledger = new Ack();
        var sync = new PermissionSyncCoordinator("li-01", service, new Source(snapshot), acknowledger, TimeProvider.System);
        Assert.False(sync.IsReady);
        Assert.True(await sync.ApplyAsync(new PermissionRevisionChanged { EventId = Guid.NewGuid(), Revision = 2 }, default));
        Assert.True(sync.IsReady);
        Assert.Equal(2, acknowledger.Revision);
        Assert.True(service.HasPermission(UserId, "server.stop"));
    }

    private sealed class Source(PermissionSnapshotDocument document) : IPermissionSnapshotSource
    {
        public Task<PermissionSnapshotDocument> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(document);
    }

    private sealed class Ack : IPermissionRevisionAcknowledger
    {
        public long Revision { get; private set; }
        public Task<bool> AcknowledgeAsync(PermissionRevisionAcknowledged acknowledgement, CancellationToken cancellationToken)
        { Revision = acknowledgement.Revision; return Task.FromResult(true); }
    }
}
