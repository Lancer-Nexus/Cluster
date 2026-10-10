using LancerNexus.Protocol;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LancerNexus.Cluster;
using Xunit;

namespace LancerNexus.Cluster.Tests;

public sealed class RedisPermissionRevisionListenerTests
{
    [Fact]
    public async Task ListenerBecomesReadyOnlyAfterSubscriptionAndSnapshotAcknowledgement()
    {
        using var redis = new TcpListener(IPAddress.Loopback, 0);
        redis.Start();
        var address = (IPEndPoint)redis.LocalEndpoint;
        var service = new PermissionService(new PermissionSnapshot(0, [], []));
        var snapshot = new PermissionSnapshotDocument(3, [], []);
        var acknowledger = new RecordingAcknowledger();
        var coordinator = new PermissionSyncCoordinator("li-01", service, new SnapshotSource(snapshot), acknowledger, TimeProvider.System);
        var listener = new RedisPermissionRevisionListener($"127.0.0.1:{address.Port}", service, coordinator);
        using var cancellation = new CancellationTokenSource();
        var redisConnection = RespondToSubscriptionAsync(redis, cancellation.Token);
        var listening = listener.RunAsync(cancellation.Token);

        Assert.False(listener.IsReady);
        await WaitUntilAsync(() => listener.IsReady);

        Assert.True(coordinator.IsReady);
        Assert.True(service.IsSynchronized);
        Assert.Equal(3, acknowledger.Revision);

        cancellation.Cancel();
        await listening;
        try { await redisConnection; }
        catch (OperationCanceledException) { }
        Assert.False(listener.IsReady);
        Assert.False(service.IsSynchronized);
    }

    private static async Task RespondToSubscriptionAsync(TcpListener redis, CancellationToken cancellationToken)
    {
        using var client = await redis.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        const string channel = "lancer-nexus:permissions:revision";
        var response = Encoding.ASCII.GetBytes($"*3\r\n$9\r\nsubscribe\r\n${Encoding.UTF8.GetByteCount(channel)}\r\n{channel}\r\n:1\r\n");
        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), "Permission listener did not become ready after its SQL snapshot was acknowledged.");
    }

    private sealed class SnapshotSource(PermissionSnapshotDocument snapshot) : IPermissionSnapshotSource
    {
        public Task<PermissionSnapshotDocument> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }

    private sealed class RecordingAcknowledger : IPermissionRevisionAcknowledger
    {
        public long Revision { get; private set; }

        public Task<bool> AcknowledgeAsync(PermissionRevisionAcknowledged acknowledgement, CancellationToken cancellationToken)
        {
            Revision = acknowledgement.Revision;
            return Task.FromResult(true);
        }
    }
}
