# Ark video protocol tests

Offline xUnit v3 tests verify native request preservation, task identity, lifecycle boundaries, original API/task errors and resource ownership using independent JSON fixtures and substitute HTTP transports. Transport cases cover byte limits, strict JSON/UTF-8, declared length, late faults and cancellation through final disposal.

The tests use no real credentials, network, Redis or media downloads. They validate SDK protocol behavior; they do not certify real generation quality or asset preprocessing.
