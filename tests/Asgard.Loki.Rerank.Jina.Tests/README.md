# Jina native rerank tests

The tests exercise the public Rerank SDK and direct Jina adapter calls. Seven independently authored final-wire fixtures cover native text, TextDoc/ImageDoc, image queries, nullable/suppressed options and listwise document vectors. Production mappings do not generate expected wire JSON.

Tests check original document indices/content/modality, top-n counts, required usage, exact integer semantics, native score range/precision, per-item optional vectors, extension fields, input immutability, request snapshots, error bodies, malformed/truncated/duplicate JSON, early SSE rejection, cancellation and borrowed resources. Returned unknown fields are preserved; a native TextDoc/ImageDoc does not promise to echo unknown input metadata.

All HTTP and media paths are in-memory fixtures; no content is downloaded and no Jina Key, network, Redis or real model is used. Passing tests establishes only the documented offline protocol scope, not relevance quality or live provider support.
