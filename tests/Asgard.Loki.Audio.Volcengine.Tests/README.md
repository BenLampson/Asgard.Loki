# Asgard.Loki.Audio.Volcengine.Tests

Local protocol tests for Volcengine V3 HTTP TTS, independent authentication, decoding, errors and completion.

xUnit v3 / Microsoft.Testing.Platform. Tests use no real provider credentials or external services.

## Native V1 protocol fixtures

`Fixtures/volcengine-v1-speech.json` contains independently authored complete native requests, HTTP results and binary packet vectors. The official [V1 WebSocket contract](https://docs.volcengine.com/docs/DoubaoVoice/Websocketinterface?lang=zh), [parameter/response contract](https://docs.volcengine.com/docs/DoubaoVoice/Parameterbasicdescription?lang=zh) and [V1 HTTP contract](https://docs.volcengine.com/docs/DoubaoVoice/HTTPone-timecompositenon-streaminginterface-V1notrecommended?lang=zh) still document these explicitly selected interfaces as not recommended; these fixtures do not establish live availability or model/account acceptance.

Binary vectors use Python standard-library struct/gzip/Base64 independently of production codecs. They cover the full JSON client header, big-endian lengths, no-sequence acknowledgement/audio, positive sequences, both documented negative final flags, frontend JSON and compressed packets, raw text errors and the maximum unsigned 32-bit error code. Original request model/voice/settings, omitted optional encoding/model, string/object extra parameters, zero/false/null, exact large integers and native metadata stay distinct from SDK transport records. App token markers and header tokens deliberately differ; all credentials, text/audio bytes and IDs are fictional. The HTTP fixture retains reqid/code/negative sequence/Base64 audio and unparsed frontend JSON strings. Structural vector checks are offline evidence, not a completed supplier implementation or live model test.
