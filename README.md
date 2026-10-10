# Lancer Nexus Cluster

The Cluster repository contains shared abstractions and integration helpers for running LibreLancer as an optional multi-instance MMO platform.

## Responsibilities

- Cluster configuration and feature flags
- Optional server hooks and null implementations
- Shared lifecycle, lease and transfer abstractions
- Integration between existing Librelancer code and Lancer Nexus services
- Common health, capability and observability helpers

The default standalone path must remain usable when the cluster is disabled. Service-specific logic belongs in its dedicated repository.

See [Administration's permission system guide](../Administration/docs/permission-system.md) for the full rights model and the current LLServer integration boundary.

## Shared permissions

`IPermissionService` is the common fail-closed evaluator for user nodes, ordered group membership and inheritance, context scopes, expiry, rank, prefix and suffix. `PermissionSyncCoordinator` activates a complete SQL-backed snapshot before ACKing its revision. `RedisPermissionRevisionListener` listens for invalidations and reloads the snapshot after every reconnect; `GatewayPermissionSyncClient` provides the authenticated snapshot/ACK adapter. Instantiate the coordinator during cluster server initialization and gate request-serving readiness on `IsReady`. Standalone mode keeps its local operator fallback and does not connect to Nexus permissions.

Redis is notification only. Keep the instance credential private and use HTTPS. Cluster is an independently versioned repository; service repositories consume a pinned Cluster package once one is published.

## Shared Protocol

The shared contracts are checked out in the `Protocol` submodule. Update it before local builds with:

```bash
git submodule update --init --remote --merge Protocol
```

CI recursively checks out the `Protocol` revision pinned by the Cluster repository before restoring and building.

CI also packs both `LancerNexus.Protocol` and `LancerNexus.Cluster` with one run-scoped prerelease version and uploads them together as a short-lived workflow artifact. The Cluster package records the matching Protocol package as a dependency. This makes the dependency boundary reviewable without publishing to a package registry; consumers must continue using their current integration until a deliberate package release is made.
