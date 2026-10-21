# Vertex Image protocol fixtures

Independent complete native transport/request/response/SSE vectors, describing native resources and the primary [cloud REST authentication](https://docs.cloud.google.com/docs/authentication/rest), [Express resource/authentication contract](https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/start/express-mode/overview), and native Generate Content image structures. Expected values are not produced by adapter code. All credentials/resources/media are fictional and never contacted.

Tests cover full project/location Bearer, full project API Key and short Express API Key, each JSON/SSE. Native image history/signatures, resource spelling, explicit quota, zero/false/null, large integers, safety/no-image results, omitted default index0, noncontiguous candidate5, cumulative usage and metadata after terminal candidates remain intact.

The [official Vertex Imagen model page](https://docs.cloud.google.com/vertex-ai/generative-ai/docs/models/imagen/4-0-generate), checked 2026-10-20, lists old Imagen3/4 endpoints discontinued 2026-06-30. These retired predict calls are excluded. No real Vertex image/model/key validation, IAM configuration, credential discovery or media download is claimed.
