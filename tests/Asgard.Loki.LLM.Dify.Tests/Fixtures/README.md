# Native Dify chat fixtures

Hand-authored native request/wire/blocking windows and three separately authored expected JSON/SSE transcripts. These are offline protocol examples, not recorded account/model results. Fixture credentials and hosts are synthetic; media URLs are never fetched.

Chatbot includes answer replacement and TTS after message_end. Agent keeps distinct thought/file/event/message/task IDs and the final full-answer message without concatenation. Chatflow keeps node execution IDs separate from workflow run IDs and retains message_end plus workflow_finished and the TTS tail. Nested business event names are not stream terminals. Exact numbers, false, null, original thinking markup, file references and unknown fields are intentional.

Primary contracts: [Send Chat Message](https://docs.dify.ai/en/api-reference/chat-messages/send-chat-message), [Streaming](https://docs.dify.ai/en/api-reference/guides/streaming), and the [native Chatflow pipeline](https://github.com/langgenius/dify/blob/e7c2c6fad5ac08364b51578da676a98469edd97f/api/core/app/apps/advanced_chat/generate_task_pipeline.py). The source confirms successful message_end before workflow_finished, native failed/paused outcomes, and possible TTS after generation terminal events. No standalone completion/workflow, upload, stop, reconnect or management operation is implied.
