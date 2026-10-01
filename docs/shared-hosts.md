# Shared application and hosts

Lumibelle has one implementation of its studios and production services. The browser edition remains Blazor Interactive Server. The desktop feasibility host runs the same Razor components locally in MAUI BlazorWebView; it does not start Kestrel or offer browser sessions.

| Project | Responsibility |
| --- | --- |
| `src/Lumibelle.Core` | Models, persistence, editing/application rules, providers, jobs, prompt profiles, media processing and resource resolution |
| `src/Lumibelle.UI` | Studios, components, layouts, UI coordination, HTML formatting, Tiptap sources and all shared frontend assets |
| `src/Lumibelle.Web` | HTML shell, Interactive Server, HTTP resource adapter and credential adapter |
| `src/Lumibelle.Desktop` | MAUI shell, native lifecycle, file dialogs, external links and WebView resource adapters; Windows and Mac Catalyst only |

Existing `lumibelle.*` namespaces, JSON formats and embedded resource names are retained. `AddLumibelleCore` registers production services once; `AddLumibelleUI` registers shared UI services. The isolated BrowserHost calls `AddLumibelleWeb` and `MapLumibelleWeb`, then explicitly overrides providers. Headless tools reference Core, not the web executable. Do not create another service provider inside a desktop adapter.

## Build and launch

The repository pins SDK **10.0.400**, .NET libraries **10.0.11**, and MAUI **10.0.20**. Install the SDK selected by `global.json`. Use its bundled workload manifests with `--skip-manifest-update`; update the SDK, workload baseline and packages together. The verified local workload report is `10.0.400-manifests.b0ae88bd`.

Web builds do not require MAUI workloads. The `CI` GitHub Actions workflow runs the tests on Windows and publishes unsigned Windows and Mac desktop packages as build artifacts for every pull request and push to `main`. To build, test and run locally:

```powershell
dotnet restore lumibelle.slnx
dotnet test lumibelle.slnx -c Release
dotnet run --project src/Lumibelle.Web --launch-profile http
```

The Development profile remains at `http://localhost:5183`. Both editions support manual work without configured AI models. FFmpeg/FFprobe and provider executables remain external dependencies; configure absolute executable paths when a desktop launch does not inherit your terminal's PATH.

Windows feasibility host:

```powershell
dotnet workload install maui-windows --skip-manifest-update
dotnet build lumibelle.desktop.slnx -c Debug
dotnet build src/Lumibelle.Desktop -t:Run -f net10.0-windows10.0.19041.0
```

Mac feasibility host (requires a compatible Xcode installation):

```bash
dotnet workload install maui-maccatalyst --skip-manifest-update
dotnet build lumibelle.desktop.slnx -c Debug
dotnet build src/Lumibelle.Desktop -t:Run -f net10.0-maccatalyst
```

No Mac is available for this implementation pass. See the release gates below before using that host for important work.

## Frontend ownership

`src/Lumibelle.UI/wwwroot/lumibelle.css` and `bootstrap.js` are the shared entry points. Hosts consume `_content/Lumibelle.UI/...`; C# module imports use `UiAssets.Module`. Relative imports inside modules remain within the RCL. The small web `App.razor` and desktop `index.html` contain only host bootstrapping, with `blazor.web.js` and `blazor.webview.js` respectively.

Ordinary .NET builds consume checked-in editor bundles. There is one npm workspace:

```powershell
npm ci
npm run build
node build/check-editor-bundles.mjs
```

The last command rebuilds in memory and checks byte equality, including license sidecars. It fails when checked-in output is stale, and CI runs it on every change.

## Data and credentials

`ApplicationPaths` supplies absolute data, project-library, settings, jobs, log, key and temporary paths. Installed editions default to the current user's local application-data `Lumibelle` directory. Project libraries default to its `Projects` subdirectory. No files are moved or reset automatically.

The Web Development configuration explicitly points back to the existing repository `App_Data`; it is excluded from publish output. Installed Web configuration uses `Lumibelle:DataDirectory` and the existing `Projects:RootDirectory` override. For example:

```powershell
dotnet src/Lumibelle.Web/bin/Release/net10.0/Lumibelle.Web.dll --Lumibelle:DataDirectory="D:\LumibelleData" --Projects:RootDirectory="D:\Films"
```

Desktop accepts the same two roots through `LUMIBELLE_DATA_DIRECTORY` and `LUMIBELLE_PROJECTS_DIRECTORY`. Set them explicitly to reuse a development library; close its Web process first. Do not point smoke tests at a live library.

`WorkspaceOwnership` holds OS file locks in both the data directory and project library before workers start. A second process fails clearly. Multiple browser tabs in one Web process continue to work. Do not delete lock files to bypass this check; the open file handle, not a PID or stale file, owns the lease.

Linked project folders also hold a project lock. Linking checks for a running library
in the folder's ancestors, and library startup checks whether any of its internal
projects is already linked elsewhere. These checks run while holding their respective
locks, so neither startup order permits two libraries to edit the same project.

Settings depend on `ISecretProtector`. Both host adapters retain the existing DataProtection application name `Lumibelle` and purpose `Lumibelle.AiCredentials.v1`; existing account key rings are the compatibility reader. Windows uses DataProtection's account protection. An unreadable saved key remains an error until explicitly replaced, and unrelated settings saves preserve its encrypted value. The `ApplicationPaths.Keys` location is reserved for an explicit future key-ring policy; changing it automatically would strand existing encrypted settings. Mac Keychain protection still needs completion and native validation; the current Mac feasibility adapter uses the existing account DataProtection key ring and must not be described as Keychain protected.

## Media and lifecycle

The shared `MediaResources` resolver owns logical `/media/...` and `/downloads/...` lookup, MIME types, missing-resource handling, conditional requests, GET/HEAD and single byte ranges. The HTTP adapter translates its response directly. Streams own their underlying files; ranged streams remain seekable within their slice, with no whole-video buffer.

Windows intercepts local WebView requests with a native deferral so resource lookup remains asynchronous before status/headers are supplied. The Mac prototype redirects logical resource URLs to a dedicated `lumibelle-media` scheme, whose native scheme handler streams 64 KiB chunks and cancels work in `StopUrlSchemeTask`. This avoids MAUI 10.0.20's whole-stream `NSData.FromStream` helper. WKWebView redirect, CORS, native playback and seeking remain unverified; there is no embedded HTTP fallback.

The Web host uses normal hosted-service startup/shutdown. Desktop explicitly starts/stops the same registered workers, in order, from its one container. `EditSessionRegistry` calls existing serialized editor save paths. Windows intercepts native close, saves first, leaves the window open after a save failure, and asks before quitting with queued/running jobs. Quitting stops local observation and retains existing recovery behavior; it does not assert remote cancellation. Desktop also provides **File → Quit safely**.

**Mac release gate:** MAUI's `Destroying` event cannot veto native close or Cmd+Q. The current Mac adapter does not yet provide that native veto; only **Quit safely** follows the save/confirmation path. Finish native termination interception before treating Mac as a supported release.

## Publish

```powershell
./build/publish-web.ps1
./build/publish-windows.ps1
# Self-contained folder for local testing without MSIX installation:
./build/publish-windows.ps1 -Unpackaged
```

```bash
bash build/publish-macos.sh
```

The Web ZIP contains a framework-dependent, RID-independent application; install the ASP.NET Core Runtime 10 and run `dotnet Lumibelle.Web.dll`. It binds to localhost:5183 by default. Windows builds an unsigned MSIX unless external signing settings are supplied. The pinned Windows App SDK requires `WindowsPackageType=MSIX`. Mac produces an unsigned feasibility package. Neither unsigned artifact is a finished public installer.

Both publish scripts take their version from `LUMIBELLE_VERSION` when it is set. To release, run the **Release** GitHub Actions workflow from `main` with a version such as `1.2.3`. It runs the CI tests and desktop builds at that version, then tags the commit `v1.2.3` and publishes a GitHub release with the self-contained Windows x64 ZIP alongside GitHub's source archives. The MSIX and Mac package stay out of releases until they can be signed with trusted certificates; CI still builds the Mac package to keep that target compiling.

Pass Windows signing settings in an external MSBuild targets file with `-SigningProperties`, or set `LUMIBELLE_SIGNING_TARGETS` on Mac. Keep publisher identities, certificate paths/passwords, signing identities and notarization credentials outside source. The Windows certificate subject must match the manifest publisher, or override the publisher for your release. Signing and notarization follow Microsoft's [Windows packaging](https://learn.microsoft.com/en-us/dotnet/maui/windows/deployment/publish-cli?view=net-maui-10.0) and [Mac distribution](https://learn.microsoft.com/en-us/dotnet/maui/mac-catalyst/deployment/publish-outside-app-store?view=net-maui-10.0) guidance.

## Validation

Architecture tests reject Core references to either host, UI, MAUI or ASP.NET assemblies, and reject UI references to native/server APIs. Shared tests cover path ownership, unchanged script reopening, legacy secret compatibility, close-registry failure behavior, byte ranges, frame/resource endpoints, cancellation, disposal and an 8 GiB logical stream without allocation or eager reading.

```powershell
dotnet test lumibelle.slnx -c Release
$env:LUMIBELLE_BROWSER_CONFIGURATION='Release'
npx playwright test script-polish.spec.js script-revisions.spec.js script-transport.spec.js unified-shots.spec.js text-assistance.spec.js cut.spec.js shared-host.spec.js
npx playwright test reference-reels.spec.js
```

bUnit component test classes carry `[Trait("Category", "Component")]`. CI runs them in a separate pass with `xUnit.ParallelizeTestCollections=false`, because they race their own background renders on a busy runner, and bUnit waits allow 30 seconds when `CI` is set. Tag new component test classes the same way.

The current shared studio suite uses mocked providers. The broad legacy browser suite also contains expectations from retired workflows (inline model settings, approval, separate Production); sampled failures reproduce on the pre-extraction commit. CI runs the focused suites above, each browser group on its own runner in parallel with the .NET tests; that does not establish that the full legacy suite passes.

`tests/native/desktop-smoke.mjs` attaches to a debug WebView via local CDP and requires an explicitly supplied disposable copied library/project. It edits and undoes Script/Prompt content, verifies stored data, loads references, plays/seeks an existing take, fetches a frame, and plays Cut. Copy the associated completed job records as well as project media: take review depends on captured batch history. Pause every provider in the fixture. Never generate extra live videos for this check.

The fixture library must contain `.native-smoke-fixture` with the literal text `disposable`. Set `LUMIBELLE_NATIVE_PROJECT`, `LUMIBELLE_NATIVE_LIBRARY`, and optionally `LUMIBELLE_NATIVE_CDP` (default `http://127.0.0.1:9228`). Enable the local diagnostic port only for a disposable smoke launch, using `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9228 --remote-debugging-address=127.0.0.1`. Normal desktop launches do not expose that port.

Windows native observations, packaged installation results and Mac observations must be reported separately. A compile, successful queue job, or unsigned package does not prove native compatibility.
