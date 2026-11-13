# -*- coding: utf-8 -*-
"""生成 LYJ 功能区按钮图标（60x60 32bpp BMP）。

版式与已有的 lyj_import_auto.bmp 保持一致：
  画布 60x60，最外 1px 留空，2px 实心矩形边框，浅色底，黑色粗体文字两行居中。

用法：
    python 工具脚本/gen_ribbon_icon.py            # 生成 CONFIG 里全部图标
    python 工具脚本/gen_ribbon_icon.py lyj_export_auto
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
ICON_DIR = os.path.join(ROOT, "资源", "按钮图标")

SIZE = 60
FONT_CANDIDATES = [
    r"C:\Windows\Fonts\msyhbd.ttc",   # 微软雅黑 Bold
    r"C:\Windows\Fonts\simhei.ttf",   # 黑体
]

# 每个图标：文字 + 边框色 + 底色。
#   style="solid"    纯色底（默认）
#   style="checker"  棋盘格底，表示"透明"
#   style="palette"  3x3 彩色小方块，表示"调色板/配置"
#   ink              文字颜色（深色底用白字）
#   size             画布边长（默认 60；「显示」组中号按钮用 48）
#   chip=True        只画色块、不写字（色块图标不写字，名字交给功能区 LABEL）
#   border=None      不画边框（「显示」组要的是纯色块，有边框反而不像颜色本身）
CONFIG = {
    "lyj_export_auto": dict(
        lines=["导出", "图档"],
        border=(126, 194, 36),
        bg=(231, 243, 214),
    ),
    # ---- 显示组：中号（MEDIUM_IMAGE）、只有图标、纯色块（无边框无文字） ----
    "lyj_transparency": dict(
        size=48, chip=True, style="checker", border=None,
        bg=(255, 255, 255),
        checker=((255, 255, 255), (184, 184, 184)),
    ),
    # 默认前五颗：淡蓝 / 浅绿 / 浅灰 / 白色 / 黑色（与 ColorSlots.Default 一致）
    "lyj_color_01": dict(size=48, chip=True, border=None, bg=(137, 182, 255)),
    "lyj_color_02": dict(size=48, chip=True, border=None, bg=(215, 255, 199)),
    "lyj_color_03": dict(size=48, chip=True, border=None, bg=(153, 153, 153)),
    "lyj_color_04": dict(size=48, chip=True, border=None, bg=(255, 255, 255)),
    "lyj_color_05": dict(size=48, chip=True, border=None, bg=(0, 0, 0)),
    # 未启用的槽：中性灰块（随便什么颜色，反正不显示）
    "lyj_color_06": dict(size=48, chip=True, border=None, bg=(170, 170, 170)),
    "lyj_color_07": dict(size=48, chip=True, border=None, bg=(170, 170, 170)),
    "lyj_color_08": dict(size=48, chip=True, border=None, bg=(170, 170, 170)),
    "lyj_color_09": dict(size=48, chip=True, border=None, bg=(170, 170, 170)),
    "lyj_color_10": dict(size=48, chip=True, border=None, bg=(170, 170, 170)),
    "lyj_color_config": dict(size=48, chip=True, style="palette", border=None),
}

# 目标墨迹位置（与 lyj_import_auto 完全一致）
LINE1_TOP, LINE1_H = 5, 25
LINE2_TOP, LINE2_H = 31, 24
CENTER_X = SIZE // 2


def load_font(px):
    for p in FONT_CANDIDATES:
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, px)
            except Exception:
                pass
    return ImageFont.load_default()


def fit_font(text, target_h):
    """挑一个字号，使该行文字的墨迹高度最接近 target_h。"""
    best = None
    for px in range(16, 34):
        f = load_font(px)
        mask = Image.new("L", (SIZE * 3, SIZE * 3), 0)
        ImageDraw.Draw(mask).text((SIZE, SIZE), text, font=f, fill=255)
        bbox = mask.getbbox()
        if not bbox:
            continue
        h = bbox[3] - bbox[1]
        w = bbox[2] - bbox[0]
        if w > SIZE - 8:          # 宽度超出可用区，放弃这个字号
            continue
        score = abs(h - target_h)
        if best is None or score < best[0]:
            best = (score, f, bbox)
    return best[1], best[2]


def draw_line(canvas, text, top, target_h, ink=(0, 0, 0)):
    font, _ = fit_font(text, target_h)
    # 先画到足够大的临时图，再按墨迹范围裁剪，避免 paste 用负坐标（PIL 会丢弃）
    tmp = Image.new("L", (SIZE * 3, SIZE * 3), 0)
    ImageDraw.Draw(tmp).text((SIZE, SIZE), text, font=font, fill=255)
    bb = tmp.getbbox()
    glyph = tmp.crop(bb)
    gw, gh = glyph.size

    layer = Image.new("RGB", (gw, gh), ink)
    x = CENTER_X - gw // 2          # 墨迹水平居中
    y = top                          # 墨迹顶边落在 top
    canvas.paste(layer, (x, y), glyph)


def build(name, cfg):
    s = cfg.get("size", SIZE)
    img = Image.new("RGB", (s, s), cfg.get("bg", (245, 245, 245)))
    d = ImageDraw.Draw(img)

    if cfg.get("style") == "checker":
        c1, c2 = cfg.get("checker", ((255, 255, 255), (205, 205, 210)))
        cell = 3 if s <= 32 else 6
        for y in range(0, s, cell):
            for x in range(0, s, cell):
                col = c1 if ((x // cell + y // cell) % 2 == 0) else c2
                d.rectangle([x, y, x + cell - 1, y + cell - 1], fill=col)

    if cfg.get("style") == "palette":
        # 3x3 彩色小方块（NX 调色板的样子），无边框
        cell = s // 3
        cols = [(92, 92, 92), (60, 76, 231), (48, 162, 65),
                (34, 34, 34), (219, 219, 219), (42, 142, 216),
                (232, 232, 232), (20, 20, 20), (168, 168, 168)]
        for ry in range(3):
            for rx in range(3):
                d.rectangle([rx * cell, ry * cell, (rx + 1) * cell - 1, (ry + 1) * cell - 1],
                            fill=cols[ry * 3 + rx])

    # 边框：border=None 表示不画（「显示」组的色块要"只有颜色本身"）
    if cfg.get("border") is not None:
        d.rectangle([1, 1, s - 2, s - 2], outline=cfg["border"], width=2)

    if not cfg.get("chip"):
        ink = cfg.get("ink", (0, 0, 0))
        lines = cfg["lines"][:2]
        if len(lines) == 1:
            # 单行：字号放大到一行占满，垂直居中
            draw_line(img, lines[0], 16, 27, ink)
        else:
            for i, text in enumerate(lines):
                draw_line(img, text, (LINE1_TOP, LINE2_TOP)[i], (LINE1_H, LINE2_H)[i], ink)

    out = os.path.join(ICON_DIR, name + ".bmp")
    img.save(out, "BMP")
    return out


def main():
    names = sys.argv[1:] or list(CONFIG)
    for n in names:
        if n not in CONFIG:
            print("未知图标:", n)
            continue
        p = build(n, CONFIG[n])
        im = Image.open(p)
        print("%-20s %s %s %d 字节" % (n, im.size, im.mode, os.path.getsize(p)))


if __name__ == "__main__":
    main()
