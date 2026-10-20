# Asgard.Loki.Video.MiniMax.Tests

Offline xUnit v3 tests for explicit native MiniMax video v1/v2 submission, matching task-query envelopes and v1 video-result file metadata. Fixtures use fictional prompts, credentials, identifiers and media; they do not establish live generation or account/model availability.

The [v1 query](https://platform.minimax.io/docs/api-reference/video-generation-query), [v2 create](https://platform.minimax.io/docs/api-reference/video-generation-v2-create), [v2 query](https://platform.minimax.io/docs/api-reference/video-generation-v2-query) and [result-file metadata](https://platform.minimax.io/docs/api-reference/file-management-retrieve) contracts retain distinct native shapes. Native task IDs, original statuses/errors, media structures, usage, unknown members and large identifiers remain intact. The result-file call is scoped to a successful video task; these tests do not add general file management or CDN downloading.
