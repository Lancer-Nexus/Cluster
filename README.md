# Lancer Nexus Cluster

The Cluster repository contains shared abstractions and integration helpers for running LibreLancer as an optional multi-instance MMO platform.

## Responsibilities

- Cluster configuration and feature flags
- Optional server hooks and null implementations
- Shared lifecycle, lease and transfer abstractions
- Integration between existing Librelancer code and Lancer Nexus services
- Common health, capability and observability helpers

The default standalone path must remain usable when the cluster is disabled. Service-specific logic belongs in its dedicated repository.
