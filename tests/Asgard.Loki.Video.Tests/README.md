# Asgard.Loki.Video.Tests

Offline public SDK tests cover independent native fixtures, input snapshots, complete-target task-reference persistence/reconstruction, changed server/model/credential/implementation, removed targets, wrong task IDs, no failover and borrowed adapter ownership.

xUnit v3 / Microsoft.Testing.Platform. No real provider credentials, network, timers or Redis. Native fixtures do not establish actual model acceptance or current service availability. Supplier transport/signature tests belong to their corresponding provider test projects.

Optional multipart/remix/content contracts are tested with in-memory adapters: ordered repeated fields, metadata snapshots, borrowed current-position streams, unsupported capability failures, original-target resolution, new-task/content association, final cancellation/drift and partial destination writes on failure. Historical Sora fixtures explicitly record the official service's shutdown; they do not claim an available OpenAI video service.
