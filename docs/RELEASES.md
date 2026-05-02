# Release Downloads

OpenClaw.NET's low-friction desktop path is the **desktop bundle** published on [GitHub Releases](https://github.com/clawdotnet/openclaw.net/releases/latest). It bundles:

- Companion
- the standard NativeAOT gateway
- the NativeAOT CLI

Users should start with the desktop bundle for their platform instead of GitHub Actions artifacts.

| Asset | Audience |
| --- | --- |
| [`openclaw-desktop-win-x64.zip`](https://github.com/clawdotnet/openclaw.net/releases/latest/download/openclaw-desktop-win-x64.zip) | Windows desktop users |
| [`openclaw-desktop-osx-arm64.zip`](https://github.com/clawdotnet/openclaw.net/releases/latest/download/openclaw-desktop-osx-arm64.zip) | Apple Silicon macOS desktop users |
| [`openclaw-desktop-linux-x64.zip`](https://github.com/clawdotnet/openclaw.net/releases/latest/download/openclaw-desktop-linux-x64.zip) | Linux desktop users |
| `openclaw-gateway-standard-aot-*.zip` | Operators who only want the native gateway |
| `openclaw-gateway-maf-enabled-aot-*.zip` | Operators who need optional `Runtime.Orchestrator=maf` |
| `openclaw-cli-aot-*.zip` | CLI-only installs and scripting |

Each archive has a matching `.sha256` checksum file.

## User First Run

1. Download the desktop bundle from the [latest GitHub Release](https://github.com/clawdotnet/openclaw.net/releases/latest).
2. Extract the archive.
3. Launch Companion from the `companion` folder.
4. Use the **Setup** tab to enter a provider, model, workspace, and provider key.
5. Click **Set Up and Start**.

Companion writes the normal local OpenClaw config, starts the bundled gateway on `127.0.0.1`, and connects to it. If a config already exists, Companion can auto-start the local gateway on launch.

## Current Signing State

The release workflow builds Windows and macOS archives and has optional signing/notarization hooks. Assets are unsigned unless the required repository secrets are configured.

- Windows archives are unsigned until Authenticode signing secrets are configured. Some users may see SmartScreen warnings.
- macOS archives are unsigned and not notarized until Apple Developer ID signing secrets are configured. Users may need to right-click Open or remove quarantine for local testing.

Release-grade onboarding requires these secrets:

| Secret | Used for |
| --- | --- |
| `WINDOWS_SIGNING_CERT_BASE64` | Base64-encoded Authenticode `.pfx` |
| `WINDOWS_SIGNING_CERT_PASSWORD` | Authenticode certificate password |
| `APPLE_DEVELOPER_ID_CERT_BASE64` | Base64-encoded Apple Developer ID `.p12` |
| `APPLE_DEVELOPER_ID_CERT_PASSWORD` | Apple certificate password |
| `APPLE_CODESIGN_IDENTITY` | Developer ID Application identity |
| `APPLE_ID` | Apple ID for notarization |
| `APPLE_TEAM_ID` | Apple team ID for notarization |
| `APPLE_APP_SPECIFIC_PASSWORD` | App-specific password for notarization |

Installer polish is still a follow-up: `.exe`/`.msix` for Windows and `.dmg` for macOS.

## Maintainer Flow

Tagged releases build and publish assets automatically:

```bash
git tag v0.1.0
git push origin v0.1.0
```

Maintainers can also run the `Release` workflow manually. Manual runs can create or update a draft release when a tag is supplied.

The workflow currently builds:

- `linux-x64` on `ubuntu-latest`
- `win-x64` on `windows-latest`
- `osx-arm64` on `macos-15`

The macOS runner label is intentionally ARM-native for the `osx-arm64` artifact. Add an Intel macOS row only if you want to support older Intel Macs and have a runner that can NativeAOT publish that RID reliably.

## CI Artifacts vs Releases

Actions artifacts are useful for validating a commit, but they are not a user-friendly distribution channel. They can expire, may require GitHub access, and are harder for users to find. GitHub Releases are the supported download surface for normal users.
