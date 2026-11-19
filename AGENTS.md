# Project Context: reSlate (`Personal/reStation`)

- **Role:** SlateOne (MIPS32 R3000A) Recomp Engine, Static Lifter & Player (`v0.2.8` Pre-1.0 Beta -> Next: `v0.3.0` Public-Ready Beta; C# namespaces stay `reStation.*` with `reStation.{Contracts,Core}` ABI compatibility, while `v0.3.0` binaries/releases switch to `reSlate`, `engines\reSlate\`, and `SharedLibs\reSlate\`).
- **Boundaries:** `Foundation\`, `_DevBible\` & `Research\*` (zero `Corpus\`) RO; `.recomp` supplies `data\rom\`; child `PATCH+1`; `APP_ISOLATED_TEST=1`; Debug/Test-only `Foundation.Automation`; user-scope `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reSlate\v<Ver>\` (+ `.ready`).

## 1. Quartet, Security & State
- [README.md](README.md) | [FEATURES.md](FEATURES.md) | [ROADMAP.md](ROADMAP.md) | [CHANGELOG.md](CHANGELOG.md) | [SECURITY.md](SECURITY.md) | [CONTRIBUTING.md](CONTRIBUTING.md) | [active.state](active.state)

## 2. Manual Shelf (`Dev\docs\`) & Workspace
- [ARCHITECTURE.md](Dev/docs/ARCHITECTURE.md) | [INTERFACES.md](Dev/docs/INTERFACES.md) | [USER_GUIDE.md](Dev/docs/USER_GUIDE.md) | [OPERATIONS.md](Dev/docs/OPERATIONS.md)
- **IDE, Plans/Reports & Legal:** [reStation.code-workspace](Dev/reStation.code-workspace) | `Dev\docs\{plans,reports}\` | [LICENSE](LICENSE) (`MPL-2.0`) | Active Dev Bible

## 3. Governance & Notion
- **Notion (`🛠️ Projects`):** `<NOTION_PAGE_ID>` (local UUIDs in gitignored `Dev\data\notion_hub.local.json`; `Project = "reSlate"` in `<NOTION_TASK_LIST_COLLECTION>`)
- **Public Release Gate:** Beta (`--prerelease`); `requireHumanReviewBeforePush: true` (pre-public `git-filter-repo` + `ask_question` review before `v0.3.0` public flip).
- **Commit / Release `/post` & 4-Channel Mirrors:** GitHub `--prerelease` (7-file w/ `Portable.zip`), Google Drive mirror, Discord `/post` (`active.state -> discordPost`), and Notion public web page (`*.notion.site` once public).
