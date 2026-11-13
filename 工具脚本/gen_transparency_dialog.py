# -*- coding: utf-8 -*-
"""生成「透明化」Block Styler 对话框定义（资源\\对话框\\LYJ_Transparency.dlx）。

结构：
    Dialog 「透明化」
      Group 「部件透明度」
        SelectObject  partSelect   选择对象（体/片体/面/边/特征，可多选）
        Integer        transValue  透明度滑条 0~100（整数块 + Scale 样式）

层级规则见 dlx_common.py 顶部注释：对话框成员只留 <item>，分组成员保留 <Property> 壳。

用法：python 工具脚本/gen_transparency_dialog.py
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dlx_common as dc

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(ROOT, "资源", "对话框", "LYJ_Transparency.dlx")


def build_selection(widget_id="partSelect", label="选择对象"):
    x = dc.load("select_object.dlx")
    x = x.replace("{WIDGET_ID}", widget_id).replace("{ATTACHMENTS}", "")
    x = dc.set_attr(x, "Label", "value", label)          # 显示名
    x = dc.set_attr(x, "Cue", "value", "点选实体 / 片体 / 面 / 边")   # 行内提示
    x = dc.set_attr(x, "Many", "selected", "1")          # 0=单选 1=多选
    x = dc.set_attr(x, "StepStatus", "selected", "1")    # Optional，不强制必选
    x = dc.set_item_attr(x, "presentation", "Select Object")   # 模板里是占位符
    return x


def build_integer(widget_id="transValue", title="透明度 %", vmin=0, vmax=100, value=50):
    """整数块做滑条。

    这里**不用** Double 块：Double 的 `VisibleDecimals=0` 只管输入框，Scale 样式上方那个
    数值气泡仍按 2 位小数显示（实测 29.64 / 1.00 / 100.00）。Integer 块同样支持
    `PresentationStyle=Scale`，值本身是 int，滑条和读数天然就是 0~100 的整数级（没有小数）。
    """
    x = dc.load("integer.dlx")
    x = x.replace("{WIDGET_ID}", widget_id).replace("{ATTACHMENTS}", "")
    x = dc.set_attr(x, "Title", "value", title)
    x = dc.set_attr(x, "PresentationStyle", "selected", "2")   # 0=Keyin 1=Spin 2=Scale 3=ScaleKeyin
    x = dc.set_attr(x, "MinimumValue", "value", str(vmin))
    x = dc.set_attr(x, "MaximumValue", "value", str(vmax))
    x = dc.set_attr(x, "Value", "value", str(value))
    x = dc.set_attr(x, "Increment", "value", "1")
    # 上下限标签与当前值都显示（模板里这两个属性的 id 与 sname 不同名）
    x = dc.set_attr(x, "LimitsVisibility", "value", "True")    # sname=ScaleLimits
    x = dc.set_attr(x, "ValueVisibility", "value", "True")     # sname=ShowScaleValue
    x = dc.set_attr(x, "TitleVisibility", "value", "True")
    return x


def main():
    children = build_selection() + "\n" + build_integer()      # 分组成员：保留 Property 壳
    group = dc.build_group("group0", children=children, title="部件透明度")
    doc = dc.build_dialog("透明化", dc.as_item(group))        # 对话框成员：只留 item

    dc.write_out(OUT, doc, ["partSelect", "transValue", "group0",
                            "UICOMP_selection", "UICOMP_integer"])


if __name__ == "__main__":
    main()
