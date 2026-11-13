# -*- coding: utf-8 -*-
"""生成「改颜色」选择对话框定义（资源\\对话框\\LYJ_ColorPick.dlx）。

结构：
    Dialog 「改颜色」
      Group 「应用颜色」
        Label        colorInfo    顶部说明（运行时写入目标颜色名）
        SelectObject colorSelect  选择对象（体/片体/面/边/特征，可多选）

流程：点颜色按钮 → 弹出本对话框 → 图形区选对象（选中即预览）→ 确定保留 / 取消恢复原色。

层级规则见 dlx_common.py 顶部注释：对话框成员只留 <item>，分组成员保留 <Property> 壳。

用法：python 工具脚本/gen_color_pick_dialog.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dlx_common as dc

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "资源", "对话框", "LYJ_ColorPick.dlx")


def build_label(widget_id="colorInfo", text="目标颜色", wrap="True"):
    """说明标签。文字运行时由 UIBlock.Label 覆盖，这里只是设计器里的占位。"""
    x = dc.load("label.dlx")
    x = x.replace("{WIDGET_ID}", widget_id).replace("{ATTACHMENTS}", "")
    x = dc.set_attr(x, "Title", "value", text)
    x = dc.set_attr(x, "WordWrap", "value", wrap)      # 长句自动折行
    return x


def build_selection(widget_id="colorSelect", label="选择对象"):
    x = dc.load("select_object.dlx")
    x = x.replace("{WIDGET_ID}", widget_id).replace("{ATTACHMENTS}", "")
    x = dc.set_attr(x, "Label", "value", label)
    x = dc.set_attr(x, "Cue", "value", "点选实体 / 片体 / 面 / 边")
    x = dc.set_attr(x, "Many", "selected", "1")        # 0=单选 1=多选
    x = dc.set_attr(x, "StepStatus", "selected", "1")  # Optional，不强制必选
    x = dc.set_item_attr(x, "presentation", "Select Object")
    return x


def main():
    children = build_label() + "\n" + build_selection()      # 分组成员：保留 Property 壳
    group = dc.build_group("group0", children=children, title="应用颜色")
    doc = dc.build_dialog("改颜色", dc.as_item(group))       # 对话框成员：只留 item

    dc.write_out(OUT, doc, ["colorInfo", "colorSelect", "group0",
                            "UICOMP_label", "UICOMP_selection"])


if __name__ == "__main__":
    main()
