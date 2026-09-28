using System.Collections.Frozen;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace LancerNexus.Cluster;

public enum PermissionPatternKind { Exact, Wildcard, Regex }

public sealed record PermissionContext(string? InstanceId = null, string? SystemId = null);
public sealed record PermissionGroupMembership(string GroupName, string? InstanceId = null, string? SystemId = null,
    DateTimeOffset? ExpiresUtc = null);
public sealed record PermissionNode(string Pattern, bool Allowed, PermissionPatternKind Kind = PermissionPatternKind.Wildcard,
    string? InstanceId = null, string? SystemId = null, DateTimeOffset? ExpiresUtc = null);
public sealed record PermissionGroup(string Name, string[] Inherits, int Rank, string? Ladder,
    string? Prefix, string? Suffix, PermissionNode[] Nodes);
public sealed record PermissionUser(Guid AccountId, PermissionGroupMembership[] Groups, PermissionNode[] Nodes);
public sealed record PermissionSnapshotDocument(long Revision, PermissionUser[] Users, PermissionGroup[] Groups)
{
    public PermissionSnapshot ToSnapshot() => new(Revision, Users, Groups);
    public static PermissionSnapshotDocument FromSnapshot(PermissionSnapshot snapshot) =>
        new(snapshot.Revision, snapshot.Users.Values.ToArray(), snapshot.Groups.Values.ToArray());
}

/// <summary>Immutable, validated permission state suitable for lock-free publication to game threads.</summary>
public sealed class PermissionSnapshot
{
    public long Revision { get; }
    public FrozenDictionary<Guid, PermissionUser> Users { get; }
    public FrozenDictionary<string, PermissionGroup> Groups { get; }

    public PermissionSnapshot(long revision, IEnumerable<PermissionUser> users, IEnumerable<PermissionGroup> groups)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision;
        Groups = groups.Select(g => g with { Inherits = g.Inherits.ToArray(), Nodes = g.Nodes.ToArray() })
            .ToFrozenDictionary(g => ValidateName(g.Name), StringComparer.OrdinalIgnoreCase);
        Users = users.Select(u => u with { Groups = u.Groups.ToArray(), Nodes = u.Nodes.ToArray() })
            .ToFrozenDictionary(u => u.AccountId);
        foreach (var user in Users.Values)
        {
            if (user.AccountId == Guid.Empty || user.Groups.Any(g => !Groups.ContainsKey(g.GroupName)))
                throw new ArgumentException("Permission user contains an invalid account or unknown group.", nameof(users));
            ValidateNodes(user.Nodes);
        }
        foreach (var group in Groups.Values)
        {
            if (group.Inherits.Any(g => !Groups.ContainsKey(g)) || group.Inherits.Distinct(StringComparer.OrdinalIgnoreCase).Count() != group.Inherits.Length)
                throw new ArgumentException($"Group '{group.Name}' contains an unknown or duplicate parent group.", nameof(groups));
            ValidateNodes(group.Nodes);
        }
        foreach (var name in Groups.Keys) Visit(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private void Visit(string name, HashSet<string> active, HashSet<string> visited)
    {
        if (visited.Contains(name)) return;
        if (!active.Add(name)) throw new ArgumentException("Permission group inheritance contains a cycle.");
        foreach (var parent in Groups[name].Inherits) Visit(parent, active, visited);
        active.Remove(name);
        visited.Add(name);
    }

    private static string ValidateName(string value) => value is { Length: > 0 and <= 64 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? value :
        throw new ArgumentException("Group names must be 1-64 ASCII letters, digits, '.', '_' or '-'.");

    private static void ValidateNodes(IEnumerable<PermissionNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Pattern is not { Length: > 0 and <= 256 } ||
                (node.InstanceId is not null && node.InstanceId.Length > 96) ||
                (node.SystemId is not null && node.SystemId.Length > 96) || !Enum.IsDefined(node.Kind))
                throw new ArgumentException("Permission node is invalid.");
            if (node.Kind == PermissionPatternKind.Regex)
                _ = CompileSafeRegex(node.Pattern);
        }
    }

    internal static Regex CompileSafeRegex(string pattern)
    {
        // .NET's non-backtracking engine rejects constructs outside the safe PEX-compatible subset.
        if (pattern.Length > 256 || pattern.Contains("(?", StringComparison.Ordinal) || pattern.Contains('\\'))
            throw new ArgumentException("Regex permission node uses an unsupported construct.");
        try { return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(25)); }
        catch (ArgumentException e) { throw new ArgumentException("Invalid or unsupported regex permission node.", e); }
    }
}

public interface IPermissionService
{
    long Revision { get; }
    bool IsSynchronized { get; }
    bool HasPermission(Guid accountId, string permission, PermissionContext? context = null, DateTimeOffset? now = null);
    IReadOnlyList<string> GetGroups(Guid accountId, PermissionContext? context = null);
    IReadOnlyList<string> GetGroupNames(Guid accountId, PermissionContext? context = null);
    string? GetPrefix(Guid accountId, PermissionContext? context = null);
    string? GetSuffix(Guid accountId, PermissionContext? context = null);
    int? GetRank(Guid accountId, string? ladder = null, PermissionContext? context = null);
}

/// <summary>Common evaluation API. Call ReplaceSnapshot after validating the complete SQL revision.</summary>
public sealed class PermissionService(PermissionSnapshot snapshot) : IPermissionService
{
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new(StringComparer.Ordinal);
    private const int RegexCacheLimit = 1024;
    private PermissionSnapshot current = snapshot;
    private long requiredRevision = snapshot.Revision;
    private int snapshotAvailable = 1;
    public long Revision => Volatile.Read(ref current).Revision;
    public bool IsSynchronized => IsSnapshotSynchronized(Volatile.Read(ref current));

    private bool IsSnapshotSynchronized(PermissionSnapshot snapshot) =>
        Volatile.Read(ref snapshotAvailable) == 1 && snapshot.Revision >= Interlocked.Read(ref requiredRevision);

    public void MarkUnsynchronized() => Interlocked.Exchange(ref snapshotAvailable, 0);

    public void RequireRevision(long revision)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        InterlockedExtensions.Max(ref requiredRevision, revision);
    }

    public void ReplaceSnapshot(PermissionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Revision < Interlocked.Read(ref requiredRevision) || snapshot.Revision < Revision)
            throw new InvalidOperationException("Permission snapshot is stale.");
        Interlocked.Exchange(ref current, snapshot);
        Interlocked.Exchange(ref snapshotAvailable, 1);
    }

    public bool HasPermission(Guid accountId, string permission, PermissionContext? context = null, DateTimeOffset? now = null)
    {
        var snapshot = Volatile.Read(ref current);
        if (!IsSnapshotSynchronized(snapshot) || accountId == Guid.Empty || permission.Length is 0 or > 256) return false;
        if (!snapshot.Users.TryGetValue(accountId, out var user)) return false;
        var instant = now ?? DateTimeOffset.UtcNow;
        foreach (var node in OrderedNodes(snapshot, user, context, instant))
        {
            if (MatchesContext(node, context, instant) && Matches(node, permission)) return node.Allowed;
        }
        return false;
    }

    public IReadOnlyList<string> GetGroups(Guid accountId, PermissionContext? context = null)
    {
        var snapshot = Volatile.Read(ref current);
        return IsSnapshotSynchronized(snapshot) && snapshot.Users.TryGetValue(accountId, out var user)
            ? ExpandGroups(snapshot, user, context, DateTimeOffset.UtcNow).Select(g => g.Name).ToArray() : [];
    }
    public IReadOnlyList<string> GetGroupNames(Guid accountId, PermissionContext? context = null) => GetGroups(accountId, context);
    public string? GetPrefix(Guid accountId, PermissionContext? context = null) => GetDecoration(accountId, context, true);
    public string? GetSuffix(Guid accountId, PermissionContext? context = null) => GetDecoration(accountId, context, false);
    public int? GetRank(Guid accountId, string? ladder = null, PermissionContext? context = null)
    {
        var snapshot = Volatile.Read(ref current);
        return IsSnapshotSynchronized(snapshot) && snapshot.Users.TryGetValue(accountId, out var user)
            ? ExpandGroups(snapshot, user, context, DateTimeOffset.UtcNow).Where(g => ladder is null || string.Equals(g.Ladder, ladder, StringComparison.OrdinalIgnoreCase))
                .Select(g => (int?)g.Rank).FirstOrDefault() : null;
    }

    private string? GetDecoration(Guid accountId, PermissionContext? context, bool prefix)
    {
        var snapshot = Volatile.Read(ref current);
        if (!IsSnapshotSynchronized(snapshot) || !snapshot.Users.TryGetValue(accountId, out var user)) return null;
        foreach (var group in ExpandGroups(snapshot, user, context, DateTimeOffset.UtcNow))
        {
            var value = prefix ? group.Prefix : group.Suffix;
            if (value is not null && value.Length <= 128) return value;
        }
        return null;
    }

    private IEnumerable<PermissionNode> OrderedNodes(PermissionSnapshot snapshot, PermissionUser user, PermissionContext? context, DateTimeOffset now)
    {
        foreach (var node in ForContext(user.Nodes, context)) yield return node;
        foreach (var group in ExpandGroups(snapshot, user, context, now)) foreach (var node in ForContext(group.Nodes, context)) yield return node;
        static IEnumerable<PermissionNode> ForContext(PermissionNode[] nodes, PermissionContext? context) =>
            nodes.Select((node, index) => (node, index)).OrderByDescending(x => Specificity(x.node, context))
                .ThenBy(x => x.index).Select(x => x.node);
    }

    private static IEnumerable<PermissionGroup> ExpandGroups(PermissionSnapshot snapshot, PermissionUser user, PermissionContext? context, DateTimeOffset now)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var membership in user.Groups.OrderByDescending(m => Specificity(m, context)))
            if ((!membership.ExpiresUtc.HasValue || membership.ExpiresUtc.Value > now) &&
                (membership.InstanceId is null || string.Equals(membership.InstanceId, context?.InstanceId, StringComparison.OrdinalIgnoreCase)) &&
                (membership.SystemId is null || string.Equals(membership.SystemId, context?.SystemId, StringComparison.OrdinalIgnoreCase)))
                foreach (var group in Walk(membership.GroupName)) yield return group;
        IEnumerable<PermissionGroup> Walk(string name)
        {
            if (!visited.Add(name)) yield break;
            var group = snapshot.Groups[name];
            yield return group;
            foreach (var parent in group.Inherits) foreach (var inherited in Walk(parent)) yield return inherited;
        }
    }

    private static bool MatchesContext(PermissionNode node, PermissionContext? context, DateTimeOffset now) =>
        (!node.ExpiresUtc.HasValue || node.ExpiresUtc.Value > now) &&
        (node.InstanceId is null || string.Equals(node.InstanceId, context?.InstanceId, StringComparison.OrdinalIgnoreCase)) &&
        (node.SystemId is null || string.Equals(node.SystemId, context?.SystemId, StringComparison.OrdinalIgnoreCase));

    private static bool Matches(PermissionNode node, string permission) => node.Kind switch
    {
        PermissionPatternKind.Exact => string.Equals(node.Pattern, permission, StringComparison.OrdinalIgnoreCase),
        PermissionPatternKind.Wildcard => WildcardMatches(node.Pattern, permission),
        PermissionPatternKind.Regex => GetRegex(node.Pattern).IsMatch(permission),
        _ => false
    };

    private static Regex GetRegex(string pattern) => RegexCache.Count < RegexCacheLimit
        ? RegexCache.GetOrAdd(pattern, PermissionSnapshot.CompileSafeRegex)
        : PermissionSnapshot.CompileSafeRegex(pattern);

    private static int Specificity(PermissionNode node, PermissionContext? context) =>
        (node.InstanceId is not null && string.Equals(node.InstanceId, context?.InstanceId, StringComparison.OrdinalIgnoreCase) ? 2 : 0) +
        (node.SystemId is not null && string.Equals(node.SystemId, context?.SystemId, StringComparison.OrdinalIgnoreCase) ? 1 : 0);

    private static int Specificity(PermissionGroupMembership membership, PermissionContext? context) =>
        (membership.InstanceId is not null && string.Equals(membership.InstanceId, context?.InstanceId, StringComparison.OrdinalIgnoreCase) ? 2 : 0) +
        (membership.SystemId is not null && string.Equals(membership.SystemId, context?.SystemId, StringComparison.OrdinalIgnoreCase) ? 1 : 0);

    private static bool WildcardMatches(string pattern, string value)
    {
        var p = pattern.Split('.'); var v = value.Split('.');
        var memo = new Dictionary<(int, int), bool>();
        return Match(0, 0);
        bool Match(int pi, int vi)
        {
            if (memo.TryGetValue((pi, vi), out var cached)) return cached;
            bool result;
            if (pi == p.Length) return vi == v.Length;
            if (p[pi] == "**")
            {
                result = false;
                for (var n = vi; n <= v.Length && !result; n++) result = Match(pi + 1, n);
                memo[(pi, vi)] = result;
                return result;
            }
            if (p[pi] == "*" && pi == p.Length - 1)
                result = vi < v.Length;
            else
                result = vi < v.Length && (p[pi] == "*" || string.Equals(p[pi], v[vi], StringComparison.OrdinalIgnoreCase)) && Match(pi + 1, vi + 1);
            memo[(pi, vi)] = result;
            return result;
        }
    }
}

internal static class InterlockedExtensions
{
    public static void Max(ref long location, long value)
    {
        var current = Interlocked.Read(ref location);
        while (current < value)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current) return;
            current = observed;
        }
    }
}
