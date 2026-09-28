using LancerNexus.Protocol;

namespace LancerNexus.Cluster;

public interface IPermissionSnapshotSource
{
    Task<PermissionSnapshotDocument> LoadAsync(CancellationToken cancellationToken);
}

public interface IPermissionRevisionAcknowledger
{
    Task<bool> AcknowledgeAsync(PermissionRevisionAcknowledged acknowledgement, CancellationToken cancellationToken);
}

/// <summary>Applies SQL snapshots atomically and acknowledges only the revision that became active.</summary>
public sealed class PermissionSyncCoordinator(string instanceId, PermissionService permissions,
    IPermissionSnapshotSource snapshots, IPermissionRevisionAcknowledger acknowledger, TimeProvider clock)
{
    private readonly SemaphoreSlim reloadLock = new(1, 1);
    private volatile bool initialized;
    private readonly bool validInstance = !string.IsNullOrWhiteSpace(instanceId);
    private readonly PermissionService permissionService = MarkUnavailable(permissions);

    private static PermissionService MarkUnavailable(PermissionService service)
    {
        service.MarkUnsynchronized();
        return service;
    }

    public bool IsReady => validInstance && initialized && permissionService.IsSynchronized;

    public async Task<bool> InitializeAsync(CancellationToken ct)
    {
        await reloadLock.WaitAsync(ct);
        try
        {
            var document = await snapshots.LoadAsync(ct);
            permissionService.ReplaceSnapshot(document.ToSnapshot());
            var accepted = await acknowledger.AcknowledgeAsync(new PermissionRevisionAcknowledged
            { InstanceId = instanceId, Revision = document.Revision, AppliedUtc = clock.GetUtcNow() }, ct);
            initialized = accepted;
            return accepted;
        }
        finally { reloadLock.Release(); }
    }

    public async Task<bool> ApplyAsync(PermissionRevisionChanged notice, CancellationToken ct)
    {
        if (notice.EventId == Guid.Empty || notice.Revision < 0 || !validInstance) return false;
        permissionService.RequireRevision(notice.Revision);
        await reloadLock.WaitAsync(ct);
        try
        {
            if (permissionService.Revision >= notice.Revision && permissionService.IsSynchronized)
            {
                var acknowledged = await acknowledger.AcknowledgeAsync(new PermissionRevisionAcknowledged
                { InstanceId = instanceId, Revision = permissionService.Revision, AppliedUtc = clock.GetUtcNow() }, ct);
                initialized = acknowledged;
                return acknowledged;
            }
            var document = await snapshots.LoadAsync(ct);
            if (document.Revision < notice.Revision) return false;
            permissionService.ReplaceSnapshot(document.ToSnapshot());
            var applied = await acknowledger.AcknowledgeAsync(new PermissionRevisionAcknowledged
            { InstanceId = instanceId, Revision = document.Revision, AppliedUtc = clock.GetUtcNow() }, ct);
            initialized = applied;
            return applied;
        }
        finally { reloadLock.Release(); }
    }
}
