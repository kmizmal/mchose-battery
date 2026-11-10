#!/usr/bin/env python3
"""构建 MCHOSE_Battery Rainmeter 插件

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
import locale
import shutil
import struct
import subprocess
import sys
from pathlib import Path

# FIX: 统一用函数包装，避免模块级循环变量泄漏；同时兼容无 reconfigure 的旧版本
def _force_utf8_streams() -> None:
    """把 stdout/stderr 切到 UTF-8，避免 CI（cp1252）打印中文炸掉。"""
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            reconfigure(encoding="utf-8", errors="replace")


_force_utf8_streams()

ROOT = Path(__file__).resolve().parent
PROJECT = ROOT / "src" / "MchoseBattery"
PLUGIN_NAME = "MCHOSE_Battery"
TOOLS = ROOT / "tools" / "ilasm"
ILASM = TOOLS / "ilasm.exe"
ILDASM = TOOLS / "ildasm.exe"
DIST_PLUGIN = ROOT / "dist" / "plugin"

USER_LOCAL_PLUGINS = (
    Path.home() / "AppData" / "Local" / "Programs" / "Rainmeter" / "Plugins"
)
USER_ROAMING_PLUGINS = Path.home() / "AppData" / "Roaming" / "Rainmeter" / "Plugins"
PROGRAM_FILES = Path(
    __import__("os").environ.get("ProgramFiles", r"C:\Program Files")
)
SYSTEM_PLUGINS = PROGRAM_FILES / "Rainmeter" / "Plugins"

PLUGIN_DIR_CANDIDATES = (
    USER_LOCAL_PLUGINS,
    SYSTEM_PLUGINS,
    USER_ROAMING_PLUGINS,
)

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

def _child_encoding() -> str:
    if sys.platform == "win32":
        return locale.getpreferredencoding(False) or "mbcs"
    return "utf-8"


def run(cmd: list[str], cwd: Path | None = None) -> subprocess.CompletedProcess:
    print("  $", " ".join(str(c) for c in cmd))
    proc = subprocess.run(
        cmd,
        cwd=str(cwd) if cwd else None,
        capture_output=True,
        text=True,
        encoding=_child_encoding(),
        errors="replace",
    )
    if proc.returncode != 0:
        print(proc.stdout or "", proc.stderr or "")
        raise SystemExit(f"命令失败（{proc.returncode}）：{cmd[0]}")
    return proc


def build_managed(arch: str) -> Path:
    spec = ARCHS[arch]
    out_dir = PROJECT / spec["build_dir"]
    print(f"[1/5] dotnet build ({arch})")
    run(
        [
            "dotnet",
            "build",
            "-c",
            "Release",
            "-v",
            "q",
            "--no-incremental",
            f"-p:PlatformTarget={spec['platform']}",
            "-o",
            str(out_dir),
        ],
        cwd=PROJECT,
    )
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
    """在 IL 中为每个导出方法插入一行 `.export [n]`。"""
    print("[3/5] 注入 .export")
    text = il.read_text(encoding="utf-8", errors="replace")

    insertions: list[tuple[int, str]] = []
    for ordinal, name in enumerate(EXPORTS, start=1):
        marker = f"}} // end of method Plugin::{name}"
        end_idx = text.rfind(marker)
        if end_idx < 0:
            raise SystemExit(f"IL 里找不到导出方法：{name}（方法名被改过？）")

        # 从方法结束标记向前找方法签名起点
        sig_idx = text.rfind("method", 0, end_idx)
        if sig_idx < 0:
            raise SystemExit(f"定位不到 {name} 的方法签名")

        # 从签名起点往后找方法体的 '{'（行首缩进后的独立 { 行）
        brace_idx = -1
        probe = sig_idx
        while True:
            nl = text.find("\n", probe)
            if nl < 0 or nl > end_idx:
                break
            line_start = nl + 1
            # 跳过空白，看是不是 '{' 开头
            j = line_start
            while j < len(text) and text[j] in " \t":
                j += 1
            if j < len(text) and text[j] == "{":
                brace_idx = j
                break
            probe = nl + 1

        if brace_idx < 0:
            raise SystemExit(f"定位不到 {name} 的方法体起始位置")

        # 插到 '{' 所在行的下一行开头
        next_nl = text.find("\n", brace_idx)
        if next_nl < 0:
            raise SystemExit(f"{name} 的方法体格式异常")
        insert_at = next_nl + 1
        insertions.append((insert_at, f"    .export [{ordinal}]\n"))

    # 从后往前插，避免前面的插入影响后面的偏移
    for insert_at, snippet in sorted(insertions, key=lambda x: x[0], reverse=True):
        text = text[:insert_at] + snippet + text[insert_at:]

    patched = il.with_name(il.stem + ".export.il")
    patched.write_text(text, encoding="utf-8")
    print(f"      已注入 {len(insertions)} 个导出：{', '.join(EXPORTS)}")
    return patched


def assemble(arch: str, il: Path, output: Path) -> Path:
    """ilasm 汇编。"""
    spec = ARCHS[arch]
    print(f"[4/5] ilasm {spec['ilasm_flag'] or '(default PE32)'}")
    if output.exists():
        output.unlink()
    cmd = [
        str(ILASM),
        "/dll",
        "/mdv:v4.0.30319",
        f"/output:{output.name}",
        str(il.resolve()),
    ]
    if spec["ilasm_flag"]:
        cmd.insert(2, spec["ilasm_flag"])
    run(cmd, cwd=output.parent)
    if not output.exists():
        raise SystemExit("ilasm 未产出 DLL")
    return output


def _to_offset(data: bytes, nsec: int, sec: int, rva: int) -> int:
    """RVA -> 文件偏移。"""
    for i in range(nsec):
        off = sec + i * 40
        vsize, vaddr, rsize, raddr = struct.unpack_from("<IIII", data, off + 8)
        if vaddr <= rva < vaddr + max(vsize, rsize):
            return raddr + (rva - vaddr)
    raise ValueError(f"RVA 0x{rva:X} 未映射到任何节")


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

    def to_offset(rva: int) -> int:
        return _to_offset(data, nsec, sec, rva)

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
        # metadata root 结构：
        #   Signature(4) Major(2) Minor(2) Reserved(4) VersionLength(4) Version[...]
        length = struct.unpack_from("<I", data, md_off + 12)[0]
        metadata = (
            data[md_off + 16 : md_off + 16 + length]
            .split(b"\0")[0]
            .decode("ascii", "replace")
        )

    return names, machine, metadata, flags


def verify(arch: str, dll: Path) -> None:
    spec = ARCHS[arch]
    print("[5/5] 校验")
    names, machine, metadata, flags = read_exports(dll)
    missing = [n for n in EXPORTS if n not in names]
    # COMIMAGE_FLAGS_32BITREQUIRED = 0x2
    is32 = (flags & 0x2) != 0
    print(
        f"      machine=0x{machine:04X}  metadata={metadata}  clr_flags=0x{flags:X}  "
        f"32bit_required={is32}  导出={names}"
    )

    if machine != spec["machine"]:
        raise SystemExit(
            f"位数不符：期望 0x{spec['machine']:04X}，实际 0x{machine:04X}"
        )
    if arch == "x86" and not is32:
        raise SystemExit("x86 产物缺少 32BITREQUIRED 标志，32 位宿主可能拒绝加载")
    if missing:
        raise SystemExit(f"缺少导出：{', '.join(missing)}")
    if not metadata.startswith("v4.0"):
        raise SystemExit(f"元数据版本异常：{metadata}")


def _install_dll(src: Path, file_name: str) -> None:
    """把 DLL 复制到所有存在的 Rainmeter Plugins 目录。

    FIX: 原来只写用户级两个路径，漏了 Program Files 系统级安装。
         现在按候选目录顺序全部尝试，权限不足只提示不中断。
    """
    installed_any = False
    for folder in PLUGIN_DIR_CANDIDATES:
        if not folder.exists():
            continue
        target = folder / file_name
        try:
            shutil.copy2(src, target)
            print(f"已安装：{target}")
            installed_any = True
        except PermissionError:
            print(f"！DLL 被占用/无权限，未覆盖：{target}")
            print("  退出 Rainmeter 后重试，或手动替换。")
    if not installed_any:
        print("！未找到任何 Rainmeter Plugins 目录，产物只留在 dist/plugin/")


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
        # FIX: --install 只对 x64 生效，x86 只在 dist 留档。
        #       若目标宿主是 32 位 Rainmeter，请改用 --install-arch x86
        #       （见 main 中的参数）。
        if arch == "x64":
            _install_dll(final, spec["file_name"])
        else:
            print("！x86 产物只留在 dist/plugin/，不自动安装（宿主通常是 x64）")
    return final


def main() -> int:
    parser = argparse.ArgumentParser(description="构建 MCHOSE V7 Rainmeter 插件")
    parser.add_argument(
        "--arch",
        choices=["x64", "x86", "both"],
        default="x64",
        help="目标架构：x64（默认）/ x86 / both",
    )
    parser.add_argument(
        "--install",
        action="store_true",
        help="构建后复制到本机 Rainmeter 的 Plugins 目录（默认只装 x64）",
    )
    parser.add_argument(
        "--install-arch",
        choices=["x64", "x86", "both"],
        default=None,
        help="指定安装哪个架构（默认与 --arch 的 x64 部分一致）",
    )
    args = parser.parse_args()

    archs = ["x64", "x86"] if args.arch == "both" else [args.arch]
    for arch in archs:
        build_one(arch, args.install)

    install_archs = (
        [args.install_arch]
        if args.install_arch and args.install_arch != "both"
        else (["x64", "x86"] if args.install_arch == "both" else [])
    )
    if install_archs:
        for arch in install_archs:
            spec = ARCHS[arch]
            src = DIST_PLUGIN / spec["file_name"]
            if src.exists():
                _install_dll(src, spec["file_name"])

    print("\n产物目录：" + str(DIST_PLUGIN))
    return 0


if __name__ == "__main__":
    sys.exit(main())