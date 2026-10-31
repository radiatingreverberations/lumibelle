# Saved H3 generation settings

New H3 requests save a versioned snapshot of the reviewed prompt, dialogue,
references, audio mappings, output dimensions, sampler settings, model filenames
and ordered adapters. Normal shots, reels, regeneration, refinement, extensions
and motion-reference generation share this capture path.

Lumibelle uses the installed native and upstream ComfyUI nodes. It requires no
Lumibelle custom nodes, recorder endpoint, or full-checkpoint hashing. A saved
filename identifies the selected model; it does not certify its file contents or
the attention kernel actually executed. Runtime versions and actual sigma tensors
are not collected by the application.

## Preparation and retries

Preparation checks setup, prepares reference media, saves the owned inputs and
reviewed request, then queues generation. The dialog shows these stages, actual
input counts when available, and elapsed preparation time. Unknown-duration work
uses an indeterminate progress indicator.

Prepared media retain integrity hashes. Retries reuse the saved media and prompt,
without reading an edited or deleted source take or reel. Each execution keeps
its submitted graph and ordinary ComfyUI history, including failures. Changing
settings requires a newly captured request. Replacing model files or updating
ComfyUI can affect a retry's result; model contents are not audited.

Historical recipes and execution receipts remain readable. Requests captured with
the retired recorder cannot be replayed as native graphs silently; regenerate or
capture a new request. Historical completed outputs can still be retrieved using
their saved execution records.

## PTD

PTD uses upstream `PTDH3CompactLoader`, `PTDH3CompactSampler` and
`PTDH3CompactSigmas`, with `exact (PTD reference)` kernels. Allocation compilation
and fallback follow the upstream sampler's policy. There is no Lumibelle wrapper.

The dated promotion benchmark retains its original recorder-based evidence and
limitations. That experiment does not define current application dependencies.
