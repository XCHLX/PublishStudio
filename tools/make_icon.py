"""生成 app.ico：蓝底圆角方块 + 白色向上箭头，多尺寸，纯标准库实现。"""
import struct

BLUE = (0xD4, 0x78, 0x00)  # BGRA 里的 BGR，即 #0078D4
WHITE = (0xFF, 0xFF, 0xFF)


def in_round_rect(x, y, x0, y0, x1, y1, r):
    if x < x0 or x > x1 or y < y0 or y > y1:
        return False
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def in_triangle(px, py, a, b, c):
    def sign(p1, p2, p3):
        return (p1[0] - p3[0]) * (p2[1] - p3[1]) - (p2[0] - p3[0]) * (p1[1] - p3[1])

    d1 = sign((px, py), a, b)
    d2 = sign((px, py), b, c)
    d3 = sign((px, py), c, a)
    has_neg = d1 < 0 or d2 < 0 or d3 < 0
    has_pos = d1 > 0 or d2 > 0 or d3 > 0
    return not (has_neg and has_pos)


TRI = ((0.50, 0.19), (0.79, 0.52), (0.21, 0.52))
BAR = (0.395, 0.47, 0.605, 0.83)


def sample(x, y):
    """返回 (是否在底板上, 是否在箭头上)。"""
    body = in_round_rect(x, y, 0.02, 0.02, 0.98, 0.98, 0.24)
    if not body:
        return False, False
    arrow = in_triangle(x, y, *TRI) or (BAR[0] <= x <= BAR[2] and BAR[1] <= y <= BAR[3])
    return True, arrow


def render(size, ss=4):
    """超采样渲染，返回 bottom-up 的 BGRA 字节。"""
    rows = []
    for py in range(size - 1, -1, -1):
        row = bytearray()
        for px in range(size):
            body_hits = 0
            arrow_hits = 0
            for sy in range(ss):
                for sx in range(ss):
                    x = (px + (sx + 0.5) / ss) / size
                    y = (py + (sy + 0.5) / ss) / size
                    body, arrow = sample(x, y)
                    if body:
                        body_hits += 1
                        if arrow:
                            arrow_hits += 1
            total = ss * ss
            alpha = body_hits / total
            if alpha == 0:
                row += b"\x00\x00\x00\x00"
                continue
            white_ratio = arrow_hits / body_hits if body_hits else 0
            b = int(BLUE[0] * (1 - white_ratio) + WHITE[0] * white_ratio)
            g = int(BLUE[1] * (1 - white_ratio) + WHITE[1] * white_ratio)
            r = int(BLUE[2] * (1 - white_ratio) + WHITE[2] * white_ratio)
            row += bytes((b, g, r, int(alpha * 255)))
        rows.append(bytes(row))
    return b"".join(rows)


def make_image(size):
    pixels = render(size)
    mask_row = ((size + 31) // 32) * 4
    mask = b"\x00" * (mask_row * size)
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, len(pixels), 0, 0, 0, 0)
    return header + pixels + mask


def main(path, sizes=(16, 32, 48, 64, 256)):
    images = [(s, make_image(s)) for s in sizes]
    out = bytearray(struct.pack("<HHH", 0, 1, len(images)))
    offset = 6 + 16 * len(images)
    for size, data in images:
        dim = 0 if size >= 256 else size
        out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in images:
        out += data
    with open(path, "wb") as f:
        f.write(out)
    print("OK", path, len(out))


if __name__ == "__main__":
    import sys
    main(sys.argv[1])
