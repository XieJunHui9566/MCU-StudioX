"""Extract exact classic PIC16 identities from the pinned, Apache-2.0 Microchip DFP."""
import hashlib
import json
import pathlib
import shutil
import sys
import xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parent.parent
dfp = pathlib.Path(sys.argv[1]).resolve()
ns = "{http://crownking/edc}"
version = "1.9.176"
pdsc = ET.parse(dfp / "Microchip.PIC16Fxxx_DFP.pdsc").getroot()
assert pdsc.find("releases/release").get("version") == version
facts = []
groups = {}


def attr(element, name):
    return element.get(ns + name)


def size(element):
    return int(attr(element, "endaddr"), 0) - int(attr(element, "beginaddr"), 0)


for identity in pdsc.findall(".//device"):
    name = identity.get("Dname")
    if not name.startswith("PIC16"):
        continue
    source = dfp / "edc" / (name + ".PIC")
    tree = ET.parse(source).getroot()
    architecture = attr(tree, "arch")
    assert architecture in ("16c5x", "16xxxx"), name
    bits = 12 if architecture == "16c5x" else 14
    words = sum(size(e) for e in tree.findall("./" + ns + "ProgramSpace/" + ns + "CodeSector"))
    ram = sum(size(e) for e in tree.findall(".//" + ns + "GPRDataSector") if not attr(e, "shadowidref"))
    eeprom = sum(size(e) for e in tree.findall("./" + ns + "ProgramSpace/" + ns + "EEDataSector"))
    assert words > 0 and ram > 0 and words * bits % 8 == 0, name
    header = dfp / "xc8/pic/include/proc" / (name.lower() + ".h")
    assert header.is_file(), name
    facts.append(dict(id=name, programWords=words, programWordBits=bits, ramBytes=ram, eepromBytes=eeprom))
    if name == "PIC16F877A":
        continue  # Preserve the already released single-device identity and GPIO template.
    # Keep low-voltage variants with their numbered subfamily, without inventing package suffixes.
    number = name.removeprefix("PIC16").removeprefix("LF").removeprefix("F")
    group = "microchip.pic16hv" if name.startswith("PIC16HV") else "microchip.pic16f" + number[0] + "xx"
    device = dict(id=name, displayName=f"{name} · {words}×{bits}-bit Flash / {ram} B RAM",
                  architecture=f"pic{bits}", flashOrigin=0, flashBytes=words * bits // 8,
                  ramOrigin=0, ramBytes=ram, toolsetId="microchip.xc8", toolsetVersion="4.0.0",
                  compilerId="xc8-4.00-pic", cpuFlags=[], defines=[], includeDirectories=[], sources=[],
                  linkerScript="", compileOptions=[], linkOptions=[],
                  pic=dict(compilerTarget=name[3:], programWords=words, programWordBits=bits,
                           eepromBytes=eeprom, dfpVersion=version),
                  templates=[dict(id="xc8-basic", displayName="XC8 基础 C（板级配置待设置）",
                                  description="不假设晶振、引脚及配置位；仅验证 C 编译和编辑，使用前需核对实际板级配置。",
                                  entryFile="templates/basic.c")])
    groups.setdefault(group, []).append((device, source))

assert len(facts) == 122
(root / "src/StudioX.Packages/Pic16Catalog.json").write_text(
    json.dumps(sorted(facts, key=lambda f: f["id"]), indent=2) + "\n", encoding="utf-8")
for group, entries in sorted(groups.items()):
    destination = root / "examples/packs" / group
    destination.mkdir(exist_ok=True)
    (destination / "templates").mkdir(exist_ok=True)
    shutil.copyfile(root / "examples/packs/microchip.pic16f877a/templates/basic.c", destination / "templates/basic.c")
    shutil.copyfile(dfp / "LICENSE.txt", destination / "LICENSE.txt")
    manifest = dict(formatVersion=1, id=group, version="0.1.0",
                    displayName="Microchip " + group.split(".")[1].upper() + " · XC8",
                    vendor="Microchip", devices=[d for d, _ in entries])
    (destination / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    provenance = dict(dfp="Microchip.PIC16Fxxx_DFP", version=version,
                      url="https://packs.download.microchip.com/Microchip.PIC16Fxxx_DFP.1.9.176.atpack",
                      archiveSha256="C5C3CE16F908635A6858FC128494B82AE9AA61B7F5AB932A12843BBDFA8F8F62",
                      derivation="CodeSector words; CodeMemTraits instruction width; non-shadow GPRDataSector bytes; EEDataSector bytes",
                      devices=[dict(id=d["id"], edcSha256=hashlib.sha256(s.read_bytes()).hexdigest()) for d, s in entries])
    (destination / "sources.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
print(f"Verified {len(facts)} identities; generated {len(groups)} subfamily packs plus preserved PIC16F877A pack")
