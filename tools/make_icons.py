"""Generates RambiBox icons (app icon + tray state icons) as multi-size .ico files.
Pure standard library: signed-distance-field shapes rendered with supersampling.
Usage: python tools/make_icons.py [preview_dir]
"""
import math, os, struct, sys, zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "src", "RambiBox.App", "Assets")


def sd_round_rect(px, py, cx, cy, hw, hh, r):
    qx = abs(px - cx) - hw + r
    qy = abs(py - cy) - hh + r
    ox, oy = max(qx, 0.0), max(qy, 0.0)
    return math.hypot(ox, oy) + min(max(qx, qy), 0.0) - r


# Ambilight palette around the screen (conic gradient), clockwise from the right.
PALETTE = [(0xFF, 0x4F, 0x9A), (0xB0, 0x5C, 0xFF), (0x3D, 0x8B, 0xFF), (0x14, 0xD8, 0xF0),
           (0x3C, 0xE8, 0x8C), (0xFF, 0xD0, 0x3A), (0xFF, 0x7A, 0x3D)]


def conic(px, py, cx, cy):
    a = (math.atan2(py - cy, px - cx) / (2 * math.pi)) % 1.0
    f = a * len(PALETTE)
    i = int(f) % len(PALETTE)
    j = (i + 1) % len(PALETTE)
    t = f - int(f)
    t = t * t * (3 - 2 * t)
    c0, c1 = PALETTE[i], PALETTE[j]
    return tuple((c0[k] * (1 - t) + c1[k] * t) / 255.0 for k in range(3))


def over(dst, src):
    """Porter-Duff over, colors premultiplied (r,g,b,a)."""
    sa = src[3]
    return (src[0] + dst[0] * (1 - sa), src[1] + dst[1] * (1 - sa), src[2] + dst[2] * (1 - sa), sa + dst[3] * (1 - sa))


def coverage(d, px_size):
    # d in normalized units; antialias over one pixel
    return min(1.0, max(0.0, 0.5 - d / px_size))


def render(size, shader, ss=4):
    out = bytearray(size * size * 4)
    inv = 1.0 / size
    for y in range(size):
        for x in range(size):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sy in range(ss):
                for sx in range(ss):
                    px = (x + (sx + 0.5) / ss) * inv
                    py = (y + (sy + 0.5) / ss) * inv
                    c = shader(px, py, inv)
                    for k in range(4):
                        acc[k] += c[k]
            n = ss * ss
            a = acc[3] / n
            if a > 0:
                r, g, b = (acc[0] / n) / a, (acc[1] / n) / a, (acc[2] / n) / a
            else:
                r = g = b = 0
            i = (y * size + x) * 4
            out[i:i + 4] = bytes([min(255, int(r * 255 + 0.5)), min(255, int(g * 255 + 0.5)),
                                  min(255, int(b * 255 + 0.5)), min(255, int(a * 255 + 0.5))])
    return out


# ---------------- shaders ----------------

def app_shader(size):
    small = size <= 24
    cx, cy = 0.5, (0.5 if small else 0.46)
    hw, hh = (0.34, 0.25) if small else (0.33, 0.235)
    rad = 0.05
    glow_w = 0.06 if small else 0.075

    def sh(px, py, pxs):
        d = sd_round_rect(px, py, cx, cy, hw, hh, rad)
        col = (0.0, 0.0, 0.0, 0.0)
        if d > 0:
            # soft halo + brighter "LED strip" band right outside the screen
            g = math.exp(-d / glow_w) * 0.85
            band = math.exp(-((d - 0.03) / 0.018) ** 2) * 0.35
            a = min(1.0, g + band)
            # fade out towards the icon edge so the glow never looks clipped
            edge = min(px, py, 1 - px, 1 - py)
            a *= min(1.0, max(0.0, edge / 0.06))
            r, g2, b = conic(px, py, cx, cy)
            col = (r * a, g2 * a, b * a, a)
        # small stand for larger sizes (drawn before the screen)
        if not small:
            ds = min(sd_round_rect(px, py, 0.5, cy + hh + 0.05, 0.035, 0.05, 0.01),
                     sd_round_rect(px, py, 0.5, cy + hh + 0.105, 0.13, 0.022, 0.02))
            cs = coverage(ds, pxs)
            if cs > 0:
                c = (0x3A / 255, 0x42 / 255, 0x55 / 255)
                col = over(col, (c[0] * cs, c[1] * cs, c[2] * cs, cs))
        # screen body
        cov = coverage(d, pxs)
        if cov > 0:
            t = (py - (cy - hh)) / (2 * hh)
            top, bot = (0x2C / 255, 0x33 / 255, 0x45 / 255), (0x14 / 255, 0x17 / 255, 0x20 / 255)
            base = tuple(top[k] * (1 - t) + bot[k] * t for k in range(3))
            bez = coverage(d + max(pxs, 0.02), pxs)
            rim = (0x5A / 255, 0x64 / 255, 0x7A / 255)
            c = tuple(base[k] * bez + rim[k] * (1 - bez) for k in range(3))
            if bez > 0 and (px - cx) + (py - cy) * 1.6 < -0.12:
                c = tuple(min(1.0, v + 0.05) for v in c)
            col = over(col, (c[0] * cov, c[1] * cov, c[2] * cov, cov))
        return col
    return sh


def tray_shader(size, state, fg):
    cx, cy = 0.5, 0.5
    hw, hh, rad = 0.335, 0.245, 0.06
    stroke = max(1.15 / size, 0.085)  # normalized stroke width
    fr, fgc, fb = fg

    def sh(px, py, pxs):
        d = sd_round_rect(px, py, cx, cy, hw, hh, rad)
        col = (0.0, 0.0, 0.0, 0.0)
        if state == "on" and d > 0:
            a = min(1.0, math.exp(-d / 0.06) * 0.95)
            edge = min(px, py, 1 - px, 1 - py)
            a *= min(1.0, max(0.0, edge / 0.04))
            r, g, b = conic(px, py, cx, cy)
            col = (r * a, g * a, b * a, a)
        ring = coverage(d, pxs) * (1 - coverage(d + stroke, pxs))
        alpha = 1.0 if state != "off" else 0.6
        if state == "on":
            fill = coverage(d + stroke, pxs) * 0.22
            col = over(col, (fr * fill, fgc * fill, fb * fill, fill))
        a = ring * alpha
        col = over(col, (fr * a, fgc * a, fb * a, a))
        if state == "off":
            # diagonal slash = lights off
            dl = abs((px - 0.5) + (py - 0.5)) / math.sqrt(2) - stroke * 0.55
            seg = max(abs((px - 0.5) - (py - 0.5)) / math.sqrt(2) - 0.36, 0)
            dd = max(dl, seg)
            cut = coverage(dd - stroke * 0.9, pxs)
            col = tuple(v * (1 - cut) for v in col)
            s = coverage(dd, pxs) * alpha
            col = over(col, (fr * s, fgc * s, fb * s, s))
        if state == "nc":
            bx, by, br = 0.76, 0.74, 0.2
            db = math.hypot(px - bx, py - by) - br
            cut = coverage(db - max(1.0 / size, 0.06), pxs)
            col = tuple(v * (1 - cut) for v in col)
            s = coverage(db, pxs)
            c = (0xFF / 255, 0xB9 / 255, 0x00 / 255)
            col = over(col, (c[0] * s, c[1] * s, c[2] * s, s))
            if size >= 24:
                dx = abs(px - bx)
                bar = max(dx - 0.022, abs(py - (by - 0.045)) - 0.065)
                dot = math.hypot(px - bx, py - (by + 0.095)) - 0.028
                e = coverage(min(bar, dot), pxs) * s
                col = over(col, (0.1 * e, 0.1 * e, 0.1 * e, e))
        return col
    return sh


# ---------------- encoding ----------------

def png_bytes(size, rgba):
    raw = b"".join(b"\x00" + bytes(rgba[y * size * 4:(y + 1) * size * 4]) for y in range(size))

    def chunk(t, data):
        c = struct.pack(">I", len(data)) + t + data
        return c + struct.pack(">I", zlib.crc32(t + data) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)) +
            chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def dib_bytes(size, rgba):
    hdr = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    rows = []
    for y in range(size - 1, -1, -1):
        row = bytearray()
        for x in range(size):
            i = (y * size + x) * 4
            r, g, b, a = rgba[i:i + 4]
            row += bytes([b, g, r, a])
        rows.append(bytes(row))
    mask_stride = ((size + 31) // 32) * 4
    mask = b"\x00" * (mask_stride * size)
    return hdr + b"".join(rows) + mask


def write_ico(path, images):
    images = sorted(images, key=lambda t: t[0])
    blobs = [png_bytes(s, px) if s >= 256 else dib_bytes(s, px) for s, px in images]
    out = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for (s, _), blob in zip(images, blobs):
        out += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    out += b"".join(blobs)
    with open(path, "wb") as f:
        f.write(out)


def main():
    preview = sys.argv[1] if len(sys.argv) > 1 else None
    os.makedirs(os.path.join(ASSETS, "Tray"), exist_ok=True)
    app_sizes = [16, 20, 24, 32, 40, 48, 64, 256]
    imgs = [(s, render(s, app_shader(s), ss=4 if s <= 64 else 2)) for s in app_sizes]
    write_ico(os.path.join(ASSETS, "RambiBox.ico"), imgs)
    if preview:
        for s, px in imgs:
            open(os.path.join(preview, f"app_{s}.png"), "wb").write(png_bytes(s, px))
    tray_sizes = [16, 20, 24, 32, 40, 48, 64]
    for theme, fg in (("dark", (1.0, 1.0, 1.0)), ("light", (0.1, 0.1, 0.1))):
        for state in ("on", "off", "nc"):
            imgs = [(s, render(s, tray_shader(s, state, fg))) for s in tray_sizes]
            write_ico(os.path.join(ASSETS, "Tray", f"{state}_{theme}.ico"), imgs)
            if preview:
                for s, px in imgs:
                    open(os.path.join(preview, f"tray_{state}_{theme}_{s}.png"), "wb").write(png_bytes(s, px))
    print("icons written")


if __name__ == "__main__":
    main()
