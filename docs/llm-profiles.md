# LLM profiles

In **AI settings → Text models → Defaults & profiles → LLM profiles**, create a named configuration such as
“Fast”, “Careful”, or “Creative”. Several profiles may use the same provider and model;
they share its connection, credentials, model alias and ComfyUI verification. Profiles
appear in all text-model pickers without needing to star each configuration.

Choose a profile for one request, set it as the project default through the model picker,
or choose it in the global-default selector. Duplicate copies a profile's parameters into
a new identity; saving it does not modify the original. The unprofiled entry in the global
selector restores the original provider/settings defaults, even if that model is not starred. A model change in the editor clears
parameter overrides so settings for one model are not accidentally applied to another.

## Supported controls

| Backend | Controls |
| --- | --- |
| OpenRouter | Optional temperature (0–2), output limit, and either reasoning effort (including `none`) or a thinking-token budget. |
| Codex | The chosen model's advertised reasoning effort, or **Model default**. This does not inherit the global Codex effort. |
| ComfyUI | Optional temperature (0.01–2) and output limit (1–32,768). The existing text workflow still disables thinking. |

Blank OpenRouter fields are **omitted**, not filled with the application's ComfyUI
fallback settings. Blank ComfyUI fields use per-model settings, falling back to the
existing global ComfyUI defaults. Asset extraction retains its 0.2 temperature unless
an explicit profile temperature overrides it. Ordinary unprofiled Codex
selections keep their global-effort inheritance.

OpenRouter effort and token budget are mutually exclusive. The application permits
positive hosted output limits and thinking budgets up to 1,000,000 tokens, but that is an
application validation ceiling, **not** a promise that a model supports that many tokens.
Explicit output limits must exceed the thinking budget. Reported model capabilities and
output ceilings are checked; when metadata is absent, the provider remains authoritative.
A requested budget may be interpreted or adapted by the selected provider. Unsupported
Codex/ComfyUI controls are rejected rather than silently ignored.

## Snapshot semantics

A profile library entry is a template. Selecting it copies its ID, name, physical model
and explicit overrides into the selection. Global defaults, project defaults, queued
requests and retries keep that copy. Editing or deleting the library entry **does not
retroactively change them**. Select the updated profile and explicitly reset a default to
adopt changes. An older saved snapshot and an updated library version can coexist in the
picker, with their configurations shown separately. ComfyUI profiles also capture the
server address and cannot silently move to a different server.

Explicit overrides are frozen. Unset provider controls still mean “use provider default”;
these remote defaults are not a promise of reproducibility. ComfyUI fallback values are
captured when a job is queued, as before.

## Implementation

`TextModelPolicy.Key` remains physical provider/model/server identity. Aliases,
verification, starring and ComfyUI settings keep their existing keys.
`TextModelProfiles.ChoiceKey` adds profile identity and snapshot configuration for pickers;
`SameConfiguration` protects submission freshness and attribution.

`AiSettings.TextModelProfiles` stores the library. The additive nullable `TextDefault`
stores a global selection snapshot; legacy scalar model defaults remain the fallback.
`TextModelReference` gains nullable profile ID, temperature, output-limit and reasoning-
budget properties while preserving its constructor. Existing settings and project JSON
need no migration. Versions 1 and 2 retain their existing replay behavior. Requests with profiles use version 3
and send explicit overrides through `TextGenerationOptions`; ComfyUI's separate queued-workflow
path also uses the captured options. Older binaries reject version 3 instead of silently
dropping paid-request overrides. The existing `AiTextJobRequest.Profile` field remains
the **prompt-template identifier**, not the LLM-profile name.

OpenRouter uses the SDK's additional-properties API to send the unified
`reasoning.effort` or `reasoning.max_tokens` object, not an OpenAI-specific top-level field.
No provider transport, retry policy, authentication mechanism or dependency is replaced.

## Verification

Run the new profile tests and the existing regression suite:

```sh
dotnet test tests/Lumibelle.Tests/Lumibelle.Tests.csproj --filter 'FullyQualifiedName~Profile'
dotnet test tests/Lumibelle.Tests/Lumibelle.Tests.csproj
```

The added tests cover identity, legacy JSON, validation, model capability metadata,
provider-default omission, actual SDK HTTP serialization, all five existing queued text
operation fixtures, snapshot attribution, Codex model defaults, and profile editor/picker
interactions. HTTP and Codex tests use existing fakes; they do not make paid requests.
The complete solution's normal UI/browser verification should also be run before merging.

Upstream references checked during implementation:
- https://openrouter.ai/docs/guides/best-practices/reasoning-tokens
- https://github.com/openai/openai-dotnet/blob/main/examples/Chat/Example10_AdditionalProperties.cs
- https://github.com/dotnet/extensions/blob/main/src/Libraries/Microsoft.Extensions.AI.OpenAI/OpenAIChatClient.cs
