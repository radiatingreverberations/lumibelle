# Character voices across looks and reels

Saved voice recordings share the [media-details layout and action order](../asset-media-details.md) with images and reels.

A character has one optional **default voice recording**, shared across looks. A shot setup chooses a voice once per character, independently of its visual references. For example, save Elementalist's voice as Riley's default, then attach Riley Casual for appearance: the setup uses the saved Elementalist recording. Playing Riley Casual still plays its original soundtrack.

## Save and choose a default

On a character reel with audio, choose **Save voice…** from its menu or details footer. Preview the recording, name it, and choose a 1–15 second excerpt. The starting selection is the available recording capped at 15 seconds. **Make [character]'s default voice** starts unchecked. Save publishes the recording and optional default assignment together. Cancel publishes nothing; a failed save retains the draft for retry.

The soundtrack is extracted locally into an independent WAV. There is no speech isolation or AI analysis. The voice details show source-reel provenance, but moving or deleting the reel cannot break the recording. Voice cards and details offer **Make default voice / Clear default voice**; the selected recording carries a **Default voice** badge. Character details also contain a compact default selector.

Moving or trashing the default recording clears the assignment and shows a notice. Restoring a recording never replaces a newer default. Saved setups retain their chosen source, with the usual unavailable-recording repair flow if it is removed.

Fresh character reel recipes preselect the default under **Existing recording**, including its excerpt. With no default they start with **New voice**. **Silent** and another recording remain explicit choices. Existing drafts, variations, One more and retries keep their captured settings. **Use character default** explicitly changes a draft and marks its prompt text for checking.

## Choose voices in Manage references

**Shots → References → Manage references** contains a **Character voices** section. Each character has one row with a source summary, actual Audio number, preview, excerpt and speaker mapping. Choose **Character default**, a saved recording, an attached character reel's audio, or **None**. **Add character voice** also supports a character without visual references.

Adding another image or reel for the same character preserves the choice. A speaking character uses its default when available; otherwise **Choose voice** requires an explicit source or None before Apply. Characters without dialogue start with None. Character names and existing explicit speaker mappings can prefill the speaker when exactly one character fits. A speaker fits a character with the same name, or when every word of it is in the character's name. Screenplay extensions such as (V.O.), (O.S.) and (CONT'D) are not part of the name, so JAN (V.O.) is Jan Sherwood. Ambiguous names require a decision. Choosing a voice for a character set to None looks for their lines the same way. Choose **No dialogue · vocalizations** for nonverbal audio.

A warning names each dialogue speaker that fits a character but has no voice. Without one, the video model makes up a voice for those lines, so the character can sound different from shot to shot. Speakers that fit no character, such as an unnamed extra, are not flagged.

**Copy from shot…** keeps each copied voice with its speaker. A character who was silent in the source shot gets their default voice where they speak in this one, unless this shot already chose None for them. The copy dialog shows the same warning when lines are left without a voice.

Character reels show the resolved voice choice instead of a competing Use audio checkbox. Selecting a recording disables their conditioning soundtrack. Selecting a reel's audio enables exactly that source. Keyframe and audio-only modes use the selected excerpt; Full reel keeps the existing paired video/audio prefix and duration warning. Environment reels and unassigned legacy clips retain their soundtrack controls.

Apply saves visual and voice changes together, with one Undo step and Check prompt. Cancel leaves the saved setup unchanged. Save failure retains the draft; concurrent reference/default changes require review. Authored prompts are never rewritten automatically. The shared resolved-reference plan drives Audio numbers, summaries, limits, Compose context and generation ordering. Existing picture, video, audio and combined-duration limits still apply; excess inputs require correction.

## Compatibility and captured inputs

Character defaults, recording provenance and reel ownership are optional metadata. Version-one character voice choices save the source identity, excerpt, speaker mapping, source name and whether the source was chosen from a default. They do not follow future default changes dynamically.

Setups without this metadata retain their saved voice and soundtrack inputs. Merely opening Manage references does not normalize competing legacy inputs. Choosing a character voice explicitly replaces that character's legacy inputs; unavailable legacy recordings remain removable in the manager.

Generation uses the existing immutable audio-input capture, so retries retain exact bytes. Compose receives source names and speaker mappings, without copying sample dialogue or making an audio-analysis request. No migration or production reset is required.
