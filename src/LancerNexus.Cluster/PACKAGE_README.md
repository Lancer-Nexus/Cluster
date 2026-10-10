# LancerNexus.Cluster

Shared optional runtime integration for Lancer Nexus game instances. The package
contains permission evaluation and synchronization, lifecycle hooks, and service
adapters used by host applications.

Cluster features remain opt-in. A host must load and activate the authoritative
Gateway-backed permission snapshot before serving protected requests. Redis only
signals revisions; it does not carry permission data or credentials.

This package depends on the matching `LancerNexus.Protocol` package. Protocol
contracts and Cluster APIs must be pinned to compatible versions by each
consuming repository.
