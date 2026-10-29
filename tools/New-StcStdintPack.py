"""Upgrade the verified STC pack headers without changing vendor registers or firmware."""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import zipfile

BASE_SHA256 = "799bed2e327e2f2730f85923b2a0deb65f9b346b47697bde765d74f196af1282"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-pack", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError("Use a new package source directory.")
    if hashlib.sha256(args.base_pack.read_bytes()).hexdigest() != BASE_SHA256:
        raise ValueError("Expected the reviewed STC 0.1.6 archive.")
    with zipfile.ZipFile(args.base_pack) as archive:
        index = json.loads(archive.read("files.sha256.json"))
        if set(archive.namelist()) != set(index) | {"files.sha256.json"}:
            raise ValueError("Base archive file set mismatch.")
        for name, expected in index.items():
            path = PurePosixPath(name)
            if path.is_absolute() or ".." in path.parts or "\\" in name or ":" in name:
                raise ValueError("Unsafe package path.")
            data = archive.read(name)
            if hashlib.sha256(data).hexdigest().lower() != expected.lower():
                raise ValueError("Base resource hash mismatch.")
            destination = args.output.joinpath(*path.parts)
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(data)
    manifest_path = args.output / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if (manifest["id"], manifest["version"]) != ("stc.stc8", "0.1.6"):
        raise ValueError("Unexpected base manifest.")
    updated = []
    for family in ("stc89", "stc12", "stc15", "stc8g", "stc8h"):
        matches = list(args.output.rglob(family + ".h"))
        if len(matches) != 1:
            raise ValueError("Expected exactly one register header: " + family)
        header = matches[0]
        lines = header.read_text(encoding="utf-8-sig").splitlines()
        insert = next(i for i, line in enumerate(lines) if line.startswith("#define STUDIOX_")) + 1
        lines.insert(insert, "#include <stdint.h>")
        header.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
        updated.append(header.relative_to(args.output).as_posix())
    manifest["version"] = "0.1.7"
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    provenance = {
        "basePackSha256": BASE_SHA256,
        "baseVersion": "0.1.6",
        "version": "0.1.7",
        "updatedHeaders": updated,
        "change": "Include the project-locked SDCC standard stdint.h; vendor register declarations and all monitor images remain byte-identical.",
    }
    (args.output / "vendor/stdint-upgrade.json").write_text(
        json.dumps(provenance, indent=2) + "\n", encoding="utf-8"
    )
    print("Prepared stc.stc8/0.1.7: all five family headers include stdint.h.")


if __name__ == "__main__":
    main()
