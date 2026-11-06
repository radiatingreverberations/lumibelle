# Review

Open **Review** in a project to check it for continuity errors, and to approve fixes that an AI agent proposes. Review reads your project; it changes nothing until you approve a plan.

## Continuity checks

The checks run when you open the page. They read the project's saved shots, prompts, references, takes and cut. They need no model and no GPU, and they give the same answer every time. Use **Run checks again** after you change something.

Each finding is either a **Problem**, which the saved data shows to be wrong, or **Worth a look**, which may be fine. A finding about a shot links to it in Shots.

| Check | Reports |
| --- | --- |
| Missing references | A shot with no pictures or reels selected, a character in the shot with no picture, or a reference that is no longer in Assets |
| Voices not chosen | A dialogue speaker with no voice. Without one, the video model makes up a voice for the lines |
| Voices that differ between takes | A character whose selected takes were rendered with different voice recordings |
| Durations outside the refine range | A shot whose length on the frame grid is under 2 or over 15 seconds. A 14.5-second shot renders 362 frames, which is 15.083 seconds |
| Stage directions stored as dialogue | A dialogue line that is only a parenthetical or only emphasis, such as `*(quietly, to himself)*`. Composition puts every dialogue line in the prompt as speech |
| Prompt text | Leftover JSON, the word “placeholder”, and the prompt review's own warnings |
| Prompts to review | A missing prompt, or one not reviewed against its current references |
| Selected takes older than their inputs | A selected take rendered before the script, the prompt or the references changed |
| Shot frames from replaced takes | A shot frame or starting frame from a take that is not its shot's selected take, is in Trash, or no longer exists |
| Cut and selected takes | A clip that shows another take than the one selected for its shot, and selected shots missing from the cut |
| Shots without a selected take | A shot with no takes, or with takes and none selected |
| Render order | Shots that take frames from one another, so none can be rendered first |

**Render order** lists the shots that depend on one another. A shot that uses a frame from another shot's take can only be rendered after that shot.

**Measure sound** decodes each selected take and adds findings for a take with no soundtrack, a silent one, one much quieter than the rest of the film, and a shot with dialogue whose take is mostly silent. It takes a few seconds per shot.

The checks cannot see motion, and they cannot tell whose voice is heard or what is said. Watch each cut with sound as well.

## Plans

A plan is a list of findings and proposed steps, written by an AI agent you connected under [Agent access](#connect-an-agent). Writing a plan changes nothing in the project. The plan appears under **Plans** with the agent's name, and Review updates as the agent adds to it.

A step is one change or one render:

- change a shot's prompt and mark it reviewed, or mark an unchanged prompt reviewed;
- set a character's voice;
- add or remove a shot frame;
- replace or remove a reference picture;
- select a take;
- render one to four takes of a shot;
- point the cut at the selected takes.

A plan has no step that deletes anything.

### Review and approve

The line under the plan's title says what approving it does: how many changes it makes, how many takes it renders, and how many seconds of footage that is. For a prompt change, open **Compare prompts** to see the prompt when the step was written beside the proposed one.

Use **Reject step** on anything you do not want. A step that depended on a rejected one is skipped when the plan runs, and says why. Then choose **Approve plan**.

### What happens after approval

- Changes run at once, through the same saves as the studios.
- A shot's changes run before its render. Renders are ordinary requests in [AI activity](ai-activity.md).
- When a render finishes, its step shows the new takes under **Pick a take**. Play them, then choose **Use this take**. The agent may recommend one with a reason; **Accept recommendations** takes every recommended take. Nothing selects a take for you.
- A step that adds a frame from another shot's new take waits for that pick, then uses the first or last frame of the take you picked.
- The cut is pointed at the selected takes last.

A step is **Held** when its shot's prompt was edited after the step was written. Read the step, then choose **Apply to the shot as it is now**, or leave it and ask the agent to rewrite it.

**Stop plan** skips the steps that have not run. A render already in the queue continues; cancel it in AI activity if you do not want it.

If a shot is open in Shots with unsaved edits when a plan changes it, that tab cannot save over the plan's change and keeps your draft. Reload the shot there.

Plans are saved with the project. They are not included when you export a project package.

## Connect an agent

Agent access lets an AI agent that you run yourself work with this library through the Model Context Protocol (MCP). It is off until you turn it on, and it needs Lumibelle running in a browser; the desktop app has no address for an agent to connect to.

1. Open **AI settings → Connections** and find **Agent access**.
2. Turn on **Allow agent access from this computer**.
3. Choose **Show command** and run the command in a terminal. It adds Lumibelle to Claude Code. The command contains the access token, so treat it like a password.

Another MCP client needs the address shown there and an `Authorization: Bearer` header with the token. Only programs on this computer can connect. **Replace token…** makes a new token; clients set up with the old one are refused until you add Lumibelle to them again.

With access on, the agent can:

- read projects, shots, prompts, references, takes and the cut;
- look at take frames as contact sheets, and at reference pictures;
- run the continuity checks and measure sound;
- turn a time in the cut into a shot and a frame, for a note such as “3:54, the table disappears”;
- write plans.

It sees still frames only, and it hears nothing. Text in your project reaches the agent as it reads; it is told to treat that text as content to review, not as instructions.

The server also offers a `review` prompt that carries a review procedure.

### Approving in the agent's conversation

By default you approve plans and pick takes on the Review page. **Allow approving plans in the agent's conversation** adds two tools the agent can call after you agree in its conversation: one approves a submitted plan, the other selects the takes you named.

Lumibelle refuses an approval that does not repeat the plan's current revision and totals, so an approval never covers a plan that changed after the agent read it.

Claude Code asks you each time before it calls either tool, and none of its permission settings skips the question. Lumibelle cannot see that question, and another client may not ask it. Turn this on only for a client you trust to ask.

## Claude and other models

Lumibelle does not run Claude itself. To use Claude with a Claude plan, run Claude Code and connect it through Agent access. The model, its limits and its sign-in are Claude Code's own.
