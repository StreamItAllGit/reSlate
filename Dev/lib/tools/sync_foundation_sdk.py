# @module: Foundation.Tools.SyncFoundationSdk
# @purpose: Publishes complete Foundation SDK snapshots (.dll, .xml, WebAssets, foundation_core, tools, LICENSE, sdk-manifest.json) to SharedLibs/Foundation/vX.Y.Z, SharedLibs/Foundation/Latest, and Builds/vX.Y.Z, and syncs consumer projects (--sync-consumer).
# @depends-on: argparse, datetime, hashlib, json, os, pathlib, shutil
# @exports: publish_sdk, sync_consumer, purge_stale_pdbs, stage_clean_release_bundle, main

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import stat
import sys
from datetime import datetime, timezone
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))
from build_release_universal import stage_sdk_release_zip

FOUNDATION_ROOT = Path(__file__).resolve().parents[2]
DEV_ROOT = FOUNDATION_ROOT / "Dev"
SHARED_LIBS_ROOT = Path(
    os.environ.get("FOUNDATION_SHARED_LIBS_ROOT")
    or (FOUNDATION_ROOT.parent.parent / "SharedLibs" / "Foundation")
)


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _copy_tree_contents(src: Path, dst: Path) -> None:
    if not src.exists():
        return
    dst.mkdir(parents=True, exist_ok=True)
    for item in src.rglob("*"):
        if "__pycache__" in item.parts:
            continue
        rel = item.relative_to(src)
        target = dst / rel
        if item.is_dir():
            target.mkdir(parents=True, exist_ok=True)
        elif item.is_file() and item.resolve() != target.resolve():
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(item, target)


def purge_stale_pdbs(root_dir: Path) -> int:
    removed = 0
    if not root_dir.exists():
        return 0
    for pdb_file in root_dir.rglob("*.pdb"):
        if pdb_file.is_file():
            pdb_file.unlink()
            removed += 1
    return removed


def collect_compiled_binaries(configuration: str = "Release") -> list[Path]:
    src_root = DEV_ROOT / "src"
    selected: dict[str, Path] = {}
    configs = [configuration, "Release", "Debug"]
    for cfg in configs:
        for proj_dir in sorted(src_root.glob("Foundation.*")):
            if not proj_dir.is_dir():
                continue
            bin_cfg = proj_dir / "bin" / cfg
            if not bin_cfg.exists():
                continue
            for tfm_dir in sorted(bin_cfg.glob("net8.0*")):
                for ext in ("*.dll", "*.xml", "*.deps.json"):
                    for f in tfm_dir.glob(ext):
                        if f.name.startswith("Foundation.") and f.name not in selected:
                            selected[f.name] = f
    return [selected[k] for k in sorted(selected.keys())]


def _populate_sdk_target(
    target_dir: Path,
    version: str,
    ui_version: str,
    binaries: list[Path],
    web_src: Path,
    web_assets: Path,
    py_core: Path,
    tools_dir: Path,
) -> dict[str, object]:
    target_dir.mkdir(parents=True, exist_ok=True)
    purge_stale_pdbs(target_dir)

    for bin_file in binaries:
        dst = target_dir / bin_file.name
        if bin_file.resolve() != dst.resolve():
            shutil.copy2(bin_file, dst)

    license_file = FOUNDATION_ROOT / "LICENSE"
    if license_file.exists():
        shutil.copy2(license_file, target_dir / "LICENSE")

    target_web_assets = target_dir / "WebAssets"
    _copy_tree_contents(web_assets, target_web_assets)
    _copy_tree_contents(web_src, target_web_assets)
    _copy_tree_contents(py_core, target_dir / "foundation_core")
    _copy_tree_contents(tools_dir, target_dir / "tools")

    files_inventory: list[dict[str, object]] = []
    for f in sorted(target_dir.rglob("*")):
        if f.is_file() and f.name != "sdk-manifest.json":
            files_inventory.append(
                {
                    "relativePath": f.relative_to(target_dir).as_posix(),
                    "sizeBytes": f.stat().st_size,
                    "sha256": _sha256_file(f),
                }
            )

    manifest = {
        "schemaVersion": 1,
        "sdkId": "Foundation",
        "version": version,
        "uiVersion": ui_version,
        "publishedAtUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "fileCount": len(files_inventory),
        "files": files_inventory,
    }
    (target_dir / "sdk-manifest.json").write_text(
        json.dumps(manifest, indent=2), encoding="utf-8"
    )
    return manifest


def _handle_remove_readonly(func, path, exc_info):
    os.chmod(path, stat.S_IWRITE)
    func(path)


def stage_clean_release_bundle(version: str = "0.5.2", sdk_dir: Path | None = None) -> Path:
    latest_dir = FOUNDATION_ROOT / "Releases" / "Latest"
    latest_dir.mkdir(parents=True, exist_ok=True)

    keep_names = {"README.md", "FEATURES.md", "RELEASE_NOTES.md", "CHANGELOG.md", "SHA256SUMS.txt"}
    for item in list(latest_dir.iterdir()):
        if item.is_dir():
            shutil.rmtree(item, onexc=_handle_remove_readonly)
        elif item.is_file() and item.name not in keep_names and not (item.name.startswith("Foundation-v") and item.name.endswith(".zip")):
            os.chmod(item, stat.S_IWRITE)
            item.unlink()

    if sdk_dir is not None and sdk_dir.exists() and not os.environ.get("FOUNDATION_SKIP_RELEASE_ZIP"):
        stage_sdk_release_zip(sdk_dir, latest_dir, version)
        return latest_dir

    lines: list[str] = []
    for item in sorted(latest_dir.iterdir(), key=lambda p: p.name.lower()):
        if item.is_file() and item.name != "SHA256SUMS.txt":
            lines.append(f"{_sha256_file(item).upper()}  {item.name}")
    (latest_dir / "SHA256SUMS.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return latest_dir


def publish_sdk(version: str = "0.5.2", ui_version: str = "0.5.2", configuration: str = "Release") -> dict[str, object]:
    clean_ver = version.lstrip("v")
    ver_tag = f"v{clean_ver}"
    ui_dir = DEV_ROOT / "src" / "Foundation.UI"
    web_src = ui_dir / "Web"
    web_assets = ui_dir / "WebAssets"
    binaries = collect_compiled_binaries(configuration)
    py_core = DEV_ROOT / "src" / "foundation_core"
    tools_dir = DEV_ROOT / "tools"
    isolated_shared_libs = bool(os.environ.get("FOUNDATION_SHARED_LIBS_ROOT"))
    targets = [SHARED_LIBS_ROOT / ver_tag, SHARED_LIBS_ROOT / "Latest"]
    if not isolated_shared_libs:
        purge_stale_pdbs(FOUNDATION_ROOT / "Builds")
        targets.append(FOUNDATION_ROOT / "Builds" / ver_tag / "app")
    manifest: dict[str, object] = {}
    for target in targets:
        manifest = _populate_sdk_target(target, clean_ver, ui_version, binaries, web_src, web_assets, py_core, tools_dir)
    if not isolated_shared_libs:
        stage_clean_release_bundle(clean_ver, SHARED_LIBS_ROOT / ver_tag)
    return manifest

def sync_consumer(consumer_root: Path, source_version: str = "Latest", web_dir: Path | None = None) -> Path:
    src_dir = SHARED_LIBS_ROOT / source_version
    if not src_dir.exists():
        raise FileNotFoundError(f"Foundation SharedLibs source not found: {src_dir}")

    dev_lib = consumer_root / "Dev" / "lib" if (consumer_root / "Dev").exists() else consumer_root / "lib"
    dev_lib.mkdir(parents=True, exist_ok=True)
    nested_foundation = dev_lib / "Foundation"
    has_flat_dlls = any(dev_lib.glob("Foundation.*.dll"))
    target_lib = dev_lib if has_flat_dlls or not nested_foundation.exists() else nested_foundation

    for item in src_dir.glob("Foundation.*"):
        if item.is_file():
            shutil.copy2(item, target_lib / item.name)
            if nested_foundation.exists() and target_lib != nested_foundation:
                shutil.copy2(item, nested_foundation / item.name)

    if (src_dir / "sdk-manifest.json").exists():
        shutil.copy2(src_dir / "sdk-manifest.json", target_lib / "sdk-manifest.json")

    _copy_tree_contents(src_dir / "WebAssets", target_lib / "WebAssets")
    _copy_tree_contents(src_dir / "foundation_core", target_lib / "foundation_core")
    if nested_foundation.exists() and target_lib != nested_foundation:
        _copy_tree_contents(src_dir / "WebAssets", nested_foundation / "WebAssets")
        _copy_tree_contents(src_dir / "foundation_core", nested_foundation / "foundation_core")

    if web_dir is not None:
        resolved_web = web_dir if web_dir.is_absolute() else (consumer_root / web_dir)
        _copy_tree_contents(src_dir / "WebAssets", resolved_web)

    for stale_root in {dev_lib, target_lib, nested_foundation}:
        if not stale_root.exists(): continue
        for pat in ("tools/verify_sandbox_universal.py", "Foundation.Sandbox.*", "*VisibleTest*", "*InstallerSandboxContext*"):
            for item in stale_root.rglob(pat):
                if item.is_file(): item.unlink(missing_ok=True)
    return target_lib

def main() -> int:
    parser = argparse.ArgumentParser(description="Foundation SDK Publisher & Consumer Sync Tool")
    parser.add_argument("--version", default="0.5.2", help="Foundation SDK SemVer (default: 0.5.2)")
    parser.add_argument("--ui-version", default="0.5.2", help="Foundation.UI SemVer (default: 0.5.2)")
    parser.add_argument("--configuration", default="Release", help="Build configuration (Release/Debug)")
    parser.add_argument("--publish", action="store_true", help="Publish SDK to SharedLibs and Builds")
    parser.add_argument("--sync-consumer", type=Path, help="Consumer project root to sync from SharedLibs")
    parser.add_argument("--source-version", default="Latest", help="SharedLibs version folder (default: Latest)")
    parser.add_argument("--web-dir", type=Path, help="Optional consumer web assets target directory")
    args = parser.parse_args()

    if args.publish or args.sync_consumer is None:
        manifest = publish_sdk(args.version, args.ui_version, args.configuration)
        print(f"[SYNC-SDK] Published Foundation v{manifest['version']} ({manifest['fileCount']} files) to SharedLibs & Builds.")

    if args.sync_consumer is not None:
        synced = sync_consumer(args.sync_consumer.resolve(), args.source_version, args.web_dir)
        print(f"[SYNC-SDK] Synced consumer library at {synced}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
