# Security Policy

## Supported Versions

| Version | Supported | Security Boundary Notes |
| :--- | :--- | :--- |
| `0.3.x` | Yes (active public beta) | Target mainline (`reSlate` binaries, `reEngine.Host.Contracts`, `Release` automation strip) |
| `0.2.x` | Security fixes only | Prior `reStation` beta track (`v0.2.8` active baseline) |
| `< 0.2.0` | No | Upgrade to `v0.2.8+` or `v0.3.0+` |

## Security Boundaries

- **Path & Data Isolation:** Runtime arguments (`--data-dir`, `--config`, `--log-dir`, `--save-dir`) and pack/archive extractors (`data\rom\`, `hdpacks\`, `data\cache\`) enforce fail-closed containment checks (`ExitCode = 3` on host escape).
- **Release Automation Strip:** Production `Release` builds omit `Foundation.Automation` completely and fail closed with `ExitCode = 2` if `--automation-port` is supplied.
- **No Background Daemons:** `AllowAutoStart = false` is enforced across installer and launcher targets with zero Windows Startup entries or network sockets.
- **No Secrets or Copyrighted Media:** API tokens, webhook URLs, local hub IDs, retail disc images, vendor BIOS dumps, and generated cache assemblies (`*.Generated.dll`) are never committed or included in release bundles.

## Reporting a Vulnerability

Please **do not** open a public issue for suspected security vulnerabilities or accidental asset leakage.

1. Report privately via GitHub **Security Advisories** (`Report a vulnerability` on the repository Security tab).
2. Include the affected component, version, reproduction steps, and expected vs. actual behavior.
3. Maintainers acknowledge reports within **72 hours** and coordinate a fix before public disclosure.
