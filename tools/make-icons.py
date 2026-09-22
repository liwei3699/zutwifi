"""生成图标资产：`assets/app.ico` + `assets/logo-256.png`。

    python tools/make-icons.py

来源按这个顺序挑：

1. `assets/logo.png` —— **本地私有**的一张图（各人自己的校徽、社团徽标……）。它在 .gitignore 里，
   所以仓库永远不带任何机构的标志，而本人构建时想要什么就是什么。
2. 没有那个文件时，就地画一个中性标记（品牌绿圆角方块 + 白色 Z）。画出来的东西完全由这段代码决定，
   所以公开仓库里那份 ico 是有出处、可复现的，不是"谁用画图点出来的一个二进制"。

为什么留脚本而不是只交二进制：ico 与 png 是**生成物**，出问题的样子（白底方块、缺 256 档、
边缘毛、名字打错导致运行期拿默认图标）只有重跑一遍才看得见。

抠底只处理"与外边连通"的近白像素，图内部的白（字、留白）一概不动 —— 所以先把原图收到 256
再做 BFS：256² 才 6.5 万像素，纯 Python 也是瞬间，而 256 本来就是 .ico 的最大一档，
先缩小不丢任何要发布的信息。
"""
import sys
from collections import deque
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
PERSONAL = ROOT / "assets" / "logo.png"          # 可选，且被 .gitignore 挡着
OUT_PNG = ROOT / "assets" / "logo-256.png"
OUT_ICO = ROOT / "assets" / "app.ico"
SIDE = 256
ICO_SIZES = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (256, 256)]
WHITE_MIN = 226          # 三个通道都 >= 这个值算"近白"
BRAND_GREEN = (22, 128, 61)     # 与 IconFactory 的 "green" 同一个数（用例盯着）


def draw_default_mark() -> Image.Image:
    """中性标记：品牌绿圆角方块 + 一个粗白 Z。16 像素下也要认得出，所以只有两笔。"""
    img = Image.new("RGBA", (SIDE, SIDE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([8, 8, SIDE - 9, SIDE - 9], radius=52, fill=(*BRAND_GREEN, 255))
    # Z 用多边形而不是字体：字体是外部依赖，装不装、哪个版本都会让"重跑一遍"给出不同的图。
    x0, y0, x1, y1, t = 72, 62, 184, 194, 30
    d.polygon([(x0, y0), (x1, y0), (x1, y0 + t), (x0 + t + 8, y1 - t),
               (x1, y1 - t), (x1, y1), (x0, y1), (x0, y1 - t),
               (x0 + t + 8, y0 + t), (x0, y0 + t)], fill=(255, 255, 255, 255))
    return img


def load_personal() -> Image.Image:
    img = Image.open(PERSONAL).convert("RGB")
    w, h = img.size
    if w != h:                          # 非方形就裁中央正方，别让徽标被拉扁
        side = min(w, h)
        img = img.crop(((w - side) // 2, (h - side) // 2,
                        (w - side) // 2 + side, (h - side) // 2 + side))
    return img.resize((SIDE, SIDE), Image.LANCZOS)


def near_white(px, x, y) -> bool:
    r, g, b = px[x, y][:3]
    return r >= WHITE_MIN and g >= WHITE_MIN and b >= WHITE_MIN


def punch_out_background(img: Image.Image) -> Image.Image:
    """把与边界连通的近白像素改成全透明，其余像素补上不透明 alpha。"""
    rgba = img.convert("RGBA")
    px = rgba.load()
    seen = [bytearray(SIDE) for _ in range(SIDE)]
    q = deque()

    def seed(x: int, y: int) -> None:
        if not seen[y][x] and near_white(px, x, y):
            seen[y][x] = 1
            q.append((x, y))

    for x in range(SIDE):
        seed(x, 0)
        seed(x, SIDE - 1)
    for y in range(SIDE):
        seed(0, y)
        seed(SIDE - 1, y)
    while q:                            # BFS 而不是递归：细颈处递归会先炸栈
        x, y = q.popleft()
        for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
            if 0 <= nx < SIDE and 0 <= ny < SIDE:
                seed(nx, ny)
    for y in range(SIDE):
        for x in range(SIDE):
            r, g, b, _ = px[x, y]
            px[x, y] = (0, 0, 0, 0) if seen[y][x] else (r, g, b, 255)
    return rgba


def main() -> int:
    if PERSONAL.exists():
        brand = punch_out_background(load_personal())
        print(f"来源：本地私有图 {PERSONAL.name}（抠掉外圈白底）")
    else:
        brand = draw_default_mark()
        print("来源：内置中性标记（没有 assets/logo.png，仓库里那份就是这个）")
    brand.save(OUT_PNG)                                     # 「关于」页与 .ico 的共同母图
    brand.save(OUT_ICO, sizes=ICO_SIZES)                    # Pillow 给 256 档写 PNG 负载
    px = brand.load()
    corner = (px[0, 0][3], px[SIDE - 1, SIDE - 1][3])
    print(f"{OUT_PNG.name} {OUT_PNG.stat().st_size} B / {OUT_ICO.name} {OUT_ICO.stat().st_size} B")
    print(f"角上 alpha={corner[0]},{corner[1]}（都该是 0）")
    return 0 if corner == (0, 0) else 1


if __name__ == "__main__":
    sys.exit(main())
