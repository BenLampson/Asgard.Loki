# Vertex Image protocol fixtures

Independent complete native transport/request/response/SSE vectors, authored before implementation from pinned `058c8231b68c51ee02abee15befcd720fee9f4a2` Vertex native resource routing and primary [cloud REST authentication](https://docs.cloud.google.com/docs/authentication/rest), [Express resource/authentication contract](https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/start/express-mode/overview), and native Generate Content image structures. Expected values are not produced by adapter code. All credentials/resources/media are fictional and never contacted.

Six transport cases cover full project/location Bearer, full project API Key and short Express API Key, each JSON/SSE. Native image history/signatures, resource spelling, explicit quota, zero/false/null, large integers, safety/no-image results, omitted default index0, noncontiguous candidate5, cumulative usage and metadata after terminal candidates remain intact.

The [official Vertex Imagen model page](https://docs.cloud.google.com/vertex-ai/generative-ai/docs/models/imagen/4-0-generate), checked 2026-10-20, lists old Imagen3/4 endpoints discontinued 2026-06-30. These retired predict calls are excluded. No real Vertex image/model/key validation, IAM configuration, credential discovery or media download is claimed.
