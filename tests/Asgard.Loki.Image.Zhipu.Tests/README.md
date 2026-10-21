# Zhipu v4 native Image vectors

Independent original request/result/error vectors describing the primary [v4 image generation API](https://docs.bigmodel.cn/api-reference/模型-api/图像生成). Expected values are authored separately from provider code. All target/model/key/user identifiers are fictional.

Preserves original quality/size/false watermark/user_id and unknown JSON. Native result vectors cover url/image_url and b64_json/b64_image carriers from the native response contract, content_filter, request_id, extendParam, usage and raw errors. Explicit zero/absent/null created values stay unchanged; no local timestamp or token estimate is inserted. Output URLs are never fetched or replaced by downloaded Base64. These are offline protocol and preservation vectors, not real model/entitlement/media/retirement validation. Async/newer model operations and v3 are outside this v4 complete-call test set.
