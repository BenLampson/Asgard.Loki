# Azure offline protocol tests

Run the xUnit v3 executable after building this project in Release, or use the repository's ordinary offline test entry point. No network, credentials, Azure account or Redis is needed.

The seven embedded JSON fixtures are authored independently of the provider and converter: caller Chat/Responses inputs, v1/dated final Chat wire bodies, final Responses wire body and both complete native results. Tests compare entire JSON trees, including unknown members, filters, encrypted reasoning, schema wrappers, original tool association IDs and exact large integers. Only deployment/stream/routing changes are intentional. Conversion integration separately uses a representable common-subset fixture; it does not claim conversion of every Azure-specific field.

Authentication tests use a mutable deterministic clock and borrowed token source. Lifecycle tests fragment response bytes, inject EOF/disposal failures and cancellations, and check client ownership. Session tests use `MemoryDistributedCache` and the explicit test coordinator. Token refresh is simulated; the suite is protocol verification, not live cloud authentication/model verification. SSE is pending and is not counted as covered by complete JSON tests.
