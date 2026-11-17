#!/usr/bin/env python3
"""Repository-local development commands. Python standard library only."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import venv
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src/Gengyi.Cad.AutoCAD2024/Gengyi.Cad.AutoCAD2024.csproj"
CORE_CHECKS = ROOT / "tests/Gengyi.Core.Checks/Gengyi.Core.Checks.csproj"


def run(args: list[str], **kwargs) -> subprocess.CompletedProcess:
    return subprocess.run(args, cwd=ROOT, check=True, **kwargs)


def dotnet() -> str:
    local = ROOT / ".dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet")
    result = str(local) if local.is_file() else shutil.which("dotnet")
    if not result:
        raise RuntimeError("Install the .NET SDK version in global.json (compiler only); see docs/08_DEVELOPMENT.md")
    return result


def dotnet_env() -> dict[str, str]:
    # Keep tool caches and first-run files inside this checkout.
    env = os.environ.copy()
    env.update(DOTNET_CLI_HOME=str(ROOT / "work/dotnet-cli"),
               NUGET_PACKAGES=str(ROOT / "work/nuget"),
               DOTNET_CLI_TELEMETRY_OPTOUT="1",
               DOTNET_NOLOGO="1")
    return env


def setup() -> None:
    if sys.version_info < (3, 10):
        raise RuntimeError("Python 3.10 or newer is required")
    if not (ROOT / ".venv/pyvenv.cfg").exists():
        venv.EnvBuilder(with_pip=False).create(ROOT / ".venv")
    print("Python environment ready: .venv (no third-party Python dependencies)")


def check() -> None:
    run([sys.executable, "-m", "unittest", "discover", "-s", "tests", "-v"])
    files = [p for folder in ("data", "schemas", "examples")
             for p in (ROOT / folder).rglob("*.json")]
    files.append(ROOT / "references/image_manifest.json")
    for file in files:
        json.loads(file.read_text(encoding="utf-8"))
    manifest = json.loads((ROOT / "references/image_manifest.json").read_text(encoding="utf-8"))
    for item in manifest["images"]:
        data = (ROOT / item["file"]).read_bytes()
        if hashlib.sha256(data).hexdigest() != item["sha256"] or len(data) != item["bytes"]:
            raise RuntimeError("Original image integrity failed: " + item["file"])
    print(f"JSON syntax: {len(files)} files OK; original image SHA256/size: {len(manifest['images'])} OK")
    executable, env = dotnet(), dotnet_env()
    run([executable, "restore", str(CORE_CHECKS), "--locked-mode"], env=env)
    run([executable, "run", "--project", str(CORE_CHECKS), "-c", "Release", "--no-restore",
         "--", "--fixtures", str(ROOT / "work/schema-fixtures")], env=env)
    try:
        import jsonschema
    except ImportError:
        print("Full JSON Schema validation unavailable; install tools/requirements-checks.txt for this check")
    else:
        schema = json.loads((ROOT / "schemas/layout_instance.schema.json").read_text(encoding="utf-8"))
        for fixture in (ROOT / "work/schema-fixtures").glob("*.json"):
            jsonschema.Draft202012Validator(schema).validate(json.loads(fixture.read_text(encoding="utf-8")))
        print("All three room export fixtures pass the repository JSON Schema")
    print("Scope: core algorithms, reference arithmetic and source integrity; real CAD acceptance not executed")


def build() -> None:
    executable, env = dotnet(), dotnet_env()
    run([executable, "restore", str(PROJECT), "--locked-mode"], env=env)
    run([executable, "build", str(PROJECT), "-c", "Release", "--no-restore"], env=env)


def package() -> None:
    build()
    bundle = ROOT / "artifacts/Gengyi.bundle"
    contents = bundle / "Contents/Windows"
    if bundle.exists():
        shutil.rmtree(bundle)
    contents.mkdir(parents=True)
    shutil.copy2(ROOT / "packaging/Gengyi.bundle/PackageContents.xml", bundle)
    # Explicit allowlist: ship runtime dependencies, never Autodesk API DLLs or private blocks.
    binaries = ROOT / "src/Gengyi.Cad.AutoCAD2024/bin/Release/net48"
    for name in ("Gengyi.Cad.AutoCAD2024", "Gengyi.Core", "NetTopologySuite", "Newtonsoft.Json",
                 "System.Buffers", "System.Memory", "System.Numerics.Vectors", "System.Runtime.CompilerServices.Unsafe"):
        shutil.copy2(binaries / (name + ".dll"), contents)
    shutil.copy2(ROOT / "docs/08_DEVELOPMENT.md", bundle / "DEVELOPMENT.md")
    shutil.copy2(ROOT / "docs/09_USER_GUIDE.md", bundle / "USAGE.md")
    shutil.copy2(ROOT / "docs/environment-report.md", bundle)
    shutil.copytree(ROOT / "packaging/licenses", bundle / "licenses")
    metadata = {"plugin_version": "0.2.0", "source_commit": run(["git", "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip(),
                "working_tree_dirty": bool(run(["git", "status", "--porcelain"], capture_output=True, text=True).stdout.strip()),
                "target_host": "AutoCAD 2024 Windows x64", "target_framework": "net48",
                "host_acceptance": "not_run", "source_block_visual_acceptance": "pending_local_original_CAD"}
    (bundle / "build-info.json").write_text(json.dumps(metadata, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    archive = ROOT / "artifacts/Gengyi.AutoCAD2024.zip"
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as z:
        for path in sorted(bundle.rglob("*")):
            if path.is_file():
                z.write(path, path.relative_to(bundle.parent))
    print("Created: " + str(archive))
    print("Three-room plugin build; real AutoCAD loading and original-block visual acceptance remain pending")


def doctor() -> None:
    report = {
        "scope": "three_room_plugin_host_acceptance_pending",
        "source_commit": run(["git", "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip(),
        "os": platform.platform(), "python": platform.python_version(),
        "target": {"host": "AutoCAD 2024", "api": "24.3.0", "framework": "net48", "architecture": "x64"},
        "host_verification": "not_run_use_GYENV_in_AutoCAD",
        "original_native_blocks": "not_in_repository",
    }
    try:
        report["build_sdk"] = run([dotnet(), "--version"], env=dotnet_env(),
                                  capture_output=True, text=True).stdout.strip()
    except (RuntimeError, subprocess.CalledProcessError) as exc:
        report["build_sdk"] = str(exc)
    print(json.dumps(report, ensure_ascii=False, indent=2))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("setup", "check", "doctor", "build", "package"))
    args = parser.parse_args()
    try:
        {"setup": setup, "check": check, "doctor": doctor, "build": build, "package": package}[args.command]()
    except (RuntimeError, subprocess.CalledProcessError, OSError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
