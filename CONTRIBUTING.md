# Contributing

Contributions are welcome, including bug reports, clean-room tooling improvements, documentation updates, and pull requests.

## Ways to Contribute

- **Bug Reports:** Open an issue with your OS, `.NET` runtime version, steps to reproduce, and sanitized log excerpts.
- **Pull Requests:** Keep changes focused on a single fix or feature, match existing style rules, and include automated unit tests for new behavior.
- **Documentation:** Improvements to clarity, examples, and user guides are always appreciated.

## Dev Setup & Running Tests

```powershell
git clone https://github.com/StreamItAllGit/reSlate.git
cd reSlate\Dev
$env:APP_ISOLATED_TEST = "1"
dotnet test src\reStation.slnx -c Release -p:TreatWarningsAsErrors=true
```

## Pull Request Checklist

- [ ] All unit tests pass under `APP_ISOLATED_TEST=1` with zero warnings (`-p:TreatWarningsAsErrors=true`).
- [ ] Changes stay focused and avoid unrelated edits or hardcoded workstation paths.
- [ ] User-facing changes are noted in `CHANGELOG.md` under `[Unreleased]`.

## Clean-Room & Trademark Rules

- **No Copyrighted Material:** Never commit or attach retail disc/ROM images, vendor BIOS files, extracted retail audio/textures, Ghidra `.gpr` databases, decompiled retail function bodies, or pre-generated `*.Generated.dll` assemblies.
- **Zero-Warning Clean Source:** Generated or refactored C test/decomp outputs must compile cleanly under `-Wall -Wextra -Wno-unused-parameter` with zero compiler-warning suppression flags (`-Wno-everything` is disallowed).
- **Trademark Neutrality:** Keep commercial console and game trademarks out of code symbols, filenames, commit messages, issues, and public blurbs; use factual hardware/architecture terms (`SlateOne`, `MIPS32`, `R3000A`, catalog serials) and keep non-affiliation notices inside legal sections only.
