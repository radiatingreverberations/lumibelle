# Lumibelle design system

Version 1 · 2026-09-16. Behavioral rules remain in [UX guidelines](ux-guidelines.md).

## Foundations

`src/Lumibelle.UI/wwwroot/design-tokens.css` owns the shared `--lumi-*` contract. `design-system.css` supplies shell, control, pane, and dialog treatments; feature and isolated styles consume the same tokens. Existing `--ink`, `--muted`, `--plum`, `--line`, and `--paper` aliases resolve to semantic tokens. Do not add fixed UI colors, radii or elevation shadows to feature styles.

| Role | Light | Dark |
| --- | --- | --- |
| Canvas | `#f7f6f7` | `#18161a` |
| Surface | `#ffffff` | `#211e23` |
| Subtle surface | `#f3f1f3` | `#2b262d` |
| Selected surface | `#f3ebf2` | `#392d3a` |
| Text | `#252127` | `#f2edf3` |
| Supporting text | `#716a73` | `#b6abb8` |
| Accent | `#7d4f78` | `#c99ac2` |
| Accent hover | `#684162` | `#dfb8d9` |
| Structural divider | `#e5e1e5` | `#403943` |
| Control boundary | `#8a818c` | `#86798a` |

Status colors have text, surface, and boundary roles. Review additions/removals have dedicated background and text tokens plus underline/strike-through, while applied edits use a distinct highlight. Media stage, overlay, text, and crop boundaries have fixed media tokens: never invert or recolor user images, video, or waveform content.

UI typography uses locally bundled Geist variable sans v1.7.2, with Segoe UI and system-font fallbacks. After trying Segoe UI, the user requested Geist again at larger sizes. The shared CSS tokens and MudBlazor theme now use 16px body text, 15px compact controls, 14px supporting text, 13px small labels, and 18px small headings. Feature styles consume these tokens instead of the previous 8–14px sizes; mobile layouts retain the larger scale. Screenplay blocks retain Courier at 16px. Fonts are rendered at their actual sizes without page scaling. Spacing follows 4/8/12/16/24/32/48px; radii are small 4px (badges, thumbnails, help triggers, compact toolbar and player buttons), control 6px, surface 8px (cards, panes, popovers), dialog 12px (dialogs and drawers), and pill for fully rounded chips and counts. Menus and popovers use `--lumi-shadow-popover`; dialogs and drawers use `--lumi-shadow-dialog`. Desktop controls target 32px, principal actions 36px, touch controls 44px. Icons require accessible names and visible keyboard focus. Information must not depend only on color or hover.

## Appearance

The app bar offers **System**, **Light**, and **Dark**. System is the default. The preference is browser/WebView-local (`lumibelle.appearance.v1`), outside project and provider settings. A synchronous head script applies it before stylesheet paint; `AppearanceState` bridges it to MudBlazor. OS changes affect System only; storage events synchronize tabs. Invalid or unavailable storage falls back to System, and an in-memory choice still works. Theme changes update variables and the theme provider without remounting editors or navigation sections.

Both web and desktop load the same script, locally bundled font, styles, brand assets, and components. The font and its SIL Open Font License are in `wwwroot/fonts`. Theme changes do not submit requests, change media, or save project drafts.

## Shell and workspaces

The 48px app bar contains the logo, project identity linking to Overview, global routes, appearance, and AI activity. The 40px project bar contains Overview / Script / Assets / Shots / Cut / Settings, save state and document actions, then pane visibility and Layout. Studio headings remain available to assistive technology and route focus. Section slots keep controls owned by their page while displaying them in the shared shell.

Project-bar horizontal padding is shared across Overview, Settings, and all studios at each breakpoint. Centered page content can extend the bar to the viewport edges with negative margins, but must not add a separate navigation inset.

Below 760px, global routes become a menu, project tabs stay on one scrollable row, and compact actions sit directly above the pane. The right pane becomes a drawer below 1100px; both sides use drawers below 760px. Pane sizing, keyboard resizing, focus restoration, drawer suspension for dialogs, tab state, and per-project scroll positions retain their existing behavior. The external workspace toolbar uses the same controller and disposal lifecycle.

All studios keep their sidebars visible whenever they fit inline. Pane widths remain resizable and Reset layout remains available; desktop hide controls and splitter-collapse shortcuts are removed. Ignore legacy collapsed preferences while retaining saved widths. Show each sidebar's opener only at the breakpoint where that sidebar becomes a drawer (tools below 1100px, library or outline below 760px).

Studios use adjoining surfaces and dividers. Script has a subdued canvas around the document, a quiet formatting toolbar, and word count in its footer. Assets retain full-image fitting, separate preview and selection, a whole-card selection ring, and actions visible on hover, keyboard focus, selection, or touch. Shots keeps its dedicated views and generation tools. Cut retains its viewer and timeline.

Selecting an act or scene in Script's outline aligns its heading 24px below the editor pane's top, with normal clamping at the document end so the opening text stays visible. Only the editor pane scrolls; ordinary caret scrolling while typing retains its existing behavior.

Assets opens directly into its Library, References, and Asset tools panes, with a quiet loading message inside the workspace. Keep the workspace mounted while the library and provider setup complete; show editing controls and apply pending detail links only when the content is ready. Loading must not briefly display the empty-library invitation.

Library category filters live in the filter menu beside Search. Show counts and the checked category in the menu; use a filled, highlighted filter icon and an accessible category name while filtering. Selecting All assets clears the category without clearing search. Escape closes the menu and restores its trigger before dismissing a containing mobile drawer.

The Assets image panel shows the current image workflow and an **Improve prompt** action. Manage the project's default image model in **Project Settings → Images**; avoid repeating that project-level picker in the creation panel.

Asset tools follow gallery selection without separate Create/Edit tabs. No selected reference shows Create; selecting a reference opens its tools. The header's **×** clears the selection, or selecting the same card again returns to Create and restores its draft. Each image retains its own edit draft. Previewing media, filtering the gallery and choosing multiple images do not change the active editor. Clearing the selection returns keyboard focus to the creation media picker.

In Create, the matching **×** clears draft content and returns focus to the image prompt or reel name. Model and output settings stay selected. Image prompts, tags, seed and reference inputs clear; reel authored text and references clear into a fresh saved draft. Existing media and captured recipes remain intact. The action has an accessible label and tooltip, and is disabled while its draft is busy. Errors appear beside the control.

**Create similar** loads a selected image or reel's saved creation recipe into Create without queueing generation. It is disabled with an explanation when no recipe is available. Image recipes retain the captured prompt, workflow, seed, available per-request settings, LoRAs, and original edit inputs/crops/regions; those LoRAs remain local to the draft. Reel recipes get a fresh identity and retain an available look on the current asset. Existing captured recipes remain unchanged. **Regenerate…** combines reel repetition and regeneration in one dialog. It reuses captured inputs, defaults to the source resolution, and offers one to four takes with new seeds. **Use original seed** sets the count to one and disables it; unchecking restores the chosen count. New reels stay with the selected reel's current asset. Image review's **Generate more…** uses the same dialog for one to four new takes in the original batch. Only the explicit **Queue N** action submits; Cancel or Escape returns to the underlying editor or review.

**Extract from script** carries a small `!` badge only when saved scenes are new or changed since their extraction review. Coverage counts and scene states live inside the extraction dialog. **Ignore extraction reminders for this project** hides that badge without marking any scene reviewed or disabling extraction. The choice is remembered per project in the browser's workspace preferences and can be reversed in the same dialog.

In the image details inspector, beside the preview, Use guidance sits below Name, Tags and Image look.

Assets identifies the selected asset with its cover, category, description and media counts. Character references default to collapsible look rows, with horizontal browsing within each row; **Group by look** returns to the flat gallery and is remembered per asset. Images and reels share their assigned look, while voices have a separate row. Filtering never reassigns media or changes a creation destination. The right pane names the active asset and operation, with the relevant look beside the image or reel tools. Keep reuse/clipboard actions in the asset footer and library deletion in **Asset library options**. Existing pane widths remain intact; resetting the layout opts into 280px Library and 320px Asset tools defaults.

Shots keeps **Shot / Takes** in the center and references in the sidebar. The sidebar's **Prompt** and **Generation settings** buttons open two tabs in one dialog. Named setups are global generation presets, shared across all shots and projects. Their selector and naming/duplication controls appear inside **Generation settings**. The sidebar header shows the setup's name with a help popup for its preset, resolution, take count and shared scope, and an edit menu that switches setups directly or opens **Generation settings…**. The selected preset follows the author between shots and projects; edits update its settings for future generations. Existing jobs and takes retain their captured settings. The prompt, AI direction, prompt history, and references belong to the shot and remain unchanged when switching or duplicating setups. Prompt edits are saved before changing setups or closing the dialog, and closing returns focus to the sidebar trigger. Takes can be filtered by setup and by changes to the shot's inputs; show the matching count and a clear-filters action when nothing matches. Keep incomplete comparisons separate from known current inputs.

Shots' first overhaul pass adds a thumbnail navigator, an editable title in the heading, compact scene/duration fields, and an inline take preview beside the creative direction when the center has at least 600px available. Narrower centers stack those surfaces. The preview starts with the selected production take, falling back to the latest master take, then any available take. Browsing previews does not select a production take; arriving results do not replace a preview already being viewed. Leaving the Shot tab pauses playback. Cast, sound/music, language versions, and source use disclosures. Bulk operations contains bulk generation and deletion.

The reference inspector uses a numbered contact sheet, image-slot/reel/audio counts, and Manage references. Detailed reference guidance, overrides, voice controls, and recovery remain in a disclosure. Missing-reference warnings stay visible beside generation. Reference management retains staged Apply/Cancel and identifies the target shot in its heading. Shots defaults to 280px navigation and 320px tools; saved custom widths remain in effect until Reset layout.

## Components

Shots' **Bulk operations** menu opens **Generate takes** and **Delete shots**. Both dialogs use `ShotPicker`: scene groups, take thumbnails, search, a scene filter, and explicit multi-selection. **Select all shown** adds eligible visible shots without clearing hidden selections; the hidden selection count remains visible. **Clear selection** clears all selected shots. Keep operation settings or consequences beside the picker on desktop and below it on narrow screens, with action buttons in a persistent footer. Unavailable generation shots show the reason. Changing deletion selection clears any production-take acknowledgement.

Use filled accent for the principal action, outlined for a secondary action, text/ghost for tertiary actions, a labelled icon button for compact utilities, and danger color for destructive actions. Navigation remains links, pane views remain tabs, and mode controls remain pressed/selected choices. Status text never impersonates a control.

AI assistance uses the shared `AiAssistIcon` ribbon-and-sparkle vector, derived from the brand. Assist entry buttons have a selected surface and accent border; the 24px icon uses accessible gold/periwinkle theme tokens and switches to the button foreground on filled primary actions. Reuse it for assistance triggers, composer submits, and AI activity. Preserve the queued clock, running spinner, attention indicator, labels, and accessible names.

Dialogs use small (520px), medium (800px), and large (1200px) classes, bounded by the viewport; media review dialogs may span the viewport. Every dialog shares one anatomy. The header holds an optional context line (the shot or asset concerned), the title, an optional one-line summary, and a close button. The body is padded 20px 24px (16px on phones) and separates sections with spacing or a divider rather than nested cards. The footer puts status and secondary tools on the left, then the dismiss action and the single primary action on the right; destructive primaries use the danger color. Say Cancel when closing discards staged changes, Close when nothing is pending, and Done when edits were saved as they were made. `InlineDialog`'s `Title`, `Context`, `Summary`, `Status` and `Actions` parameters, and `DialogHeader` for service dialogs, render this anatomy; the close button follows the dialog's own close handler and guards, and assistive technology names the dialog by its title and describes it by its summary. Keyboard users dismiss through Escape or the footer, because MudBlazor keeps the heading outside its focus trap. Shared dialog spacing lives in `app.css` at MudBlazor's selector specificity, so feature styles can still refine a layout. Titles and action footers remain stable while long bodies scroll. Specialized review layouts preserve media sizing and comparison controls. Asset details remain dialogs with existing staged drafts, save guards, and cancel behavior.

Write inline dialogs bound to a visibility flag as `InlineDialog`, not `MudDialog`. MudBlazor 9.9 reports each close through a late continuation that can hide a dialog reopened immediately afterwards; `InlineDialog` gives every opening a fresh instance. A dialog component mounted once per opening and closed through its own reference can keep `MudDialog`.

`/dev/design-system` is a development-only gallery of live production controls, palette tokens, deterministic media states, screenplay diffs, feedback, pane chrome, and three dialog sizes. It does not call AI providers. Production hosts show a not-found page. Use the appearance menu or gallery controls to inspect both palettes.

## Maintenance

1. Choose a semantic token and existing component before adding a new treatment.
2. Preserve editing and generation behavior when changing presentation.
3. Check light/dark, keyboard focus, touch, long labels, scrollable dialogs, and 390px layouts.
4. Run appearance/workspace tests after changing the shell, sections, or theme bridge. Review actual screenshots; token replacement alone does not prove readable media or status treatments.
