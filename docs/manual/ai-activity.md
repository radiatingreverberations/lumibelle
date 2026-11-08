# AI activity

The header drawer keeps **Active**, **Needs attention** and **History** separate.

## Reading results

Results become read when a visible studio review, model-test dialog or response inspector successfully displays them. Opening the drawer alone does not mark results read, and reading an older result never clears a newer notification. **Mark all read** applies to the selected project, or all projects, across pages.

## Clearing history

**Clear history** hides finished requests, including failures and unapplied proposals. It keeps responses, review drafts, recovery data, media and project content. Running, waiting and remotely unconfirmed requests stay visible.

Use **Undo**, or **History → Show cleared → Restore to activity**, to bring entries back without creating unread notifications. Explicitly resuming a cleared request also returns it to Activity.

## Lost ComfyUI connections

When the connection to ComfyUI drops during a request, ComfyUI may still be working, so later ComfyUI requests wait. Lumibelle checks the request automatically, after 5 seconds at first and then at most a minute apart. Finished work is retrieved without rendering it again, and the queue continues on its own.

If ComfyUI stays unreachable, the queue keeps waiting. If ComfyUI keeps answering for two minutes without any record of the request, it is reported in **Needs attention** and the queue moves on. It is not repeated automatically; use **Retry when connected** to run its captured workflow again.

## Provider capacity

Expand a provider row for capacity information:

- **ComfyUI** reports available/total memory per device, never pooled across GPUs. Available memory may include reusable cached memory and does not predict whether a request will fit. These checks only read `/system_stats`; they do not clear caches or unload models.
- **OpenRouter** shows the configured key's spending allowance, cap, reset and usage. An uncapped key is distinct from account credits ([key limits](https://openrouter.ai/docs/api_reference/limits)).
- **Codex**, for ChatGPT sign-ins, displays the shared allowance reported by its CLI, for information only.

Visible expanded panels refresh ComfyUI every five seconds and hosted allowances no more than once per minute, with manual refresh available. Failed refreshes keep prior readings with a stale indicator. Changing the server or credential clears the cached display.

## Costs

Queued OpenRouter text requests and model tests show **Reported cost** and expandable token/model details when OpenRouter supplies them. Repeated totals are snapshots, not additional charges ([usage accounting](https://openrouter.ai/docs/cookbook/administration/usage-accounting)). Missing costs remain unreported, and historical requests are not backfilled. Observed usage survives parsing failures and recovery without another generation call.
