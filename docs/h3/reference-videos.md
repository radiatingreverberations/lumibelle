# Reference video experiment

For a source take's blocking and camera motion with fresh generated audio, use
the experimental [Refine from video workflow](motion-reference-promotion.md).

In **Shots → References → Manage video references**, import an MP4 or choose a take from this project. Lumibelle copies it into project storage. Removing or deleting the original take does not remove the attachment. Changes to names, descriptions, ordering, and soundtrack choices belong to the selected setup; duplicated setups can edit them independently.

Use complete clips lasting 2–15 seconds (including H3's 15-second frame-grid rounding), up to 250 MB each. Trim longer files beforehand. Keep at most three videos, three enabled audio references (video soundtracks plus voice recordings), 15 seconds of each modality, and twelve source files in total. Playback uses the original attachment. Generation prepares a bounded 24 fps MP4 and, when enabled, synchronized 32 kHz stereo WAV. The interface reports when H3 will use a shorter prefix to fit its frame grid or the target duration. No new frames or speech are generated during preparation.

**Use soundtrack** defaults on for clips with audio. Its Audio label appears alongside the Video label. Enabled video soundtracks receive Audio numbers first, followed by standalone voice recordings. A video without an enabled soundtrack has no Audio label. Optional speaker mapping associates the soundtrack with a dialogue speaker, without character/look assignments. Do not add the same soundtrack again as a standalone voice.

The assistant receives the editable **Clip description**, timing, and mappings; it does not inspect the clip or hear its audio. Selected still images continue to be inspected. Explicit directing instructions and manual prompt wording take precedence over the default description. Changing references preserves prompt text: check the displayed Video/Audio labels before generating. The next generation captures the displayed prompt, inputs, preparation and settings. Retries and One more take retain that capture.

## Make the initial character clip

Copy [the character-reference prompt](character-reference-prompt.txt) into a shot with:

- 1:1 aspect and requested duration **5 seconds** (generated duration **5.167 seconds**).
- Exactly three pictures in **face, body, outfit** order.
- `{{CHARACTER_NAME}}` replaced by the shot's exact dialogue speaker name.
- One English dialogue line, copied exactly:

  I thought we'd take the quiet road home before the rain starts again. All's well that ends well!

- No voice recordings or video references, so H3 originates the voice.

The shot also needs its normal source scene and coverage. If you choose another duration, replace both 5.167-second values with the displayed generated duration. The section colons, speaker definition, and exact dialogue are required by Lumibelle's current validator. The test fixture uses this same text file rather than another copy.

Generate and inspect the clip manually, then attach the chosen take to a different shot. That shot receives only the concise clip description; the original prompt and source dialogue are not automatically added to its AI context. In the new prompt, describe the appearance from `<Video 1>` and use the displayed `<Audio N>` for voice timbre with new target dialogue.

## Validation scope

Native ComfyUI `LoadVideo`, `GetVideoComponents`, `MiniMaxH3ReferenceToVideo.ref_videos`, and `ref_video_audios` are required only for setups containing videos. Both H3 VAEs remain connected. No video-analysis provider or additional companion node is required.

Tests use disposable media and mocked AI providers. They establish capture, numbering, preparation, review and submission behavior, not improved identity or voice consistency. Live output quality remains a manual experiment. Desktop playback uses the same resource resolver; macOS native compatibility retains the feasibility limitations documented in [shared hosts](../shared-hosts.md).
