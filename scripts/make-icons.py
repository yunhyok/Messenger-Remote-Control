# Generates the Master and Slave application icons (ICO with 16-256 px entries) from vector-like drawings.
# Master: a messenger speech bubble carrying a remote-control signal; Slave: a workstation screen with a
# frequency sweep that ends in a check mark (PowerSI completion watch).
from PIL import Image, ImageDraw
import math, sys, os

OUT = sys.argv[1]
SCALE = 4            # draw at 1024 px, downsample for antialiasing
BASE = 256
W = BASE * SCALE

def canvas():
    return Image.new("RGBA", (W, W), (0, 0, 0, 0))

def rounded(draw, box, r, fill, outline=None, width=0):
    draw.rounded_rectangle(box, radius=r, fill=fill, outline=outline, width=width)

def master():
    img = canvas(); d = ImageDraw.Draw(img)
    s = SCALE
    # Background tile: deep navy rounded square (Win7-safe plain colors)
    rounded(d, (12*s, 12*s, 244*s, 244*s), 52*s, (24, 56, 112, 255))
    # Speech bubble (white)
    bx0, by0, bx1, by1 = 44*s, 54*s, 212*s, 176*s
    rounded(d, (bx0, by0, bx1, by1), 34*s, (255, 255, 255, 255))
    # Bubble tail
    d.polygon([(74*s, 170*s), (62*s, 214*s), (118*s, 174*s)], fill=(255, 255, 255, 255))
    # Signal arcs inside the bubble (remote control / wireless), navy strokes
    cx, cy = 128*s, 150*s
    for i, r in enumerate((26, 48, 70)):
        bbox = (cx - r*s, cy - r*s, cx + r*s, cy + r*s)
        d.arc(bbox, start=215, end=325, fill=(24, 56, 112, 255), width=13*s)
    # Signal origin dot
    d.ellipse((cx - 11*s, cy - 11*s, cx + 11*s, cy + 11*s), fill=(24, 56, 112, 255))
    # Ready dot (green) at the bubble's top-right
    d.ellipse((176*s, 60*s, 206*s, 90*s), fill=(46, 170, 96, 255), outline=(255, 255, 255, 255), width=5*s)
    return img

def slave():
    img = canvas(); d = ImageDraw.Draw(img)
    s = SCALE
    # Background tile: teal rounded square
    rounded(d, (12*s, 12*s, 244*s, 244*s), 52*s, (18, 94, 104, 255))
    # Monitor body (white frame) and dark screen
    rounded(d, (40*s, 50*s, 216*s, 170*s), 18*s, (255, 255, 255, 255))
    rounded(d, (52*s, 62*s, 204*s, 158*s), 10*s, (16, 36, 44, 255))
    # Stand
    d.rectangle((116*s, 170*s, 140*s, 192*s), fill=(255, 255, 255, 255))
    rounded(d, (84*s, 190*s, 172*s, 206*s), 7*s, (255, 255, 255, 255))
    # Frequency sweep: sine wave across the screen (light teal), amplitude decaying toward the right
    pts = []
    x0, x1 = 62*s, 150*s
    for i in range(0, 161):
        t = i / 160.0
        x = x0 + (x1 - x0) * t
        amp = 22 * (1.0 - 0.45 * t)
        y = 110*s + amp * s * math.sin(t * math.pi * 5)
        pts.append((x, y))
    d.line(pts, fill=(120, 220, 210, 255), width=9*s, joint="curve")
    # Completion check mark (green) at the right of the screen
    d.line([(154*s, 112*s), (170*s, 130*s), (196*s, 90*s)], fill=(80, 210, 120, 255), width=13*s, joint="curve")
    return img

def save(img, name):
    small = img.resize((BASE, BASE), Image.LANCZOS)
    small.save(os.path.join(OUT, name + ".png"))
    sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    small.save(os.path.join(OUT, name + ".ico"), format="ICO", sizes=sizes)

save(master(), "master")
save(slave(), "slave")
print("ok")
