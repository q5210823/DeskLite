# -*- coding: utf-8 -*-
"""
DeskLite 应用图标生成脚本（纯 Pillow 矢量绘制，无外部依赖）

设计语义：
  蓝紫渐变圆角底板  = 毛玻璃格子窗口
  2×2 白色圆角方块  = 桌面被切成四个格子
  右下琥珀色格+↓箭头 = 「文件归入格子」这一核心动作

每个尺寸单独按比例绘制（非缩放），并用 4× 超采样保证小尺寸边缘平滑。

用法：
    python make_icon.py                 # 生成 app.ico + 各尺寸 PNG 预览
"""
import os
from PIL import Image, ImageDraw

OUT_DIR = os.path.dirname(os.path.abspath(__file__))

# ── 配色 ────────────────────────────────────────────────────────────────
C_TOPLEFT = (90, 134, 255)     # 5A86FF 亮蓝
C_BOTRIGHT = (108, 77, 247)    # 6C4DF7 蓝紫
C_CELL = (255, 255, 255)       # 格子
C_ACCENT = (255, 176, 32)      # FFB020 高亮格（琥珀）

SS = 4                          # 超采样倍数


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def diag_gradient(n, c0, c1):
    """对角线渐变（小图生成后放大，避免大画布逐像素的开销）"""
    img = Image.new("RGB", (n, n))
    px = img.load()
    denom = 2.0 * (n - 1)
    for y in range(n):
        for x in range(n):
            px[x, y] = lerp(c0, c1, (x + y) / denom)
    return img


def vstrip(n, stops):
    """竖直渐变条（1×n，L 模式），stops = [(位置0~1, 亮度0~255), ...] 自上而下"""
    img = Image.new("L", (1, n))
    px = img.load()
    for y in range(n):
        t = y / (n - 1)
        v = stops[-1][1]
        for i in range(len(stops) - 1):
            p0, v0 = stops[i]
            p1, v1 = stops[i + 1]
            if t <= p1:
                if p1 == p0:
                    v = v1
                else:
                    k = (t - p0) / (p1 - p0)
                    k = 0.0 if k < 0 else (1.0 if k > 1 else k)
                    v = v0 + (v1 - v0) * k
                break
        px[0, y] = int(round(v))
    return img


def draw_icon(size):
    """按 size 单独绘制一枚图标（RGBA）"""
    S = size * SS
    u = S / 256.0                       # 设计单位换算

    pad = 10 * u                        # 外留白
    bx0, by0, bx1, by1 = pad, pad, S - pad, S - pad
    bw = bx1 - bx0
    plate_r = 0.235 * bw

    # ── 1. 底板：圆角矩形 + 对角渐变 ──────────────────────────────────
    base = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    shape = Image.new("L", (S, S), 0)
    ImageDraw.Draw(shape).rounded_rectangle([bx0, by0, bx1, by1], radius=plate_r, fill=255)

    grad = diag_gradient(256, C_TOPLEFT, C_BOTRIGHT).resize((S, S), Image.BICUBIC)
    plate = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    plate.paste(grad, (0, 0), shape)
    base = Image.alpha_composite(base, plate)

    # ── 2. 顶部高光（玻璃感）──────────────────────────────────────────
    hl = vstrip(256, [(0.0, 253), (0.42, 77), (0.72, 0), (1.0, 0)]).resize((S, S), Image.BICUBIC)
    hl_mask = Image.composite(hl, Image.new("L", (S, S), 0), shape)
    gloss = Image.new("RGBA", (S, S), (255, 255, 255, 255))
    base.paste(gloss, (0, 0), hl_mask)

    # ── 3. 底部内阴影（立体感）────────────────────────────────────────
    sh = vstrip(256, [(0.0, 0), (0.62, 0), (1.0, 42)]).resize((S, S), Image.BICUBIC)
    sh_mask = Image.composite(sh, Image.new("L", (S, S), 0), shape)
    base.paste(Image.new("RGBA", (S, S), (0, 0, 0, 255)), (0, 0), sh_mask)

    # ── 4. 细描边（浅色任务栏上也能看清轮廓）─────────────────────────
    if size >= 32:
        edge = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        w = max(1, int(round(1.6 * u)))
        ImageDraw.Draw(edge).rounded_rectangle(
            [bx0 + w / 2, by0 + w / 2, bx1 - w / 2, by1 - w / 2],
            radius=plate_r, outline=(255, 255, 255, 58), width=w)
        base = Image.alpha_composite(base, edge)

    # ── 5. 2×2 格子 ───────────────────────────────────────────────────
    inner = 0.135 * bw
    gx0, gy0 = bx0 + inner, by0 + inner
    gw = bw - 2 * inner
    gap = 0.088 * bw
    cell = (gw - gap) / 2.0
    cell_r = 0.20 * cell
    cell_a = 240 if size >= 32 else 255

    def cell_rect(c, r):
        x = gx0 + c * (cell + gap)
        y = gy0 + r * (cell + gap)
        return [x, y, x + cell, y + cell]

    # 格子投影
    shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shadow)
    off = 2.2 * u
    for c in range(2):
        for r in range(2):
            x0, y0, x1, y1 = cell_rect(c, r)
            sd.rounded_rectangle([x0, y0 + off, x1, y1 + off], radius=cell_r,
                                 fill=(0, 0, 0, 40))
    base = Image.alpha_composite(base, shadow)

    # 格子本体
    cells = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    cd = ImageDraw.Draw(cells)
    for c in range(2):
        for r in range(2):
            if c == 1 and r == 1:
                continue
            cd.rounded_rectangle(cell_rect(c, r), radius=cell_r,
                                 fill=C_CELL + (cell_a,))
    cd.rounded_rectangle(cell_rect(1, 1), radius=cell_r, fill=C_ACCENT + (255,))
    base = Image.alpha_composite(base, cells)

    # ── 6. 高亮格里的「归入」箭头（小尺寸省略）────────────────────────
    if size >= 48:
        x0, y0, x1, y1 = cell_rect(1, 1)
        cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
        aw = 0.30 * cell          # 箭杆半宽
        ah = 0.34 * cell          # 箭头总高
        arw = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        ad = ImageDraw.Draw(arw)
        stem_w = 0.115 * cell
        ad.rectangle([cx - stem_w, cy - ah * 0.5, cx + stem_w, cy + ah * 0.06],
                     fill=(255, 255, 255, 245))
        ad.polygon([(cx - aw, cy - ah * 0.02),
                    (cx + aw, cy - ah * 0.02),
                    (cx, cy + ah * 0.52)], fill=(255, 255, 255, 245))
        base = Image.alpha_composite(base, arw)

    return base.resize((size, size), Image.LANCZOS)


def pack_ico(entries, path):
    """手写 ICO 打包。entries = [(size, blob, is_png), ...]（blob 已是条目数据）"""
    import struct

    n = len(entries)
    blobs, dirs = [], []
    offset = 6 + 16 * n
    for s, data, _ in entries:
        b = 0 if s >= 256 else s                      # 256 在 ICO 里记为 0
        dirs.append(struct.pack("<BBBBHHII", b, b, 0, 0, 1, 32, len(data), offset))
        blobs.append(data)
        offset += len(data)
    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, n))
        f.write(b"".join(dirs))
        f.write(b"".join(blobs))


def bmp_blob(img):
    """单帧 BMP 条目数据（借 Pillow 编码，去掉 22 字节 ICO 头）——GDI 兼容性最好"""
    import io
    buf = io.BytesIO()
    img.save(buf, format="ICO", sizes=[(img.width, img.height)])
    return buf.getvalue()[22:]


def png_blob(img):
    import io
    buf = io.BytesIO()
    img.save(buf, "PNG", optimize=True)
    return buf.getvalue()


def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    imgs = {s: draw_icon(s) for s in sizes}

    ico = os.path.join(OUT_DIR, "app.ico")
    pack_ico([(s, png_blob(imgs[s]), True) for s in sorted(sizes)], ico)

    # 托盘专用：只留托盘会用到的四档，BMP 条目（new Icon(stream, w, h) 挑档最稳）
    tray = os.path.join(OUT_DIR, "app-tray.ico")
    tray_sizes = [16, 20, 24, 32]
    pack_ico([(s, bmp_blob(imgs[s]), False) for s in tray_sizes], tray)

    for s in (256, 48, 32, 16):
        imgs[s].save(os.path.join(OUT_DIR, f"app-{s}.png"))

    # 展示用的对比图：一排不同尺寸
    strip = Image.new("RGBA", (16 + sum(s + 16 for s in (256, 128, 64, 48, 32, 24, 16)), 288),
                      (246, 247, 251, 255))
    x = 16
    for s in (256, 128, 64, 48, 32, 24, 16):
        strip.paste(imgs[s], (x, (288 - s) // 2), imgs[s])
        x += s + 16
    strip.save(os.path.join(OUT_DIR, "app-preview.png"))

    print("ico :", ico, os.path.getsize(ico), "bytes")
    print("tray:", tray, os.path.getsize(tray), "bytes")
    for s in (256, 48, 32, 16):
        print(f"png {s:>3}:", os.path.join(OUT_DIR, f"app-{s}.png"))
    print("preview:", os.path.join(OUT_DIR, "app-preview.png"))


if __name__ == "__main__":
    main()
