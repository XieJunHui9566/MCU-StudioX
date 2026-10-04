"""Package the local Alientek Zephyr board example without an SDK or download."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import zipfile


PACK_ID = "zephyr.alientek-explorer-v35"
PACK_VERSION = "0.1.1"
SOURCE = Path(__file__).resolve().parents[1] / "examples" / "packs" / PACK_ID


def collect_payload() -> dict[str, bytes]:
    manifest_path = SOURCE / "zephyr-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if (
        manifest.get("schema") != "studiox.zephyr-pack"
        or manifest.get("formatVersion") != 1
        or manifest.get("id") != PACK_ID
        or manifest.get("version") != PACK_VERSION
        or manifest.get("zephyrVersion") != "4.4.2"
        or manifest.get("experimental") is not True
    ):
        raise ValueError("Zephyr experimental manifest identity changed")

    payload: dict[str, bytes] = {}
    for path in sorted(SOURCE.rglob("*")):
        if path.is_symlink():
            raise ValueError(f"Symlink is not allowed in pack source: {path}")
        if not path.is_file():
            continue
        name = path.relative_to(SOURCE).as_posix()
        if name in {"manifest.json", "files.sha256.json"}:
            raise ValueError(f"Reserved source name: {name}")
        payload[name] = path.read_bytes()

    if not payload or "zephyr-manifest.json" not in payload:
        raise ValueError("Zephyr experimental pack source is incomplete")
    for board in manifest["boards"]:
        for template in board["templates"]:
            for source_path in template["files"].values():
                if not source_path.startswith("templates/") or source_path not in payload:
                    raise ValueError(f"Missing template resource: {source_path}")
    return payload


def archive_bytes(payload: dict[str, bytes]) -> bytes:
    # 索引覆盖原始资源，索引自身不递归计算；ZIP 时间戳固定以便核对重复构建。
    checksums = {name: hashlib.sha256(data).hexdigest() for name, data in sorted(payload.items())}
    indexed_payload = dict(payload)
    indexed_payload["files.sha256.json"] = (
        json.dumps(checksums, ensure_ascii=False, indent=2) + "\n"
    ).encode("utf-8")

    import io

    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(indexed_payload.items()):
            entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            entry.create_system = 3
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
    return stream.getvalue()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output", type=Path, required=True, help="Output directory for the .mcupack"
    )
    args = parser.parse_args()

    output = args.output.resolve()
    if output == SOURCE or SOURCE in output.parents:
        raise ValueError("Output directory cannot be inside the pack source")

    payload = collect_payload()
    data = archive_bytes(payload)
    output.mkdir(parents=True, exist_ok=True)
    archive = output / f"{PACK_ID}-{PACK_VERSION}.mcupack"
    if archive.exists():
        if archive.read_bytes() != data:
            raise FileExistsError(
                f"Existing package differs; choose another output directory: {archive}"
            )
    else:
        archive.write_bytes(data)

    print(
        f"{archive} ({len(payload)} indexed resources, SHA-256 {hashlib.sha256(data).hexdigest()})"
    )


if __name__ == "__main__":
    main()
