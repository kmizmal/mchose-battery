#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把 MCHOSE_Battery 皮肤与 C# 插件打包成可直接安装的 .rmskin。

做的事：
  1. 把源码树复制到 dist/，并把 .ini / .inc 转成 UTF-16 LE + BOM
     （Rainmeter 只认这个编码，UTF-8 会按 ANSI 解码导致中文乱码）；
  2. 生成 RMSKIN.ini 元数据（.rmskin 本质是带元数据的 zip）；
  3. 把插件 DLL 放进包内的 Plugins/ 目录 —— 安装器会自动装到 Rainmeter 的插件目录；
  4. 压缩成 .rmskin；
  5. 可选：直接投影到本机（--install），同时复制 DLL。

用法：
    python build_plugin.py            # 先构建插件
    python build_package.py           # 打包
    python build_package.py --install # 打包并安装到本机 Rainmeter
"""

from __future__ import annotations

import argparse
import shutil
import struct
import sys
import zipfile
from pathlib import Path

# 同 build_plugin.py：CI 的 cp1252 控制台无法输出中文，统一转 UTF-8。
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

CONFIG = "MCHOSE_Battery"
PACKAGE_NAME = "MCHOSE_Battery_1.2"
PLUGIN_NAME = "MCHOSE_Battery"
UTF16_SUFFIXES = {".ini", ".inc"}

ROOT = Path(__file__).resolve().parent
SRC = ROOT / CONFIG
DIST = ROOT / "dist"
PLUGIN_DLL = ROOT / "dist" / "plugin" / f"{PLUGIN_NAME}.dll"
RAINMETER_PLUGINS = Path.home() / "AppData" / "Local" / "Programs" / "Rainmeter" / "Plugins"

RMSKIN_INI = """[rmskin]
Name=MCHOSE Battery
Author=WorkBuddy
Version=1.2
LoadType=Skin
Load={config}\\Keyboard\\G87Battery.ini
MinimumRainmeter=4.5
MinimumWindows=10
""".format(config=CONFIG)


def stage() -> Path:
    target = DIST / CONFIG
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(SRC, target, ignore=shutil.ignore_patterns(
        "*.tmp", "__pycache__", "*.pyc", "v7-state.txt"))

    for path in target.rglob("*"):
        if path.is_file() and path.suffix.lower() in UTF16_SUFFIXES:
            text = path.read_text(encoding="utf-8")
            path.write_text(text, encoding="utf-16")   # LE + BOM
    return target


def build_package(config_dir: Path) -> Path:
    if not PLUGIN_DLL.exists():
        raise SystemExit(
            f"缺少插件 DLL：{PLUGIN_DLL}\n先运行 python build_plugin.py 生成。")

    DIST.mkdir(parents=True, exist_ok=True)
    package = DIST / (PACKAGE_NAME + ".rmskin")
    if package.exists():
        package.unlink()

    meta = DIST / "RMSKIN.ini"
    meta.write_text(RMSKIN_INI, encoding="utf-8")

    with zipfile.ZipFile(package, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(meta, "RMSKIN.ini")
        # 安装器按宿主位数选目录：IsWin32Build() ? "32bit" : "64bit"
        # 放在 Plugins/ 根目录下不会被识别，必须带位数子目录。
        z.write(PLUGIN_DLL, f"Plugins/64bit/{PLUGIN_NAME}.dll")
        for path in sorted(config_dir.rglob("*")):
            if path.is_file():
                z.write(path, f"Skins/{CONFIG}/{path.relative_to(config_dir).as_posix()}")
    meta.unlink()

    # .rmskin 不是普通 zip：末尾要追加 16 字节 PackageFooter，
    # 否则安装器按 strcmp(key, "RMSKIN") 判定失败，直接拒绝这个包。
    #   struct PackageFooter { __int64 size; BYTE flags; char key[7]; };
    #   size = zip 部分的大小（= 当前文件长度），flags = 0
    zip_size = package.stat().st_size
    with package.open("ab") as f:
        f.write(struct.pack("<qB7s", zip_size, 0, b"RMSKIN\0"))

    return package


def verify_package(package: Path) -> None:
    """按安装器的判定规则自检，避免产出无法安装的包。"""
    raw = package.read_bytes()
    size, flags, key = struct.unpack("<qB7s", raw[-16:])
    if key.rstrip(b"\x00") != b"RMSKIN":
        raise SystemExit(f"footer 魔数错误：{key!r}")
    if size != len(raw) - 16:
        raise SystemExit(f"footer.size={size} 与 zip 实际大小 {len(raw) - 16} 不符")
    if flags != 0:
        raise SystemExit(f"footer.flags 应为 0，实际 {flags}")

    with zipfile.ZipFile(package) as z:
        names = z.namelist()
        if "RMSKIN.ini" not in names:
            raise SystemExit("包内缺少 RMSKIN.ini")

        meta = z.read("RMSKIN.ini").decode("utf-8")
        load = [line for line in meta.splitlines() if line.lower().startswith("load=")]
        if not load:
            raise SystemExit("RMSKIN.ini 缺少 Load= 键")
        rel = load[0].split("=", 1)[1].strip().replace("\\", "/")
        if f"Skins/{rel}" not in names:
            raise SystemExit(f"Load 指向的皮肤不在包里：Skins/{rel}")

        plugins = [n for n in names if n.startswith("Plugins/64bit/") and n.endswith(".dll")]
        if not plugins:
            raise SystemExit("缺少 Plugins/64bit/*.dll（安装器按宿主位数选目录）")

        dll = z.read(plugins[0])
        pe = struct.unpack_from("<I", dll, 0x3C)[0]
        machine = struct.unpack_from("<H", dll, pe + 4)[0]
        if machine != 0x8664:
            raise SystemExit(f"插件不是 x64（machine=0x{machine:04X}）")

    print(f"包校验通过：footer OK，{len(names)} 个条目，插件 {plugins[0]}（x64）")


# 运行期缓存：安装时不要用包里的旧值覆盖用户当前的最后读数
RUNTIME_FILES = {"v7-state.txt"}


def install(config_dir: Path) -> tuple[Path, Path]:
    """覆盖式安装。

    不走「删除旧目录再复制」：Rainmeter 正加载皮肤时目录/图片可能被占用，
    删除会失败。改成逐文件覆盖，再清掉源里已经不存在的残留文件。
    """
    skins = Path.home() / "Documents" / "Rainmeter" / "Skins" / CONFIG
    skins.mkdir(parents=True, exist_ok=True)

    for src in config_dir.rglob("*"):
        if not src.is_file() or src.name in RUNTIME_FILES:
            continue
        dst = skins / src.relative_to(config_dir)
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)

    for dst in sorted(skins.rglob("*"), reverse=True):
        if dst.is_file() and not (config_dir / dst.relative_to(skins)).exists():
            try:
                dst.unlink()
            except OSError:
                pass

    plugin_target = RAINMETER_PLUGINS / f"{PLUGIN_NAME}.dll"
    if RAINMETER_PLUGINS.exists():
        try:
            shutil.copy2(PLUGIN_DLL, plugin_target)
        except PermissionError:
            print(f"！插件 DLL 被占用，未覆盖：{plugin_target}")
            print("  退出 Rainmeter 后再运行一次 --install，或手动替换。")
    return skins, plugin_target


def main() -> int:
    ap = argparse.ArgumentParser(description="打包 MCHOSE_Battery 雨滴皮肤与插件（V7 鼠标 + G87 键盘）")
    ap.add_argument("--install", action="store_true", help="同时安装到本机 Rainmeter")
    args = ap.parse_args()

    config_dir = stage()
    package = build_package(config_dir)
    verify_package(package)
    size = package.stat().st_size / 1024
    print(f"已生成: {package}  ({size:.1f} KB)")

    if args.install:
        skins, plugin = install(config_dir)
        print(f"已安装皮肤: {skins}")
        print(f"已安装插件: {plugin}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
