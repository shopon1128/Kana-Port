"""pl の顔テクスチャを numpy で生成する。

方針:
  テクスチャ上のピクセルを「そのピクセルが頭部のどのワールド座標(x, z)に当たるか」に
  変換したグリッドを先に作り、以降の描画をすべてメートル単位で行う。
  こうするとUVの縦横比や解像度が変わっても顔が歪まず、pl_spec.FACE の値が
  そのままモデル上の実寸として読める。

  頭部のUVは pl_mesh 側で「正面 ±FACE_UV_HALF_ANGLE 度に幅 FACE_UV_SPAN を割り当て、
  背面中央に継ぎ目を置く円筒展開」として張る。正面側では u がワールド x に対して
  線形になるため、ここでの逆変換も線形で済む。
"""

import math

import numpy as np

import pl_spec

# 1ピクセル程度のにじみ幅(メートル)。アンチエイリアスに使う。
AA = 0.00055


# ---------------------------------------------------------------------------
# 色ユーティリティ
# ---------------------------------------------------------------------------


def SrgbToLinear(a_color):
    """sRGB表記の色(0-1)をBlenderが扱うリニア値に変換する。"""
    out = []
    for c in a_color:
        if c <= 0.04045:
            out.append(c / 12.92)
        else:
            out.append(((c + 0.055) / 1.055) ** 2.4)
    return tuple(out)


def Mix(a_color1, a_color2, a_t):
    """2色を線形補間する。"""
    return tuple(c1 + (c2 - c1) * a_t for c1, c2 in zip(a_color1, a_color2))


def Shade(a_color, a_factor):
    """色を明暗させる。a_factor が1未満で暗く、1超で明るくなる。"""
    return tuple(min(1.0, max(0.0, c * a_factor)) for c in a_color)


# ---------------------------------------------------------------------------
# マスク生成（すべてメートル座標で計算する）
# ---------------------------------------------------------------------------


def _SmoothStep(a_edge0, a_edge1, a_value):
    """a_edge0→a_edge1 の範囲で 0→1 に滑らかに遷移させる。

    a_edge1 < a_edge0（降順）も許す。マスクの内側を1にしたい場面で
    _SmoothStep(r + にじみ, r - にじみ, 距離) と書けるようにするため、
    分母のゼロ除算対策では符号を保ったまま最小値でクランプする。
    a_edge0/a_edge1 には配列も渡せる（Stroke の可変幅で使う）。
    """
    denom = np.asarray(a_edge1, dtype=np.float32) - np.asarray(a_edge0, dtype=np.float32)
    denom = np.where(np.abs(denom) < 1e-9, 1e-9, denom)
    t = np.clip((a_value - a_edge0) / denom, 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


class FaceCanvas:
    """顔テクスチャの描画先。メートル座標で描画を受け付ける。

    色は **sRGB のまま** 保持する。bpy.data.images.new() が作るのはバイト画像で、
    image.pixels へ書いた値はカラースペース変換を経ずにそのままPNGのバイトになるため、
    ここでリニア変換してしまうと二重変換で暗く濁る。
    （リニアが要るのはマテリアルのノード入力側だけなので、そちらで SrgbToLinear を使う。）
    合成がsRGB空間で行われる点は、ペイントソフトの挙動と同じなので絵としては素直。

    a_x_per_u はテクスチャのu方向1.0あたりのワールドx幅、
    a_z_range は v=0 と v=1 に対応するワールドzの範囲。
    """

    def __init__(self, a_size, a_x_per_u, a_z_range, a_base_color):
        self._size = a_size
        self._x_per_u = a_x_per_u
        self._z_min, self._z_max = a_z_range

        # 各テクセルの中心が対応するワールド座標(x, z)のグリッドを作る。
        u = (np.arange(a_size, dtype=np.float32) + 0.5) / a_size
        v = (np.arange(a_size, dtype=np.float32) + 0.5) / a_size
        self._gx = ((u - 0.5) * a_x_per_u)[None, :].repeat(a_size, axis=0)
        self._gz = (self._z_min + v * (self._z_max - self._z_min))[:, None].repeat(
            a_size, axis=1
        )

        base = np.array(a_base_color, dtype=np.float32)
        self._rgb = np.empty((a_size, a_size, 3), dtype=np.float32)
        self._rgb[:, :] = base

    # -- 座標変換 ----------------------------------------------------------

    def _Bounds(self, a_x_min, a_x_max, a_z_min, a_z_max, a_margin):
        """描画範囲をテクセルのスライスに落とす。全画素を舐めないための最適化。"""
        size = self._size
        u0 = (a_x_min - a_margin) / self._x_per_u + 0.5
        u1 = (a_x_max + a_margin) / self._x_per_u + 0.5
        zr = self._z_max - self._z_min
        v0 = (a_z_min - a_margin - self._z_min) / zr
        v1 = (a_z_max + a_margin - self._z_min) / zr
        c0 = max(0, int(math.floor(u0 * size)) - 1)
        c1 = min(size, int(math.ceil(u1 * size)) + 1)
        r0 = max(0, int(math.floor(v0 * size)) - 1)
        r1 = min(size, int(math.ceil(v1 * size)) + 1)
        if c0 >= c1 or r0 >= r1:
            return None
        return (slice(r0, r1), slice(c0, c1))

    # -- マスク ------------------------------------------------------------

    def Ellipse(self, a_cx, a_cz, a_rx, a_rz, a_power=2.0):
        """超楕円のマスクを (スライス, 値) で返す。a_power=2 で楕円、大きいほど角丸矩形。"""
        sl = self._Bounds(a_cx - a_rx, a_cx + a_rx, a_cz - a_rz, a_cz + a_rz, AA * 3)
        if sl is None:
            return None
        dx = np.abs((self._gx[sl] - a_cx) / max(a_rx, 1e-9))
        dz = np.abs((self._gz[sl] - a_cz) / max(a_rz, 1e-9))
        d = (dx**a_power + dz**a_power) ** (1.0 / a_power)
        # にじみ幅を正規化空間に換算する。
        e = AA / max(min(a_rx, a_rz), 1e-9)
        return sl, _SmoothStep(1.0 + e, 1.0 - e, d)

    def Stroke(self, a_points, a_widths):
        """可変幅の折れ線を塗るマスクを返す。a_widths は各点での半幅。"""
        xs = [p[0] for p in a_points]
        zs = [p[1] for p in a_points]
        wmax = max(a_widths)
        sl = self._Bounds(min(xs), max(xs), min(zs), max(zs), wmax + AA * 3)
        if sl is None:
            return None
        gx = self._gx[sl]
        gz = self._gz[sl]
        acc = np.zeros(gx.shape, dtype=np.float32)
        for i in range(len(a_points) - 1):
            x0, z0 = a_points[i]
            x1, z1 = a_points[i + 1]
            ex, ez = x1 - x0, z1 - z0
            length2 = ex * ex + ez * ez
            if length2 < 1e-12:
                continue
            # 線分上の最近点パラメータ t（0-1にクランプ）
            t = np.clip(((gx - x0) * ex + (gz - z0) * ez) / length2, 0.0, 1.0)
            dx = gx - (x0 + t * ex)
            dz = gz - (z0 + t * ez)
            dist = np.sqrt(dx * dx + dz * dz)
            w = a_widths[i] + (a_widths[i + 1] - a_widths[i]) * t
            acc = np.maximum(acc, _SmoothStep(w + AA, w - AA, dist))
        return sl, acc

    # -- 合成 --------------------------------------------------------------

    def Paint(self, a_mask, a_color, a_opacity=1.0):
        """マスクの形に単色を乗せる。"""
        if a_mask is None:
            return
        sl, value = a_mask
        col = np.array(a_color, dtype=np.float32)
        alpha = (value * a_opacity)[:, :, None]
        self._rgb[sl] = self._rgb[sl] * (1.0 - alpha) + col * alpha

    def PaintGradient(self, a_mask, a_color_bottom, a_color_top, a_z0, a_z1):
        """マスクの形に、z方向のグラデーションを乗せる。瞳の色に使う。"""
        if a_mask is None:
            return
        sl, value = a_mask
        t = np.clip((self._gz[sl] - a_z0) / max(a_z1 - a_z0, 1e-9), 0.0, 1.0)[:, :, None]
        c0 = np.array(a_color_bottom, dtype=np.float32)
        c1 = np.array(a_color_top, dtype=np.float32)
        col = c0 * (1.0 - t) + c1 * t
        alpha = value[:, :, None]
        self._rgb[sl] = self._rgb[sl] * (1.0 - alpha) + col * alpha

    def Multiply(self, a_mask, a_color, a_opacity=1.0):
        """マスクの形に色を乗算する。頬の赤みや落ち影に使う。"""
        if a_mask is None:
            return
        sl, value = a_mask
        col = np.array(a_color, dtype=np.float32)
        alpha = (value * a_opacity)[:, :, None]
        self._rgb[sl] = self._rgb[sl] * (1.0 - alpha + alpha * col)

    @staticmethod
    def Intersect(a_mask1, a_mask2):
        """2つのマスクの共通部分を取る。スライス範囲は a_mask1 側に合わせる。"""
        if a_mask1 is None or a_mask2 is None:
            return None
        sl1, v1 = a_mask1
        sl2, v2 = a_mask2
        # a_mask2 を a_mask1 のスライス範囲に合わせて切り出す。
        r1, c1 = sl1
        r2, c2 = sl2
        rs = slice(max(r1.start, r2.start), min(r1.stop, r2.stop))
        cs = slice(max(c1.start, c2.start), min(c1.stop, c2.stop))
        if rs.start >= rs.stop or cs.start >= cs.stop:
            return None
        sub1 = v1[rs.start - r1.start : rs.stop - r1.start, cs.start - c1.start : cs.stop - c1.start]
        sub2 = v2[rs.start - r2.start : rs.stop - r2.start, cs.start - c2.start : cs.stop - c2.start]
        return (rs, cs), sub1 * sub2

    def ToRgba(self):
        """Blender の image.pixels に流し込める (H, W, 4) の配列を返す。"""
        rgba = np.ones((self._size, self._size, 4), dtype=np.float32)
        rgba[:, :, :3] = np.clip(self._rgb, 0.0, 1.0)
        return rgba


# ---------------------------------------------------------------------------
# 顔パーツの描画
# ---------------------------------------------------------------------------


def _EyeApertureTop(a_canvas, a_cx, a_cz, a_hw, a_hh, a_power, a_drop, a_samples=24):
    """目の開口部の上縁をなぞる点列を返す。まつ毛を縁に沿わせるために使う。"""
    pts = []
    for i in range(a_samples + 1):
        t = -1.0 + 2.0 * i / a_samples
        # 超楕円 |t|^p + |s|^p = 1 を s について解く
        s = max(0.0, 1.0 - abs(t) ** a_power) ** (1.0 / a_power)
        z = a_cz + a_hh * s - a_hh * a_drop * (1.0 - t * t)
        pts.append((a_cx + a_hw * t, z))
    return pts


def _DrawEye(a_canvas, a_side):
    """片目を描く。a_side は +1 がキャラクターの左目(画面右)。"""
    f = pl_spec.FACE
    col = pl_spec.COLOR

    cx = f["eye_x"] * a_side
    cz = f["eye_z"]
    hw = f["eye_half_w"]
    hh = f["eye_half_h"]
    power = f["eye_squareness"]
    drop = f["eye_lid_drop"]

    # 開口部。以降の瞳・ハイライトはすべてこれでクリップする。
    aperture = a_canvas.Ellipse(cx, cz, hw, hh, power)
    a_canvas.Paint(aperture, col["eye_white"])

    # --- 虹彩 ---
    ix = cx
    iz = cz + f["iris_offset_z"]
    irx = hw * f["iris_scale_w"]
    irz = hh * f["iris_scale_h"]

    rim_color = Shade(col["iris_top"], 0.55)
    iris = a_canvas.Intersect(a_canvas.Ellipse(ix, iz, irx, irz), aperture)
    a_canvas.Paint(iris, rim_color)

    inner_rx = irx * (1.0 - f["iris_rim"])
    inner_rz = irz * (1.0 - f["iris_rim"])
    inner = a_canvas.Intersect(a_canvas.Ellipse(ix, iz, inner_rx, inner_rz), aperture)
    a_canvas.PaintGradient(
        inner, col["iris_bottom"], col["iris_top"], iz - inner_rz, iz + inner_rz
    )

    # 虹彩下部の明るい抜け。アニメ的な透明感はここで出る。
    glow_rz = inner_rz * f["glow_scale"]
    glow = a_canvas.Intersect(
        a_canvas.Ellipse(ix, iz - inner_rz * 0.42, inner_rx * 0.80, glow_rz), aperture
    )
    a_canvas.Paint(glow, Mix(col["iris_bottom"], (1.0, 1.0, 1.0), 0.45), 0.75)

    # 瞳孔
    pupil = a_canvas.Intersect(
        a_canvas.Ellipse(ix, iz, irx * f["pupil_scale"], irz * f["pupil_scale"] * 1.05),
        aperture,
    )
    a_canvas.Paint(pupil, Shade(col["lash"], 0.85))

    # --- 上まぶたの落ち影 ---
    # 目尻まで届かせると、開口部が細くなる端で灰色の縦帯になって汚れる。
    # 中央寄りの幅に留め、薄くかける。
    shadow = a_canvas.Intersect(
        a_canvas.Ellipse(cx, cz + hh * 0.66, hw * 0.74, hh * 0.42), aperture
    )
    a_canvas.Multiply(shadow, (0.66, 0.62, 0.70), 0.32)

    # --- 上まつ毛 ---
    # 太さは「目頭で細く、中央〜目尻でいちばん太く、目尻の先で再び細って跳ねる」。
    # 端まで太いままだと眼が黒く囲まれてSDらしい軽さが消える。
    lash_pts = _EyeApertureTop(a_canvas, cx, cz, hw, hh, power, drop)
    th = f["lash_thickness"]
    n = len(lash_pts)
    widths = []
    for i in range(n):
        t = -1.0 + 2.0 * i / (n - 1)
        s = t * a_side  # s=+1 が目尻側
        # 目尻寄り(s=0.35)を頂点にする。細くしすぎると跳ね上げが本体から切れて
        # 「ヒゲ」に見え、太いまま端まで行くと眼が黒く囲まれて重くなる。その中間。
        spread = 1.15 if s < 0.35 else 0.80
        widths.append(th * max(0.14, 1.0 - ((s - 0.35) / spread) ** 2))

    # 目尻の跳ね上げ。上へ向けるほどアイラインらしくなる。
    tip = lash_pts[-1] if a_side > 0 else lash_pts[0]
    flick = f["lash_flick"]
    flick_pt = (tip[0] + flick * a_side * 0.70, tip[1] + flick * 0.85)
    if a_side > 0:
        lash_pts = lash_pts + [flick_pt]
        widths = widths + [th * 0.10]
    else:
        lash_pts = [flick_pt] + lash_pts
        widths = [th * 0.10] + widths
    a_canvas.Paint(a_canvas.Stroke(lash_pts, widths), col["lash"])

    # --- 下まつ毛（目尻側だけに細く入れる） ---
    # 超楕円の縁は |s|→1 で目の中央高さまで駆け上がるため、そこまで引くと
    # 目尻の外に灰色の縦棒が立つ。s を 0.72 で止めて底面沿いに留める。
    lower = []
    lw = []
    span = 0.72
    for i in range(13):
        t = i / 12.0
        s = (-span + 2.0 * span * t) * a_side
        val = max(0.0, 1.0 - abs(s) ** power) ** (1.0 / power)
        lower.append((cx + hw * s * 0.95, cz - hh * val * 0.95))
        # 目尻(t=1)側だけに出し、目頭側は消す
        lw.append(f["lash_lower"] * _SmoothStep(0.40, 0.95, t))
    a_canvas.Paint(a_canvas.Stroke(lower, lw), Mix(col["lash"], col["skin_shade"], 0.45))

    # --- ハイライト（最後に乗せる） ---
    for key, opacity in (("highlight_main", 1.0), ("highlight_sub", 0.85)):
        dx, dz, r = f[key]
        hl = a_canvas.Intersect(
            a_canvas.Ellipse(ix + irx * dx * a_side, iz + irz * dz, irx * r, irz * r),
            aperture,
        )
        a_canvas.Paint(hl, (1.0, 1.0, 1.0), opacity)


def _DrawBrow(a_canvas, a_side):
    """片方の眉を描く。"""
    f = pl_spec.FACE
    cx = f["brow_x"] * a_side
    cz = f["brow_z"]
    hw = f["brow_half_w"]
    tilt = f["brow_tilt"]
    th = f["brow_thickness"]

    pts = []
    widths = []
    n = 14
    for i in range(n + 1):
        t = -1.0 + 2.0 * i / n  # t=-1 が目頭側、t=+1 が目尻側
        x = cx + hw * t * a_side
        # 眉山を目尻寄り(t=0.3)に置いた、ゆるいアーチ
        z = cz + tilt * t - 0.006 * (t - 0.3) ** 2
        pts.append((x, z))
        # 目頭側が太く、目尻へ細って消える
        widths.append(th * max(0.10, (1.0 - 0.55 * (t + 1.0) * 0.5) * (1.0 - t**8)))
    a_canvas.Paint(a_canvas.Stroke(pts, widths), pl_spec.COLOR["brow"], 0.85)


def _DrawMouth(a_canvas):
    """小さな笑みの口を描く。"""
    f = pl_spec.FACE
    hw = f["mouth_half_w"]
    cz = f["mouth_z"]
    depth = f["mouth_depth"]
    pts = []
    widths = []
    n = 14
    for i in range(n + 1):
        t = -1.0 + 2.0 * i / n
        # 中央が下がる緩いカーブ＝閉じた微笑み
        pts.append((hw * t, cz - depth * (1.0 - t * t)))
        widths.append(f["mouth_thickness"] * (0.25 + 0.75 * (1.0 - abs(t) ** 1.6)))
    a_canvas.Paint(a_canvas.Stroke(pts, widths), pl_spec.COLOR["mouth"])


def _DrawBlush(a_canvas, a_side):
    """頬の赤みと、その上の斜線を描く。"""
    f = pl_spec.FACE
    cx = f["blush_x"] * a_side
    cz = f["blush_z"]
    rx = f["blush_half_w"]
    rz = f["blush_half_h"]

    # 境界をはっきりさせず、径の違う楕円を重ねてふんわり見せる。
    for scale, opacity in ((1.00, 0.30), (0.78, 0.28), (0.52, 0.26)):
        a_canvas.Paint(
            a_canvas.Ellipse(cx, cz, rx * scale, rz * scale),
            pl_spec.COLOR["blush"],
            f["blush_strength"] * opacity,
        )

    # 斜線。参考画像にある「頬の描き込み」。
    # 楕円からはみ出すと落書きに見えるので、頬の内側でクリップする。
    clip = a_canvas.Ellipse(cx, cz, rx * 0.72, rz * 0.68)
    count = f["blush_hatch"]
    for i in range(count):
        t = (i + 0.5) / count
        x = cx + rx * (t - 0.5) * 1.15
        a_canvas.Paint(
            a_canvas.Intersect(
                a_canvas.Stroke(
                    [(x - rx * 0.09, cz + rz * 0.95), (x + rx * 0.09, cz - rz * 0.95)],
                    [0.0013, 0.0013],
                ),
                clip,
            ),
            Shade(pl_spec.COLOR["blush"], 0.80),
            0.40,
        )


def _DrawNose(a_canvas):
    """鼻はごく淡い点だけ置く。アニメ調では主張させない。"""
    f = pl_spec.FACE
    a_canvas.Paint(
        a_canvas.Ellipse(0.0, f["nose_z"], f["nose_radius"] * 1.3, f["nose_radius"]),
        pl_spec.COLOR["skin_shade"],
        f["nose_strength"],
    )


def BuildFaceTexture(a_x_per_u, a_z_range):
    """顔テクスチャを (H, W, 4) のリニアRGBA配列として返す。

    a_x_per_u: テクスチャのu方向1.0に対応するワールドxの幅
    a_z_range: (v=0のワールドz, v=1のワールドz)
    """
    canvas = FaceCanvas(
        pl_spec.FACE_TEX_SIZE, a_x_per_u, a_z_range, pl_spec.COLOR["skin"]
    )

    # あご下や首元がのっぺりしないよう、下端にごく薄い陰を敷く。
    zmin, zmax = a_z_range
    canvas.Paint(
        canvas.Ellipse(0.0, zmin, a_x_per_u * 0.5, (zmax - zmin) * 0.10),
        pl_spec.COLOR["skin_shade"],
        0.35,
    )

    _DrawNose(canvas)
    for side in (-1, 1):
        _DrawBlush(canvas, side)
    _DrawMouth(canvas)
    for side in (-1, 1):
        _DrawBrow(canvas, side)
        _DrawEye(canvas, side)

    return canvas.ToRgba()
