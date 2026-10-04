"""Prepare an ARM32 public pack set without replacing existing public revisions.

Input is a completed delivery, not an unvalidated generator output. Publication
adds license material and dated conversion comments only; firmware tokens,
device definitions, templates and linker scripts must remain unchanged.
"""

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import urllib.request
import zipfile


def digest(data):
    return hashlib.sha256(data).hexdigest()


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def encode(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(encode(value))


def checked_path(root, relative):
    if not relative or "\\" in relative or ":" in relative:
        raise ValueError("Invalid relative path: " + relative)
    path = (root / relative).resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError("Path escapes root: " + relative)
    return path


def archive_files(path):
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError("Duplicate archive names: " + str(path))
        result = {name: archive.read(name) for name in names}
    index = json.loads(result.pop("files.sha256.json"))
    if set(index) != set(result):
        raise ValueError("Incomplete file index: " + str(path))
    for name, data in result.items():
        checked_path(Path("virtual-pack-root"), name)
        if digest(data) != index[name].lower():
            raise ValueError("Content hash mismatch: " + name)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--delivery", type=Path, required=True)
    parser.add_argument("--existing-repository", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument(
        "--policy",
        type=Path,
        default=Path(__file__).resolve().parents[1]
        / "examples/packs/arm32-expansion/publication-policy.json",
    )
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError("Use a new output directory")
    policy = read(args.policy)
    catalog = read(args.delivery / "catalog.json")
    matrix = read(args.delivery / "validation/new-model-matrix.json")
    original_index = read(args.existing_repository / "index.json")
    existing_ids = {entry["id"] for entry in original_index["packs"]}
    if len(existing_ids) != len(original_index["packs"]):
        raise ValueError("Existing public index must have one active version per ID")
    licenses = {}
    for source in policy["licenses"]:
        with urllib.request.urlopen(source["url"], timeout=60) as response:
            data = response.read()
        if digest(data) != source["sha256"]:
            raise ValueError("License hash mismatch: " + source["url"])
        licenses[source["key"]] = data
    args.output.mkdir(parents=True)
    # 先保留公开目录的准确版本；本地旧 HAL/SPL 包不得覆盖公开 HAL-only 包。
    for entry in original_index["packs"]:
        source = checked_path(args.existing_repository, entry["path"])
        if (
            source.stat().st_size != entry["size"]
            or digest(source.read_bytes()) != entry["sha256"].lower()
        ):
            raise ValueError("Existing public archive mismatch: " + entry["id"])
        target = checked_path(args.output, entry["path"])
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
    for source in args.existing_repository.glob("*.md"):
        shutil.copyfile(source, args.output / source.name)
    index = original_index["packs"][:]
    published = []
    excluded = []
    public_matrix = []
    for row in catalog:
        if row["origin"] != "new":
            continue
        pack_id = row["id"]
        if pack_id in policy["excluded"]:
            excluded.append(
                dict(id=pack_id, devices=row["devices"], reason=policy["excluded"][pack_id])
            )
            continue
        if pack_id in existing_ids:
            raise ValueError("Refusing to overwrite public pack: " + pack_id)
        source = checked_path(args.delivery, row["file"])
        if digest(source.read_bytes()) != row["sha256"]:
            raise ValueError("Delivery hash mismatch: " + pack_id)
        files = archive_files(source)
        manifest = json.loads(files["manifest.json"])
        if manifest["id"] != pack_id or manifest["version"] != row["version"]:
            raise ValueError("Delivery identity mismatch: " + pack_id)
        expected = {
            pack_id + "/" + d["id"] + "/" + t["id"]
            for d in manifest["devices"]
            for t in d["templates"]
        }
        matches = [r for r in matrix if r["model"].startswith(pack_id + "/")]
        if (
            len(matches) != len(expected)
            or {r["model"] for r in matches} != expected
            or any(r["status"] != "PASS" for r in matches)
        ):
            raise ValueError("Incomplete passed model matrix: " + pack_id)
        if not any(r["compiled"] for r in matches):
            raise ValueError("No representative build: " + pack_id)
        for name, data in files.items():
            if Path(name).suffix.lower() in (".c", ".h", ".s") and re.search(
                rb"may not duplicate|prohibit.{0,40}distribut", data, re.I
            ):
                raise ValueError("Unreviewed distribution restriction: " + pack_id + "/" + name)
        modified = []
        notice = (
            "/* StudioX modification, " + policy["date"] + ": converted the vendor\n"
            " * startup for GCC/StudioX runtime; original notices and full source\n"
            " * remain in vendor/startup/. Vendor terms in licenses/ and the\n"
            " * original source apply to this derived startup. */\n"
        ).encode("utf-8")
        for name in list(files):
            if name.startswith(("system/reset_", "system/startup_")):
                old = files[name]
                files[name] = notice + old
                assert files[name][len(notice) :] == old
                modified.append(name)
        added = []
        license_keys = []
        if pack_id.startswith("geehy."):
            series = (
                "geehy-f0"
                if pack_id.startswith("geehy.apm32f0")
                else "geehy-f1" if pack_id.startswith("geehy.apm32f1") else "geehy-f4"
            )
            license_keys.append(series)
        if pack_id in policy["nxpBsdSupplement"]:
            license_keys.append("nxp-bsd")
        for key in license_keys:
            name = "licenses/publication/" + key + ".txt"
            files[name] = licenses[key]
            added.append(name)
        if modified or added:
            manifest["version"] = policy["revision"]
            files["manifest.json"] = encode(manifest)
            release = dict(
                date=policy["date"],
                baseArchiveSha256=row["sha256"],
                baseVersion=row["version"],
                change="License texts and dated source comments only; firmware tokens and device definitions unchanged.",
                commentedFiles=modified,
                licenseSources=[s for s in policy["licenses"] if s["key"] in license_keys],
            )
            files["publication.json"] = encode(release)
            files["PUBLICATION-NOTICE.md"] = (
                "# Public revision " + manifest["version"] + "\n\n"
                "StudioX packaging/conversion notice, " + policy["date"] + ".\n"
                "Converted startup code remains subject to the original vendor terms.\n"
                "Original notices, disclaimers and startup files are preserved in vendor/startup/.\n"
                "Geehy files and derivatives are released under the Geehy Software License Agreement,\n"
                "when present in licenses/publication/, and only for the corresponding Geehy devices.\n"
                "Other vendors retain their original file-specific licenses. No vendor code is relicensed.\n"
                "This revision adds notices and license texts, without changing firmware instructions,\n"
                "templates, memory maps, compiler options, or hardware capabilities.\n"
            ).encode("utf-8")
        path = (
            policy["vendorFolders"][row["vendor"]]
            + "/"
            + pack_id
            + "-"
            + manifest["version"]
            + ".mcupack"
        )
        target = checked_path(args.output, path)
        target.parent.mkdir(parents=True, exist_ok=True)
        if modified or added:
            files["files.sha256.json"] = encode(
                {name: digest(data) for name, data in sorted(files.items())}
            )
            with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
                for name, data in sorted(files.items()):
                    info = zipfile.ZipInfo(name, (2026, 9, 30, 0, 0, 0))
                    info.compress_type = zipfile.ZIP_DEFLATED
                    archive.writestr(info, data)
        else:
            shutil.copyfile(source, target)
        archive_files(target)
        entry = dict(
            path=path,
            id=pack_id,
            version=manifest["version"],
            sha256=digest(target.read_bytes()),
            size=target.stat().st_size,
        )
        index.append(entry)
        published.append(
            dict(
                **entry,
                vendor=row["vendor"],
                devices=row["devices"],
                compiledModels=row["compiledModels"],
                baseArchiveSha256=row["sha256"],
                noticeRevision=bool(modified or added)
            )
        )
        public_matrix.extend(matches)
    index.sort(key=lambda r: r["path"])
    save(args.output / "index.json", dict(formatVersion=1, packs=index))
    (args.output / "SHA256SUMS.txt").write_text(
        "".join(r["sha256"] + "  " + r["path"] + "\n" for r in index), encoding="utf-8"
    )
    report = args.output / "validation/arm32-2026-09-30"
    save(
        report / "publication.json",
        dict(
            newPacks=published,
            excluded=excluded,
            existingPublicPacksPreserved=len(existing_ids),
            hardwareAccessed=False,
        ),
    )
    save(report / "model-matrix.json", public_matrix)
    save(report / "sources.lock.json", read(args.delivery / "sources.lock.json"))
    save(report / "publication-policy.json", policy)
    print(
        json.dumps(
            dict(
                totalPacks=len(index),
                newPacks=len(published),
                newDevices=sum(r["devices"] for r in published),
                newBuilds=sum(r["compiledModels"] for r in published),
                excludedPacks=len(excluded),
            ),
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
