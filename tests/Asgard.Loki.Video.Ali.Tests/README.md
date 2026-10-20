# Wan video protocol tests

Offline xUnit v3 tests use independent native fixtures and substitute HTTP transports. They verify complete request/result preservation, explicit service selection, original status/errors, task/reference identity, snapshots, byte limits, strict JSON/UTF-8 and final cancellation/resource disposal.

No real credentials, network, Redis, video generation or media download is used. Protocol fixtures validate SDK behavior; they do not certify model-specific parameter acceptance or output quality.
