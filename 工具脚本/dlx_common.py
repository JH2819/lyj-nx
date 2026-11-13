# -*- coding: utf-8 -*-
"""Block Styler 对话框（.dlx）拼装公共件。

NX 没有公开 .dlx 的最小格式说明，最可靠的做法是拼装 NX 自带的按块模板：

    <NX_BASE>\\NXCAE_EXTRAS\\nxrsimc_wntx64\\nx_plugin\\snapy\\nx\\gui\\templates\\
        dialog.dlx / group.dlx / toggle.dlx / select_object.dlx / double.dlx …

目录名里那个 `nxrsimc_wntx64` 是 NX 的写法，别以为是换行。

------------------------------ 结构要点 ------------------------------
对照 NX 自带 448 个 .dlx 的实际层级：

    Dialog
      item                              ← 对话框的直接成员是 <item>
        PropertyList / Property(Members) / PropertyList(ContainerItems)
          Property type="uicomp"        ← 分组里的成员是 <Property> 包 <item>
            item
              PropertyList …

而 snapy 模板片段本身正好是 <Property> 包 <item> 两层，所以：

  * 作为「对话框」的成员  → 要用 as_item() 剥掉 Property 壳（只留 <item>）
  * 作为「分组」的成员    → 原样放（分组里必须带 Property 壳）

搞反的后果：对话框能建出来但里面一个块都没有，点按钮像"没反应"。
"""
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
NX_BASE = os.environ.get("NX_BASE_DIR") or r"D:\Program Files\Siemens\DC 2606"
TPL_DIR = os.path.join(NX_BASE, "NXCAE_EXTRAS", "nxrsimc_wntx64", "nx_plugin",
                       "snapy", "nx", "gui", "templates")
LOCAL_TPL_DIR = os.path.join(HERE, "templates")


def load(name):
    """读一个块模板（先找本仓库 工具脚本/templates，再找 NX 自带）。

    统一去掉 XML 声明：模板要嵌进别的文件里，留着声明会让整个 .dlx 解析失败
    （xml.etree 报 "XML or text declaration not at start of entity"）。
    """
    for d in (LOCAL_TPL_DIR, TPL_DIR):
        p = os.path.join(d, name)
        if os.path.exists(p):
            with open(p, "rb") as f:
                text = f.read().decode("utf-8")
            return re.sub(r"^\s*<\?xml[^>]*\?>\s*", "", text)
    raise SystemExit("找不到模板：%s（查过 %s 与 %s）" % (name, LOCAL_TPL_DIR, TPL_DIR))


def set_attr(xml, prop_id, attr, value, nth=0):
    """把 <Property id="prop_id" ... attr="旧值"> 的 attr 改成 value（只改第 nth 个匹配）。

    注意：dlx 里 Property 的 id 与 sname 常常不同名
    （例如 id="LimitsVisibility" sname="ScaleLimits"），要按 id 找。
    """
    pat = re.compile(r'(<Property\b[^>]*\bid="%s"[^>]*\b%s=")([^"]*)(")'
                     % (re.escape(prop_id), attr))
    count = [0]

    def repl(m):
        if count[0] == nth:
            count[0] += 1
            return m.group(1) + value + m.group(3)
        count[0] += 1
        return m.group(0)

    out = pat.sub(repl, xml)
    if count[0] <= nth:
        raise SystemExit("属性未找到：id=%s @%s" % (prop_id, attr))
    return out


def as_item(frag):
    """剥掉片段外层的 <Property …> 壳，只留里面的 <item …>…</item>。"""
    s = frag.strip()
    m = re.match(r"<Property\b[^>]*>\s*(<item\b[\s\S]*</item>)\s*</Property>\s*$", s)
    if not m:
        raise SystemExit("片段不是 <Property><item>…</item></Property> 结构，无法转换")
    return m.group(1)


def set_item_attr(frag, attr, value):
    """改片段里第一个 <item …> 起始标签上的某个属性（生成器里有些块的 presentation
    被模板写成占位符或 "Not set"，要按 NX 官方 .dlx 的写法补成块类型名）。"""
    return re.sub(r'(<item\b[^>]*\b%s=")([^"]*)(")' % re.escape(attr),
                  lambda m: m.group(1) + value + m.group(3), frag, count=1)


def build_group(widget_id, children="", title="", show_label="True"):
    """分组块。children 是「Property 包 item」序列（不要剥壳）。"""
    x = load("group.dlx")
    # Title 必须在插入 children 之前改，否则会命中子块里的同名 Title 属性
    x = set_attr(x, "Title", "value", title)
    x = x.replace("{CHILDREN}", children)
    x = x.replace("{WIDGET_ID}", widget_id)
    x = x.replace("{SHOW_LABEL}", show_label)
    x = x.replace("{ATTACHMENTS}", "")
    return x


def build_toggle(widget_id, label, value="False"):
    x = load("toggle.dlx")
    x = x.replace("{WIDGET_ID}", widget_id)
    x = set_attr(x, "Title", "value", label)
    x = set_attr(x, "Value", "value", value)
    x = x.replace("{ATTACHMENTS}", "")
    return x


def build_dialog(title, children, navigation="0"):
    """对话框骨架。children 是「只含 <item>」的序列（要剥壳）。"""
    x = load("dialog.dlx")
    x = x.replace("{CHILDREN}", children)
    x = x.replace("{TITLE}", title)
    x = x.replace("{POLICY_ID}", "0")                 # 0 = 允许调整大小
    x = x.replace("{NAVIGATION_STYLE}", navigation)   # 0 = 确定/取消
    x = re.sub(r'(<Dialog\b[^>]*\btitle=")[^"]*(")', r"\g<1>%s\g<2>" % title, x, count=1)
    if not x.lstrip().startswith("<?xml"):
        x = '<?xml version="1.0" encoding="UTF-8"?>\n' + x
    return x


def write_out(path, doc, expect_ids):
    """写文件 + 结构自检（XML 可解析、块 id 齐全、容器层级正确）。"""
    import xml.etree.ElementTree as ET

    out_dir = os.path.dirname(path)
    if not os.path.isdir(out_dir):
        os.makedirs(out_dir)
    data = doc.encode("utf-8")
    with open(path, "wb") as f:
        f.write(data)

    root = ET.fromstring(data)
    for need in expect_ids:
        if need not in doc:
            raise SystemExit("生成结果缺少块：" + need)

    kids = [c.tag for c in list(root)]
    if not kids or kids[0] != "item":
        raise SystemExit("对话框的第一个子元素应为 item，实际是 %r" % (kids[:1],))
    # 分组里的成员必须是 <Property> 包 <item>
    for it in root.iter("item"):
        pl = it.find("PropertyList")
        if pl is None:
            continue
        mem = pl.find("./Property[@id='Members']")
        if mem is None:
            continue
        for box in mem:
            if box.tag != "PropertyList":
                raise SystemExit("Members 下应为 PropertyList，实际是 " + box.tag)
            for child in box:
                if child.tag != "Property":
                    raise SystemExit("分组成员应为 Property，实际是 " + child.tag)

    print("已生成 %s" % path)
    print("大小 %.1f KB，顶层块 %d 个" % (len(data) / 1024.0, len(kids)))
