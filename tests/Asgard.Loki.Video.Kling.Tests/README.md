# Asgard.Loki.Video.Kling.Tests

Offline xUnit v3 tests for native Kling text2video/image2video submission and operation-specific task queries. Fixtures use fictional credentials, media and identifiers; they do not establish real model/account availability or generated-video validity.

The original JSON fixtures follow the official [text](https://kling.ai/document-api/api/video/2-5-turbo/text-to-video/legacy), [image](https://kling.ai/document-api/api/video/2-5-turbo/image-to-video/legacy) and [authentication](https://kling.ai/document-api/api/get-started/authentication) contracts. Requests retain `model_name`, original string durations, media, callback/external identifiers and unknown values. Results retain native code/status/errors, all video outputs, timestamps and unconverted metadata. The JWT vector is calculated independently with Python's standard library using fake AK/SK, fixed time, HS256, iss/exp/nbf and base64url without padding.
