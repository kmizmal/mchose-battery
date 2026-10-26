#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""构建 MCHOSE_Battery Rainmeter 插件（x64 / x86，覆盖 V7 鼠标与 G87 键盘）。

流程：
    dotnet build   ->  MCHOSE_Battery.dll（托管，无导出表）
    ildasm         ->  .il
    注入 .export   ->  每个导出函数一行
    ilasm /x64|x86 ->  带导出表的 DLL（原生宿主可 LoadLibrary）

为什么不用 DllExport / DllExporter.exe：
    DllExport 的元数据库生成依赖 Visual Studio 自带的 ildasm，本机没有装 VS。
    ilasm 的 `.export [n]` 指令能直接产出 EAT，且不需要 VS。

注意：
    · 位数必须与目标 Rainmeter 严格一致：64 位宿主只认 PE32+（0x8664），
      32 位宿主只认 PE32（0x014C），不匹配会静默加载失败
    · 必须 /mdv:v4.0.30319，保证 CLR 头是 .NET Framework 可加载的版本
    · 导出名就是 C# 方法名，改名等于改 ABI
    · `.export [n]` 只写一行即可；再加 `.vtentry n : n` 会导致只导出一个方法
"""

from __future__ import annotations

import argparse
import shutil
import struct
import subprocess
import sys
from pathlib import Path

# CI（GitHub Actions / 英文 Windows）下 stdout 默认是 cp1252，打印中文会抛
# UnicodeEncodeError 让整个构建失败。这里统一改成 UTF-8，本机行为不变。
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

ROOT = Path(__file__).resolve().parent
PROJECT = ROOT / "src" / "MchoseBattery"
PLUGIN_NAME = "MCHOSE_Battery"
TOOLS = ROOT / "tools" / "ilasm"
ILASM = TOOLS / "ilasm.exe"
ILDASM = TOOLS / "ildasm.exe"
DIST_PLUGIN = ROOT / "dist" / "plugin"
# Rainmeter 加载插件的搜索顺序：安装目录 Plugins 优先，其次用户插件目录
# （%APPDATA%\Rainmeter\Plugins）。两者都存在同名 DLL 时，装的是哪个完全取决于
# 加载顺序 —— 之前把新 DLL 拷到用户目录却没管安装目录，导致改动长期不生效。
# 所以两处都写。
DEFAULT_PLUGINS = Path.home() / "AppData" / "Local" / "Programs" / "Rainmeter" / "Plugins"
USER_PLUGINS = Path.home() / "AppData" / "Roaming" / "Rainmeter" / "Plugins"

# 导出名顺序即 EAT 序号，Rainmeter 只按名字查找，序号仅需唯一
EXPORTS = ["Initialize", "Reload", "Update", "Finalize", "GetString", "ExecuteBang"]

ARCHS = {
    "x64": {
        "platform": "x64",
        "ilasm_flag": "/x64",
        "machine": 0x8664,
        "build_dir": Path("bin") / "Release",
        "file_name": f"{PLUGIN_NAME}.dll",
    },
    "x86": {
        "platform": "x86",
        # ilasm 没有 /x86 开关：不指定架构参数时默认输出 PE32（32 位）
        "ilasm_flag": "",
        "machine": 0x014C,
        "build_dir": Path("bin") / "x86",
        # 带 -32 后缀便于和 x64 版区分；注意皮肤里的 Plugin= 必须与文件名一致
        "file_name": f"{PLUGIN_NAME}-32.dll",
    },
}


def run(cmd: list[str], cwd: Path | None = None) -> subprocess.CompletedProcess:
    print("  $", " ".join(str(c) for c in cmd))
    proc = subprocess.run(cmd, cwd=str(cwd) if cwd else None,
                          capture_output=True, text=True, encoding="utf-8", errors="replace")
    if proc.returncode != 0:
        print(proc.stdout or "", proc.stderr or "")
        raise SystemExit(f"命令失败（{proc.returncode}）：{cmd[0]}")
    return proc


def build_managed(arch: str) -> Path:
    spec = ARCHS[arch]
    out_dir = PROJECT / spec["build_dir"]
    print(f"[1/5] dotnet build ({arch})")
    run([
        "dotnet", "build", "-c", "Release", "-v", "q", "--no-incremental",
        f"-p:PlatformTarget={spec['platform']}",
        "-o", str(out_dir),
    ], cwd=PROJECT)
    dll = out_dir / f"{PLUGIN_NAME}.dll"
    if not dll.exists():
        raise SystemExit(f"未找到编译产物：{dll}")
    return dll


def disassemble(dll: Path) -> Path:
    print("[2/5] ildasm")
    il = dll.with_suffix(".il")
    run([str(ILDASM), str(dll), f"/OUT={il}"])
    if not il.exists():
        raise SystemExit("ildasm 未产出 IL")
    return il


def inject_exports(il: Path) -> Path:
    print("[3/5] 注入 .export")
    text = il.read_text(encoding="utf-8", errors="replace")

    insertions: list[tuple[int, str]] = []
    for ordinal, name in enumerate(EXPORTS, start=1):
        marker = f"}} // end of method Plugin::{name}"
        idx = text.rfind(marker)
        if idx < 0:
            raise SystemExit(f"IL 里找不到导出方法：{name}（方法名被改过？）")

        brace = text.rfind("\n  {\n", 0, idx)
        if brace < 0:
            raise SystemExit(f"定位不到 {name} 的方法体起始位置")

        insert_at = brace + len("\n  {\n")
        insertions.append((insert_at, f"    .export [{ordinal}]\n"))

    # 从后往前插，避免前面的插入影响后面的偏移
    for insert_at, snippet in sorted(insertions, key=lambda x: x[0], reverse=True):
        text = text[:insert_at] + snippet + text[insert_at:]

    patched = il.with_name(il.stem + ".export.il")
    patched.write_text(text, encoding="utf-8")
    print(f"      已注入 {len(insertions)} 个导出：{', '.join(EXPORTS)}")
    return patched


def assemble(arch: str, il: Path, output: Path) -> Path:
    spec = ARCHS[arch]
    print(f"[4/5] ilasm {spec['ilasm_flag']}")
    if output.exists():
        output.unlink()
    cmd = [str(ILASM), "/dll", "/mdv:v4.0.30319", f"/output:{output}", str(il)]
    if spec["ilasm_flag"]:
        cmd.insert(2, spec["ilasm_flag"])
    run(cmd)
    if not output.exists():
        raise SystemExit("ilasm 未产出 DLL")
    return output


def read_exports(dll: Path) -> tuple[list[str], int, str, int]:
    """返回 (导出名列表, machine, metadata version, CLR flags)。"""
    data = dll.read_bytes()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    machine = struct.unpack_from("<H", data, pe + 4)[0]
    magic = struct.unpack_from("<H", data, pe + 24)[0]
    dd = pe + 24 + (112 if magic == 0x20B else 96)

    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    sec = pe + 24 + opt_size

    def to_offset(rva: int) -> int | None:
        for i in range(nsec):
            off = sec + i * 40
            vsize, vaddr, rsize, raddr = struct.unpack_from("<IIII", data, off + 8)
            if vaddr <= rva < vaddr + max(vsize, rsize):
                return raddr + (rva - vaddr)
        return None

    names: list[str] = []
    exp_rva = struct.unpack_from("<I", data, dd)[0]
    if exp_rva:
        exp_off = to_offset(exp_rva)
        names_rva = struct.unpack_from("<I", data, exp_off + 32)[0]
        count = struct.unpack_from("<I", data, exp_off + 24)[0]
        names_off = to_offset(names_rva)
        for i in range(count):
            str_rva = struct.unpack_from("<I", data, names_off + i * 4)[0]
            str_off = to_offset(str_rva)
            end = data.index(b"\0", str_off)
            names.append(data[str_off:end].decode("ascii"))

    clr_rva = struct.unpack_from("<I", data, dd + 14 * 8)[0]
    metadata = "?"
    flags = 0
    if clr_rva:
        clr_off = to_offset(clr_rva)
        flags = struct.unpack_from("<I", data, clr_off + 16)[0]
        md_rva = struct.unpack_from("<I", data, clr_off + 8)[0]
        md_off = to_offset(md_rva)
        length = struct.unpack_from("<I", data, md_off + 12)[0]
        metadata = data[md_off + 16: md_off + 16 + length].split(b"\0")[0].decode("ascii", "replace")

    return names, machine, metadata, flags


def verify(arch: str, dll: Path) -> None:
    spec = ARCHS[arch]
    print("[5/5] 校验")
    names, machine, metadata, flags = read_exports(dll)
    missing = [n for n in EXPORTS if n not in names]
    # COMIMAGE_FLAGS_32BITREQUIRED = 0x2
    is32 = (flags & 0x2) != 0
    print(f"      machine=0x{machine:04X}  metadata={metadata}  clr_flags=0x{flags:X}  "
          f"32bit_required={is32}  导出={names}")

    if machine != spec["machine"]:
        raise SystemExit(f"位数不符：期望 0x{spec['machine']:04X}，实际 0x{machine:04X}")
    if arch == "x86" and not is32:
        raise SystemExit("x86 产物缺少 32BITREQUIRED 标志，32 位宿主可能拒绝加载")
    if missing:
        raise SystemExit(f"缺少导出：{', '.join(missing)}")
    if not metadata.startswith("v4.0"):
        raise SystemExit(f"元数据版本异常：{metadata}")


def build_one(arch: str, install: bool) -> Path:
    spec = ARCHS[arch]
    print(f"\n===== {arch} =====")
    dll = build_managed(arch)
    il = disassemble(dll)
    patched = inject_exports(il)
    out = assemble(arch, patched, dll)
    verify(arch, out)

    DIST_PLUGIN.mkdir(parents=True, exist_ok=True)
    final = DIST_PLUGIN / spec["file_name"]
    shutil.copy2(out, final)
    print(f"\n已生成：{final}（{final.stat().st_size / 1024:.1f} KB）")

    if install:
        if arch != "x64":
            print("！--install 只处理 x64（当前 Rainmeter 是 64 位）；x86 产物留在 dist/plugin/")
        else:
            # 两处都写：安装目录优先加载，用户目录是备用位置。
            # 只写一处会导致"改了但没生效"。
            for folder in (DEFAULT_PLUGINS, USER_PLUGINS):
                if not folder.exists():
                    continue
                target = folder / spec["file_name"]
                try:
                    shutil.copy2(final, target)
                    print(f"已安装：{target}")
                except PermissionError:
                    print(f"！DLL 被占用，未覆盖：{target}")
                    print("  退出 Rainmeter 后再运行一次 --install，或手动替换。")
    return final


def main() -> int:
    parser = argparse.ArgumentParser(description="构建 MCHOSE V7 Rainmeter 插件")
    parser.add_argument("--arch", choices=["x64", "x86", "both"], default="x64",
                        help="目标架构：x64（默认）/ x86 / both")
    parser.add_argument("--install", action="store_true",
                        help="x64 构建后复制到本机 Rainmeter 的 Plugins 目录")
    args = parser.parse_args()

    archs = ["x64", "x86"] if args.arch == "both" else [args.arch]
    for arch in archs:
        build_one(arch, args.install)

    print("\n产物目录：" + str(DIST_PLUGIN))
    return 0


if __name__ == "__main__":
    sys.exit(main())
