"""Format owned Python/C sources and verify that executable content stays identical."""

from __future__ import annotations

import argparse
import ast
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import urllib.request
import zipfile

REPOSITORY = Path(__file__).resolve().parents[1]
TOOL_DIRECTORY = REPOSITORY / "artifacts/validation/source-style-current"
TOOL_MANIFEST = Path(__file__).with_name("source-style-tools.json")

# 先识别字符串，避免把字面量里的 // 或 /* 当作注释删除。
C_TOKEN = re.compile(
    r'"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|//[^\n]*|/\*.*?\*/|'
    r"[A-Za-z_][A-Za-z_0-9]*|[0-9]+(?:\.[0-9]+)?|"
    r"<<=|>>=|\.\.\.|##|->|\+\+|--|&&|\|\||<=|>=|==|!=|<<|>>|"
    r"\+=|-=|\*=|/=|%=|&=|\|=|\^=|[^\s]",
    re.DOTALL,
)


def prepare_tools() -> None:
    """Download only pinned formatting tools into the disposable validation directory."""
    TOOL_DIRECTORY.mkdir(parents=True, exist_ok=True)
    records = []
    for item in json.loads(TOOL_MANIFEST.read_text(encoding="utf-8")):
        archive_path = TOOL_DIRECTORY / item["filename"]
        if not archive_path.is_file():
            with urllib.request.urlopen(item["url"], timeout=60) as response:
                archive_path.write_bytes(response.read())
        digest = hashlib.sha256(archive_path.read_bytes()).hexdigest()
        if digest != item["sha256"]:
            raise ValueError("Formatter archive hash mismatch: " + item["filename"])
        destination = TOOL_DIRECTORY / item["directory"]
        destination.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(archive_path) as archive:
            # 格式器与许可证来自固定上游版本；拒绝归档路径逃出专用工具目录。
            for entry in archive.infolist():
                if (
                    not (destination / entry.filename)
                    .resolve()
                    .is_relative_to(destination.resolve())
                ):
                    raise ValueError("Formatter archive path escapes its directory")
            # 只运行排版规则；跨平台兼容性数据库约 280 MiB，无需解包到开发磁盘。
            for entry in archive.infolist():
                if not entry.filename.startswith("compatibility_profiles/"):
                    archive.extract(entry, destination)
        records.append({**item, "bytes": archive_path.stat().st_size})
    (TOOL_DIRECTORY / "tool-sources.json").write_text(
        json.dumps(records, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )


def python_sources() -> list[Path]:
    paths = list((REPOSITORY / "tools").glob("*.py"))
    paths.append(REPOSITORY / "tools/StudioX.StcIspValidation/guard_offline.py")
    paths.append(REPOSITORY / "src/StudioX.Engine/Resources/studiox-stcgal-guard.py")
    return sorted(path for path in paths if path.is_file())


def c_sources() -> list[Path]:
    # examples/packs 只包含本仓库维护的模板；厂商 SDK 与生成验收工程不在此列表中。
    paths = list((REPOSITORY / "examples/packs").rglob("*.c"))
    paths += list((REPOSITORY / "examples/packs").rglob("*.h"))
    paths += list((REPOSITORY / "src/StudioX.Engine/Resources/Lvgl").glob("*.c"))
    paths += list((REPOSITORY / "examples/components").rglob("*.h"))
    for directory in ("StudioX.Ag32SystemValidation", "StudioX.Ag32PeripheralValidation"):
        paths += list((REPOSITORY / "tools" / directory / "native").glob("*.c"))
        paths += list((REPOSITORY / "tools" / directory / "native").glob("*.h"))
    paths += [
        REPOSITORY / "tools/stcgal-portable-launcher.c",
        REPOSITORY / "tools/StudioX.WchValidation/InterruptRam/main.c",
        REPOSITORY / "tools/StudioX.RtosValidation/Fixtures/freertos-snapshot.c",
    ]
    return sorted(path for path in paths if path.is_file())


def c_tokens(source: str) -> list[str]:
    # C 的续行在词法分析前拼接；保留其余 token 和字符串的原始拼写与顺序。
    source = re.sub(r"\\\r?\n", "", source)
    return [token for token in C_TOKEN.findall(source) if not token.startswith(("//", "/*"))]


def record_result(path: Path, original: str, formatted: str, kind: str, write: bool) -> dict:
    formatted = formatted.replace("\r\n", "\n").rstrip() + "\n"
    changed = original != formatted
    if write and changed:
        path.write_text(formatted, encoding="utf-8", newline="\n")
    return {
        "file": path.relative_to(REPOSITORY).as_posix(),
        "kind": kind,
        "changed": changed,
        "executableContentIdentical": True,
    }


def format_sources(write: bool) -> list[dict]:
    python_directory = TOOL_DIRECTORY / "python-libs"
    sys.path.insert(0, str(python_directory))
    try:
        import black
    except ImportError as error:
        raise RuntimeError("Run tools/Format-FirstPartySources.ps1 -PrepareTools first") from error
    if black.__version__ != "24.10.0":
        raise RuntimeError("Expected pinned Black 24.10.0")
    formatter = python_directory / "clang_format/data/bin/clang-format.exe"
    version = subprocess.check_output([str(formatter), "--version"], text=True)
    if "18.1.8" not in version:
        raise RuntimeError("Expected pinned clang-format 18.1.8")

    results = []
    for path in python_sources():
        original = path.read_text(encoding="utf-8-sig")
        try:
            formatted = black.format_file_contents(
                original, fast=False, mode=black.Mode(line_length=100, string_normalization=False)
            )
        except black.NothingChanged:
            formatted = original
        # 包脚本含完整 C、汇编、链接脚本；AST 相等也要求这些生成字符串逐字不变。
        if ast.dump(ast.parse(original), include_attributes=False) != ast.dump(
            ast.parse(formatted), include_attributes=False
        ):
            raise ValueError("Python AST changed while formatting: " + str(path))
        compile(formatted, str(path), "exec")
        results.append(record_result(path, original, formatted, "Python", write))

    for path in c_sources():
        original = path.read_text(encoding="utf-8-sig")
        formatted = subprocess.check_output(
            [str(formatter), "--style=file", str(path)], text=True, encoding="utf-8"
        )
        if c_tokens(original) != c_tokens(formatted):
            raise ValueError("C tokens changed while formatting: " + str(path))
        results.append(record_result(path, original, formatted, "C/H", write))

    TOOL_DIRECTORY.mkdir(parents=True, exist_ok=True)
    (TOOL_DIRECTORY / "python-c-results.json").write_text(
        json.dumps(results, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    return results


def main() -> int:
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument(
        "--write", action="store_true", help="Apply formatting after equivalence checks"
    )
    parser.add_argument(
        "--prepare-tools", action="store_true", help="Download pinned formatting tools"
    )
    args = parser.parse_args()
    if args.prepare_tools:
        prepare_tools()
    results = format_sources(args.write)
    changed = sum(item["changed"] for item in results)
    print(
        f"Verified {len(results)} Python/C files; {changed} require formatting; write={args.write}"
    )
    return 0 if args.write or changed == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
