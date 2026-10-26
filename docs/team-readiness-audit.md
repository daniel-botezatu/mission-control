# Team readiness audit — 26 October 2026

**Verdict: not yet ready for a general Mac team rollout.** The source is now suitable for a controlled Mac build/pilot. The previous release had concrete portability/setup defects; the fixes below are prepared as 0.4.11. No Mac was available in this session, so a native Mac build, signing/notarization, Finder/login behavior and real-account integration remain acceptance requirements. The existing installed Windows desktop 0.4.8 / plugin 0.4.10 were not upgraded or reconfigured during this audit.

**Source package:** use `MissionControl-0.4.14-source.zip`. It supersedes the 0.4.11 to 0.4.13 packages. 0.4.14 adds the Open loops panel (branches read from Git metadata files, so the same code runs on macOS), starts each session's GitHub server only on its first GitHub call, keeps tool facts and interrupted updates in saved replies, filters secret-looking values from Slack results in Claude Code, runs Claude recaps in a fresh coordinator, and records why the desktop started and stopped. 0.4.13 stops reporting completely read files that the host later deleted as missing history. Since 0.4.11-r2 the source also adds automatic daily retention by message date, macOS quit handling for Cmd+Q, Dock > Quit and logout, a monochrome menu-bar icon, safer login items (refused from Downloads or a disk image; shown off when disabled in System Settings), protection for the memory server the host packages use during storage cleanup, and documentation without personal installation history. The macOS changes are covered on Windows only by unit tests and code review; they remain part of the Mac pilot checklist.

## Findings and fixes

| Finding | Resolution / remaining limit |
|---|---|
| Release archives contained generated plugins with the builder's absolute Windows paths | Removed builder-generated plugin ZIPs. Recipients generate their own packages with the new portable `integrations/setup.py`. |
| Packaged integration resources omitted the recap coordinator and reader files required by `manage.py` | Complete setup resources are enumerated in the tested build configuration. |
| Mac MCP was packaged as a resource while the dashboard expected it beside the backend | All three executables are now Tauri sidecars in `Contents/MacOS`; setup documentation/resources resolve under `Contents/Resources`. |
| Package preflight copied away Unix execute permissions and hardcoded an `.exe` suffix | Preflight preserves file mode and uses the actual executable name. Native Unix execution test is supplied and explicitly skipped on Windows. |
| GitHub launcher/configurator expected Windows executable names and rejected ordinary Homebrew symlink paths | Platform-native names are supported; configuration pins the explicitly resolved binary. Windows replacement protection is not claimed on Unix. Protect pinned executable directories from other writers. |
| Finder's PATH can omit Homebrew, leaving the PR overview unavailable | Apple Silicon/Intel Homebrew discovery plus a saved, validated custom GitHub CLI path in Settings. |
| Fresh installs assumed UTC; data overrides were inconsistent across processes | Fresh packages use the recipient's local timezone, with an explicit override. Saved choices remain. Shell/backend/MCP/setup share `MISSION_CONTROL_DATA_DIR` semantics. |
| Source ZIP enumeration could include editor/user state outside intended source roots | Replaced with an explicit source-root/file allowlist; excludes artifacts, caches, credentials/config directories, database files, environment files, certificates and symlink/junction entries. |
| Copied CLI commands used unsafe double quotes for Unix paths containing shell expansion | Platform-appropriate literal argument quoting, tested with spaces, apostrophes, `$()` and backticks. |
| New Unix database directories relied on the caller's umask | New workspace/database directories request owner-only permissions. Existing user-selected parents are preserved. Native permission verification remains a Mac test. |
| Codex can insert native marketplace/plugin tables before our trailing ownership marker, breaking repeat setup | First register native plugin entries, then apply the owned MCP block. Recover appended tables only when the original owned prefix matches its exact saved hash; edited values still fail. Fresh install and repeat passed with real native CLIs in isolated Windows profiles. |
| No Mac runtime signing/minimum-OS configuration | Added .NET apphost entitlements and macOS 15 minimum to the Mac build configuration. Organisation signing credentials remain external. Must validate the signed/notarized app on Mac. |

## Personal-PC assumptions

The production-source scan found no literal developer username/home path, Acme repository allowlist, HelpDesk project rule or personal credential. Existing user-specific project roots, repositories, source choices and tokens are local configuration, not compiled defaults. Amsterdam appears as a timezone example and in tests/historical documentation; it is not the default for a teammate. Demo history and unit-test paths are synthetic fixtures and are not personal configuration.

Adjustable settings and onboarding commands are in [the Mac setup guide](mac-team-setup.md). Each teammate must independently approve projects and choose private Slack access. The source package excludes the current user's databases, original conversation files, generated host profiles and evaluation transcripts.

## Verification performed here

Windows execution, not Mac execution:

| Check | Result |
|---|---|
| Full .NET suite, refreshed source extraction | 202 passed, 2 skipped: 145 core, 25 backend, 16 MCP, 16 provider tests. Skips are the existing privileged symlink fixture and new Unix permissions test. The added compressed-rollout coverage is included. |
| Python integration/setup tests | 29 passed, 1 Unix executable-mode test skipped. Covers preservation, collisions, explicit provider preferences, defaults, source archive contents and the native Codex append behavior. |
| Build configuration tests | 4 passed, including Apple Silicon/Intel plans, complete resources, unsupported-architecture rejection and absence of builder-local plugin packaging. |
| Rust lifecycle/pipe suite | 6 passed, 1 ignored subprocess fixture. |
| UI typecheck/build and synthetic browser flows | Passed; 18 browser checks, no runtime errors. |
| Complete Windows release build | Passed with all three self-contained sidecars and setup resources, `--no-bundle`. No new installer or Mac DMG was produced. |
| Native recipient setup | Fresh install and immediate repeat both passed in isolated Claude/Codex profiles, each loading cached version 0.4.11. Real host configuration fingerprints unchanged; no model turns or real history used. |
| Dependency advisories | npm production dependencies: zero reported advisories. NuGet including transitive packages across ten projects: zero reported advisories. This is not proof of absence of vulnerabilities; Rust advisories and a dedicated security assessment were not completed. |

The Windows credential-store test needed a normal logon session and passed there; its initial sandbox failure is not concealed. Claude's initial sandbox path/trust rejection also passed in normal-session isolated profiles. Neither result proves Mac Keychain or Mac trust behavior. Evidence is under local `artifacts/mac-readiness/`, `artifacts/mac-audit-*` and `artifacts/memory-ui-flows/report.json`; private/local artifacts are intentionally excluded from the shareable source archive.

The refreshed package was extracted into an empty directory and restored, compiled and tested there. Python and build-configuration tests were also rerun from that extraction. Its executable source, tests, dependencies and plugin instructions match the final archive by SHA-256; only this report, the checklist and setup-guide clarifications changed after testing. Rust, UI, native-host and release-build results above remain the earlier audit runs, not new runs of the refreshed collector. The installed app, data and host settings were not updated.

## Remaining rollout blockers and product limits

1. Build and execute on actual Apple Silicon and Intel Macs, run `scripts/verify-macos.py`, then complete the UI/login/update/account checklist in the setup guide. The new script intentionally refuses to certify a Windows run as a Mac test.
2. Produce a signed/notarized distribution under the team's Apple Developer identity. See [Tauri signing](https://v2.tauri.app/distribute/sign/macos/) and [Microsoft apphost entitlements](https://learn.microsoft.com/en-us/dotnet/core/install/macos-notarization-issues). No signing keys or Apple account are configured in this project.
3. Recap completeness remains below the old comparison on the full 24-item rubric. Earlier results were 13 full / 9 partial / 2 missing for the installed workflow versus 16 / 7 / 1 for the old recap. This audit changes portability, not that behavior; no new paid recap evaluation was run.
4. Direct Slack responses can contain credentials and bypass local-history redaction. The gateway was explicitly deferred. New users default to asking before private searches; consent and host transcript retention need a team decision.
5. MCP history tools are read-only; the parent AI host retains its other granted tools. A skill is not a global read-only sandbox. No shared history service, SSO, team RBAC or central retention management exists.
6. Local SQLite is not encrypted by Mission Control. New Unix directory permissions help local isolation; device encryption, backups and source-transcript retention remain separate. Do not share a live database through cloud/network synchronization.
7. The optional PR overview targets the active github.com CLI account; Enterprise/multi-account aggregation is unsupported. Symlink project aliases and unapproved roots remain deliberately excluded, and missing upstream history cannot be recovered.
8. Use `scripts/build-tauri.mjs` for packaging. The bare Tauri command does not apply the complete sidecar/resource configuration. The current Mac entitlements are bundle-wide; limiting the .NET exceptions to its three sidecars remains a signing-stage improvement requiring Mac verification.
9. Every active AI-host session starts its own memory server and GitHub helper. The user reported approximately 864 MB across 11 open sessions, with no orphan servers; that measurement was not repeated during this rebuild. A shared server is a separate future design, not implemented here.

No repository checkout/remote or release CI exists in this workspace. The source ZIP and repeatable Mac commands provide a handoff; they are not a published or automatically updated team release.
