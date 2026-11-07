# -*- coding: utf-8 -*-
"""生成 LYJ 安装包图标（纯代码绘制，无第三方素材，无版权问题）。

设计：居中的蓝色立体包裹盒（package），可叠加白色向下箭头表示“安装”。
所有尺寸从 4096 主画布预乘 alpha 缩放下来，16px 也能看清轮廓。

用法：
    python 工具脚本/gen_icon.py            # 只生成 打包输出/build/icon_*.png 预览
    python 工具脚本/gen_icon.py --write    # 同时写 源码/安装器/app.ico
"""
import os
import struct
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter

# 路径统一相对项目根推导，脚本从任意目录运行都能找到位置
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PREVIEW_DIR = os.path.join(ROOT, u"打包输出", "build")
ICO_PATH = os.path.join(ROOT, u"源码", u"安装器", "app.ico")

BASE = 1024          # 设计坐标基准
SS = 4               # 超采样倍数（主画布 = BASE * SS）
CANVAS = BASE * SS

# 配色：蓝色包裹，和功能区 / 按钮的蓝色一致
C_TOP    = (108, 158, 240)
C_FRONT  = (52, 112, 220)
C_SIDE   = (28, 68, 148)
C_TAPE   = (223, 236, 255)
C_WHITE  = (255, 255, 255)
C_SHADOW = (14, 34, 78, 120)

OFF_X, OFF_Y, K = -8.0, -28.0, 0.95


def T(x, y):
    """设计坐标 → 主画布坐标（先居中微调，再等比缩到 95% 留边）。"""
    x = x + OFF_X
    y = y + OFF_Y
    return ((512 + (x - 512) * K) * SS, (512 + (y - 512) * K) * SS)


def TP(pts):
    return [T(x, y) for x, y in pts]


def K2(v):
    return v * K * SS


# ---- 盒子几何（设计坐标） ----
FX0, FX1 = 235.0, 665.0
FY0, FY1 = 405.0, 790.0
DX, DY = 140.0, -115.0

P_FRONT = [(FX0, FY0), (FX1, FY0), (FX1, FY1), (FX0, FY1)]
P_TOP = [(FX0, FY0), (FX0 + DX, FY0 + DY), (FX1 + DX, FY0 + DY), (FX1, FY0)]
P_SIDE = [(FX1, FY0), (FX1 + DX, FY0 + DY), (FX1 + DX, FY1 + DY), (FX1, FY1)]

# 顶面封条：沿纵深方向，宽 70
TAPE = [(415.0, FY0), (485.0, FY0), (485.0 + DX, FY0 + DY), (415.0 + DX, FY0 + DY)]

# 正面向下箭头
ARROW_SHAFT = [(418.0, 505.0), (482.0, 505.0), (482.0, 602.0), (418.0, 602.0)]
ARROW_HEAD = [(376.0, 596.0), (524.0, 596.0), (450.0, 678.0)]


def new_canvas():
    return Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))


def add_shadow(img):
    sh = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    d = ImageDraw.Draw(sh)
    cx, cy = T(520, 800)
    rx, ry = K2(198), K2(40)
    d.ellipse([cx - rx, cy - ry, cx + rx, cy + ry], fill=C_SHADOW)
    sh = sh.filter(ImageFilter.GaussianBlur(K2(15)))
    img.alpha_composite(sh)


def draw_box_3d(img, arrow=False):
    d = ImageDraw.Draw(img)
    d.polygon(TP(P_SIDE), fill=C_SIDE)
    d.polygon(TP(P_TOP), fill=C_TOP)
    d.polygon(TP(P_FRONT), fill=C_FRONT)
    d.polygon(TP(TAPE), fill=C_TAPE)
    if arrow:
        # 有箭头时不画正面竖封条，否则白箭头压在白胶带上分不清
        d.polygon(TP(ARROW_SHAFT), fill=C_WHITE)
        d.polygon(TP(ARROW_HEAD), fill=C_WHITE)


def draw_box_badge(img):
    """圆形蓝底 + 白色包裹剪影（现代 App 风）。"""
    d = ImageDraw.Draw(img)
    cx, cy = 512 * SS, 512 * SS
    r = 468 * SS
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=C_FRONT)

    # 顶部高光：单独一层，只保留圆内部分，再 alpha 叠加（不能用 composite 整体替换）
    hl = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    ImageDraw.Draw(hl).ellipse(
        [cx - r, cy - r - K2(60), cx + r, cy + r - K2(60)], fill=(255, 255, 255, 34))
    hl = hl.filter(ImageFilter.GaussianBlur(K2(26)))
    mask = Image.new("L", (CANVAS, CANVAS), 0)
    ImageDraw.Draw(mask).ellipse([cx - r, cy - r, cx + r, cy + r], fill=255)
    hl.putalpha(ImageChops.multiply(hl.split()[3], mask))
    img.alpha_composite(hl)

    # 白色盒子（正视 + 顶部斜面）
    bx0, bx1 = 292.0, 668.0
    by0, by1 = 476.0, 780.0
    ix, iy = 124.0, -104.0
    front = [(bx0, by0), (bx1, by0), (bx1, by1), (bx0, by1)]
    top = [(bx0, by0), (bx0 + ix, by0 + iy), (bx1 + ix, by0 + iy), (bx1, by0)]
    d.polygon(TP(top), fill=(210, 226, 249))
    d.polygon(TP(front), fill=C_WHITE)
    # 封条缝：正面用圆底色挖出，顶面用更深的蓝
    tw, tm = 28.0, 466.0
    d.polygon(TP([(tm, by0), (tm + tw, by0), (tm + tw + ix, by0 + iy), (tm + ix, by0 + iy)]),
              fill=(28, 68, 148))
    d.polygon(TP([(tm, by0), (tm + tw, by0), (tm + tw, by1), (tm, by1)]), fill=C_FRONT)


def make(kind):
    img = new_canvas()
    if kind in ("a", "b"):
        add_shadow(img)
        draw_box_3d(img, arrow=(kind == "b"))
    else:
        draw_box_badge(img)
    return img


# ---- 缩放 / 打包 ----
def resize_premul(im, s, filt=Image.LANCZOS):
    r, g, b, a = im.split()
    sr = ImageChops.multiply(r, a).resize((s, s), filt, reducing_gap=3.0)
    sg = ImageChops.multiply(g, a).resize((s, s), filt, reducing_gap=3.0)
    sb = ImageChops.multiply(b, a).resize((s, s), filt, reducing_gap=3.0)
    sa = a.resize((s, s), filt, reducing_gap=3.0)
    pa = sa.load()
    for ch in (sr.load(), sg.load(), sb.load()):
        for y in range(s):
            for x in range(s):
                av = pa[x, y]
                ch[x, y] = min(255, int(round(ch[x, y] * 255.0 / av))) if av else 0
    return Image.merge("RGBA", (sr, sg, sb, sa))


def ico_bytes(frames):
    """frames: [(size, RGBA)] → 标准 32bpp BMP 帧的 .ico 字节。"""
    n = len(frames)
    head = struct.pack("<HHH", 0, 1, n)
    entries, datas = b"", b""
    offset = 6 + 16 * n
    for size, im in frames:
        w = h = size
        dib = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
        px = im.load()
        body = bytearray(dib)          # BITMAPINFOHEADER 必须在像素之前，否则 ico 无效
        for y in range(h - 1, -1, -1):
            for x in range(w):
                r, g, b, a = px[x, y]
                body += bytes((b, g, r, a))
        stride = ((w + 31) // 32) * 4
        for y in range(h - 1, -1, -1):
            row = bytearray(stride)
            for x in range(w):
                if px[x, y][3] < 128:
                    row[x >> 3] |= 0x80 >> (x & 7)
            body += row
        entries += struct.pack("<BBBBHHII",
                               w if w < 256 else 0, h if h < 256 else 0,
                               0, 0, 1, 32, len(body), offset)
        offset += len(body)
        datas += bytes(body)
    return head + entries + datas


def build_ico(master, path):
    sizes = [256, 128, 64, 48, 32, 24, 16]
    frames = [(s, resize_premul(master, s)) for s in sizes]
    with open(path, "wb") as f:
        f.write(ico_bytes(frames))
    return path


def checker(size, d=16):
    im = Image.new("RGB", (size, size), (255, 255, 255))
    px = im.load()
    for y in range(size):
        for x in range(size):
            if ((x // d) + (y // d)) % 2:
                px[x, y] = (222, 226, 232)
    return im


def preview_sheet(items, out):
    """对比图：256 大图 + 32/16 实际像素放大（左浅底右深底，便于检查两侧边缘）。"""
    cw, gap = 300, 40
    W = gap + len(items) * (cw + gap)
    ybig, ysmall, z = 40, 40 + 256 + 26, 112
    H = ysmall + z + 20
    sheet = Image.new("RGB", (W, H), (247, 249, 252))
    d = ImageDraw.Draw(sheet)
    for i, (label, master) in enumerate(items):
        x = gap + i * (cw + gap)
        big = resize_premul(master, 256)
        bg = checker(256)
        bg.paste(big, (0, 0), big)
        sheet.paste(bg, (x, ybig))
        d.text((x, 18), label, fill=(40, 48, 64))
        sx = x
        for s in (32, 16):
            small = resize_premul(master, s)
            tile = Image.new("RGB", (s, s))
            for yy in range(s):
                for xx in range(s):
                    tile.putpixel((xx, yy), (236, 239, 245) if xx < s // 2 else (44, 48, 56))
            bigpx = small.resize((z, z), Image.NEAREST)
            tile = tile.resize((z, z), Image.NEAREST)
            tile.paste(bigpx, (0, 0), bigpx)
            sheet.paste(tile, (sx, ysmall))
            sx += z + 12
    sheet.save(out)
    return out


if __name__ == "__main__":
    os.makedirs(PREVIEW_DIR, exist_ok=True)
    items = [
        ("A  package", make("a")),
        ("B  package + arrow", make("b")),
        ("C  badge", make("c")),
    ]
    for label, m in items:
        resize_premul(m, 256).save(os.path.join(PREVIEW_DIR, "icon_%s.png" % label.split()[0].lower()))
    preview_sheet(items, os.path.join(PREVIEW_DIR, "icon_preview.png"))
    print("preview: " + os.path.join(PREVIEW_DIR, "icon_preview.png"))
    if "--write" in sys.argv:
        pick = "a"
        for arg in sys.argv:
            if arg.startswith("--pick="):
                pick = arg.split("=")[1]
        m = make(pick)
        build_ico(m, ICO_PATH)
        resize_premul(m, 256).save(os.path.join(PREVIEW_DIR, "icon_preview_%s.png" % pick))
        print("wrote " + ICO_PATH + " (variant %s)" % pick)
