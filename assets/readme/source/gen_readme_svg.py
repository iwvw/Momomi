import html

CAT = [
    ".#.....#",
    ".##...##",
    ".#.###.#",
    "#.......",
    "#.#...#.",
    "#...#...",
    "#..###..",
    ".#.....#",
]

BG = "#0B0F14"
PANEL = "#0F1620"
STROKE = "#243040"
GRID = "#161F2B"
FG = "#E6EDF3"
MUTED = "#8B98A5"
DIM = "#5A6675"
GREEN = "#3FB950"
AMBER = "#E3B341"
BLUE = "#58A6FF"

FONT = "'Segoe UI','PingFang SC','Microsoft YaHei',system-ui,sans-serif"
MONO = "'Cascadia Code','Consolas','SFMono-Regular',ui-monospace,monospace"


def cat_glyph(x, y, cell, color, opacity=1.0):
    """把填充格并集描成单条闭合路径，避免逐格矩形之间的抗锯齿接缝。"""
    h = len(CAT)
    w = len(CAT[0])
    filled = lambda i, j: 0 <= i < w and 0 <= j < h and CAT[j][i] == "#"

    edges = {}  # start -> list[end]
    def add(a, b):
        edges.setdefault(a, []).append(b)

    for j in range(h):
        for i in range(w):
            if not filled(i, j):
                continue
            if not filled(i, j - 1):      # 上边
                add((i, j), (i + 1, j))
            if not filled(i + 1, j):      # 右边
                add((i + 1, j), (i + 1, j + 1))
            if not filled(i, j + 1):      # 下边
                add((i + 1, j + 1), (i, j + 1))
            if not filled(i - 1, j):      # 左边
                add((i, j + 1), (i, j))

    paths = []
    while edges:
        start = next(iter(edges))
        loop = [start]
        cur = start
        while True:
            nxts = edges.get(cur)
            if not nxts:
                break
            nxt = nxts.pop()
            if not nxts:
                del edges[cur]
            if nxt == start:
                break
            loop.append(nxt)
            cur = nxt

        def pt(p):
            return (x + p[0] * cell, y + p[1] * cell)

        d = f"M {pt(loop[0])[0]:g} {pt(loop[0])[1]:g}"
        for p in loop[1:]:
            px, py = pt(p)
            d += f" L {px:g} {py:g}"
        d += " Z"
        paths.append(d)

    op = f' opacity="{opacity}"' if opacity != 1.0 else ""
    return f'<path d="{" ".join(paths)}" fill="{color}" fill-rule="nonzero"{op}/>'


def pill(x, y, text, w=None, color=MUTED, fill=PANEL, stroke=STROKE, fs=13, mono=True):
    pad = 12
    cw = fs * 0.62 if mono else fs * 0.95
    width = w if w else pad * 2 + len(text) * cw
    fam = MONO if mono else FONT
    return (
        f'<g><rect x="{x:g}" y="{y:g}" width="{width:g}" height="26" rx="13" '
        f'fill="{fill}" stroke="{stroke}"/><text x="{x + width / 2:g}" y="{y + 17:g}" '
        f'font-family="{fam}" font-size="{fs}" fill="{color}" text-anchor="middle">{html.escape(text)}</text></g>',
        width,
    )


def node_pill(x, y, label, active=False):
    w, h = (96, 44)
    stroke = GREEN if active else STROKE
    fill = "#10261A" if active else PANEL
    dot = GREEN if active else DIM
    label_color = FG if active else MUTED
    return f"""<g>
      <rect x="{x:g}" y="{y:g}" width="{w}" height="{h}" rx="12" fill="{fill}" stroke="{stroke}"/>
      <circle cx="{x + 18:g}" cy="{y + h / 2:g}" r="4" fill="{dot}"/>
      <text x="{x + 32:g}" y="{y + h / 2 + 6:g}" font-family="{FONT}" font-size="17" fill="{label_color}">{html.escape(label)}</text>
    </g>"""


def build_logo():
    cell = 32
    size = cell * 8
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}" width="{size}" height="{size}" role="img" aria-labelledby="t d">
  <title id="t">Momomi logo</title>
  <desc id="d">A pixel-art cat face, the Momomi application icon.</desc>
  {cat_glyph(0, 0, cell, "#111827")}
</svg>
"""


def build_hero():
    w, h = 1200, 400
    # background grid motif
    grid = []
    for gx in range(40, w, 40):
        grid.append(f'<line x1="{gx}" y1="0" x2="{gx}" y2="{h}"/>')
    for gy in range(40, h, 40):
        grid.append(f'<line x1="0" y1="{gy}" x2="{w}" y2="{gy}"/>')
    grid = "\n    ".join(grid)

    # metadata pills
    p1, w1 = pill(64, 286, "v0.7.1", color=GREEN, mono=True)
    p2, w2 = pill(64 + w1 + 8, 286, "x64 / ARM64", color=MUTED)
    p3, w3 = pill(64 + w1 + w2 + 16, 286, "Windows 10+", color=MUTED)
    p4, w4 = pill(64 + w1 + w2 + w3 + 24, 286, "WinUI 3", color=BLUE)

    # routing diagram
    hub_cx, hub_cy, hub_r = 906, 196, 54
    src_x, src_y, src_w, src_h = 664, 160, 132, 72

    node_defs = [
        ("香港", True, 74),
        ("日本", False, 174),
        ("新加坡", False, 274),
    ]
    node_svg_parts = []
    node_line_parts = []
    for label, active, ny in node_defs:
        nw = 96
        nx = 1060
        node_svg_parts.append(node_pill(nx, ny, label, active))
        # 从圆边沿朝节点中心方向出发，落到节点左缘
        ncy = ny + 22
        dx, dy = (nx - hub_cx), (ncy - hub_cy)
        dist = (dx * dx + dy * dy) ** 0.5
        sx = hub_cx + hub_r * dx / dist
        sy = hub_cy + hub_r * dy / dist
        ex = nx - 4
        node_line_parts.append(f'<line x1="{sx:.1f}" y1="{sy:.1f}" x2="{ex:.1f}" y2="{ncy:.1f}"/>')
        # 箭头指向节点：tip 在节点左缘，三角底边垂直于连线方向
        ax, ay = ex, ncy
        ux, uy = dx / dist, dy / dist
        px, py = -uy, ux
        node_line_parts.append(
            f'<path d="M {ax:.1f} {ay:.1f} '
            f'l {(-9*ux+5*px):.1f} {(-9*uy+5*py):.1f} '
            f'l {(-10*px):.1f} {(-10*py):.1f} z" fill="{STROKE}"/>'
        )
    node_svg = "\n    ".join(node_svg_parts)
    node_lines = '<g stroke="' + STROKE + '" stroke-width="1.5" fill="none">\n    ' + "\n    ".join(
        p for p in node_line_parts if p.startswith("<line")
    ) + "\n  </g>\n  " + "\n  ".join(p for p in node_line_parts if p.startswith("<path"))

    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" width="{w}" height="{h}" role="img" aria-labelledby="title desc">
  <title id="title">Momomi — Windows 上的 mihomo 代理客户端</title>
  <desc id="desc">Momomi 是一个基于 mihomo 内核的 Windows 代理客户端，支持规则/全局/直连三种模式、系统代理与 TUN、订阅与代理组、实时连接和流量统计。右侧用路由示意图表示应用流量经 Momomi 分流到不同节点。</desc>

  <rect width="{w}" height="{h}" fill="{BG}"/>
  <g stroke="{GRID}" stroke-width="1" opacity="0.55">
    {grid}
  </g>

  <!-- brand -->
  <text x="66" y="150" font-family="{FONT}" font-size="76" font-weight="700" letter-spacing="-1" fill="{FG}">Momomi</text>
  <text x="66" y="60" font-family="{MONO}" font-size="13" letter-spacing="2" fill="{DIM}">MIHOMO KERNEL · WINDOWS CLIENT</text>
  <text x="66" y="200" font-family="{FONT}" font-size="22" fill="{FG}">Windows 上的 mihomo 代理客户端</text>
  <text x="66" y="232" font-family="{FONT}" font-size="17" fill="{MUTED}">订阅、代理组、TUN、实时连接与流量统计。</text>

  {p1}
  {p2}
  {p3}
  {p4}

  <!-- routing diagram -->
  <text x="664" y="130" font-family="{MONO}" font-size="12" letter-spacing="1.5" fill="{DIM}">RULE-BASED ROUTING</text>

  <g>
    <rect x="{src_x}" y="{src_y}" width="{src_w}" height="{src_h}" rx="12" fill="{PANEL}" stroke="{STROKE}"/>
    <text x="{src_x + src_w / 2}" y="{src_y + 32}" font-family="{FONT}" font-size="16" fill="{FG}" text-anchor="middle">应用与系统流量</text>
    <text x="{src_x + src_w / 2}" y="{src_y + 55}" font-family="{MONO}" font-size="12" fill="{DIM}" text-anchor="middle">7890 · mixed</text>
  </g>

  <g stroke="{STROKE}" stroke-width="1.5" fill="none">
    <line x1="{src_x + src_w}" y1="{hub_cy}" x2="{hub_cx - hub_r}" y2="{hub_cy}"/>
  </g>

  {node_lines}
  {node_svg}

  <g fill="{STROKE}">
    <path d="M {hub_cx - hub_r - 2} {hub_cy} l -8 -5 l 0 10 z"/>
  </g>
  <circle cx="{hub_cx}" cy="{hub_cy}" r="{hub_r}" fill="{PANEL}" stroke="{GREEN}" stroke-width="2"/>
  {cat_glyph(hub_cx - 20, hub_cy - 20, 5, FG)}
  <text x="{hub_cx}" y="{hub_cy + hub_r + 22}" font-family="{MONO}" font-size="12" fill="{MUTED}" text-anchor="middle">规则 / 全局 / 直连</text>
</svg>
"""


with open(r"E:\Code\momomi\assets\readme\logo.svg", "w", encoding="utf-8") as f:
    f.write(build_logo())
with open(r"E:\Code\momomi\assets\readme\hero.svg", "w", encoding="utf-8") as f:
    f.write(build_hero())
print("written")
