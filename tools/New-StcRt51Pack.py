"""Build a new immutable STC package source with the first-party RT51 template."""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import shutil
import zipfile

BASE_SHA256 = "C4F8C3DBDA55D7CD179B0D475609C94939D320FF3DD4FE5C232A2128EAC099ED"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-pack", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError("Use a new output directory; package versions are immutable.")
    if hashlib.sha256(args.base_pack.read_bytes()).hexdigest().upper() != BASE_SHA256:
        raise ValueError("Base package identity differs from the verified 0.1.2 package.")
    repo = Path(__file__).resolve().parent.parent
    source = repo / "firmware/StudioX.Rt51"
    with zipfile.ZipFile(args.base_pack) as archive:
        manifest = json.loads(archive.read("manifest.json"))
        if manifest["id"] != "stc.stc8" or manifest["version"] != "0.1.2":
            raise ValueError("Requires stc.stc8 / 0.1.2; monitor metadata must be preserved.")
        index = json.loads(archive.read("files.sha256.json"))
        names = [e.filename for e in archive.infolist()]
        if len(set(names)) != len(names) or set(names) != set(index) | {"files.sha256.json"}:
            raise ValueError("Invalid base archive index.")
        for name in index:
            path = PurePosixPath(name)
            if path.is_absolute() or ".." in path.parts or "\\" in name or ":" in name:
                raise ValueError("Unsafe base archive path.")
            content = archive.read(name)
            if hashlib.sha256(content).hexdigest().lower() != index[name].lower():
                raise ValueError("Base archive hash mismatch: " + name)
            output = args.output.joinpath(*path.parts)
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_bytes(content)
    manifest["version"] = "0.1.5"
    target = next(d for d in manifest["devices"] if d["id"] == "IAP15F2K61S2")
    files = {}
    for name in (
        "include/rt51.h",
        "include/rt51_config.h",
        "src/rt51.c",
        "src/rt51_port_sdcc.c",
        "src/rt51_timers.c",
        "src/rt51_internal.h",
    ):
        destination = "templates/rt51/" + name
        path = args.output / destination
        path.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source / name, path)
        files[name] = destination
    (args.output / "templates/rt51/main.c").write_bytes(
        (source / "examples/iap15/main.c").read_bytes()
    )
    (args.output / "templates/rt51/RT51.md").write_bytes((source / "README.md").read_bytes())
    files["include/RT51.md"] = "templates/rt51/RT51.md"
    (args.output / "templates/rt51/stc15_xinit_sdcc.c").write_bytes(
        (source / "examples/iap15/stc15_xinit_sdcc.c").read_bytes()
    )
    files["src/stc15_xinit_sdcc.c"] = "templates/rt51/stc15_xinit_sdcc.c"
    (args.output / "templates/rt51/RT51-HARDWARE-20261012.md").write_bytes(
        (repo / "docs/RT51-HARDWARE-20261012.md").read_bytes()
    )
    files["include/RT51-HARDWARE-20261012.md"] = "templates/rt51/RT51-HARDWARE-20261012.md"
    (args.output / "templates/rt51/RT51-EXTENSIONS-20261012.md").write_bytes(
        (repo / "docs/RT51-EXTENSIONS-20261012.md").read_bytes()
    )
    files["include/RT51-EXTENSIONS-20261012.md"] = "templates/rt51/RT51-EXTENSIONS-20261012.md"
    target["templates"].append(
        {
            "id": "rt51",
            "displayName": "StudioX RT51 · 静态抢占式 RTOS",
            "description": "3 个用户任务、周期延时、静态软件定时器与任务快照，用户提供 tick；含 STC15 XINIT 适配，预留 Mon51 内存。新功能仅离线验证，见 RT51.md。",
            "entryFile": "templates/rt51/main.c",
            "files": files,
            "build": {
                "defines": ["STUDIOX_RT51=1"],
                "includeDirectories": [],
                "sources": [],
                "compileOptions": ["--stack-auto", "--int-long-reent", "--float-reent"],
                "linkOptions": [
                    "--stack-auto",
                    "--int-long-reent",
                    "--float-reent",
                    "--code-size",
                    "56317",
                    "--xram-loc",
                    "1",
                    "--xram-size",
                    "1023",
                    "--idata-loc",
                    "48",
                    "--stack-size",
                    "128",
                ],
            },
        }
    )
    (args.output / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    (args.output / "templates/rt51/source.json").write_text(
        json.dumps(
            {
                "kernel": "StudioX RT51",
                "version": "0.1.0",
                "firstPartySource": "firmware/StudioX.Rt51",
                "basePackSha256": hashlib.sha256(args.base_pack.read_bytes()).hexdigest(),
                "compiler": "SDCC 4.5.0 #15242",
                "verification": "compiled 8052 instruction simulation; no hardware claim",
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )
    print(args.output)


if __name__ == "__main__":
    main()
