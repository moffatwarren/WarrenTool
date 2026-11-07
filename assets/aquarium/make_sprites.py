#!/usr/bin/env python3
"""Generate the aquarium's pixel art into sprites/. Standard library only.

Catppuccin Mocha in flat colour, drawn for pure black water.

The PNGs are committed, so this only needs running after editing the art here.
Everything is drawn facing right; Aquarium.qml mirrors a sprite to swim left.
The output is deterministic: re-running it changes nothing unless the art does.

This is WarrenTool's copy of the generator in Linux-Setup (Hyprland_Setup/aquarium). Its
sprites are embedded in Aquarium.scr by Install.ps1 and drawn by src/Aquarium.cs, which
mirrors them the same way. The shark is only in this copy.
"""
import math
import random
import struct
import zlib
from pathlib import Path

OUT = Path(__file__).resolve().parent / "sprites"


# --- PNG and colour helpers -------------------------------------------------

def write_png(path, rows):
    h = len(rows)
    w = len(rows[0])
    raw = bytearray()
    for row in rows:
        raw.append(0)
        for px in row:
            raw.extend(px)

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body))

    path.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + chunk(b"IEND", b"")
    )


def rgb(hex_colour):
    h = hex_colour.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def mix(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def shade(c, f):
    return tuple(min(255, round(v * f)) for v in c)


CLEAR = (0, 0, 0, 0)


def blank(w, h):
    return [[CLEAR for _ in range(w)] for _ in range(h)]


def put(img, x, y, c, a=255):
    if 0 <= y < len(img) and 0 <= x < len(img[0]):
        img[y][x] = (*c, a)


def outline(img, c):
    """Add a one-pixel outline around everything opaque, inside the canvas."""
    h, w = len(img), len(img[0])
    out = [row[:] for row in img]
    for y in range(h):
        for x in range(w):
            if img[y][x][3]:
                continue
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nx, ny = x + dx, y + dy
                if 0 <= nx < w and 0 <= ny < h and img[ny][nx][3]:
                    out[y][x] = (*c, 255)
                    break
    return out


def distant(img):
    """Recolour a sprite for the back of the tank: a cool shape keeping a hint of its colour.

    It is not simply dimmed, because a darkened warm pastel is brown. On black that turned
    every far clownfish and goldfish the colour of mud.
    """
    low, high = rgb("#313244"), rgb("#7f849c")
    out = blank(len(img[0]), len(img))
    for y, row in enumerate(img):
        for x, (r, g, b, a) in enumerate(row):
            if a:
                light = (0.3 * r + 0.59 * g + 0.11 * b) / 255
                out[y][x] = (*mix(mix(low, high, light), (r, g, b), 0.25), a)
    return out


# --- Palette-mapped ASCII art -----------------------------------------------

WHITE = rgb("#cdd6f4")
INK = rgb("#11111b")


def palette(body, stripe, fin, belly=None):
    body, stripe, fin = rgb(body), rgb(stripe), rgb(fin)
    return {
        "B": body,
        "L": rgb(belly) if belly else mix(body, WHITE, 0.38),
        "D": shade(body, 0.7),
        "S": stripe,
        "T": shade(stripe, 0.7),
        "F": fin,
        "G": shade(fin, 0.7),
        # A dim rim in the fish's own colour, which still shows against black.
        "O": shade(body, 0.36),
        "W": WHITE,
        "K": INK,
    }


def from_ascii(art, pal):
    width = max(len(row) for row in art)
    img = blank(width, len(art))
    for y, row in enumerate(art):
        for x, ch in enumerate(row):
            if ch != ".":
                put(img, x, y, pal[ch])
    return img


def wiggle(img, tail, shift, amp):
    """Shear the tail columns vertically. Padding of `amp` rows keeps them on canvas."""
    h, w = len(img), len(img[0])
    out = blank(w, h + amp * 2)
    for x in range(w):
        offset = amp
        if x < tail:
            offset += round(shift * amp * (tail - x) / tail)
        for y in range(h):
            if img[y][x][3]:
                out[y + offset][x] = img[y][x]
    return out


FISH = {
    "clown": dict(tail=6, amp=1, art=[
        "..........OOOOO.........",
        ".........OFFGFFOO.......",
        "........OFFFGFFFFOO.....",
        ".OOO..OOBBSSBBBBSSBOO...",
        "OFFFO.OBBBSSSBBBSSBBBOO.",
        "OFGFFOBBBBSSSBBBSSBWWBBO",
        ".OGFFOBBBBSSSBBBSSBWKBBO",
        ".OGFFOBBBBSSSBBBSSBBBBBO",
        "OFGFFOLLBBSSSBBBSSBBBLLO",
        "OFFFO.OLLLSSSLLLSSLLLOO.",
        ".OOO..OOLLSSLLLLSSLOO...",
        "........OFFGOOOFFGO.....",
        ".........OOO...OOO......",
    ]),
    "tang": dict(tail=6, amp=1, art=[
        ".......OOOOOOOOOOO......",
        ".....OOFFFFFFFFFFFOO....",
        ".OO.OFFFKKKKKKKKKKFFOO..",
        "OSSOOBBKKKKKKKKKKKKBBOO.",
        "OSSSOBBBKKBBBBBBKKKKBBBO",
        ".OSSOBBBBBBBBBBBBBKBWWBO",
        ".OTSOBBBBBBBBBBBBBBBWKBO",
        ".OSSOBBBBBBBBBBBBBBBBBBO",
        "OSSSOBBBBBBBBBBBBBBBBBO.",
        "OSSOOLLLLLLLLLLLLLLLLO..",
        ".OO.OFFLLLLLLLLLLLFFO...",
        ".....OOFFFFFFFFFFFOO....",
        ".......OOOOOOOOOOO......",
    ]),
    "angel": dict(tail=5, amp=1, art=[
        "...........OO.......",
        "..........OFO.......",
        ".........OFFO.......",
        "........OFGFO.......",
        ".......OFFGFOO......",
        "......OFFGFFFFO.....",
        ".....OOBSBBSBBOO....",
        "....OBBBSBBSBBBBO...",
        "OO.OBBBBSBBSBBBWWO..",
        "OFOOBBBBSBBSBBBWKBO.",
        "OGFOBBBBSBBSBBBBBBBO",
        "OGFOBBBBSBBSBBBBBBO.",
        "OFOOLLLLSLLSLLLLLO..",
        "OO.OLLLLSLLSLLLOO...",
        "....OOLLSLLSLOO.....",
        "......OFGFFGOO......",
        ".......OFGFFO.......",
        "........OFGFO.......",
        ".........OFFO.......",
        "..........OFO.......",
        "...........OO.......",
    ]),
    "tetra": dict(tail=3, amp=1, art=[
        "....OOOOO....",
        "O..OBBBBBOOO.",
        "OOOSSSSSSSWKO",
        "OFOTTTSSSSWWO",
        "OOOLLLFFFFFLO",
        "O..OLLLFFFOO.",
        "....OOOOOO...",
    ]),
    "gold": dict(tail=9, amp=2, art=[
        "............OOOO.......",
        "OOO........OFFFFOO.....",
        "OFFOO.....OFFGFFFFOO...",
        ".OFFFOO.OOBBBBBBBBBBOO.",
        "..OGFFFOBBBBDBBBBBWWBBO",
        "...OGFFOBBBDBBBDBBWKBBO",
        "...OGGFOBBBBBBDBBBBBBBO",
        "..OGFFFOBBDBBBBBBBBBBO.",
        ".OFFFOO.OLLLLLLLLLLOO..",
        "OFFOO....OOLLLLLLOO....",
        "OOO.......OFFOOFFO.....",
        "...........OO..OO......",
    ]),
    "puffer": dict(tail=4, amp=1, art=[
        "......S...S...S....",
        ".......OOOOOOO.....",
        "....SOOBBBBBBBOOS..",
        "....OBBDBBBDBBBBO..",
        "...OBBBBBBBBBBWWBO.",
        "OO.OBBDBBBBDBBWKBO.",
        "OFOOBBBBBBBBBBWWBBO",
        "OGFOBBBDBBBBBBBBBSO",
        "OGFOLLLLLLLLLLLLBBO",
        "OFOOLLLLLLLLLLLLLO.",
        "OO.OLLLLLLLLLLLLLO.",
        "....OLLLLLLLLLLLO..",
        "...SOOLLLLLLLLOOS..",
        ".......OOOOOOO.....",
        "......S...S...S....",
    ]),
    "betta": dict(tail=12, amp=2, art=[
        "...............OOOO.........",
        "OO.............OFFFOOO......",
        "OFOO..........OFFGFFFFOO....",
        "OFFFOO.......OFFFGFFGFFFO...",
        ".OGFFFOO...OOBBBBBBBBBOOOO..",
        ".OFGFFFFOOOBBBBBBBBBBBBBBBO.",
        "..OFGGFFFFOBBDBBDBBBBBWWBBBO",
        "..OFFGFGFFOBBBBDBBDBBBWKBBBO",
        "..OFGFFGFFOBBDBBBBBBBBBBBBO.",
        ".OFGFFGFFOOLLLLLLLLLLLLOOO..",
        ".OGFFFGOO.OOFFFFFFLLLOOO....",
        "OFFFOOOO..OFFGFFGFFOO.......",
        "OFOO.....OFFGFFFGFO.........",
        "OO.......OFFFOOOFO..........",
        ".........OOO...OO...........",
    ]),
}

VARIANTS = {
    "clown": {
        "orange": palette("#fab387", "#f5e0dc", "#f9e2af", "#fbc9a8"),
        "red": palette("#f38ba8", "#f5e0dc", "#fab387", "#f5b5c8"),
    },
    "tang": {
        "blue": palette("#89b4fa", "#f9e2af", "#74c7ec", "#b4befe"),
        "teal": palette("#94e2d5", "#f5c2e7", "#74c7ec", "#b8efe6"),
    },
    "angel": {
        "yellow": palette("#f9e2af", "#585b70", "#fab387", "#f5e0dc"),
        "violet": palette("#cba6f7", "#f5e0dc", "#b4befe", "#f5c2e7"),
    },
    "tetra": {
        "neon": palette("#74c7ec", "#89dceb", "#f38ba8", "#cdd6f4"),
        "lime": palette("#a6e3a1", "#f9e2af", "#fab387", "#d3f0d0"),
    },
    "gold": {
        "gold": palette("#fab387", "#f9e2af", "#f9e2af", "#f5e0dc"),
        "pearl": palette("#eba0ac", "#f5e0dc", "#f2cdcd", "#f5e0dc"),
    },
    "puffer": {
        "yellow": palette("#f9e2af", "#f5e0dc", "#fab387", "#f5e0dc"),
        "green": palette("#a6e3a1", "#94e2d5", "#94e2d5", "#d3f0d0"),
    },
    "betta": {
        "magenta": palette("#f5c2e7", "#f2cdcd", "#cba6f7", "#f5e0dc"),
        "royal": palette("#b4befe", "#cdd6f4", "#f38ba8", "#cdd6f4"),
    },
}


def make_fish():
    for species, spec in FISH.items():
        for variant, pal in VARIANTS[species].items():
            base = from_ascii(spec["art"], pal)
            for frame, shift in enumerate((0, 1, -1)):
                img = wiggle(base, spec["tail"], shift, spec["amp"])
                write_png(OUT / f"fish_{species}_{variant}_{frame}.png", img)
                write_png(OUT / f"fish_{species}_{variant}_far_{frame}.png", distant(img))


# Drawn without its rim, which outline() adds: the canvas keeps a clear pixel on every
# side for it. There is no far version, since the one shark always swims in front.
SHARK = dict(tail=12, amp=2, art=[
    "..............................................",
    "..GF...................GF.....................",
    "..GFF..................GFF....................",
    "...GFF.................GFFF...................",
    "...GFFF...............GFFFFFF.................",
    "....GFFF......F......GFFFFFFFF................",
    ".....GFFF.....FFF.DDDDDDDDDDDDDDDD............",
    "......GFFF....DDDDBBBBBBBBBBBBBBBBDDDDD.......",
    ".......GFFBBBBBBBBBBBBBBBBBBBBBBSBSBSBBWKB....",
    "........GFBBBBBBBBBBBBBBBBBBBBBBSBSBSBBBBBBBB.",
    "........GFBBBBLLLLBBBBBBBBBBBBBBSBSBSLLLDDDB..",
    "......GFFF....LLLLLLLLLLLLLLLLLLLLLLLLLLL.....",
    ".....GFFF......FFFLLLLLLLLLLLLLLLLLLL.........",
    "....GFFF.......GG...........GFFFFF............",
    "....GF.....................GFFFF..............",
    "..........................GFFF................",
    "..........................GF..................",
    "..............................................",
])


def make_shark():
    pal = palette("#9399b2", "#6c7086", "#7f849c", "#cdd6f4")
    base = outline(from_ascii(SHARK["art"], pal), pal["O"])
    for frame, shift in enumerate((0, 1, -1)):
        write_png(OUT / f"shark_{frame}.png", wiggle(base, SHARK["tail"], shift, SHARK["amp"]))


JELLY_PAL = {
    "pink": {"B": rgb("#f5c2e7"), "L": rgb("#f5e0dc"), "D": rgb("#cba6f7"), "O": rgb("#8a6d82")},
    "aqua": {"B": rgb("#89dceb"), "L": rgb("#cdd6f4"), "D": rgb("#74c7ec"), "O": rgb("#4d7c85")},
}

JELLY = [
    [
        "....OOOOOOO....",
        "..OOLLLBBBBOO..",
        ".OLLBBBBBBBBBO.",
        "OLBBBDBBBDBBBBO",
        "OBBBBBBBBBBBBBO",
        "OBBDBBBDBBBDBBO",
        ".OOOOOOOOOOOOO.",
        "..D.B.D.B.D.B..",
        "..D.B.D.B.D.B..",
        ".D..B..D.B..D..",
        ".D.B...D.B..D..",
        "..D.B.D...B.D..",
        "..D.B.D...B..D.",
        "..D..B....B..D.",
    ],
    [
        "...............",
        "....OOOOOOO....",
        "..OOLLLBBBBOO..",
        ".OLBBBBBBBBBBO.",
        ".OBBBDBBBDBBBO.",
        ".OBDBBBDBBBDBO.",
        "..OOOOOOOOOOO..",
        "...D.B.D.B.D...",
        "...D.B.D.B.D...",
        "..D..B.D.B..D..",
        "..D.B..D..B.D..",
        "...D.B.D.B.D...",
        "...D.B...B.D...",
        "...............",
    ],
    [
        "...............",
        "...............",
        ".....OOOOO.....",
        "...OOLLBBBOO...",
        "..OLBBBBBBBBO..",
        "..OBBDBBBDBBO..",
        "..OBDBBDBBDBO..",
        "...OOOOOOOOO...",
        "....D.B.B.D....",
        "....D.B.B.D....",
        "....D.B.B.D....",
        "...D.B...B.D...",
        "...D.B...B.D...",
        "...............",
    ],
]

CRAB_PAL = {
    "O": rgb("#6f4050"), "B": rgb("#f38ba8"), "L": rgb("#f5c2d0"),
    "D": rgb("#b86880"), "W": WHITE, "K": INK,
}
CRAB = [
    [
        "...W......W...",
        "...K......K...",
        "OO..O....O..OO",
        "OBO.OOOOOO.OBO",
        "OLBOBLLLLBOBBO",
        ".OOBBBBBBBBOO.",
        "...OBDBBDBO...",
        "..D.OOOOOO.D..",
        ".D.D......D.D.",
    ],
    [
        "...W......W...",
        "...K......K...",
        ".OO.O....O.OO.",
        "OBBOOOOOOOOBBO",
        "OLBOBLLLLBOBBO",
        ".OOBBBBBBBBOO.",
        "...OBDBBDBO...",
        "...DOOOOOOD...",
        "..D.D....D.D..",
    ],
]

CHEST_PAL = {
    "O": rgb("#45332b"), "B": rgb("#8a6450"), "D": rgb("#6b4e3f"), "L": rgb("#b58a70"),
    "S": rgb("#f9e2af"), "T": rgb("#c4b08a"), "K": INK,
}
CHEST = [
    "...OOOOOOOOOOOOOOOO...",
    "..OLLLLSLLLLLLSLLLLO..",
    ".OLBBBBSBBBBBBSBBBBBO.",
    ".OBBBBBSBBBBBBSBBBBBO.",
    "OOSSSSSSSSSSSSSSSSSSOO",
    "OTTTTTTTTTSSTTTTTTTTTO",
    "OBBBBBBSBBSKSBSBBBBBBO",
    "OBDBBBBSBBSSSBSBBDBBBO",
    "OBBBDBBSBBBBBBSBBBBDBO",
    "ODDDDDDTDDDDDDTDDDDDDO",
    "OOOOOOOOOOOOOOOOOOOOOO",
]

BUBBLES = {
    "bubble_0": ["OO", "OO"],
    "bubble_1": [".OO.", "OL.O", "O..O", ".OO."],
    "bubble_2": ["..OOO..", ".O...O.", "OLL...O", "OL....O", "O.....O", ".O...O.", "..OOO.."],
}


def make_creatures():
    for colour, pal in JELLY_PAL.items():
        for frame, art in enumerate(JELLY):
            write_png(OUT / f"jelly_{colour}_{frame}.png", from_ascii(art, pal))
    for frame, art in enumerate(CRAB):
        write_png(OUT / f"crab_{frame}.png", from_ascii(art, CRAB_PAL))
    write_png(OUT / "chest.png", from_ascii(CHEST, CHEST_PAL))
    bubble_pal = {"O": rgb("#89dceb"), "L": WHITE}
    for name, art in BUBBLES.items():
        write_png(OUT / f"{name}.png", from_ascii(art, bubble_pal))


# --- Procedural scenery -----------------------------------------------------
#
# The water itself is not drawn at all: it is black, and Aquarium.qml's background
# already is. The scenery is kept dark, so that the creatures are what stands out.

def make_surface():
    light = rgb("#89b4fa")
    w, h = 64, 8
    for frame in range(3):
        img = blank(w, h)
        for x in range(w):
            phase = 2 * math.pi * (x / w + frame / 3)
            y = round(3 + 1.4 * math.sin(phase) + 0.6 * math.sin(phase * 2 + 1))
            put(img, x, y - 1, light, 24)
            put(img, x, y, light, 150)
            put(img, x, y + 1, light, 24)
        write_png(OUT / f"surface_{frame}.png", img)


def make_sand():
    rng = random.Random(7)
    w, h = 128, 40
    line, upper, lower = rgb("#6c7086"), rgb("#181825"), rgb("#0c0c13")
    specks = [rgb("#9399b2"), rgb("#7f849c"), rgb("#6c7086"), rgb("#a6adc8")]
    img = blank(w, h)
    for x in range(w):
        t = 2 * math.pi * x / w
        top = round(7 + 3 * math.sin(t) + 1.6 * math.sin(t * 3 + 0.8))
        for y in range(top, h):
            depth = y - top
            put(img, x, y, line if depth == 0 else upper if depth < 12 else lower)
    # Glints in the sand, dimmer the deeper they are.
    for _ in range(46):
        x, y = rng.randrange(w), rng.randrange(h)
        if img[y][x][3] == 255 and img[y][x][:3] != line:
            put(img, x, y, shade(rng.choice(specks), 0.75 if y < 20 else 0.45))
    write_png(OUT / "sand.png", img)


def make_far_rocks():
    # An outline only, filled with black: a dim skyline that leaves the water
    # black, and in which the nearer range still hides the one behind it.
    for name, col, peak, freq in (("far_0", "#313244", 58, 2), ("far_1", "#45475a", 40, 3)):
        w, h = 256, 66
        colour = rgb(col)
        tops = []
        for x in range(w):
            t = 2 * math.pi * x / w
            v = (math.sin(t * freq) + 0.5 * math.sin(t * freq * 2 + 1)
                 + 0.25 * math.sin(t * freq * 5 + 2) + 1.75) / 3.5
            top = h - round(v * peak) - 2
            # Stepped rather than smooth, so the skyline reads as pixels.
            tops.append(max(0, top - top % 2))
        img = blank(w, h)
        for x in range(w):
            for y in range(tops[x], h):
                edge = y == tops[x] or y < tops[(x - 1) % w] or y < tops[(x + 1) % w]
                put(img, x, y, colour if edge else (0, 0, 0))
        write_png(OUT / f"{name}.png", img)


def make_weed():
    specs = {
        "weed_green": ("#a6e3a1", "#7bb078", "#3c5a3e", 72, 2.2),
        "weed_teal": ("#94e2d5", "#6aa89d", "#355a56", 52, 1.8),
        "weed_lime": ("#a6e3a1", "#74c7ec", "#2f5560", 96, 2.6),
    }
    for name, (hi, lo, edge, h, amp) in specs.items():
        hi, lo, edge = rgb(hi), rgb(lo), rgb(edge)
        w = 27
        cx = w // 2
        for frame in range(4):
            img = blank(w, h)
            phase = frame * math.pi / 2
            for y in range(h):
                up = (h - 1 - y) / h
                x = cx + round(math.sin(up * 7 + phase) * amp * up * 1.6)
                put(img, x, y, hi)
                put(img, x + 1, y, lo)
                # A frond every few rows, alternating sides: two rows deep at the
                # stem and rising to a tip, so it reads as a leaf and not a thorn.
                if y % 5 == 0 and y < h - 3:
                    side = 1 if (y // 5) % 2 == 0 else -1
                    for i in range(1, 6):
                        lx = x + (i + 1 if side > 0 else -i)
                        rise = i // 2
                        put(img, lx, y - rise, hi if i < 4 else lo)
                        if i < 4:
                            put(img, lx, y - rise + 1, lo)
            write_png(OUT / f"{name}_{frame}.png", outline(img, edge))


def branch(img, rng, x, y, angle, length, width, hi, lo):
    px, py = x, y
    for i in range(length):
        px += math.cos(angle)
        py -= math.sin(angle)
        angle += rng.uniform(-0.1, 0.1)
        for off in range(width):
            put(img, round(px) + off, round(py), hi if off == 0 else lo)
    if length > 3:
        for delta in (-0.75, 0.1, 0.8):
            branch(img, rng, px, py, angle + delta + rng.uniform(-0.15, 0.15),
                   int(length * 0.68), max(2, width - 1), hi, lo)
    else:
        # A rounded tip, so the branches end in polyps rather than points.
        for off_x, off_y in ((0, -1), (1, -1), (-1, 0), (0, 0), (1, 0), (2, 0)):
            put(img, round(px) + off_x, round(py) + off_y, hi)


def make_coral():
    for name, hi, lo, edge, seed in (
        ("coral_pink", "#f5c2e7", "#c293b6", "#5e4a5a", 3),
        ("coral_orange", "#fab387", "#c58763", "#5f4333", 11),
        ("coral_violet", "#cba6f7", "#9b7cc4", "#4c3e60", 21),
    ):
        hi, lo, edge = rgb(hi), rgb(lo), rgb(edge)
        rng = random.Random(seed)
        img = blank(60, 44)
        branch(img, rng, 29, 43, math.pi / 2, 13, 4, hi, lo)
        write_png(OUT / f"{name}.png", outline(img, edge))

    # Brain coral: a dome with a squiggle through it.
    w, h = 30, 17
    hi, lo, edge = rgb("#f9e2af"), rgb("#c4ae80"), rgb("#5f5540")
    img = blank(w, h)
    for y in range(h):
        for x in range(w):
            dx, dy = (x - 14.5) / 13.5, (h - 1 - y) / 15
            if dx * dx + dy * dy <= 1:
                wave = math.sin(x * 0.9 + 2.2 * math.sin(y * 0.8))
                put(img, x, y, lo if wave > 0.35 else hi)
    write_png(OUT / "coral_brain.png", outline(img, edge))

    # Tube sponges: a cluster of open-topped columns.
    w, h = 26, 30
    hi, lo, edge, inner = rgb("#74c7ec"), rgb("#5499b8"), rgb("#2f4f60"), rgb("#1e1e2e")
    img = blank(w, h)
    for cx, top in ((5, 12), (12, 2), (19, 8)):
        for y in range(top, h):
            for x in range(cx - 2, cx + 3):
                put(img, x, y, hi if x <= cx - 1 else lo)
        for x in range(cx - 1, cx + 2):
            put(img, x, top, inner)
    write_png(OUT / "coral_tube.png", outline(img, edge))


def make_rocks():
    # Dark, with a lit rim: the one thing in the tank that should not compete.
    rim = rgb("#6c7086")
    for name, w, h, base, seed in (
        ("rock_0", 46, 24, "#262636", 5),
        ("rock_1", 30, 17, "#313244", 9),
    ):
        rng = random.Random(seed)
        c = rgb(base)
        img = blank(w, h)
        cx, rx = (w - 1) / 2, (w - 2) / 2
        for y in range(h):
            for x in range(w):
                dx, dy = (x - cx) / rx, (h - 1 - y) / (h - 2)
                bump = 0.08 * math.sin(x * 0.7 + seed)
                if dx * dx + dy * dy <= 1 + bump:
                    lit = -dx * 0.5 + dy * 0.8
                    col = mix(c, WHITE, 0.2) if lit > 0.55 else c if lit > 0.05 else shade(c, 0.72)
                    if rng.random() < 0.05:
                        col = shade(col, 0.85)
                    put(img, x, y, col)
        write_png(OUT / f"{name}.png", outline(img, rim))


# --- The lock screen's clock --------------------------------------------------

GLYPHS = {
    "0": ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
    "1": ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
    "2": ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
    "3": ["11111", "00010", "00100", "00010", "00001", "10001", "01110"],
    "4": ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
    "5": ["11111", "10000", "11110", "00001", "00001", "10001", "01110"],
    "6": ["00110", "01000", "10000", "11110", "10001", "10001", "01110"],
    "7": ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
    "8": ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
    "9": ["01110", "10001", "10001", "01111", "00001", "00010", "01100"],
    "colon": ["0", "1", "1", "0", "1", "1", "0"],
    "A": ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
    "P": ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
    "M": ["10001", "11011", "10101", "10101", "10001", "10001", "10001"],
}


def make_clock_glyphs():
    # A 5x7 arcade font at 3 tank pixels per dot, lavender fading down to mauve.
    scale = 3
    top, bottom = rgb("#b4befe"), rgb("#cba6f7")
    for name, rows in GLYPHS.items():
        img = blank(len(rows[0]) * scale, len(rows) * scale)
        for row, bits in enumerate(rows):
            colour = mix(top, bottom, row / (len(rows) - 1))
            for col, bit in enumerate(bits):
                if bit == "1":
                    for dy in range(scale):
                        for dx in range(scale):
                            put(img, col * scale + dx, row * scale + dy, colour)
        write_png(OUT / f"glyph_{name}.png", img)


def main():
    OUT.mkdir(exist_ok=True)
    # Start clean, so a sprite dropped from this file does not live on in sprites/.
    for old in OUT.glob("*.png"):
        old.unlink()
    make_fish()
    make_shark()
    make_creatures()
    make_surface()
    make_sand()
    make_far_rocks()
    make_weed()
    make_coral()
    make_rocks()
    make_clock_glyphs()
    print(f"{len(list(OUT.glob('*.png')))} sprites in {OUT}")


if __name__ == "__main__":
    main()
