#!/usr/bin/env python3
"""把 MCHOSE_Battery 皮肤与 C# 插件打包成可直接安装的 .rmskin。

做的事：
  1. 把源码树复制到 dist/，并把 .ini / .inc 转成 UTF-16 LE + BOM
  2. 生成 RMSKIN.ini 元数据（.rmskin 本质是 zip + 16 字节 PackageFooter）；
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
import hashlib
import os
import shutil
import struct
import sys
import zipfile
from pathlib import Path

def _force_utf8_streams() -> None:
    """CI（cp1252）控制台无法输出中文，统一转 UTF-8。"""
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            reconfigure(encoding="utf-8", errors="replace")


_force_utf8_streams()

CONFIG = "MCHOSE_Battery"
# 版本号唯一源头：包名、rmskin 元数据都从这里派生，别再各写一份。
# 皮肤 ini 的 [Metadata] Version 也要跟着改（Rainmeter 管理界面显示的是它，不是包版本）。
VERSION = "1.3"
PACKAGE_NAME = f"MCHOSE_Battery_{VERSION}"
PLUGIN_NAME = "MCHOSE_Battery"
UTF16_SUFFIXES = {".ini", ".inc"}

ROOT = Path(__file__).resolve().parent
SRC = ROOT / CONFIG
DIST = ROOT / "dist"
PLUGIN_DLL = ROOT / "dist" / "plugin" / f"{PLUGIN_NAME}.dll"

RUNTIME_FILES = {"v7-state.txt", "v9-state.txt"}
RUNTIME_PATTERNS = ("*-state.txt",)

PROGRAM_FILES = Path(os.environ.get("ProgramFiles", r"C:\Program Files"))
PLUGIN_DIR_CANDIDATES = (
    Path.home() / "AppData" / "Local" / "Programs" / "Rainmeter" / "Plugins",
    PROGRAM_FILES / "Rainmeter" / "Plugins",
    Path.home() / "AppData" / "Roaming" / "Rainmeter" / "Plugins",
)
DEFAULT_PLUGIN_DIR = PLUGIN_DIR_CANDIDATES[0]

RMSKIN_INI = """[rmskin]
Name=MCHOSE Battery
Author=kmizmal
Version={version}
LoadType=Skin
Load={config}\\Keyboard\\G87Battery.ini
MinimumRainmeter=4.5
MinimumWindows=10
""".format(config=CONFIG, version=VERSION)

def _is_runtime_file(name: str) -> bool:
    if name in RUNTIME_FILES:
        return True
    return any(Path(name).match(pat) for pat in RUNTIME_PATTERNS)


def stage() -> Path:
    """复制源码到 dist/，转换 ini/inc 编码，剔除运行期文件。"""
    target = DIST / CONFIG
    if target.exists():
        shutil.rmtree(target)

    def _ignore(directory: str, names: list[str]) -> set[str]:
        ignored: set[str] = set()
        for name in names:
            if name in {"__pycache__"}:
                ignored.add(name)
            elif name.endswith((".tmp", ".pyc")):
                ignored.add(name)
            elif _is_runtime_file(name):
                ignored.add(name)
        return ignored

    shutil.copytree(SRC, target, ignore=_ignore)

    for path in target.rglob("*"):
        if path.is_file() and path.suffix.lower() in UTF16_SUFFIXES:
            text = path.read_text(encoding="utf-8")
            path.write_bytes(b"\xff\xfe" + text.encode("utf-16-le"))
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

    try:
        with zipfile.ZipFile(package, "w", zipfile.ZIP_DEFLATED) as z:
            z.write(meta, "RMSKIN.ini")
            # 安装器按宿主位数选目录：IsWin32Build() ? "32bit" : "64bit"
            # 放在 Plugins/ 根目录下不会被识别，必须带位数子目录。
            z.write(PLUGIN_DLL, f"Plugins/64bit/{PLUGIN_NAME}.dll")
            for path in sorted(config_dir.rglob("*")):
                if path.is_file():
                    rel = path.relative_to(config_dir).as_posix()
                    z.write(path, f"Skins/{CONFIG}/{rel}")
    finally:
        if meta.exists():
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

        # TODO: 若未来打包 Plugins/32bit/，需按目录分别校验 machine。
        plugins = [n for n in names if n.startswith("Plugins/64bit/") and n.endswith(".dll")]
        if not plugins:
            raise SystemExit("缺少 Plugins/64bit/*.dll（安装器按宿主位数选目录）")

        dll = z.read(plugins[0])
        pe = struct.unpack_from("<I", dll, 0x3C)[0]
        machine = struct.unpack_from("<H", dll, pe + 4)[0]
        if machine != 0x8664:
            raise SystemExit(f"插件不是 x64（machine=0x{machine:04X}）")

    print(f"包校验通过：footer OK，{len(names)} 个条目，插件 {plugins[0]}（x64）")


class InstallError(RuntimeError):
    """安装过程中不可静默跳过的失败。"""


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def ensure_dll_replaceable(target: Path) -> None:
    """确认目标 DLL 可被替换；被 Rainmeter 占用时直接失败。

    为什么必须硬失败：DLL 被占用时 copy 抛 PermissionError，而旧的实现只打印
    一行警告就返回 0。皮肤 ini 确实换成了新版，DLL 却还是旧的 ——「元数据对了、
    功能没对」，排查时会一路怀疑协议实现。宁可在安装阶段就炸出来。
    """
    if not target.exists():
        return
    try:
        with target.open("r+b"):
            pass
    except PermissionError:
        raise InstallError(
            f"插件 DLL 被占用，无法覆盖：{target}\n"
            "  Rainmeter 正在加载它。请完全退出 Rainmeter（托盘图标右键退出）后重装。"
        ) from None


def install_plugin(plugin_src: Path, plugin_target: Path) -> None:
    """复制插件 DLL 并校验哈希，任何一步不符都算失败。"""
    plugins_dir = plugin_target.parent
    if not plugins_dir.exists():
        raise InstallError(
            f"找不到 Rainmeter 插件目录：{plugins_dir}\n"
            "  确认 Rainmeter 装在这里，或用 --plugin-dir 指定。"
        )

    ensure_dll_replaceable(plugin_target)

    expected = sha256(plugin_src)
    backup = None
    if plugin_target.exists():
        backup = plugin_target.with_name(plugin_target.name + ".preinstall")
        shutil.copy2(plugin_target, backup)

    try:
        shutil.copy2(plugin_src, plugin_target)
    except PermissionError:
        raise InstallError(
            f"复制插件 DLL 失败（被占用）：{plugin_target}\n"
            "  请完全退出 Rainmeter 后重试。"
        ) from None

    actual = sha256(plugin_target)
    if actual != expected:
        if backup is not None:
            shutil.copy2(backup, plugin_target)
            print(f"！已回滚到备份：{backup}")
        raise InstallError(
            "插件 DLL 校验失败，安装未生效：\n"
            f"  期望 {expected}\n"
            f"  实际 {actual}\n"
            f"  目标 {plugin_target}"
        )

    print(f"插件校验通过：{plugin_target}")
    print(f"  sha256 {actual}")
    if backup is not None:
        print(f"  旧版已备份：{backup}")


def _copy_skins(config_dir: Path, skins: Path) -> None:
    """逐文件覆盖皮肤，并清理源里已不存在的残留文件。"""
    for src in config_dir.rglob("*"):
        if not src.is_file():
            continue
        if _is_runtime_file(src.name):
            continue
        dst = skins / src.relative_to(config_dir)
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)

    # 深度优先删除：先清叶子文件，再清空目录（当前只删文件，但按深度排序更稳）
    for dst in sorted(skins.rglob("*"), key=lambda p: len(p.parts), reverse=True):
        if dst.is_file() and not (config_dir / dst.relative_to(skins)).exists():
            try:
                dst.unlink()
            except OSError:
                pass


def install(config_dir: Path, plugin_dir: Path) -> tuple[Path, Path]:
    """覆盖式安装。

    不走「删除旧目录再复制」：Rainmeter 正加载皮肤时目录/图片可能被占用，
    删除会失败。改成逐文件覆盖，再清掉源里已经不存在的残留文件。

    插件 DLL 与皮肤走不同的宿主行为：皮肤是纯读取，DLL 会被进程锁住。
    因此 DLL 那一步必须校验 + 硬失败，不能像皮肤那样尽力而为。
    """
    plugin_target = plugin_dir / f"{PLUGIN_NAME}.dll"
    install_plugin(PLUGIN_DLL, plugin_target)

    skins = Path.home() / "Documents" / "Rainmeter" / "Skins" / CONFIG
    skins.mkdir(parents=True, exist_ok=True)
    _copy_skins(config_dir, skins)
    return skins, plugin_target


def main() -> int:
    ap = argparse.ArgumentParser(
        description="打包 MCHOSE_Battery 雨滴皮肤与插件（V7 鼠标 / G87 键盘 / V9 耳机）"
    )
    ap.add_argument("--install", action="store_true", help="同时安装到本机 Rainmeter")
    ap.add_argument(
        "--plugin-dir",
        type=Path,
        default=None,
        help=f"Rainmeter 插件目录（默认自动探测，当前默认 {DEFAULT_PLUGIN_DIR}）",
    )
    args = ap.parse_args()

    if args.plugin_dir is not None:
        plugin_dir = args.plugin_dir
    else:
        plugin_dir = DEFAULT_PLUGIN_DIR
        if not plugin_dir.exists():
            for candidate in PLUGIN_DIR_CANDIDATES:
                if candidate.exists():
                    plugin_dir = candidate
                    print(f"自动探测到插件目录：{plugin_dir}")
                    break
            else:
                print(
                    "！未探测到 Rainmeter Plugins 目录，"
                    f"使用默认值（可能不存在）：{plugin_dir}",
                    file=sys.stderr,
                )

    config_dir = stage()
    package = build_package(config_dir)
    verify_package(package)
    size = package.stat().st_size / 1024
    print(f"已生成: {package}  ({size:.1f} KB)")

    if args.install:
        try:
            skins, plugin = install(config_dir, plugin_dir)
        except InstallError as exc:
            print(f"\n安装失败：{exc}", file=sys.stderr)
            return 1
        print(f"已安装皮肤: {skins}")
        print(f"已安装插件: {plugin}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())