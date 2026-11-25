# Project Context: reSlate (`Personal/reStation`)

- **Role:** SlateOne (R3000A) Static Recompilation Engine & `.decomp` Lifter (`v0.3.2` Public Beta -> `v0.4.0–v0.7.0` -> `v1.0.0` Gold -> `v2.0–v5.0` Horizon; `313/313` Release & Debug tests; outputs `reSlate.*`, `engines\reSlate\v<Ver>\`, `SharedLibs\reSlate\v<Ver>\`, preserving `reStation.{Contracts,Core}` ABI & `reStation.*` namespaces).
- **Boundaries:** `Foundation\`, `reEngine\`, `_DevBible\` & `Research\*` (zero `Corpus\`) RO; `.recomp` supplies `data\rom\`; `APP_ISOLATED_TEST=1`; Debug/Test-only `Foundation.Automation` (`ExitCode=2` in `Release`); user `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reSlate\v<Ver>\` (+ `.ready`, `data\config\settings.json` single-file exception expires `v0.5.0`, `data\hdpacks\`, `data\memcards\`).

## 1. Quartet, Security & State
- [README.md](README.md) | [FEATURES.md](FEATURES.md) | [ROADMAP.md](ROADMAP.md) | [CHANGELOG.md](CHANGELOG.md) | [SECURITY.md](SECURITY.md) | [CONTRIBUTING.md](CONTRIBUTING.md) | [active.state](active.state)

## 2. Manual Shelf (`Dev\docs\`) & Workspace
- [ARCHITECTURE.md](Dev/docs/ARCHITECTURE.md) | [INTERFACES.md](Dev/docs/INTERFACES.md) | [USER_GUIDE.md](Dev/docs/USER_GUIDE.md) | [OPERATIONS.md](Dev/docs/OPERATIONS.md)
- **IDE, Plans/Reports & Legal:** [reStation.code-workspace](Dev/reStation.code-workspace) | `Dev\docs\{plans,reports}\` | [LICENSE](LICENSE) (`MPL-2.0`) | Active Dev Bible

## 3. Governance & Notion
- **Notion (`🛠️ Projects`):** `<NOTION_PAGE_ID>` (local UUIDs in gitignored `Dev\data\notion_hub.local.json`; `Project = "reSlate"` in `<NOTION_TASK_LIST_COLLECTION>`)
- **Public Gate:** Public Beta (`--prerelease`); `isPublicRepo: true`, `requireHumanReviewBeforePush: false` (`StreamItAllGit/reSlate`).
- **Release Channels:** GitHub `--prerelease` (`7-file` -> `8-file Dual-Installer` at `v0.6.0`), Discord `/post` (`active.state`), Notion (`*.notion.site` manual-only).
