# Cut studio

Open **Cut** in a project to assemble saved takes into one sequence. Cut editing requires no AI generation or media copying.

## Choose takes

Use **Choose takes** to assemble saved takes. Existing clips default to replacement in place; new shots are inserted in shot order without rearranging your existing clips. Choose **Add another clip** explicitly to repeat a shot.

For quick alternatives, select a timeline clip and use the **Take** dropdown or previous/next take buttons above the viewer. Swapping keeps its position and resets the trim to the replacement's full range; Undo restores the previous take and trim. Shot-level production selections remain intact.

## Timeline

The timeline fits the full cut initially; zoom and scroll inside it for precision. Click or drag the playhead to scrub, drag a selected clip's grip to reorder, and drag either edge to trim. The full-source strip below makes excluded footage available again. Both displayed boundary frames are included, and the original media stays intact.

Each completed drag is one Undo step; Escape cancels a drag.

| Key (trim handle focused) | Action |
| --- | --- |
| **Left / Right** | Move one frame |
| **Shift + Left / Right** | Move ten frames |
| **Home / End** | Move to its limit |

Numeric frame fields and **Move earlier / later** remain available without dragging.

## Playback

**Play** starts at the playhead; **Play from beginning** previews the whole sequence with original audio. Playback does not change your selected clip. Paused and trim previews use exact archived frames. The next clip is preloaded, with buffering and explicit retry if it cannot load. Playback pauses when editing or when the tab is hidden.

This is a browser preview, not a rendered export or a guarantee of sample-accurate joins.

## Saving and export

Each project saves one sequence in `cut.json`, with autosave, manual **Save**, Undo/Redo during the page visit, and protection against conflicting edits. A clip keeps its exact take until replaced. Restore unavailable takes through Trash and refresh, or remove the affected clips.

Use **Export MP4** to render the saved sequence with the configured FFmpeg executable.
