# Model-call delivery checklist

Updated: 2026-10-20. This is the **only active delivery checklist**. The [archived audit/history](archive/model-call-roadmap-history.md) preserves prior evidence; its milestone queues are superseded and are not instructions to continue. Current local evidence: 6909 passing offline tests, including 2535 Conversion cases, and 28 packages. Protocol fixtures establish SDK behavior, not live model acceptance or universal New API parity.

The approved SDK has six independent capability families: **LLM, Audio, Video, Image, Embedding and Rerank**. Existing package boundaries in [architecture.md](architecture.md) remain authoritative. This checklist fixes the current suppliers and concrete call operations; a new supplier, endpoint variant or conversion combination requires a separately justified caller need. Unknown/optional features are not completion blockers.

## Remaining SDK work at the current checkpoint

| Item | Verified gap | Next action / completion evidence |
| --- | --- | --- |
| LLM.OpenAI native Realtime | No Realtime client/adapter/target feature exists in current source. Native Responses WebSocket is a different operation. The pinned New API has a concrete Realtime relay implementation. | Add an explicit caller-driven native connection in existing LLM/OpenAI packages, with fixed-target/authentication, original JSON events, cancellation/disposal and offline duplex wire tests. No reconnect, voice agent, tool executor or shared Audio workflow. |
| Video native calls | `src/Asgard.Loki.Video` contains only its project/README, with no public API or supplier; its test project is empty. No video supplier has been selected. | Ask which supplier/native API is required, then add only submit/status/result calls and independent operation identity/tests in Video plus that supplier package. Caller drives status calls; no background polling host. Provider-specific implementation waits for this selection. |

Current conversion changes are finished through their documented reliable subset and require only their final local verification/commit. They do not authorize another round of multi-candidate, Bedrock/text, citation, cloud-endpoint or model-dialect expansion. Once the two concrete native gaps above are resolved or explicitly deferred by the user and the finite checks below pass, stop rather than discovering more work automatically. Remote push/release remains subject to the current hold.

## Fixed native-call inventory

“Delivered” means the concrete operation is implemented with offline contract/wire tests. Native JSON retains supported upstream extensions without creating a convenience property per field. It does not mean every supplier/model dialect, transport or account has been verified live. Links lead to the package's initialization, support matrix and limits.

| Family | Current supplier/package | Concrete accepted operations | Status |
| --- | --- | --- | --- |
| LLM | [OpenAI](../src/Asgard.Loki.LLM.OpenAI/README.md) | Chat/Responses JSON/SSE; explicit Responses compaction and Responses WebSocket | Delivered; native Realtime is the separate gap above |
| LLM | [Volcengine](../src/Asgard.Loki.LLM.Volcengine/README.md) | Ark Chat/Responses JSON/SSE | Delivered |
| LLM | [Anthropic](../src/Asgard.Loki.LLM.Anthropic/README.md) | Messages JSON/SSE, explicit native authentication/HTTP policy | Delivered |
| LLM | [Google](../src/Asgard.Loki.LLM.Google/README.md) | Gemini generateContent JSON/SSE | Delivered |
| LLM | [Ollama](../src/Asgard.Loki.LLM.Ollama/README.md) | Native Chat/generate JSON/NDJSON | Delivered |
| LLM | [Azure](../src/Asgard.Loki.LLM.Azure/README.md) | Explicit native Chat/Responses JSON/SSE with Key/borrowed external identity | Delivered |
| LLM | [GoogleVertex](../src/Asgard.Loki.LLM.GoogleVertex/README.md) | Standard Gemini/Claude, Express Gemini and MaaS Chat JSON/SSE | Delivered |
| LLM | [AWS](../src/Asgard.Loki.LLM.AWS/README.md) | Runtime Claude/Nova/Converse JSON/EventStream; explicit supported SigV4/Runtime Key modes; Mantle Messages SigV4 | Delivered for the package matrix |
| Audio | [Volcengine](../src/Asgard.Loki.Audio.Volcengine/README.md) | HTTP speech synthesis | Delivered |
| Audio | [OpenAI](../src/Asgard.Loki.Audio.OpenAI/README.md) | Native/convenience speech binary/SSE; multipart transcription/translation; transcription SSE | Delivered |
| Image | [OpenAI](../src/Asgard.Loki.Image.OpenAI/README.md) | Generations JSON/SSE; JSON/multipart edits, complete/SSE | Delivered |
| Video | [Video](../src/Asgard.Loki.Video/README.md) | Native submit/status/result for one explicitly selected supplier | Missing; supplier question pending |
| Embedding | [OpenAI](../src/Asgard.Loki.Embedding.OpenAI/README.md) | Native embedding inputs, dense vector results and documented compatible wire subset | Delivered |
| Embedding | [Google](../src/Asgard.Loki.Embedding.Google/README.md) | Native single/synchronous-batch embedding | Delivered |
| Embedding | [Ollama](../src/Asgard.Loki.Embedding.Ollama/README.md) | Native embed inputs/options and ordered vectors | Delivered |
| Embedding | [Jina](../src/Asgard.Loki.Embedding.Jina/README.md) | Documented text/media/fused inputs; Float/Base64 dense/per-token results | Delivered for the package matrix |
| Rerank | [Cohere](../src/Asgard.Loki.Rerank.Cohere/README.md) | Native v1/v2 reranking | Delivered |
| Rerank | [Jina](../src/Asgard.Loki.Rerank.Jina/README.md) | Native text/multimodal/listwise reranking | Delivered |
| Rerank | [Ali](../src/Asgard.Loki.Rerank.Ali/README.md) | Native DashScope nested text/media reranking | Delivered |
| Rerank | [SiliconFlow](../src/Asgard.Loki.Rerank.SiliconFlow/README.md) | Native scalar/text/image reranking | Delivered |

## Reliable conversion boundary

The [Conversion package matrix](../src/Asgard.Loki.LLM.Conversion/README.md) defines the delivered request/complete/stream pairs. Keep reliable, semantically explicit mappings with original source JSON, diagnostics, tool identity and required fields. An unrepresentable or unreliable case ends as documented unsupported/native-only behavior; it does **not** generate another milestone for every missing field or combination.

Existing common four-text mappings, explicit reasoning/Schema settings, complete Chat/Gemini alternatives, explicit bounded Chat/Gemini multi-candidate streams, the native Nova/Converse pair and the documented complete fixed-Converse subset stay under regression coverage. There is no requirement for universal lossless conversion. Preserve completed code/tests; do not rewrite/remove whole working modules to simplify the checklist. Stop further expansion of multi-candidate layouts, Bedrock/four-text combinations, hosted execution/citation dialects and cloud endpoint variants unless separately requested.

## Scope correction after the source recheck (2026-10-20)

This section preserves the existing public documentation anchor. Earlier plans mixed New API behavior, newer supplier-native features and SDK hardening. Test-case counts are evidence, not feature counts. A route/interface/model catalog alone is not a supported operation; require its concrete handler/adapter. The comparison reference remains pinned to New API `058c8231b68c51ee02abee15befcd720fee9f4a2`, not an assertion about all current upstream versions.

Do not do: fine-tuning, independent provider file management, administration, billing/pricing/quotas, key issuance, proxy hosting, automatic failover/retry, tool execution, RAG pipelines or background task hosting. Audio/image uploads are inference inputs. New API's inspected generic files/fine-tuning routes are unimplemented placeholders. Additional Jina encodings, supplier/endpoint variants, Gemini Live, legacy completion/moderation/discovery and standalone search/fetch are not added to this delivery's blocking queue without an explicit concrete need.

## Finite acceptance gate

- Keep only the six approved capability families and the concrete supplier/operation inventory above. Preserve existing module/provider dependency direction, complete-target identity and borrowed resource ownership.
- Finish the two explicit native-gap decisions/implementations above; unknown supplier selection is a question, not permission to invent more conversion work.
- For an implementation change, use independent full request/result/event samples and relevant offline public SDK tests. Field loss, wrong tool association or false terminal success fails acceptance. Live provider behavior stays a separate opt-in claim.
- Run one appropriate local build/test/package-boundary/metadata pass after code changes; repeat only for a new change or actual failure. Pure wording changes need document/link/format checks, not repeated whole-suite runs.
- Update the relevant READMEs/architecture/UML for public contract changes and commit in small local checkpoints. Do not push/merge main, dispatch GitHub Actions or release packages. The existing remote hold remains in effect; PR/release dual-platform gates are preserved for their authorized time.
- After this finite list is satisfied or remaining items are explicitly deferred, report the exact outcome and stop. Archived audit possibilities do not reopen the goal.
