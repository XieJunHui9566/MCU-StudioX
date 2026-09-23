"""Build the isolated GD32E51x StudioX pack from SHA-locked official sources.

This recipe performs no network or hardware operations. It requires 7-Zip and
the already-built StudioX CLI. See examples/packs/gigadevice.gd32e51x/README.md.
"""

import argparse
import hashlib
import importlib.util
import json
import shutil
import subprocess
import tempfile
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RECIPE = ROOT / "examples/packs/gigadevice.gd32e51x"
LOCK = json.loads((RECIPE / "sources.json").read_text(encoding="utf-8"))
SPEC = importlib.util.spec_from_file_location("studiox_gd32_common", ROOT / "tools/New-Gd32Packs.py")
common = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(common)
common.RECIPE = RECIPE
common.SOURCE_LOCK["GD32E51x"] = LOCK["sdk"]


def verified(path: Path, expected: str) -> bytes:
    data = path.read_bytes()
    actual = hashlib.sha256(data).hexdigest()
    if actual != expected:
        raise ValueError(f"SHA-256 mismatch: {path}: {actual}")
    return data


class E51Sdk(common.Sdk):
    def __init__(self, folder: Path):
        super().__init__("GD32E51x", folder)
        self.extra = {
            "Firmware/CMSIS/cmsis_gcc.h": verified(folder / LOCK["cmsisGcc"]["file"], LOCK["cmsisGcc"]["sha256"]),
            "Firmware/CMSIS/mpu_armv8.h": verified(folder / LOCK["cmsisMpu"]["file"], LOCK["cmsisMpu"]["sha256"]),
        }
        self.entries.update({name: name for name in self.extra})

    def read(self, relative):
        return self.extra[relative] if relative in self.extra else super().read(relative)


def e51_startup(sdk, device):
    suffix = "hd" if device["id"].startswith("GD32E513") else "cl"
    name = sdk.device_root + "Source/ARM/startup_gd32e51x_" + suffix + ".s"
    if name not in sdk.entries:
        raise ValueError(f"Official startup missing: {name}")
    return name


def extract_addon(folder: Path, target: Path) -> tuple[Path, Path]:
    seven_zip = shutil.which("7z") or shutil.which("7z.exe")
    if not seven_zip:
        raise FileNotFoundError("7-Zip 7z.exe is required")
    info = LOCK["addon"]
    outer_archive = folder / info["archive"]
    verified(outer_archive, info["sha256"])
    outer = target / "outer"
    inner = target / "inner"
    subprocess.run([seven_zip, "x", str(outer_archive), "-o" + str(outer), "-y"],
                   check=True, stdout=subprocess.DEVNULL)
    inner_archive = outer / "GD32E51x_AddOn_v1.5.0.7z"
    verified(inner_archive, info["innerSha256"])
    subprocess.run([seven_zip, "x", str(inner_archive), "-o" + str(inner), "-y"],
                   check=True, stdout=subprocess.DEVNULL)
    addon = inner / "GD32E51x_AddOn_v1.5.0"
    pack = addon / "GigaDevice.GD32E51x_DFP.1.5.0.pack"
    verified(pack, info["packSha256"])
    with zipfile.ZipFile(pack) as archive:
        pdsc_bytes = archive.read("GigaDevice.GD32E51x_DFP.pdsc")
    pdsc = target / "GigaDevice.GD32E51x_DFP.pdsc"
    pdsc.write_bytes(pdsc_bytes)
    verified(pdsc, info["pdscSha256"])
    addon_license = addon / "SOFTWARE LICENSE AGREEMENT SLA-GD0006-version1.1.pdf"
    return pdsc, addon_license


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sources", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    sources = args.sources.resolve()
    output = args.output.resolve()
    if (output / "index.json").exists() or (output / "gigadevice.gd32e51x").exists():
        raise ValueError(f"Output already contains a pack: {output}")
    output.mkdir(parents=True, exist_ok=True)
    cli = ROOT / "src/StudioX.Cli/bin/Debug/net10.0/StudioX.Cli.dll"
    if not cli.is_file():
        raise FileNotFoundError(f"Build StudioX CLI first: {cli}")

    with tempfile.TemporaryDirectory(prefix="studiox-gd32e51x-") as temp_name:
        pdsc, addon_license = extract_addon(sources, Path(temp_name))
        devices = list(common.pdsc_devices(pdsc))
        devices = [d for d in devices if d["id"].startswith(("GD32E513", "GD32E515", "GD32E517", "GD32E518"))]
        if len(devices) != 20 or len({d["id"] for d in devices}) != 20:
            raise ValueError(f"Unexpected official DFP inventory: {len(devices)}")
        for device in devices:
            device["defines"] = ["GD32E51X_HD" if device["id"].startswith("GD32E513") else "GD32E51X_CL",
                                 "GD32E518" if device["id"].startswith("GD32E518") else "USE_STDPERIPH_DRIVER"]
            if device["id"].startswith("GD32E518"):
                device["defines"].append("USE_STDPERIPH_DRIVER")
        # DFP 1.5.0 has a single erratum. The official E513xx Datasheet Rev1.6,
        # table 2-1, specifies 128 KiB for every xE device, including ZE.
        ze = next(d for d in devices if d["id"] == "GD32E513ZE")
        if ze["ram"] != 96 * 1024 or ze["flash"] != 512 * 1024:
            raise ValueError("E513ZE DFP erratum changed; re-review official datasheet")
        ze["ram"] = 128 * 1024
        sdk = E51Sdk(sources)
        common.startup_name = e51_startup
        result = common.build_pack("GD32E51x", sdk, devices, pdsc, output, cli)
        sdk.zip.close()
        stage = output / "gigadevice.gd32e51x/source"
        shutil.copy2(addon_license, stage / "licenses" / addon_license.name)

    manifest_path = stage / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    for device in manifest["devices"]:
        device["templates"][0]["id"] = "spl"
        device["templates"][0]["displayName"] = "标准外设库 · 内部时钟最小工程"
        device["templates"].append({
            "id": "cmsis", "displayName": "CMSIS · 内部时钟最小工程",
            "description": "使用厂商 CMSIS 和内部 IRC8M，不操作任何板级引脚。",
            "entryFile": "templates/cmsis.c"})
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    (stage / "templates/main.c").write_text(
        '#include <stdint.h>\n#include "gd32e51x.h"\n#include "gd32e51x_rcu.h"\n'
        'volatile uint32_t app_counter;\nint main(void) { for (;;) { app_counter = rcu_clock_freq_get(CK_SYS); __NOP(); } }\n',
        encoding="utf-8")
    (stage / "templates/cmsis.c").write_text(
        '#include <stdint.h>\n#include "gd32e51x.h"\n'
        'volatile uint32_t app_counter;\nint main(void) { for (;;) { app_counter = SystemCoreClock; __NOP(); } }\n',
        encoding="utf-8")
    provenance_path = stage / "provenance.json"
    provenance = json.loads(provenance_path.read_text(encoding="utf-8"))
    provenance.update(addonVersion=LOCK["addon"]["version"], addonSourceUrl=LOCK["addon"]["url"],
        addonArchiveSha256=LOCK["addon"]["sha256"], addonInnerSha256=LOCK["addon"]["innerSha256"],
        addonDfpPackSha256=LOCK["addon"]["packSha256"],
        cmsisGccSource=LOCK["cmsisGcc"]["url"], cmsisGccSha256=LOCK["cmsisGcc"]["sha256"],
        cmsisMpuSource=LOCK["cmsisMpu"]["url"], cmsisMpuSha256=LOCK["cmsisMpu"]["sha256"],
        memoryCorrection={"device": "GD32E513ZE", "dfpRamBytes": 96 * 1024,
            "datasheetRamBytes": 128 * 1024,
            "datasheet": "https://www.gd32mcu.com/data/documents/datasheet/GD32E513xx_Datasheet_Rev1.6.pdf",
            "table": "2-1"},
        omittedDevices=["GD32EPRTRET6A", "GD32EPRTVET6A"])
    provenance_path.write_text(json.dumps(provenance, ensure_ascii=False, indent=2), encoding="utf-8")
    archive = output / result["file"]
    final_archive = archive.with_name(archive.stem + ".final.mcupack")
    subprocess.run(["dotnet", str(cli), "pack", str(stage), str(final_archive)], check=True)
    final_archive.replace(archive)
    result["sha256"] = common.sha256(archive)
    result["bytes"] = archive.stat().st_size
    (output / "index.json").write_text(json.dumps([result], ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"GD32E51x: {len(devices)} models, two templates, {result['sha256']}")


if __name__ == "__main__":
    main()
