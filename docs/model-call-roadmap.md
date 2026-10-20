# Model-call delivery checklist

Updated: 2026-10-20. This is the **only active delivery checklist**. The [archived audit/history](archive/model-call-roadmap-history.md) preserves prior evidence; its milestone queues are superseded and are not instructions to continue. Current local evidence: 6943 passing offline tests, including 2535 Conversion cases, and 28 packages. Protocol fixtures establish SDK behavior, not live model acceptance or universal New API parity.

The approved SDK has six independent capability families: **LLM, Audio, Video, Image, Embedding and Rerank**. Existing package boundaries in [architecture.md](architecture.md) remain authoritative. The user now requires **all actually implemented suppliers/model-call operations in pinned New API `058c8231b68c51ee02abee15befcd720fee9f4a2`**, including `plugins/tasks`, not only existing SDK suppliers or Volcengine. Reuse protocol-compatible implementations behind explicit supplier configuration; real differences stay supplier-local. Conversion remains limited to reliable mappings. Supplier features beyond this pinned source are not added automatically.

The user also explicitly requested **Volcengine asset-library integration**, a finite exception to the independent-file-management exclusion. Verify the exact official product, operations, authentication and inference references first. Keep it supplier-specific, without cross-capability dependencies, asset UI, generic storage or background synchronization.

The prior two-gap inventory below describes the SDK checkpoint, **not the complete coverage target**. The complete non-truncated pinned tree has 3071 entries, including 11 task plugins and 40 channel subdirectories. The unique source-backed supplier × capability × operation table is being completed from concrete adapter/driver hooks. Directory/model names alone do not establish support. Do not declare parity after only the first video supplier.

## Remaining SDK work at the current checkpoint

| Item | Verified gap | Next action / completion evidence |
| --- | --- | --- |
| LLM.OpenAI native Realtime | Delivered in existing LLM/OpenAI packages as an explicit independent protocol. Native Responses WebSocket remains separate. | Independent full duplex transcript and public SDK tests verify target/authentication, original events/statuses, cancellation and disposal. No reconnect, voice agent, tool executor or shared Audio workflow; live behavior unverified. |
| Video native calls | User selected **Volcengine Ark** on 2026-10-20. The SDK package remains a placeholder at this checkpoint. New API has a concrete pinned `plugins/tasks/doubao/plugin.js` driver, not just router declarations. | Add native submit and task-detail/status/result retrieval in Video plus Video.Volcengine. Return original task/result URLs; caller drives queries. No background poller or speculative video format conversion. This is the first video supplier, not the entire parity target. |

Current conversion changes are finished and locally verified/committed through their documented reliable subset. They do not authorize another round of multi-candidate, Bedrock/text, citation, cloud-endpoint or model-dialect expansion. Once the fixed-source supplier/operation table is delivered or explicitly deferred by the user and the finite checks pass, stop rather than expanding beyond the pinned version automatically. Remote push/release remains subject to the current hold.

## Implemented native-call inventory

“Delivered” means the concrete operation is implemented with offline contract/wire tests. Native JSON retains supported upstream extensions without creating a convenience property per field. It does not mean every supplier/model dialect, transport or account has been verified live. Links lead to the package's initialization, support matrix and limits.

| Family | Current supplier/package | Concrete accepted operations | Status |
| --- | --- | --- | --- |
| LLM | [OpenAI](../src/Asgard.Loki.LLM.OpenAI/README.md) | Chat/Responses JSON/SSE; explicit Responses compaction, Responses WebSocket and independent Realtime JSON WebSocket | Delivered for the package matrix |
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
| Video | [Video](../src/Asgard.Loki.Video/README.md) / selected Volcengine Ark | Native submit/status/result for selected Ark tasks API | In progress; supplier selected |
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

Do not do: fine-tuning, independent provider file management, administration, billing/pricing/quotas, key issuance, proxy hosting, automatic failover/retry, tool execution, RAG pipelines or background task hosting. Audio/image uploads are inference inputs. New API's inspected generic files/fine-tuning routes are unimplemented placeholders. All supplier-native inference operations actually implemented in the pinned source must be classified in the coverage table. Supplier documentation alone cannot expand this scope; non-inference discovery/administration stays excluded. Volcengine assets are the user's specific exception.

## Finite acceptance gate

- Cover the six approved capability families and the complete pinned-source supplier/operation table; the user's Volcengine asset request is a specific exception, not generic file management. Preserve existing module/provider dependency direction, complete-target identity and borrowed resource ownership.
- Finish the fixed-source native operation rows. Video starts with selected Volcengine Ark and must also cover other concretely implemented pinned-source suppliers; conversion remains reliable-only.
- For an implementation change, use independent full request/result/event samples and relevant offline public SDK tests. Field loss, wrong tool association or false terminal success fails acceptance. Live provider behavior stays a separate opt-in claim.
- Run one appropriate local build/test/package-boundary/metadata pass after code changes; repeat only for a new change or actual failure. Pure wording changes need document/link/format checks, not repeated whole-suite runs.
- Update the relevant READMEs/architecture/UML for public contract changes and commit in small local checkpoints. Do not push/merge main, dispatch GitHub Actions or release packages. The existing remote hold remains in effect; PR/release dual-platform gates are preserved for their authorized time.
- After this finite list is satisfied or remaining items are explicitly deferred, report the exact outcome and stop. Archived audit possibilities do not reopen the goal.
