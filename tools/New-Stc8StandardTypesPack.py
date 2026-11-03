"""Update the existing STC 8-bit pack with explicit standard integer templates."""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import zipfile


BASE_SHA256 = "d39a245971e1e51a5362cde9539a333628af284ce5cfe1918a1894fed2fac8f9"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-pack", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError("Use a new source directory; existing packs are immutable.")
    if hashlib.sha256(args.base_pack.read_bytes()).hexdigest() != BASE_SHA256:
        raise ValueError("Expected the reviewed STC 0.1.8 package.")
    with zipfile.ZipFile(args.base_pack) as archive:
        index = json.loads(archive.read("files.sha256.json"))
        if len(set(archive.namelist())) != len(archive.namelist()) or set(archive.namelist()) != set(index) | {"files.sha256.json"}:
            raise ValueError("Package file set mismatch.")
        for name, expected in index.items():
            path = PurePosixPath(name)
            if path.is_absolute() or ".." in path.parts or "\\" in name or ":" in name:
                raise ValueError("Unsafe source package path.")
            data = archive.read(name)
            if hashlib.sha256(data).hexdigest().lower() != expected.lower():
                raise ValueError("Source package hash mismatch: " + name)
            destination = args.output.joinpath(*path.parts)
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(data)
    manifest_path = args.output / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if (manifest["id"], manifest["version"], len(manifest["devices"])) != ("stc.stc8", "0.1.8", 25):
        raise ValueError("Unexpected source manifest.")
    for family in ("stc89", "stc12", "stc15", "stc8g", "stc8h"):
        header = args.output / "sdk/include" / (family + ".h")
        if header.read_bytes().count(b"#include <stdint.h>") != 1:
            raise ValueError("Family header must expose the compiler's standard integer types: " + family)
    entries = sorted({template["entryFile"] for device in manifest["devices"] for template in device["templates"]})
    changes = []
    for name in entries:
        source = args.output / name
        original = source.read_bytes()
        updated = original
        if b"#include <stdint.h>" not in updated:
            if b"#include " not in updated:
                raise ValueError("No include anchor: " + name)
            updated = updated.replace(b"#include ", b"#include <stdint.h>\n#include ", 1)
        updated = updated.replace(b"volatile __xdata unsigned int app_counter;", b"volatile __xdata uint16_t app_counter;")
        updated = re.sub(rb"\bfor\s*\(\s*;\s*;\s*\)", b"while (1)", updated)
        if updated != original:
            source.write_bytes(updated)
            changes.append(name)
    manifest["version"] = "0.1.9"
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    provenance = {
        "basePackSha256": BASE_SHA256, "baseVersion": "0.1.8", "version": "0.1.9",
        "entryFiles": entries, "updatedFiles": changes,
        "types": "Use the project-locked SDCC stdint.h, including signed/unsigned 8/16/32/64-bit, least/fast, pointer and max-width types; no duplicate typedefs.",
        "preserved": "All 25 device profiles, register headers, link settings, Mon51 images and RT51 implementation resources remain byte-identical.",
    }
    (args.output / "vendor/standard-integer-templates.json").write_text(
        json.dumps(provenance, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Prepared stc.stc8/0.1.9: {len(entries)} template entries, all five family headers, 25 devices.")


if __name__ == "__main__":
    main()
