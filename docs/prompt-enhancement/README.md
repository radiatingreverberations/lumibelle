# Prompt enhancement profiles

The five supplied documents are preserved here unchanged as historical source material. Runtime profiles are embedded from `Services/AI/PromptProfiles`; downloads and internet access are not required at runtime. The Krea Edit TXT is the original instruction from its companion guide. See [current image guidance and sources](image-guidance-sources.md) for the **2026-10-25** refresh of Krea, Klein, MOSS, FLUX 3, Fibo, Qwen Image 2.1 and Z-Image.

Profiles include create/edit guides for Krea, Klein, MOSS, FLUX 3, Fibo, Qwen Image 2.1 and Codex Images, plus creation-only Z-Image. Single-image Enhance and bulk enhancement use the same selected-workflow guide. Klein Create is derived from BFL's official model-family guidance, rechecked 2026-10-25:
https://github.com/black-forest-labs/skills/blob/master/skills/flux-image-best-practices/rules/flux2-models.md

For OpenRouter, the application wrapper follows each guide’s native output: one prompt paragraph, or `NEEDS_INPUT:` / `NEEDS_SETUP:` followed by a concise question or requirement. The parser maps these to the structured application result `Prompt`, `NeedsInput`, or `NeedsSetup`; only `Prompt` can be applied. Complete JSON objects containing `kind` and `text` are also accepted. ComfyUI retains the JSON output envelope because its TextGenerate preview does not report a token stop reason. Plain-text responses require a normal provider stop; missing completion signals, truncated responses, and malformed JSON remain non-applicable. Actual application reference limits override general guide limits, without promising quality beyond the documented envelope. No guide is inserted into an image encoder's internal system template.

OpenRouter image capabilities come from `architecture.input_modalities`; images are sent as ordered private byte content, not public media URLs:
https://openrouter.ai/docs/guides/overview/multimodal/image-understanding

Text-only enhancement uses the selected supported text backend. Single-image reference inspection requires a backend and selected model with verified image input. Bulk enhancement uses captured text and reference-role notes without image inspection. Checking availability does not perform generation. Single-image enhancement preview and Undo are visit-local; bulk checkpoints preserve the original prompt, enhanced prompt and raw response. Generated images retain the submitted prompt in their provenance. Fibo's serialized VGL object is carried inside the application's text field when an outer response envelope is required.
