"""Assemble an independently usable, content-locked MX251 development SDK.

Explicit local inputs only: no downloads, installations, global PATH changes or
StudioX project dependency. The input lock is separate from the local root map.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import zipfile


GROUPS = (
    "toolchain",
    "profiles",
    "source",
    "baseSource",
    "upstreamSource",
    "hostRecipe",
    "upstreamProvenance",
    "dependencyProvenance",
)
REPO_ITEMS = (
    "VERSION.json",
    "STANDALONE.md",
    "docs/SDK-ASSEMBLY.txt",
    "build/qualified",
    "compiler-patches",
    "runtime",
    "targets",
    "validation",
)


def read(path):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("Duplicate JSON key: " + key)
            result[key] = value
        return result

    return json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write(path, value):
    path.write_text(
        json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + "\n", encoding="utf-8"
    )


def safe_path(root, relative):
    parts = PurePosixPath(relative)
    if (
        not relative
        or "\\" in relative
        or ":" in relative
        or parts.is_absolute()
        or any(p in ("..", ".") for p in relative.split("/"))
    ):
        raise ValueError("Invalid relative path: " + relative)
    path = root.joinpath(*parts.parts)
    if not path.resolve().is_relative_to(root.resolve()):
        raise ValueError("Path escapes root: " + relative)
    return path


def files(root):
    # 目录链接不能被隐式纳入分发；硬链接只读取，输出使用独立文件副本。
    if root.is_symlink() or root.is_junction():
        raise ValueError("Linked input root: " + str(root))
    if root.is_file():
        return [root]
    result = []
    for base, dirs, names in os.walk(root, followlinks=False):
        for name in dirs + names:
            path = Path(base) / name
            if path.is_symlink() or path.is_junction():
                raise ValueError("Linked input: " + str(path))
        result.extend(Path(base) / n for n in names)
    return sorted(result)


def inventory(roots, repository):
    result = {}
    for group in GROUPS:
        root = roots[group]
        result[group] = {
            p.relative_to(root).as_posix() if root.is_dir() else p.name: digest(p)
            for p in files(root)
        }
    result["repository"] = {}
    for name in REPO_ITEMS:
        for path in files(repository / name):
            if "__pycache__" in path.parts or ".build" in path.parts:
                continue
            result["repository"][path.relative_to(repository).as_posix()] = digest(path)
    return result


def zip_sources(path, roots, records):
    with zipfile.ZipFile(path, "x", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for group, prefix in (
            ("source", "compiler-current"),
            ("baseSource", "compiler-runtime-base"),
            ("hostRecipe", "build"),
        ):
            for relative, expected in sorted(records[group].items()):
                source = roots[group] / relative if roots[group].is_dir() else roots[group]
                data = source.read_bytes()
                if hashlib.sha256(data).hexdigest() != expected:
                    raise ValueError("Source changed during assembly: " + str(source))
                info = zipfile.ZipInfo(prefix + "/" + relative, (2026, 10, 27, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                archive.writestr(info, data)


def validate_profiles(root, version):
    contracts_path = root / "build/qualified/TargetContracts.json"
    contracts = read(contracts_path)["targets"]
    targets = {}
    for target in sorted(contracts):
        profile = root / "profiles" / target
        lock = read(profile / "profile.lock.json")
        if lock["schema"] != 2 or lock["target"] != target or lock["mx251Version"] != version:
            raise ValueError("Profile identity mismatch: " + target)
        if lock["targetContract"] != {"id": target, "registrySha256": digest(contracts_path)}:
            raise ValueError("Target contract mismatch: " + target)
        for group, base in (("toolchain", root / "toolchain"), ("profile", profile)):
            seen = set()
            for entry in lock[group]:
                if entry["path"].casefold() in seen:
                    raise ValueError("Duplicate pinned file: " + entry["path"])
                seen.add(entry["path"].casefold())
                if digest(safe_path(base, entry["path"])) != entry["sha256"]:
                    raise ValueError("Profile content mismatch: " + entry["path"])
        compiler = lock["compilerOverlay"]
        if digest(safe_path(profile, compiler["path"])) != compiler["sha256"]:
            raise ValueError("Compiler overlay mismatch")
        targets[target] = {
            "profile": "profiles/" + target,
            "profileLockSha256": digest(profile / "profile.lock.json"),
            "compilerSha256": compiler["sha256"],
        }
    return targets


def verify(root):
    manifest = read(root / "sdk.json")
    if (
        manifest["schema"] != 1
        or manifest["name"] != "MX251"
        or manifest["status"] != "development-not-LST"
    ):
        raise ValueError("Unsupported SDK manifest")
    actual = {
        p.relative_to(root).as_posix(): digest(p) for p in files(root) if p != root / "sdk.json"
    }
    if actual != manifest["files"]:
        raise ValueError("SDK file set or content changed")
    if validate_profiles(root, manifest["version"]) != manifest["targets"]:
        raise ValueError("SDK target identities changed")
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verify", type=Path)
    parser.add_argument(
        "--roots", type=Path, help="Local input paths; never copied into runtime configuration"
    )
    parser.add_argument("--record-lock", type=Path, help="Create a new reviewed content lock")
    parser.add_argument("--lock", type=Path, help="Require exact content identity before assembly")
    parser.add_argument("--output", type=Path)
    parser.add_argument(
        "--repository",
        type=Path,
        help="Explicit MX251 source/module root; defaults to this checkout",
    )
    args = parser.parse_args()
    if args.verify:
        m = verify(args.verify.resolve())
        print(json.dumps({"verified": True, "version": m["version"], "files": len(m["files"])}))
        return
    if not args.roots:
        parser.error("--roots is required")
    mapping = read(args.roots)
    if set(mapping) != set(GROUPS):
        raise ValueError("Input root mapping must match documented groups exactly")
    roots = {k: (args.roots.resolve().parent / v).resolve(strict=True) for k, v in mapping.items()}
    repository = (
        args.repository.resolve(strict=True)
        if args.repository
        else Path(__file__).resolve().parents[1] / "toolchains/mx251"
    )
    records = inventory(roots, repository)
    version = read(repository / "VERSION.json")["version"]
    if not re.fullmatch(r"\d+\.\d{4}", version):
        raise ValueError("Invalid MX251 version")
    identity = {
        "schema": 1,
        "version": version,
        "assemblerSha256": digest(Path(__file__)),
        "inputs": records,
    }
    if args.record_lock:
        if args.lock or args.output or args.record_lock.exists():
            raise ValueError("Record a fresh lock separately from assembly")
        write(args.record_lock, identity)
        print("Input content lock recorded; review it before assembly.")
        return
    if not args.lock or not args.output or identity != read(args.lock):
        raise ValueError("Missing or mismatched assembly input lock")
    out = args.output.resolve()
    if out.exists() or any(
        out.is_relative_to(p) for p in [*roots.values(), repository] if p.is_dir()
    ):
        raise ValueError("Output must be new and outside all input roots")
    out.mkdir(parents=True)
    try:
        for group in ("toolchain", "profiles", "repository"):
            base = repository if group == "repository" else roots[group]
            for relative, expected in records[group].items():
                dest = safe_path(out if group == "repository" else out / group, relative)
                dest.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(base / relative, dest)
                if digest(dest) != expected:
                    raise ValueError("Input changed while copying: " + relative)
        sources = out / "sources"
        sources.mkdir()
        zip_sources(sources / "mx251-source.zip", roots, records)

        def copy_single(group, name):
            expected = next(iter(records[group].values()))
            shutil.copyfile(roots[group], sources / name)
            if digest(sources / name) != expected:
                raise ValueError("Input changed while copying: " + group)

        copy_single("upstreamSource", "upstream-source.zip")
        # 来源记录作为归档证据保存；不参与运行时路径解析。
        for group in ("upstreamProvenance", "dependencyProvenance"):
            copy_single(group, group + ".json")
        write(sources / "input-lock.json", identity)
        shutil.copyfile(roots["source"] / "COPYING", sources / "COPYING")
        shutil.copyfile(roots["source"] / "LICENSE", sources / "LICENSE")
        with zipfile.ZipFile(sources / "upstream-source.zip") as archive:
            names = [n for n in archive.namelist() if n.endswith("/doc/README.txt")]
            if len(names) != 1:
                raise ValueError("Missing upstream suite license explanation")
            (sources / "UPSTREAM-README.txt").write_bytes(archive.read(names[0]))
        shutil.copyfile(Path(__file__), sources / "Prepare-Mx251Sdk.py")
        targets = validate_profiles(out, version)
        manifest = {
            "schema": 1,
            "name": "MX251",
            "version": version,
            "status": "development-not-LST",
            "host": "win-x64",
            "buildChain": ["CMake", "Ninja", "MX251"],
            "projectModule": "build/qualified/Project.cmake",
            "buildScript": "build/qualified/Build.ps1",
            "externalHostTools": ["PowerShell 7", "CMake >= 3.25", "Ninja"],
            "targets": targets,
            "inputLockSha256": digest(args.lock),
            "sourceArchive": "sources/mx251-source.zip",
            "upstreamSourceArchive": "sources/upstream-source.zip",
            "files": {p.relative_to(out).as_posix(): digest(p) for p in files(out)},
        }
        # 完成标记最后发布；失败目录保留诊断但不能被当成已完成 SDK。
        write(out / "sdk.json", manifest)
        verify(out)
        print(
            json.dumps(
                {
                    "assembled": True,
                    "version": version,
                    "files": len(manifest["files"]),
                    "manifestSha256": digest(out / "sdk.json"),
                }
            )
        )
    except Exception as error:
        (out / "ASSEMBLY-FAILED.txt").write_text(str(error) + "\n", encoding="utf-8")
        raise


if __name__ == "__main__":
    main()
