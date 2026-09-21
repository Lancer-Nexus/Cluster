# AGENTS.md – Lancer Nexus Cluster

## Mission

Provide the smallest possible integration layer between LibreLancer and the external Lancer Nexus services.

## MVP architecture baseline

- Gateway owns identity and the MySQL-backed character-persistence boundary; Coordinator owns placement and reservations; the active game instance owns live simulation.
- Cluster integration exposes safe optional hooks for the transfer lifecycle `Requested -> Reserved -> Prepared -> SourceFrozen -> TargetAccepted -> Committed -> SourceReleased`; before `Committed`, the source instance remains authoritative.
- Persistent character writes require the current MySQL `lease_version` fencing token so stale instances are rejected. Redis is only a distribution/cache layer.
- Shared contracts and capability negotiation are defined in `Protocol` before service integration is implemented.

## Rules

- Cluster behavior is disabled by default.
- Use interfaces, capability checks and null implementations at integration points.
- Do not fork or duplicate the full Librelancer simulation.
- Keep persistence, routing and event policy outside the game simulation layer.
- Make lifecycle hooks safe when services are unavailable.
- Preserve cancellation, threading and ownership rules of the host application.
- Keep cross-repository contracts in `Protocol`.
- Add integration tests for enabled and disabled modes.

## Working-model escalation

- If a task requires complex reasoning beyond the current model's reliable scope, ask the user whether switching to a stronger model is desired before continuing.
- Do not switch models silently or broaden the task because a stronger model may be useful.
