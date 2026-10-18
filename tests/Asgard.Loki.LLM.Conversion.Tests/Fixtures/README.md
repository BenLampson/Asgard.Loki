# Protocol acceptance fixtures

`source/` contains independently authored native request, complete-response and full stream transcripts for the four protocols. They include Unicode text, tool definitions, a stable original call ID, arguments/results, zero/false/null values, complete done events, usage and representative response envelopes. These are offline protocol examples, not captured live calls.

`golden/` contains separately specified target wire structures for the same semantic case. Expected JSON was authored from the protocol shapes, without invoking the production converter. Snapshots compare all object values and array/event ordering; object property order is insignificant. IDs, tool arguments and terminal events are not normalized away. Tests never rewrite fixtures automatically.

Known response envelope metadata and executed-request echoes remain available in `OriginalData` / `Metadata.Conversion.UpstreamData`. They are not interpreted as target requests or translated continuation state. Content, unknown fields and nested semantic configuration remain subject to conversion validation. Snapshot tests supplement the field, identity, HTTP pipeline and adversarial lifecycle tests; they do not replace them or prove model acceptance.
