# AGENTS.md – Lancer Nexus Cluster

## Mission

Provide the smallest possible integration layer between LibreLancer and the external Lancer Nexus services.

## Rules

- Cluster behavior is disabled by default.
- Use interfaces, capability checks and null implementations at integration points.
- Do not fork or duplicate the full Librelancer simulation.
- Keep persistence, routing and event policy outside the game simulation layer.
- Make lifecycle hooks safe when services are unavailable.
- Preserve cancellation, threading and ownership rules of the host application.
- Keep cross-repository contracts in `Protocol`.
- Add integration tests for enabled and disabled modes.
