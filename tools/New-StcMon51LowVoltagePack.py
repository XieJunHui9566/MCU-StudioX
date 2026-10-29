"""Extend the immutable STC 0.1.5 package with the exact IAP15L2K61S2 monitor."""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import zipfile

BASE_SHA256 = "2E6EDE00A91941999B1B6F255EC18B60FCBA14A79109F28801F36F0220B2A187"
IMAGE_SHA256 = "D2320E17FE8961813D8A012C46571390D6AA3BA683A594DD05697AD5B990D729"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-pack", type=Path, required=True)
    parser.add_argument("--monitor-image", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError("Use a new package source directory.")
    if hashlib.sha256(args.base_pack.read_bytes()).hexdigest().upper() != BASE_SHA256:
        raise ValueError("Base package differs from the verified STC 0.1.5 package.")
    image = args.monitor_image.read_bytes()
    if len(image) != 61440 or hashlib.sha256(image).hexdigest().upper() != IMAGE_SHA256:
        raise ValueError(
            "Not the exact low-voltage monitor image; F/L images cannot be interchanged."
        )
    with zipfile.ZipFile(args.base_pack) as archive:
        index = json.loads(archive.read("files.sha256.json"))
        for entry in archive.infolist():
            if entry.filename == "files.sha256.json":
                continue
            path = PurePosixPath(entry.filename)
            if (
                path.is_absolute()
                or ".." in path.parts
                or "\\" in entry.filename
                or ":" in entry.filename
            ):
                raise ValueError("Unsafe source package path.")
            content = archive.read(entry)
            if hashlib.sha256(content).hexdigest().lower() != index[entry.filename].lower():
                raise ValueError("Base resource hash mismatch.")
            destination = args.output.joinpath(*path.parts)
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(content)
    manifest_path = args.output / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if manifest["id"] != "stc.stc8" or manifest["version"] != "0.1.5":
        raise ValueError("Unexpected base manifest.")
    manifest["version"] = "0.1.6"
    target = next(device for device in manifest["devices"] if device["id"] == "IAP15L2K61S2")
    target["monitorFirmware"] = {
        "id": "stc.mon51.iap15l2k61s2",
        "displayName": "STC Mon51 V2.5 · IAP15L2K61S2",
        "version": "2.5.0",
        "protocol": "stc-mon51-encoded",
        "imageFile": "debug/mon51/iap15l2k61s2-7.2.5S.bin",
        "imageSha256": IMAGE_SHA256,
        "imageBytes": len(image),
        "provenanceFile": "debug/mon51/iap15l2k61s2-provenance.json",
        "bootloaderVersion": "7.2.5S",
        "bootloaderStatus": 112,
    }
    monitor_dir = args.output / "debug/mon51"
    monitor_dir.mkdir(parents=True, exist_ok=True)
    (monitor_dir / "iap15l2k61s2-7.2.5S.bin").write_bytes(image)
    provenance = {
        "formatVersion": 1,
        "vendor": "STC",
        "target": "IAP15L2K61S2",
        "monitorVersion": "2.5.0",
        "source": "User-supplied AiCube-ISP-v6.96V-plus.exe, static resources only",
        "sourceSha256": "DC8B06F1FD951D2CBCCDAC9A6BA120233E82C83E53C56761BD8C794E86E2050E",
        "basePackSha256": BASE_SHA256,
        "setupSha256": IMAGE_SHA256,
        "staticEvidence": "0x4BA582: F4C9 selects res6; 0x44DAB6: F4C9 OR 01, status 70 gives EFFF marker 11",
        "encoding": "Vendor mode 7, 62/42, deterministic FF user region, EFFF target/status marker 11",
        "bootloaderVersion": "7.2.5S",
        "bootloaderStatus": "70",
        "validation": "Static vendor evidence, real SDCC build, offline protocol and one-board hardware acceptance",
        "hardwareEvidence": "2026-10-21: IAP15L2K61S2 / CH340 / COM15 / ISP 7.2.5S / status 70; native setup, power activation and 16 debug checks passed",
        "testProgramBytes": 216,
        "testProgramSha256": "3CF1A2CF77288C0C9FD4F3D4AD28B35C9B54EB10E9FF8774B61F2E52E95D9FE4",
        "documentation": "https://stcmicro.com/cn/stc/stc15f2k60s2.html",
        "voltage": "2.4-3.6 V",
        "reservedFlash": "DC00-F3FF; IDE trampoline DBFD-DBFF",
        "reservedXdata": "0400-06FF",
        "monitorPins": ["P3.0", "P3.1"],
        "rightsNotice": "IAP15L2K61S2-NOTICE.txt",
    }
    (monitor_dir / "iap15l2k61s2-provenance.json").write_text(
        json.dumps(provenance, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    (monitor_dir / "IAP15L2K61S2-NOTICE.txt").write_text(
        "STC Mon51 firmware originates from the user's local AiCube resource.\n"
        "Rights remain with STC and its rights holders; no public redistribution license is claimed.\n"
        "Prepared for the user's requested local IAP15L2K61S2 adaptation.\n"
        "Validated on one IAP15L2K61S2 / ISP 7.2.5S / status 70 board on 2026-10-21; other combinations are unverified.\n"
        "Target, BSL version, status and image hash are checked before programming.\n"
        "No vendor executable or Keil driver is included or executed by the IDE.\n",
        encoding="utf-8",
    )
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print(
        "Prepared stc.stc8/0.1.6 with distinct F/L monitor identities; retained existing templates and resources."
    )


if __name__ == "__main__":
    main()
