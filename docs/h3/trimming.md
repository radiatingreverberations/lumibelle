# Trim takes

**Trim…** in Takes or take review saves a separate shortened version. Set the
first and last included frames with the sliders or the paused player, preview
the range, then choose **Save trimmed version**. The new version opens for review;
**Use this take** selects it for production. Existing cuts retain their clips.

Lossless source frames stay lossless in the new version's own WebP archive.
Its MP4 is encoded from those frames and the matching source audio. MP4-only
sources remain MP4-only. Frame numbering starts at zero in stored references,
so continuation from the new last frame refers to the shortened clip.

When saved latents exist, the full video/audio tensors are retained unchanged
alongside independent, hash-checked prepared inputs. They describe the full
source duration; trimming never slices tensors. **Refine** and **Rework** process
that full source and automatically reapply the saved trim. Both the full result
and its shortened child are saved, with fresh full latents for further refinement.
This takes the same GPU work as refining the full source. **Another version**
carries the range forward; a changed range starts a new batch with the previous
refinement's captured inputs.

If generation finishes but local trimming fails, retry output to finish the trim
without generating again. Stable result identities prevent duplicates across
retries and restarts. Trimming again composes the range within the full source.
Deleting an original does not remove a trimmed version's media or refinement
inputs. Project packages include the retained bundle; frame archive cleanup
removes only the lossless frames.

Regenerate and new dubbing masters use the full source for now. An imported take
can be refined using its retained bundle; replaying an old batch still requires
its queue record.

Validation includes real FFmpeg encoding and lossless pixel comparisons, file
store and queue recovery tests, project package round trips, and browser tests
with mock AI. Real GPU refinement remains a separate opt-in smoke test.
