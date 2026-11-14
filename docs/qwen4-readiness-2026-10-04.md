# Qwen 4 readiness review — October 4, 2026

The launcher can inspect new GGUF architecture names and use the selected runtime's advertised speculative modes. Actual Qwen 4 27B support requires a released model, its matching GGUF conversion, and a llama.cpp runtime that implements that architecture. This review does not establish those future requirements or certify future inference.

## Two different n-gram features

Learned PLE n-gram tables are model weights. They can be much larger than the transformer's weights and may require CPU/SSD-backed row gathering. They are loaded by the runtime's architecture implementation. The Advanced N-gram dropdown controls speculative decoding from repeated token history. It does not toggle learned tables. MTP is a third mechanism and requires compatible prediction heads.

## Changes

- Runtime flags are checked by their exact advertised names and aliases. Removed arguments are rejected. Speculative types must occur in the `--spec-type` value list, so `ngram-map-k4v` cannot incorrectly establish `ngram-map-k` support.
- MTP draft model and draft length controls resolve supported aliases. Unknown flags still fail before allocation instead of being silently dropped.
- PLE detection uses architecture metadata and the established qwen4exp tensor layout. Learned-table parameter counts include later GGUF shards. Per-layer embeddings from unrelated model families do not imply PLE.
- Learned models request `--tensor-read-lazy on` when the selected engine advertises it. This is a request, not proof that the engine implements lazy reads for that particular architecture. Without the flag, runtime defaults apply and the compatibility panel marks SSD/lazy placement unverified.
- Learned-table and recurrent/hybrid models report approximate memory estimates. A future learned-table model cannot reuse the measured small GPU/CPU MoE fractions from the existing Flash-Next build.
- Auto Optimize retains settings for learned-table models without a validated preset. The measured 48-layer, 111,334,654,784-byte Flash-Next Q4 layout keeps its existing preset. Automatic selection of the existing Flash-Next MTP head is limited to that layout; a future 27B variant requires an explicitly matched head.
- Compatibility evidence distinguishes learned PLE tables, speculative n-grams, and MTP, and reports the detected learned-table parameter count.

## Release-day validation

1. Inspect the actual released GGUF and complete shard set. Confirm architecture, native context, template, learned-table tensors, and any MTP head against the publisher's model card. New metadata/tensor names may need an inspector update.
2. Use a runtime that implements this exact architecture. The upstream Updates feature replaces the standard engine; it does not upgrade the separately pinned TurboQuant/Flash-Next experimental runtime.
3. Start at a small context and one slot with conservative batches. Confirm loading succeeds and record real model, table, KV, recurrent-state, compute, RAM, and VRAM allocations. A successful `--help` probe alone cannot establish model compatibility.
4. Run text, reasoning, tool-call, and image requests as applicable. AnswerBox's screenshot workflow additionally requires the matching vision projector and runtime multimodal support.
5. Compare speculation Off against the chosen n-gram mode on representative prompts with identical context and sampling. Inspect draft acceptance and end-to-end latency; repetitive prompts can benefit more than short unrelated questions. Test MTP separately, then the combined pipeline if supported.
6. Verify stop, restart, client connection, and rollback before replacing a known-good setup.

## Capacity

The detected GPU is an RTX 5090 with approximately 31.8 GiB dedicated VRAM. A hypothetical 27-billion-parameter dense model at exactly four bits per parameter needs approximately 12.6 GiB for those parameters alone. Actual GGUF sizes include quantization scales, mixed-precision tensors, and other weights. Learned tables, KV cache, recurrent state, compute buffers, projector, and draft heads are additional. A 27B label alone cannot establish fit, and lazy SSD placement cannot be assumed from a CLI flag.

## Verification

The updated launcher is built with the repository's .NET SDK and locked win-x64 dependencies. All 89 final regression checks passed. The suite includes synthetic future-architecture split GGUF fixtures, exact flag/type matching, removed flags, learned-table estimates, preset isolation, real small-model CPU inference with ngram-map-k and ngram-mod, and existing server lifecycle/reconnect checks. Results are saved under `.tools/future-proof-final-results/test-results.json`; the isolated runnable build is under `.tools/future-proof-build` and uses the repository runtime directory. The four application artifacts in `publish` now match that tested build; the previous application artifacts are retained under `.tools/future-proof-previous-app`.

**Follow-up release verification:** Release 1.2.2 subsequently fixed the slider-row overlap, rebuilt the clean installer, and passed all 92 regression checks including the full UI scale matrix. See `docs/clean-release-validation-2026-10-04.md` for the current result. The following records the initial review only.

The optional UI render matrix reported `Layout overlap: IQ4_NL /`. The previous published executable reproduces the same failure under the same environment. Its baseline results are under `.tools/future-proof-baseline-render/test-results.json`. The render matrix is therefore not a passing acceptance check for either build; this review leaves that pre-existing layout issue unresolved.

The new application build does not certify Qwen 4 27B, replace the installed llama.cpp binaries, or rebuild the existing installer.

## Upstream references

- [llama.cpp speculative decoding](https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md)
- [Current qwen4exp architecture implementation](https://github.com/ggml-org/llama.cpp/blob/master/src/models/qwen4exp.cpp)

The upstream source implements PLE, recurrent state, and indexer behavior for qwen4exp. It is evidence for that implementation, not a specification for an unreleased 27B variant.

