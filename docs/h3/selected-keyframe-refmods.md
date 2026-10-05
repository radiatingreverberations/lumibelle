# Selected-keyframe RefMods: on-demand cache (Fantastic only)

This opt-in trial tests sparse character/environment references. It does not establish a quality, speed or memory improvement. Selected frames, their order/crops, and the fitted source PNGs are authoritative. A server-side `.safetensors` RefMod is a disposable optimization.

## Selecting references

In Shots, open **Manage references**; when authoring a reference reel, open **Manage reel references**. Then add/expand an asset reel, and select **Selected-keyframe RefMod · Experimental** under **Visuals**. Choose/order/crop **2–9 keyframes**, then **Apply changes**. There is no separate Prepare previews, Build, or Use step.

Apply performs local CPU image preparation only. It reads the exact selected frames, applies their crops, fits/pads them onto the reference canvas, hashes and retains the resulting PNGs, and saves a reference recipe with the pending shot or reel changes. New references use 640 × 640; previously accepted references retain their saved canvas. Nothing contacts ComfyUI, enqueues a build, runs a VAE, or runs video inference at this stage. The original shot or reel recipe is not changed until the normal parent save succeeds. A failed save retains the prepared selection for retry; Cancel never starts GPU work.

The UI shows the selected source angles and the automatic-preparation explanation, rather than an artifact-management panel. A source that is missing or changes while being read causes Apply to fail and keeps the draft available. There is no full-reel fallback. A single frame should be used as an ordinary Picture, not a sparse RefMod.

When switching from Pictures to a RefMod, review/recompose the prompt for the new **Video** label. Prompt Assist inspects the retained source PNGs; it does not need the remote latent to exist. Cache creation alone does not change prompt labels or require recomposition.

## Per-asset defaults

Under **Assets → Edit details → Default reel usage**, character and environment assets can choose **Keyframes**, **Selected-keyframe RefMod**, or **Full reel**. Assets without this optional preference retain the earlier **Keyframes** default.

Adding or replacing a reel snapshots that asset's current choice into the new reference. **Visuals** overrides it locally. **Use asset default** explicitly reapplies the current default to that one pending reference. Opening an existing reference, changing the asset default, composing a prompt, or generating a take never dynamically reinterprets a saved reference's mode. Reference-copy operations retain their captured modes.

These same defaults and local overrides apply when using a reel to generate another reference reel. **Keyframes** supplies numbered Pictures; **RefMod** and **Full reel** each supply a numbered Video. A recipe may use only Video references, with no Picture input. Voice is selected separately in the recipe; adding a visual reel never imports its soundtrack or changes the chosen voice.

Character, capture, voice and environment presets use the selected Picture/Video labels. Composition inspects retained RefMod source PNGs as previews of their Video reference, not as additional Pictures. Full reels contribute author-provided guidance to composition; the composer does not inspect their motion or audio.

Existing recipes keep their explicit modes. The historical `keyframeReels` JSON property now holds all three visual modes; its name and optional serialization are retained to preserve captured recipes and fingerprints.

Audio remains independent. Existing automatic character voice selection prefers the original recording/excerpt used to generate a reel; otherwise it can use the reel soundtrack. Explicit voice choices, including None, are preserved. RefMods remain visual-only and do not embed that audio.

## Server requirement

Install **ComfyUI-Fantastic-MiniMaxH3-PromptBuilder** on every ComfyUI server used for generation, restart it and refresh Video models in Lumibelle. No Lumibelle RefMod companion, shared folder or custom transfer route is required. The unrelated refinement companion remains optional for its existing features.

The upstream contract already used by this trial is Fantastic commit `23038f5050acdcbb8e938bfd7c87a18d2cc84aab`:

- `MiniMaxH3FantasticRefModCreate`: independent picture source entries, Full Reference mode.
- `MiniMaxH3RefModStack`: saved relative stems without `.safetensors`.
- `MiniMaxH3MediaLoader`: ordinary pictures, videos and separate audio.
- `MiniMaxH3FantasticRefModTextEncode`: conditioning output 0, reference map 1, sampling latent 2.

Fantastic 1.8.0 added `stack_pictures` to the text encode, which sets how many of a RefMod's pictures H3's text encoder sees; the DiT always receives every picture as latents. Lumibelle leaves it at the default, `every 4th`: the first picture and every fourth after it, so an 8-picture turnaround shows the encoder about the front and back views. An A/B on 2026-10-05 (three RefMods of 7–8 pictures, 608×352, HyperFlow 8 steps, same seed) found `all` about 48% slower per sampling step (26.9 s against 18.1 s, plus about 30 s more text encoding) with only small detail differences. Upstream reports that `all` helps most when several similar-looking characters bleed into each other; retest it on such a shot before changing the default.
- `GET /minimax_h3/refmods` for discovery and native `POST /upload/image` for source PNGs.

The required VAE filename is captured when accepting a new source recipe. Existing recipes do not silently switch VAE because connection settings change. Select the captured VAE, or remove/re-add the reference to capture a new recipe with the intended VAE. Missing plugins or an unreachable server are reported at generation, not treated as permission to substitute full-reel conditioning.

## First build and subsequent reuse

Before submitting a new video workflow, the existing `ComfyRefModCache` operation:

1. Verifies all accepted source PNGs against their captured hashes and canvas.
2. Checks the **actual execution server**, including a regeneration override.
3. Reuses a compatible cache or builds it from those PNGs, including on the very first generation.
4. Supplies the resolved filename to the video workflow without changing the captured shot, prompt, seeds or reference numbering.

The first source attachment can contain an advisory, deterministic cache address that has never been built. It is not a completed-build receipt or a queue job. A real cache build has its own journaled operation identity. Local source capture never creates a successful-job record or a server receipt to imply encoding has happened.

Preparation reports **Preparing reference cache…** and runs inside the video job's existing ComfyUI capacity reservation. It never enqueues a child job and waits for its own occupied slot. A shared 2–4-take request prepares the reference once before the shared video workflow. Subsequent requests, including “one more,” reuse it while available.

The generation graph contains Stack/Text Encode, not Create or a second Apply. The creator is only used in a separate cache-preparation workflow. There are no extra AI-assistance calls or new ComfyUI nodes.

## Source retention, retry and cancellation

Keep **`refmod-previews/<recipe-key>` in project backups**. These fitted PNGs are actual accepted inputs, not disposable thumbnails. Captured requests retain their recipes; changing frame order/crops creates new source identity. Guidance and voice choices are not baked into the visual latent.

Source acceptance validates all prepared buffers before publication, uses atomic file writes, and refuses to overwrite corrupt previously stored sources. A partial failed acceptance can safely fill missing files with the same hash-verified bytes on retry. A failed/cancelled local preparation may leave unreferenced source files, but does not change the saved shot or enqueue GPU work.

Completed-video output recovery continues to use its saved workflow/history. It does not check RefMod availability, recreate sources, upload images or repeat video inference. Recovery of an interrupted cache build observes its accepted/uncertain submission instead of blindly submitting it again. The existing coordinator continues never-submitted video work only after reconciliation establishes that it is safe.

Remote cache deletion before an unsubmitted generation causes a rebuild. Deletion after a cache build has been accepted, or between preflight and video submission, remains a recoverable reported failure rather than a reason to repeat an uncertain operation. A fresh generation can rebuild it. Cancelling a job does not authorize starting its unsubmitted video later.

## Compatibility and boundaries

Old accepted RefMods, saved build requests and build-history pages remain readable. Their underlying explicit-build API is retained for compatibility; Manage references no longer exposes it. An asset-management prebuild command can be added later using the same source-preparation and queue paths.

The optional `ReferenceAsset.DefaultReelVisuals` field needs no library-schema migration. Missing values are omitted when serialized and preserve legacy behavior. No global default silently converts existing references to RefMods.

There is still no remote-content hashing, syncing, cleanup service, compressed mode, embedded audio, or arbitrary external RefMod import. Same-shape edits to a cached latent cannot be detected: delete an unwanted cache rather than modifying it in place. Rebuildability is not a promise of byte-identical latents across environments. The separate Refine/Rework limitation for video-type conditioning is unchanged.

## Verification

```powershell
dotnet test tests/Lumibelle.Tests/Lumibelle.Tests.csproj -c Release --filter "FullyQualifiedName~RefModOnDemand|FullyQualifiedName~ReelUsage|FullyQualifiedName~RefModCache|FullyQualifiedName~RefModTrial|FullyQualifiedName~AutomaticReelVoice|FullyQualifiedName~UnifiedReferenceEditor"
```

The new tests cover CPU source capture without a server/build receipt, changed/corrupt sources, cancellation, first-generation cache creation, reuse, defaults and overrides, source-voice retention and Apply/save retry behavior. They use synthetic frame reads and simulated HTTP/GPU execution. Run the real .NET/Razor build and a real Fantastic/H3 generation before relying on this path.
