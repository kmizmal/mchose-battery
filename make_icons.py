#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把官方产品渲染图处理成皮肤用的左上角图标。

做三件事：
  1. 按 URL 下载官方 504×952 的 RGBA 渲染图（已缓存则跳过）；
  2. 用 alpha 通道裁掉透明留白，四周再补 4% 内边距，避免贴边；
  3. 等比缩放到 ICON_H 像素高，输出到 @Resources\\Images\\mouse-<配色>.png。

官方图来自 MCHOSE 官网设备卡所用的 CDN 资源，仅作本机桌面展示使用。
重新配色/换型号时改 COLORWAYS 后重跑本脚本即可。

用法：
    python make_icons.py
"""

from __future__ import annotations

import io
import urllib.request
from pathlib import Path

from PIL import Image

COLORWAYS = {
    "white": "https://cdn.mchose.com.cn/configCenter/static/images/V7_White_0q3ivbmts4r3vysi.png",
    "black": "https://cdn.mchose.com.cn/configCenter/static/images/V7_Black_t22oxlduucamqako.png",
    "red": "https://cdn.mchose.com.cn/configCenter/static/images/V7_Red_w31fk2jwwm4jtjxn.png",
    "pink": "https://cdn.mchose.com.cn/configCenter/static/images/V7_Pink_c4tscvar7mfptbb8.png",
}

# G87 键盘：官方渲染图是横向的（1068×424），必须按宽度缩放，
# 按高度缩放会横向撑爆卡片。四角是透明的，trim 逻辑可以复用。
# 槽位沿用 Colorway 的 4 档；G87 没有红色配色，第 3 档用青极蓝。
KB_COLORWAYS = {
    "white": "https://cdn.mchose.com.cn/configCenter/static/images/G87WhiteFull_b3933ff72b9f.png",
    "black": "https://cdn.mchose.com.cn/configCenter/static/images/G87BlackFull_689dfd0af2a6.png",
    "cyan": "https://cdn.mchose.com.cn/configCenter/static/images/G87CyanFull_96955b7cc97a.png",
    "pink": "https://cdn.mchose.com.cn/configCenter/static/images/G87PinkSideFull_01718a2db8cd.png",
}

ICON_H = 104        # 2× 显示高度（鼠标图按 H=52 使用，高 DPI 下不虚）
ICON_W = 84         # 2× 显示宽度（键盘图按 W=42 使用）
PAD_RATIO = 0.04    # 裁剪后补的内边距，占短边比例

# V9 Pro 2 耳机：官方图同样是横向（640×352），但耳机较宽，按宽度收到 120 像素，
# 否则会压到右上角的连接状态胶囊。四档取官方在售配色（蓝/黑/红/绿）。
HEADSET_COLORWAYS = {
    "blue": "https://cdn.mchose.com.cn/configCenter/static/images/v9pro2Blue_053d13dc0a47.png",
    "black": "https://cdn.mchose.com.cn/configCenter/static/images/v9pro2Black_c978f09baa6b.png",
    "red": "https://cdn.mchose.com.cn/configCenter/static/images/v9pro2Red_e88e7804b984.png",
    "green": "https://cdn.mchose.com.cn/configCenter/static/images/v9pro2Green_222954fd35a4.png",
}

ICON_W_HS = 120     # 2× 显示宽度（耳机图按 W=60 使用）

ROOT = Path(__file__).resolve().parent
CACHE = ROOT / "_icon_src"
OUT = ROOT / "MCHOSE_Battery" / "@Resources" / "Images"


def fetch(url: str, dest: Path) -> Image.Image:
    if not dest.exists():
        dest.parent.mkdir(parents=True, exist_ok=True)
        with urllib.request.urlopen(url, timeout=30) as resp:
            dest.write_bytes(resp.read())
    return Image.open(dest).convert("RGBA")


def trim(image: Image.Image) -> Image.Image:
    box = image.getchannel("A").getbbox()
    if box is None:
        return image
    cropped = image.crop(box)
    pad = int(min(cropped.size) * PAD_RATIO)
    canvas = Image.new("RGBA", (cropped.width + pad * 2, cropped.height + pad * 2), (0, 0, 0, 0))
    canvas.paste(cropped, (pad, pad))
    return canvas


def scale(image: Image.Image) -> Image.Image:
    width = max(1, round(image.width * ICON_H / image.height))
    return image.resize((width, ICON_H), Image.LANCZOS)


def scale_w(image: Image.Image) -> Image.Image:
    """横向图按宽度缩放，避免高度对齐时宽度失控。"""
    height = max(1, round(image.height * ICON_W / image.width))
    return image.resize((ICON_W, height), Image.LANCZOS)


def scale_ws(image: Image.Image) -> Image.Image:
    """横向图等比装进固定框（耳机用）：先按高度约束，超宽再按宽度收。"""
    box_h = 88
    height = box_h
    width = max(1, round(image.width * height / image.height))
    if width > ICON_W_HS:
        width = ICON_W_HS
        height = max(1, round(image.height * width / image.width))
    return image.resize((width, height), Image.LANCZOS)


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)
    for name, url in COLORWAYS.items():
        src = fetch(url, CACHE / f"V7_{name}.png")
        icon = scale(trim(src))
        target = OUT / f"mouse-{name}.png"
        icon.save(target, optimize=True)
        print(f"mouse-{name:6s} {src.size} -> {icon.size}  {target.stat().st_size // 1024} KB")

    for name, url in KB_COLORWAYS.items():
        src = fetch(url, CACHE / f"G87_{name}.png")
        icon = scale_w(trim(src))
        target = OUT / f"keyboard-{name}.png"
        icon.save(target, optimize=True)
        print(f"keyboard-{name:6s} {src.size} -> {icon.size}  {target.stat().st_size // 1024} KB")

    for name, url in HEADSET_COLORWAYS.items():
        src = fetch(url, CACHE / f"V9P2_{name}.png")
        icon = scale_ws(trim(src))
        target = OUT / f"headset-{name}.png"
        icon.save(target, optimize=True)
        print(f"headset-{name:6s} {src.size} -> {icon.size}  {target.stat().st_size // 1024} KB")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
