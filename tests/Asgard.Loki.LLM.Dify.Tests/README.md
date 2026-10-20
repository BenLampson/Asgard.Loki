# Dify native chat protocol tests

Offline xUnit v3 tests through DifyChatClient with a substitute HTTP handler and byte-fragmented, fault-injected streams. No real app Key, network, Redis or model access is required. The fixed-service client needs no model Session/cache.

The native JSON checkpoint has 60 passing cases. Independent whole caller/wire/blocking samples retain native input types, optional omission/empty/null, original answer/usage/retrieval/reasoning/unknown fields, exact numbers and distinct ID domains. Cases cover explicit app profile, native pause without resume, snapshots before factory creation, invalid aliases/Unicode/duplicates/Undefined before dispatch, raw HTTP/HTTP200 errors, budgets/declared length, actual method/URI and borrowed-factory versus owned-resource lifetime. EOF/read/disposal/final-cancellation faults cannot return a successful result.

Run after Release build: `dotnet test --project tests/Asgard.Loki.LLM.Dify.Tests/Asgard.Loki.LLM.Dify.Tests.csproj -c Release --no-build`. These establish local SDK protocol behavior, not live app/model acceptance or answer quality. Literal SSE fixtures are separate native examples, not yet a claim that a stream client was exercised at this JSON checkpoint.
