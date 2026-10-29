"""从固定上游源码和已发布的器件包生成保守的 MCU 交集，不猜测相邻容量。"""

import argparse
import hashlib
import json
import re
import zipfile
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--workspace", type=Path, required=True)
    parser.add_argument("--packs", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    zephyr = args.workspace / "zephyr"
    profiles = []
    excluded = []
    for archive in sorted(args.packs.glob("studiox.stm32*.mcupack")):
        with zipfile.ZipFile(archive) as package:
            devices = json.loads(package.read("manifest.json"))["devices"]
        for device in devices:
            name = device["id"]
            match = re.fullmatch(r"(STM32F[14][0-9]{2})[A-Z]([0-9A-Z])(?:[A-Z][0-9])?", name)
            if not match:
                excluded.append(name)
                continue
            line, density = match.groups()
            series = "f1" if line.startswith("STM32F1") else "f4"
            dtsi = zephyr / "dts/arm/st" / series / (line.lower() + "X" + density.lower() + ".dtsi")
            config_path = zephyr / "soc/st/stm32" / ("stm32" + series + "x") / "Kconfig.soc"
            config = config_path.read_text(encoding="utf-8")
            symbol = "SOC_" + line + "X" + density
            # 固定版本虽声明 F401xD Kconfig，但 STM32 HAL 没有对应目标宏，真实编译失败。
            # 不在模板中偷偷改用 xE 目标；等待上游/独立适配验证后再开放。
            if symbol == "SOC_STM32F401XD":
                excluded.append(name)
                continue
            # 有些系列使用统一 xx 符号；只有上游明确声明该符号才采用。
            if not re.search(r"^config " + symbol + r"$", config, re.M):
                symbol = "SOC_" + line + "XX"
            soc = next(
                (
                    m.group(1)
                    for m in re.finditer(r'^\s*default "([a-z0-9]+)" if (.+)$', config, re.M)
                    if re.search(r"\b" + symbol + r"\b", m.group(2))
                ),
                None,
            )
            if not dtsi.exists() or not soc:
                excluded.append(name)
                continue

            sources = {}

            def flattened(path):
                data = path.read_text(encoding="utf-8")
                sources[path.relative_to(zephyr).as_posix()] = hashlib.sha256(
                    path.read_bytes()
                ).hexdigest()

                def include(m):
                    child = (
                        zephyr / "dts/arm" / m.group(1)
                        if "/" in m.group(1)
                        else path.parent / m.group(1)
                    )
                    return flattened(child) if child.exists() else m.group(0)

                return re.sub(r'#include [<"]([^>"\n]+\.dtsi)[>"]', include, data)

            data = flattened(dtsi)

            def memory(label):
                values = re.findall(
                    r'(?:&' + label + r'|' + label + r':\s*[^\{]+)\s*\{([^}]+)\}', data
                )
                regs = [
                    re.search(r'reg\s*=\s*<\s*(0x[0-9a-fA-F]+)\s+DT_SIZE_K\((\d+)\)', block)
                    for block in values
                ]
                regs = [m for m in regs if m]
                return (int(regs[-1][1], 16), int(regs[-1][2]) * 1024) if regs else None

            if memory("flash0") != (device["flashOrigin"], device["flashBytes"]) or memory(
                "sram0"
            ) != (device["ramOrigin"], device["ramBytes"]):
                excluded.append(name)
                continue
            sources[config_path.relative_to(zephyr).as_posix()] = hashlib.sha256(
                config_path.read_bytes()
            ).hexdigest()
            profiles.append(
                dict(
                    deviceId=name,
                    architecture=device["architecture"],
                    flashOrigin=device["flashOrigin"],
                    flashBytes=device["flashBytes"],
                    ramOrigin=device["ramOrigin"],
                    ramBytes=device["ramBytes"],
                    soc=soc,
                    socConfig=symbol,
                    dtsi=dtsi.relative_to(zephyr / "dts/arm").as_posix(),
                    internalClockHz=8000000 if series == "f1" else 16000000,
                    evidence=sources,
                )
            )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(profiles, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )
    print(f"Verified metadata intersection: {len(profiles)}; excluded: {len(excluded)}")
    print("Included families:", ", ".join(sorted(set(p["deviceId"][:9] for p in profiles))))
    print("Excluded:", ", ".join(excluded))


if __name__ == "__main__":
    main()
