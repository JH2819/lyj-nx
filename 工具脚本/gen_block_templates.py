# -*- coding: utf-8 -*-
"""从 NX 自带 .dlx 里抽出「块模板」，存到 工具脚本/templates/ 下长期使用。

snapy 的模板目录里没有颜色选择块，只能从官方对话框里整段抠一个出来：
取第一个 class="UGS::UI::Comp::ColorPicker" 的 <Property>，把 id/name/API Name
替换成 {WIDGET_ID}、标题替换成 {LABEL}，就得到与 snapy 其它模板同形的片段。

用法：python 工具脚本/gen_block_templates.py
"""
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT_DIR = os.path.join(HERE, "templates")

NX_BASE = os.environ.get("NX_BASE_DIR") or r"D:\Program Files\Siemens\DC 2606"

# 块类型 → 输出文件名
WANTED = {
    "UGS::UI::Comp::ColorPicker": "color_picker.dlx",
}


def find_property(wanted_class):
    """在所有 NX .dlx 里找第一个该 class 的 <Property>（带 <item> 子元素的那种）。"""
    files = glob.glob(os.path.join(NX_BASE, "**", "*.dlx"), recursive=True)
    for f in files:
        try:
            root = ET.parse(f).getroot()
        except Exception:
            continue
        for prop in root.iter("Property"):
            if prop.attrib.get("class") != wanted_class:
                continue
            if prop.find("item") is None:
                continue
            return f, prop
    return None, None


def to_template(prop, widget="__WIDGET__", label="__LABEL__"):
    """把抽出来的块改成模板：id 统一成占位符，标题改成 {LABEL}，清掉当前值。"""
    xml = ET.tostring(prop, encoding="unicode")

    # id / name / API Name / BlockID 全部换成占位符
    old_ids = set()
    old_ids.add(prop.attrib.get("id"))
    it = prop.find("item")
    if it is not None and it.attrib.get("id"):
        old_ids.add(it.attrib.get("id"))
    for oid in sorted(x for x in old_ids if x):
        xml = re.sub(r'"%s"' % re.escape(oid), '"{WIDGET_ID}"', xml)

    # 标题
    xml = re.sub(r'(<Property\b[^>]*\bid="Title"[^>]*\bvalue=")[^"]*(")',
                 r"\g<1>{LABEL}\g<2>", xml)
    # 页面上的当前值清掉（Block Styler 打开时由代码 SetValue）
    xml = re.sub(r'(<Property\b[^>]*\bid="Value"[^>]*\bvalue=")[^"]*(")',
                 r"\g<1>None\g<2>", xml)

    head = '<?xml version="1.0" encoding="UTF-8"?>\n'
    return head + xml


def main():
    if not os.path.isdir(OUT_DIR):
        os.makedirs(OUT_DIR)

    for cls, out_name in WANTED.items():
        src, prop = find_property(cls)
        if prop is None:
            print("跳过 %s：NX 安装目录里没找到该块" % cls)
            continue
        doc = to_template(prop)
        out = os.path.join(OUT_DIR, out_name)
        with open(out, "wb") as f:
            f.write(doc.encode("utf-8"))
        ET.fromstring(doc.encode("utf-8"))       # 自检
        print("%-28s ← %s  (%.1f KB)" % (out_name, os.path.basename(src),
                                         len(doc.encode("utf-8")) / 1024.0))
        if "{WIDGET_ID}" not in doc or "{LABEL}" not in doc:
            raise SystemExit("模板占位符没替换成功：" + out_name)


if __name__ == "__main__":
    main()
