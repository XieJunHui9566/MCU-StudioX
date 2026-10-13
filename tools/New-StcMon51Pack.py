"""Add the verified Mon51 resource to an immutable StudioX STC package source tree."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import zipfile

BASE_SHA256 = "6699A1C7002AD48B5CC71B6C1225A7346781495F63027AD3EA39957B751FF03F"
IMAGE_SHA256 = "ABC080915DDDCA9589F184E9BE76FBCC757EDF001BE02AF7A70ECB57451106F9"
VENDOR_SHA256 = "DC8B06F1FD951D2CBCCDAC9A6BA120233E82C83E53C56761BD8C794E86E2050E"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-pack", type=Path, required=True)
    parser.add_argument("--monitor-image", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError("Use a new source directory; published packages are immutable.")
    if hashlib.sha256(args.base_pack.read_bytes()).hexdigest().upper() != BASE_SHA256:
        raise ValueError("Base package identity differs from the verified 0.1.0 release.")
    image = args.monitor_image.read_bytes()
    if len(image) != 61440 or hashlib.sha256(image).hexdigest().upper() != IMAGE_SHA256:
        raise ValueError("Unknown monitor payload; no package generated.")
    with zipfile.ZipFile(args.base_pack) as archive:
        index = json.loads(archive.read("files.sha256.json"))
        for entry in archive.infolist():
            name = entry.filename
            if name == "files.sha256.json":
                continue
            path = PurePosixPath(name)
            if path.is_absolute() or ".." in path.parts or "\\" in name or ":" in name:
                raise ValueError("Unsafe base package path.")
            content = archive.read(entry)
            if hashlib.sha256(content).hexdigest().lower() != index[name].lower():
                raise ValueError("Base package resource hash mismatch.")
            destination = args.output.joinpath(*path.parts)
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(content)
    manifest_path = args.output / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if manifest["id"] != "stc.stc8" or manifest["version"] != "0.1.0":
        raise ValueError("Unexpected base manifest.")
    manifest["version"] = "0.1.2"
    monitor_dir = args.output / "debug/mon51"
    monitor_dir.mkdir(parents=True)
    (monitor_dir / "iap15f2k61s2-7.2.5S.bin").write_bytes(image)
    target = next(d for d in manifest["devices"] if d["id"] == "IAP15F2K61S2")
    target["monitorFirmware"] = {
        "id": "stc.mon51.iap15f2k61s2", "displayName": "STC Mon51 V2.5 · IAP15F2K61S2",
        "version": "2.5.0", "protocol": "stc-mon51-encoded",
        "imageFile": "debug/mon51/iap15f2k61s2-7.2.5S.bin", "imageSha256": IMAGE_SHA256,
        "imageBytes": len(image), "provenanceFile": "debug/mon51/provenance.json",
        "bootloaderVersion": "7.2.5S", "bootloaderStatus": 112,
    }
    provenance = {
        "formatVersion": 1, "vendor": "STC", "monitorVersion": "2.5.0",
        "source": "User-supplied AiCube-ISP-v6.96V-plus.exe", "sourceBytes": 10395648,
        "sourceSha256": VENDOR_SHA256, "basePackSha256": BASE_SHA256,
        "setupSha256": IMAGE_SHA256, "encoding": "Vendor mode 7; 62/42; EFFF target/status marker 12",
        "decodedMonitorSha256": "6F21800CC1C83D0F8FD0A5ED920186051F913EB889E0E92623E1EE63E3C71B2D",
        "decodedResetSha256": "0D16C584EF418DE8B6C451C3447084499A531A2F752568EB4930586DA075D3BD",
        "target": "IAP15F2K61S2", "bootloaderVersion": "7.2.5S", "bootloaderStatus": "70",
        "rightsNotice": "NOTICE.txt", "localBundling": "Requested by the user on 2026-10-09; no public redistribution claim",
        "hardwareEvidence": "2026-10-09: one IAP15F2K61S2 / CH340C; native setup, power activation, user update, breakpoint, step, cold reconnect passed",
        "reservedFlash": "DC00-F3FF; IDE trampoline DBFD-DBFF", "reservedXdata": "0400-06FF",
        "monitorPins": ["P3.0", "P3.1"], "requiresPowerCycleAfterSetup": True,
    }
    (monitor_dir / "provenance.json").write_text(json.dumps(provenance, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (monitor_dir / "NOTICE.txt").write_text(
        "STC Mon51 monitor firmware originates from the user-supplied STC AiCube program.\n"
        "Copyright and other rights in the vendor firmware remain with its rights holders.\n"
        "This is not an open-source license or a grant of redistribution rights.\n"
        "The verified encoded resource is included for the user's requested local IDE workflow.\n"
        "AiCube/STC-ISP executables and Keil driver DLLs are not included or required at runtime.\n"
        "Only IAP15F2K61S2 / ISP 7.2.5S / status 70 is supported by this payload.\n", encoding="utf-8")
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Prepared stc.stc8/0.1.2, {len(manifest['devices'])} devices; monitor restricted to IAP15F2K61S2.")


if __name__ == "__main__":
    main()
