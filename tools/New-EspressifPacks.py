"""生成原生 SDK 的 Espressif 小型器件包，不下载或复制工具链。"""

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import zipfile


PACK_VERSION = "0.1.1"
TARGETS = (
    ("ESP32-WROOM-32", "esp32-wroom-32", "esp32", "xtensa"),
    ("ESP32-P4", "esp32p4", "esp32p4", "riscv"),
    ("ESP32-S3", "esp32s3", "esp32s3", "xtensa"),
    ("ESP32-C3", "esp32c3", "esp32c3", "riscv"),
    ("ESP32-C5", "esp32c5", "esp32c5", "riscv"),
    ("ESP32-C6", "esp32c6", "esp32c6", "riscv"),
    ("ESP8266", "esp8266", "esp8266", "xtensa"),
)


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def official_templates(idf_root, stage, sources):
    """只复制已锁定的官方小示例；拒绝本机 build/sdkconfig 和被修改的上游文件。"""
    result = []
    for example in sources["examples"]:
        prefix = example["examplePath"] + "/"
        relative_directory = "templates/idf-" + example["id"]
        destination = stage / relative_directory
        hashes = {}
        for source_path, expected in sources["filesSha256"].items():
            if not (source_path.startswith(prefix) or source_path == "LICENSE"):
                continue
            content = (idf_root / source_path).read_bytes().replace(b"\r\n", b"\n")
            if hashlib.sha256(content).hexdigest() != expected:
                raise ValueError("Official IDF v5.5.4 template hash mismatch: " + source_path)
            relative = source_path[len(prefix) :] if source_path.startswith(prefix) else "LICENSE"
            output = destination / relative
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_bytes(content)
            hashes[relative] = expected
        write_json(
            destination / "template-source.json",
            {
                "sdkVersion": sources["sdkVersion"],
                "upstreamCommit": sources["upstreamCommit"],
                "examplePath": example["examplePath"],
                "upstream": "https://github.com/espressif/esp-idf/tree/v5.5.4/"
                + example["examplePath"],
                "filesSha256": hashes,
                "license": "Example source headers retain CC0/public-domain terms; LICENSE preserves the upstream SDK Apache-2.0 license.",
                "modifications": "Only CRLF is normalized to LF in the pack. Project creation changes project name, locks IDF target and applies verified WROOM-32 Flash default.",
            },
        )
        result.append(
            {
                "id": example["id"],
                "displayName": example["displayName"],
                "description": example["description"],
                "entryFile": relative_directory + "/" + example["entryFile"],
                "espressifExample": {"exampleDirectory": relative_directory},
            }
        )
    return result


def archive_index(archive):
    with zipfile.ZipFile(archive) as package:
        manifest = json.loads(package.read("manifest.json"))
        provenance = package.read("provenance.json")
    device = manifest["devices"][0]
    sdk = device["espressif"]
    return {
        "file": archive.name,
        "id": manifest["id"],
        "version": manifest["version"],
        "sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
        "devices": [device["id"]],
        "framework": sdk["framework"],
        "sdkVersion": sdk["sdkVersion"],
        "target": sdk["target"],
        "provenanceSha256": hashlib.sha256(provenance).hexdigest(),
    }


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument("--output", type=Path, required=True, help="New output directory")
    parser.add_argument("--cli", type=Path, help="Built StudioX.Cli.dll")
    parser.add_argument(
        "--idf-root", type=Path, required=True, help="Complete local official ESP-IDF v5.5.4 root"
    )
    parser.add_argument(
        "--esp8266-pack", type=Path, help="Existing unchanged ESP8266 0.1.0 archive"
    )
    arguments = parser.parse_args()
    repository = Path(__file__).resolve().parents[1]
    cli = arguments.cli or repository / "src/StudioX.Cli/bin/Debug/net10.0/StudioX.Cli.dll"
    if not cli.is_file():
        raise RuntimeError("Build the CLI before generating packs")
    source = repository / "examples/packs/espressif"
    sources = json.loads((source / "idf-template-sources.json").read_text(encoding="utf-8"))
    version_file = "tools/cmake/version.cmake"
    if (
        hashlib.sha256(
            (arguments.idf_root / version_file).read_bytes().replace(b"\r\n", b"\n")
        ).hexdigest()
        != sources["filesSha256"][version_file]
    ):
        raise ValueError("Require the fixed official ESP-IDF v5.5.4 template source")
    legacy_archive = (
        arguments.esp8266_pack
        or repository / "artifacts/packs/Espressif-0.1.0/espressif.esp8266-0.1.0.mcupack"
    )
    arguments.output.mkdir(parents=True, exist_ok=False)
    index = []

    for device_id, suffix, target, architecture in TARGETS:
        is_8266 = target == "esp8266"
        pack_version = "0.1.0" if is_8266 else PACK_VERSION
        framework = "esp8266-rtos-sdk" if is_8266 else "esp-idf"
        sdk_version = "3.4.0" if is_8266 else "5.5.4"
        pack_id = "espressif." + suffix
        if is_8266:
            if not legacy_archive.is_file():
                raise FileNotFoundError(
                    "Supply the unchanged ESP8266 0.1.0 archive with --esp8266-pack"
                )
            archive = arguments.output / "espressif.esp8266-0.1.0.mcupack"
            shutil.copyfile(legacy_archive, archive)
            entry = archive_index(archive)
            if (
                entry["id"] != pack_id
                or entry["version"] != pack_version
                or entry["target"] != "esp8266"
                or entry["sdkVersion"] != "3.4.0"
            ):
                raise ValueError("Legacy ESP8266 archive identity mismatch")
            index.append(entry)
            continue
        stage = arguments.output / "source" / pack_id
        templates = stage / "templates"
        templates.mkdir(parents=True)
        project_templates = official_templates(arguments.idf_root, stage, sources)
        shutil.copyfile(source / "README.md", stage / "README.md")
        device = {
            "id": device_id,
            "displayName": device_id,
            "architecture": architecture,
            "flashOrigin": 0,
            "flashBytes": 4 * 1024 * 1024 if device_id == "ESP32-WROOM-32" else 0,
            "ramOrigin": 0,
            "ramBytes": 520 * 1024 if device_id == "ESP32-WROOM-32" else 0,
            "toolsetId": "espressif.esp8266-rtos" if is_8266 else "espressif.idf",
            "toolsetVersion": sdk_version,
            "compilerId": "esp8266-rtos" if is_8266 else "esp-idf",
            "cpuFlags": [],
            "defines": [],
            "includeDirectories": [],
            "sources": [],
            "linkerScript": "",
            "compileOptions": [],
            "linkOptions": [],
            "templates": project_templates,
            "espressif": {"framework": framework, "target": target, "sdkVersion": sdk_version},
        }
        write_json(
            stage / "manifest.json",
            {
                "formatVersion": 1,
                "id": pack_id,
                "version": pack_version,
                "displayName": f"{device_id} · {'RTOS SDK' if is_8266 else 'ESP-IDF'} {sdk_version}",
                "vendor": "Espressif",
                "devices": [device],
            },
        )
        upstream = (
            "https://github.com/espressif/ESP8266_RTOS_SDK/tree/v3.4"
            if is_8266
            else "https://github.com/espressif/esp-idf/tree/v5.5.4"
        )
        write_json(
            stage / "provenance.json",
            {
                "upstream": upstream,
                "sdkVersion": sdk_version,
                "target": target,
                "sdkIncluded": False,
                "source": (
                    "StudioX legacy ESP8266 templates."
                    if is_8266
                    else "Original ESP-IDF v5.5.4 examples; shared SDK remains outside the pack."
                ),
                "officialTemplates": None if is_8266 else sources,
                "capacityEvidence": (
                    "ESP32-WROOM-32 Datasheet v3.8, sections 1.1 and 1.2"
                    if device_id == "ESP32-WROOM-32"
                    else "Generic SDK target; physical Flash, PSRAM and usable memory require the actual module configuration."
                ),
            },
        )
        archive = arguments.output / f"{pack_id}-{pack_version}.mcupack"
        subprocess.run(["dotnet", str(cli), "pack", str(stage), str(archive)], check=True)
        index.append(archive_index(archive))
    write_json(arguments.output / "index.json", index)


if __name__ == "__main__":
    main()
