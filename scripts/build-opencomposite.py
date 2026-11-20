#!/usr/bin/env python3
"""Build a pinned D3D11-only game-local OpenComposite adapter on Windows/MSVC."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parent.parent
REVISION = "cff07db75c4823afe93ed7027b03d5f7bc86f164"


def run(*args, cwd=None):
    subprocess.run(list(map(str, args)), cwd=cwd, check=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=ROOT / ".build/opencomposite-artifact")
    args = parser.parse_args()
    source = ROOT / ".build/opencomposite-source"
    build = ROOT / ".build/opencomposite-build"
    if source.exists():
        raise RuntimeError("Use a fresh .build/opencomposite-source for a reproducible adapter build.")
    run("git", "clone", "--no-checkout", "https://gitlab.com/znixian/OpenOVR.git", source)
    run("git", "checkout", "--detach", REVISION, cwd=source)
    run("git", "submodule", "update", "--init", "--recursive", "libs/openxr-sdk", "libs/glm", cwd=source)
    run(sys.executable, ROOT / "tools/opencomposite/apply.py", source)
    run("cmake", "-S", source, "-B", build, "-A", "x64", "-DCMAKE_POLICY_DEFAULT_CMP0091=NEW",
        "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded", "-DOC_VERSION=DemeoMR-OpenXR-1-" + REVISION[:12])
    run("cmake", "--build", build, "--config", "Release", "--target", "OCOVR", "--parallel", "4")
    args.output.mkdir(parents=True, exist_ok=True)
    binary = args.output / "opencomposite.bin"
    shutil.copy2(build / "bin/Release/vrclient_x64.dll", binary)
    # Full corresponding sources, including pinned OpenXR SDK / GLM and our
    # changes. Contains only open-source adapter code, no game assemblies.
    archive = args.output / "opencomposite-source.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as output:
        for path in sorted(source.rglob("*")):
            relative = path.relative_to(source)
            if path.is_file() and ".git" not in relative.parts:
                output.write(path, relative.as_posix())
        output.write(ROOT / "scripts/build-opencomposite.py", "DemeoMR/build-opencomposite.py")
        for path in sorted((ROOT / "tools/opencomposite").glob("*")):
            if path.is_file(): output.write(path, "DemeoMR/" + path.name)
    inputs = {str(p.relative_to(ROOT)).replace("\\", "/"): hashlib.sha256(p.read_bytes()).hexdigest()
              for p in sorted((ROOT / "tools/opencomposite").glob("*")) if p.is_file()}
    inputs["scripts/build-opencomposite.py"] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    receipt = {"schema": "demeo-opencomposite-build-v1", "upstream": REVISION, "inputs": inputs,
               "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
               "source_sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
               "source_commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
               "headset_tested": False}
    (args.output / "build.json").write_text(json.dumps(receipt, indent=2) + "\n")


if __name__ == "__main__": main()
