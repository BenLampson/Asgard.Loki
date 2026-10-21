# MiniMax Image protocol fixtures

Independent native request/result vectors for image_generation, including original URL/Base64 carriers, metadata counts and base_resp. References: primary [text-to-image](https://platform.minimax.io/docs/api-reference/image-generation-t2i) and [image-to-image](https://platform.minimax.io/docs/api-reference/image-generation-i2i) references. Expected vectors are not produced by adapter code.

Tests retain original n/aspect_ratio/native response_format, explicit false watermark/optimizer, large integer seed, subject_reference URL spelling, unknown/null/zero fields, independent URL and Base64 arrays, empty filtered outcomes, counts and native business errors. No OpenAI size/format mapping, fake created timestamp, metadata arithmetic, media downloads, credentials or live model validation.
