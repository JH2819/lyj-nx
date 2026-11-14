# -*- coding: utf-8 -*-
"""生成「改颜色」确认面板定义（资源\\对话框\\LYJ_ColorConfirm.dlx）。

结构：
    Dialog 「改颜色」
      Group 「应用颜色」
        Label confirmInfo   说明（运行时写入目标颜色 / 已选对象）

选对象**不在这里做** —— 交给 NX 原生类选择对话框
（`NXOpen.Selection.SelectTaggedObjectsWithFullFiltering`）。
本面板只在预览之后问一句"确定保留还是恢复原色"，所以只有一个标签 + 确定/取消。

层级规则见 dlx_common.py 顶部注释：对话框成员只留 <item>，分组成员保留 <Property> 壳。

用法：python 工具脚本/gen_color_confirm_dialog.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dlx_common as dc

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "资源", "对话框", "LYJ_ColorConfirm.dlx")


def build_label(widget_id="confirmInfo", text="改颜色", wrap="True"):
    """说明标签。文字运行时由 UIBlock.Label 覆盖，这里只是设计器里的占位。"""
    x = dc.load("label.dlx")
    x = x.replace("{WIDGET_ID}", widget_id).replace("{ATTACHMENTS}", "")
    x = dc.set_attr(x, "Title", "value", text)
    x = dc.set_attr(x, "WordWrap", "value", wrap)      # 长句自动折行
    return x


def main():
    children = build_label()                              # 分组成员：保留 Property 壳
    group = dc.build_group("group0", children=children, title="应用颜色")
    doc = dc.build_dialog("改颜色", dc.as_item(group))     # 对话框成员：只留 item

    dc.write_out(OUT, doc, ["confirmInfo", "group0", "UICOMP_label"])


if __name__ == "__main__":
    main()
