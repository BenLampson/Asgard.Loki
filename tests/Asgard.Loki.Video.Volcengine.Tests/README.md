# Ark video protocol tests

Offline xUnit v3 tests verify native request preservation, task identity, lifecycle boundaries, original API/task errors and resource ownership using independent JSON fixtures and substitute HTTP transports. Transport cases cover byte limits, strict JSON/UTF-8, declared length, late faults and cancellation through final disposal.

The tests use no real credentials, network, Redis or media downloads. They validate SDK protocol behavior; they do not certify real generation quality or asset preprocessing.

Asset cases cover the ten explicit native operations, original envelopes and opaque pagination, independent Python HMAC signature vectors (including a custom port and temporary token), preprocessing failure versus API error, request snapshots, resource identity, input boundaries and final cancellation/disposal.
