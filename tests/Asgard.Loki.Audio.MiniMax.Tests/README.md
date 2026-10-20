# Asgard.Loki.Audio.MiniMax.Tests

Independent fixtures for MiniMax native synchronous HTTP text-to-speech. [Chinese](https://platform.minimax.cn/docs/api-reference/speech-t2a-http) and [international](https://platform.minimax.io/docs/api-reference/speech-t2a-http) official contracts describe `POST /v1/t2a_v2`, Bearer authentication, native JSON success/business failures and hex/URL output.

`Fixtures/minimax-speech.json` was authored independently of production serializers. It contains three complete requests: explicit hex, explicit URL and omitted optional output/voice settings. Results retain status, trace ID, original subtitle URL, usage fields and unknown nested values, including zero/false/null and exact integers above JavaScript's safe range. Hex includes both letter cases; Python independently checks its decoded bytes. Signed URL spelling is retained unchanged; no URL is requested. Native IDs, credentials, voices, text and audio bytes are fictional.

These structural fixture checks do not prove an implemented adapter, real model/account acceptance or audio quality. No live-provider request or key is involved.
