#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
if [[ "$(uname -s)" != Darwin ]]; then echo 'Mac publishing requires macOS, Xcode and the maui-maccatalyst workload.' >&2; exit 1; fi
# Pass signing through an external MSBuild targets file. No credentials belong in the checkout.
args=(publish "$repo/src/Lumibelle.Desktop/Lumibelle.Desktop.csproj" -c Release -f net10.0-maccatalyst "-p:Version=${LUMIBELLE_VERSION:-0.1.0}" -p:CreatePackage=true)
if [[ -n "${LUMIBELLE_SIGNING_TARGETS:-}" ]]; then args+=("-p:CustomAfterMicrosoftCommonTargets=$LUMIBELLE_SIGNING_TARGETS"); else args+=(-p:EnableCodeSigning=false -p:EnablePackageSigning=false); fi
dotnet "${args[@]}"
echo 'Mac package produced for feasibility testing. Native smoke checks, signing and notarization are still required before distribution.'
