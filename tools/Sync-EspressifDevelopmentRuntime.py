"""将已索引 ESP 组件同步到受限开发输出目录，用硬链接回收重复磁盘占用。"""

import argparse
import json
import os
import shutil
from pathlib import Path


def no_redirects(root):
    if root.is_symlink() or root.is_junction():
        raise ValueError("开发输出根目录不能是重解析点：" + str(root))
    for directory, children, files in os.walk(root):
        for name in children + files:
            path = Path(directory) / name
            if path.is_symlink() or path.is_junction():
                raise ValueError("开发输出包含重解析点，停止同步：" + str(path))


def sync_component(source, target):
    manifest = json.loads((source / "toolset.json").read_text(encoding="utf-8"))
    paths = sorted([*manifest["sha256"], "toolset.json"])
    expected = set(paths)
    target.mkdir(parents=True, exist_ok=True)
    no_redirects(target)
    removed = linked = copied = 0
    for path in target.rglob("*"):
        if path.is_file() and path.relative_to(target).as_posix() not in expected:
            # 仅删除 bin 中这两个由打包工具管理的组件，绝不处理用户工程或系统 SDK。
            path.unlink()
            removed += 1
    for relative in paths:
        incoming = (source / relative).resolve()
        outgoing = (target / relative).resolve()
        if not incoming.is_relative_to(source) or not outgoing.is_relative_to(target):
            raise ValueError("索引路径越界：" + relative)
        outgoing.parent.mkdir(parents=True, exist_ok=True)
        if outgoing.exists() and os.path.samefile(incoming, outgoing):
            continue
        temporary = outgoing.with_name(outgoing.name + ".studiox-link")
        try:
            os.link(incoming, temporary)
            linked += 1
        except OSError:
            shutil.copy2(incoming, temporary)
            copied += 1
        os.replace(temporary, outgoing)
    print(
        json.dumps(
            {
                "component": manifest["id"],
                "linked": linked,
                "copied": copied,
                "removedStale": removed,
            }
        ),
        flush=True,
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--configuration", choices=["Debug", "Release"], required=True)
    arguments = parser.parse_args()
    workspace = Path(__file__).resolve().parent.parent
    source = (workspace / "artifacts/tool-runtime/toolsets").resolve()
    target = (
        workspace
        / "src/StudioX.Desktop/bin"
        / arguments.configuration
        / "net10.0-windows/runtime/toolsets"
    ).resolve()
    # 比较字面上的本工程输出边界；bin 被改成目录链接时不能把链接目标也当成授权范围。
    permitted = workspace / "src/StudioX.Desktop/bin"
    if not target.is_relative_to(permitted):
        raise ValueError("目标必须留在本工程的 Desktop bin 开发输出内。")
    no_redirects(target)
    for component, version in (
        ("espressif.idf", "5.5.4"),
        ("espressif.esp8266-rtos", "3.4.0"),
    ):
        incoming = (source / component / version).resolve()
        outgoing = (target / component / version).resolve()
        if not incoming.is_relative_to(source) or not outgoing.is_relative_to(target):
            raise ValueError("组件根目录越过已授权的开发输出边界：" + component)
        sync_component(incoming, outgoing)


if __name__ == "__main__":
    main()
