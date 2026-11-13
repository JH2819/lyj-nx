# -*- coding: utf-8 -*-
"""生成「显示组配置」Block Styler 对话框（资源\\对话框\\LYJ_ColorConfig.dlx）。

结构（10 个颜色槽，分两栏）：
    Dialog 「显示组配置」
      Group 「颜色按钮 1 ~ 5（勾选后这个按钮可用）」
        Toggle           chk01…05   启用
        ObjectColorPicker col01…05  颜色（点开就是 NX 内置调色板）
      Group 「颜色按钮 6 ~ 10（勾选后这个按钮可用）」
        Toggle           chk06…10
        ObjectColorPicker col06…10

功能区的按钮是**固定 12 个**（透明化 + 颜色 1~10 + 配置），勾选只决定某个槽
"有颜色可用"还是"画成＋空位"，所以增删颜色不需要重启 NX。

MAX_SLOTS 与 C# 侧 ColorSlots.Max 必须一致（默认 10）。

用法：python 工具脚本/gen_color_config_dialog.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dlx_common as dc

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "资源", "对话框", "LYJ_ColorConfig.dlx")

MAX_SLOTS = 10


def build_color_slot(i):
    """一颗颜色按钮：开关 + 颜色选择（NX 内置色盘）。返回两个「Property 包 item」片段。"""
    chk = dc.build_toggle("chk%02d" % i, "按钮 %d" % i)
    chk = dc.set_item_attr(chk, "presentation", "Toggle")

    col = dc.load("color_picker.dlx")
    col = col.replace("{WIDGET_ID}", "col%02d" % i).replace("{LABEL}", "颜色 %d" % i)
    col = dc.set_item_attr(col, "presentation", "Object Color Picker")
    return chk + "\n" + col


def main():
    halves = []
    for start, end, title in ((1, 5, "颜色按钮 1 ~ 5"), (6, MAX_SLOTS, "颜色按钮 6 ~ 10")):
        kids = "\n".join(build_color_slot(i) for i in range(start, end + 1))
        halves.append(dc.build_group("groupColor%d" % start, children=kids,
                                     title="%s（勾上＝这个按钮可用）" % title))

    # 对话框成员只留 <item>
    children = "\n".join(dc.as_item(g) for g in halves)
    doc = dc.build_dialog("显示组配置", children)

    ids = ["chk%02d" % i for i in (1, MAX_SLOTS)] \
        + ["col%02d" % i for i in (1, MAX_SLOTS)] \
        + ["groupColor1", "UGS::UI::Comp::ColorPicker"]
    dc.write_out(OUT, doc, ids)

    # 附带健康检查：对话框里不该再有透明化开关
    text = open(OUT, encoding="utf-8").read()
    if "chk_trans" in text:
        raise SystemExit("生成结果里仍有 chk_trans，检查脚本")
    print("对话框大小 %.1f KB" % (len(text.encode("utf-8")) / 1024.0))


if __name__ == "__main__":
    main()
