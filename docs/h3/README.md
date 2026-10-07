# H3 adapter evidence

[Upscaled preview](preview-upscaling.md) adds optional preview-size sampling with
native-size output in one job. It needs a supported learned upscaler and checkpoint.

Take refinement keeps each take's latents with stock ComfyUI nodes and refines a chosen take at a larger size. See [refinement setup and pinned evidence](refinement.md); the original workflows below remain unchanged.

Workflow and guide snapshots were vendored on 2026-09-05. The node implementation links below are pinned research references; their Python source is not bundled. Runtime uses `ComfyH3Video` and `H3Policy`, never these files or the network documentation. Community workflows are reference material, not executable application configuration.

- **official-ref2va.json**: https://raw.githubusercontent.com/Comfy-Org/workflow_templates/db9d5859d09c21a2d4101a1c18f64fc2f70e4fa4/templates/video_minimax_h3_r2v.json
  SHA-256: `14b30659a057547e02bdd4bbbdda3f8670aa6d7d81d1d8d99c4f9ad1e2eabc44`
- **nodes_minimax_h3.py**: https://raw.githubusercontent.com/Comfy-Org/ComfyUI/250b2e9551a7bc7a8ebb5beb07e0fecd2983e04a/comfy_extras/nodes_minimax_h3.py
  SHA-256: `33cb3cd5cec07ccdbacb244e0d1f141abc9959553b746e55756058afd3713a09`
- **nodes_video.py**: https://raw.githubusercontent.com/Comfy-Org/ComfyUI/250b2e9551a7bc7a8ebb5beb07e0fecd2983e04a/comfy_extras/nodes_video.py
  SHA-256: `b860df4be89e3969ad51cc85fed4c71b5e2d838ce2ed73aed79a22893fb09386`
- **ref2va-prompt-guide.md**: https://huggingface.co/MiniMaxAI/MiniMax-H3/raw/main/docs/VIDEO_PROMPT_WRITING_GUIDE_ref_en.md
  SHA-256: `1e574f356716ad55612247ffb7bbccbcdb484ad96599d63c7dca1af186b1fab7`
- **minimaxH3T2VI2VREF2VAdvanced_v20.json**: Author-supplied community workflow; local file paths removed
  SHA-256: `806adb4574eb10ad0d781bc47917cbea0583e952f38761983e5552791d456296`
- **minimaxSEEDHUNTERWorkflow_v122.json**: Author-supplied community workflow; local file paths removed
  SHA-256: `e8dde147214f21af27b2a9add40ab27f0d0ebc5d2f60b30d1b213ff8d50ce561`

One continuous camera take per shot; duration is snapped upward to 17k+5 frames, capped at 362 at 24 fps. Lossless animated WebP archival branches directly from decoded images before video compression. These are 8-bit RGB pixels, not tensor or latent archives. Selected continuity frames are exported as independent PNGs and explicitly assigned as Ref2VA references, without first-frame anchoring.

## Ref2V 8-step Turbo

Verified on 2026-09-06 against the [LightX2V release announcement](https://huggingface.co/lightx2v/Minimax-h3-Turbo/discussions/51) and the [published ComfyUI weights](https://huggingface.co/lightx2v/Minimax-h3-Turbo/blob/main/minimax_h3_ref2v_turbo_8step_v1.0_768p_comfyui_bf16.safetensors): 8 steps, Euler, video shift 12, audio shift 3, up to 768p. Lumibelle keeps the template's simple schedule and applies the LoRA at strength 1. The installed native `MiniMaxH3SigmaShift` contract is captured in `tests/Lumibelle.Tests/Fixtures/h3-shift-contract.json`; its patched model feeds both sampling branches. Standard and legacy 4-step graphs remain unchanged. These settings are bundled in `H3Policy`, with no runtime documentation lookup.

The `h3-single-take-v2` compiler uses explicit **Represents** bindings to connect pictures to a character. Pictures of the same linked character share one subject label; dialogue and voice references use that subject with the speaker number from speaking order. Capitalization does not create an implicit image binding. Renaming a speaker retains its identity, image links, and voice assignments.

In Assets, **Reference defaults for shots** holds shared identity guidance. Each image can add preservation guidance for its particular look or outfit. Shots inherit both, with **Customize for this shot** and **Reset to shared defaults** for exceptions. Existing shot notes remain overrides. Saved continuity frames inherit their state notes. These fields affect the H3 prompt; visual notes remain background asset context.

Every new batch stores resolved defaults and overrides with the exact prompt. Changing shared defaults affects future batches only; **One more take** uses its existing batch snapshot. A stale preview blocks submission until the author refreshes reference defaults and reviews the updated prompt. Saved takes and trashed take previews use captured guidance, never current asset defaults.
