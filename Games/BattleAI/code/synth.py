"""音声合成の基本部品(発振器・エンベロープ・フィルタ・エフェクト・ドラム).

曲やSEの「中身」は持たず、波形を作る処理だけを置く。
中身(譜面・音色の値)は songs.py / sfx.py 側のデータで決める。
"""

from __future__ import annotations

import numpy as np
from scipy import signal

SAMPLE_RATE: int = 44100

A4_MIDI = 69
A4_FREQ = 440.0
SEMITONES_PER_OCTAVE = 12
NOTE_OFFSETS: dict[str, int] = {"C": 0, "D": 2, "E": 4, "F": 5, "G": 7, "A": 9, "B": 11}

PEAK_DB = -1.0  # 書き出し時の最大音量(dBFS)。0dBぴったりだと変換時に割れることがあるため少し下げる
SOFT_CLIP_DRIVE = 1.2  # マスターの軽い飽和量(大きいほど音圧が上がり、歪みも増える)
MASTER_HIGHPASS_HZ = 30.0  # 再生できない超低域を削ってヘッドルームを稼ぐ


def note_to_midi(name: str) -> int:
    """'C#5' や 'Bb3' のような音名をMIDIノート番号に変換する."""
    letter = name[0].upper()
    rest = name[1:]
    accidental = 0
    while rest and rest[0] in "#b":
        accidental += 1 if rest[0] == "#" else -1
        rest = rest[1:]
    octave = int(rest)
    return (octave + 1) * SEMITONES_PER_OCTAVE + NOTE_OFFSETS[letter] + accidental


def midi_to_freq(midi: float) -> float:
    """MIDIノート番号を周波数(Hz)に変換する."""
    return A4_FREQ * 2.0 ** ((midi - A4_MIDI) / SEMITONES_PER_OCTAVE)


def db_to_gain(db: float) -> float:
    """dB値を振幅倍率に変換する."""
    return 10.0 ** (db / 20.0)


# ---------------------------------------------------------------------------
# 発振器
# ---------------------------------------------------------------------------


def _phase(freq: np.ndarray | float, n: int, phase0: float = 0.0) -> tuple[np.ndarray, np.ndarray]:
    """周波数(定数または時系列)から位相(0〜1)と1サンプルあたりの位相増分を求める."""
    freq_arr = np.broadcast_to(np.asarray(freq, dtype=np.float64), (n,))
    dt = freq_arr / SAMPLE_RATE
    phase = (phase0 + np.cumsum(dt) - dt[0]) % 1.0
    return phase, dt


def _poly_blep(t: np.ndarray, dt: np.ndarray) -> np.ndarray:
    """PolyBLEP補正量。波形の段差を滑らかにして、高音で出る耳障りな折り返しノイズを抑える."""
    out = np.zeros_like(t)
    lo = t < dt
    x = t[lo] / dt[lo]
    out[lo] = x + x - x * x - 1.0
    hi = t > 1.0 - dt
    x = (t[hi] - 1.0) / dt[hi]
    out[hi] = x * x + x + x + 1.0
    return out


def osc_pulse(freq: np.ndarray | float, n: int, duty: float = 0.5, phase0: float = 0.0) -> np.ndarray:
    """矩形波(パルス波)。dutyが小さいほど細く鼻にかかった音になる(0.125/0.25/0.5がファミコン系の定番)."""
    t, dt = _phase(freq, n, phase0)
    naive = np.where(t < duty, 1.0, -1.0)
    dc_offset = 2.0 * duty - 1.0  # dutyが0.5以外だと波形の平均が0からずれ、短いSEで「ボッ」というノイズになるため打ち消す
    return naive + _poly_blep(t, dt) - _poly_blep((t - duty) % 1.0, dt) - dc_offset


def osc_saw(freq: np.ndarray | float, n: int, phase0: float = 0.0) -> np.ndarray:
    """ノコギリ波。倍音が多く、パッド(白玉の伴奏)の素材に向く."""
    t, dt = _phase(freq, n, phase0)
    return 2.0 * t - 1.0 - _poly_blep(t, dt)


def osc_triangle(freq: np.ndarray | float, n: int, phase0: float = 0.0) -> np.ndarray:
    """三角波。倍音が少なく柔らかい(倍音が少ないので折り返し補正は省略)."""
    t, _ = _phase(freq, n, phase0)
    return 4.0 * np.abs(t - 0.5) - 1.0


def osc_sine(freq: np.ndarray | float, n: int, phase0: float = 0.0) -> np.ndarray:
    """正弦波."""
    t, _ = _phase(freq, n, phase0)
    return np.sin(2.0 * np.pi * t)


def osc_fm(
    freq: np.ndarray | float, n: int, ratio: float, index: float, index_decay: float
) -> np.ndarray:
    """2オペレータFM。ベルやエレピのような金属的な響きを作る(index_decay秒で明るさが落ちる)."""
    t_sec = np.arange(n) / SAMPLE_RATE
    mod_phase, _ = _phase(np.asarray(freq) * ratio, n)
    mod_env = index * np.exp(-t_sec / max(index_decay, 1e-4))
    car_phase, _ = _phase(freq, n)
    return np.sin(2.0 * np.pi * car_phase + mod_env * np.sin(2.0 * np.pi * mod_phase))


def osc_noise(n: int, rng: np.random.Generator) -> np.ndarray:
    """ホワイトノイズ."""
    return rng.uniform(-1.0, 1.0, n)


# ---------------------------------------------------------------------------
# エンベロープ
# ---------------------------------------------------------------------------


def adsr(n_gate: int, attack: float, decay: float, sustain: float, release: float) -> np.ndarray:
    """ADSRエンベロープ。n_gateは鍵盤を押している長さ(サンプル数)、戻り値はリリース分を含む長さ."""
    a = max(int(attack * SAMPLE_RATE), 1)
    d = max(int(decay * SAMPLE_RATE), 1)
    r = max(int(release * SAMPLE_RATE), 1)
    held = np.concatenate(
        [np.linspace(0.0, 1.0, a, endpoint=False), 1.0 - (1.0 - sustain) * (1.0 - np.exp(-5.0 * np.arange(d) / d))]
    )
    if n_gate <= len(held):
        held = held[:n_gate]
    else:
        held = np.concatenate([held, np.full(n_gate - len(held), sustain)])
    level = held[-1] if len(held) else 0.0
    tail = level * np.exp(-5.0 * np.arange(r) / r)
    return np.concatenate([held, tail])


def exp_decay(n: int, tau: float, attack: float = 0.001) -> np.ndarray:
    """立ち上がり後に指数で減衰するエンベロープ(打楽器・SE向け)."""
    t = np.arange(n) / SAMPLE_RATE
    env = np.exp(-t / max(tau, 1e-4))
    a = max(int(attack * SAMPLE_RATE), 1)
    env[:a] *= np.linspace(0.0, 1.0, a)
    return env


# ---------------------------------------------------------------------------
# フィルタ
# ---------------------------------------------------------------------------


def _clamp_cutoff(hz: float) -> float:
    return float(np.clip(hz, 20.0, SAMPLE_RATE * 0.45))


def lowpass(x: np.ndarray, cutoff: float, order: int = 2) -> np.ndarray:
    """ローパス(高域を削って丸い音にする)."""
    sos = signal.butter(order, _clamp_cutoff(cutoff), "lowpass", fs=SAMPLE_RATE, output="sos")
    return signal.sosfilt(sos, x, axis=0)


def highpass(x: np.ndarray, cutoff: float, order: int = 2) -> np.ndarray:
    """ハイパス(低域を削って軽い音にする)."""
    sos = signal.butter(order, _clamp_cutoff(cutoff), "highpass", fs=SAMPLE_RATE, output="sos")
    return signal.sosfilt(sos, x, axis=0)


def bandpass(x: np.ndarray, low: float, high: float, order: int = 2) -> np.ndarray:
    """バンドパス(指定帯域だけ残す)."""
    sos = signal.butter(order, [_clamp_cutoff(low), _clamp_cutoff(high)], "bandpass", fs=SAMPLE_RATE, output="sos")
    return signal.sosfilt(sos, x, axis=0)


def sweep_lowpass(x: np.ndarray, start_hz: float, end_hz: float, block: int = 256) -> np.ndarray:
    """カットオフを時間変化させるローパス。ブロックごとに係数を更新し、状態を引き継いで繋ぎ目を消す."""
    out = np.empty_like(x)
    n_blocks = (len(x) + block - 1) // block
    zi = None
    for i in range(n_blocks):
        rate = i / max(n_blocks - 1, 1)
        cutoff = start_hz * (end_hz / start_hz) ** rate  # 耳の感覚に合わせて指数で動かす
        sos = signal.butter(2, _clamp_cutoff(cutoff), "lowpass", fs=SAMPLE_RATE, output="sos")
        if zi is None:
            zi = np.zeros((sos.shape[0], 2))
        seg = slice(i * block, (i + 1) * block)
        out[seg], zi = signal.sosfilt(sos, x[seg], zi=zi)
    return out


def bitcrush(x: np.ndarray, hold: int) -> np.ndarray:
    """サンプルを間引いてザラついたレトロ感を出す(holdサンプルごとに値を保持)."""
    if hold <= 1:
        return x
    idx = (np.arange(len(x)) // hold) * hold
    return x[idx]


# ---------------------------------------------------------------------------
# エフェクト(ステレオ: shape=(n, 2))
# ---------------------------------------------------------------------------


def pan(x: np.ndarray, position: float) -> np.ndarray:
    """モノラルをステレオに定位させる(-1=左, 0=中央, 1=右)。等パワーで音量感を保つ."""
    angle = (position + 1.0) * np.pi / 4.0
    return np.stack([x * np.cos(angle), x * np.sin(angle)], axis=1) * np.sqrt(2.0)


def ping_pong_delay(x: np.ndarray, delay_sec: float, feedback: float, repeats: int = 6) -> np.ndarray:
    """左右交互に跳ね返るディレイのウェット成分だけを返す(アルペジオに広がりを出す)."""
    d = int(delay_sec * SAMPLE_RATE)
    mono = x.mean(axis=1)
    wet = np.zeros_like(x)
    for k in range(1, repeats + 1):
        shift = d * k
        if shift >= len(mono):
            break
        ch = (k - 1) % 2  # 1回目は左、2回目は右…と交互に置く
        wet[shift:, ch] += mono[:-shift] * feedback ** (k - 1)
    return lowpass(wet, 5000.0)  # 反射音は少し暗くして原音より奥に聞かせる


def make_reverb_ir(length_sec: float, decay_sec: float, rng: np.random.Generator) -> np.ndarray:
    """指数減衰ノイズによる簡易リバーブのインパルス応答(左右で別ノイズにして広がりを出す)."""
    n = int(length_sec * SAMPLE_RATE)
    t = np.arange(n) / SAMPLE_RATE
    env = np.exp(-t * 6.9 / decay_sec)  # decay_sec秒で約-60dB
    ir = rng.standard_normal((n, 2)) * env[:, None]
    ir = lowpass(ir, 6000.0)
    return ir / np.sqrt(np.sum(ir**2, axis=0, keepdims=True))


def reverb(x: np.ndarray, ir: np.ndarray) -> np.ndarray:
    """リバーブのウェット成分だけを返す(長さはx+irぶんに伸びる)."""
    return np.stack([signal.fftconvolve(x[:, ch], ir[:, ch]) for ch in range(2)], axis=1)


def master(x: np.ndarray, peak_db: float = PEAK_DB, ref_peak: float | None = None) -> np.ndarray:
    """マスター処理: 超低域カット → 軽い飽和 → ピーク正規化.

    ref_peakを渡すと、その値を最大音量とみなして正規化する(複数ファイルの音量を揃えたいとき用)。
    """
    x = highpass(x, MASTER_HIGHPASS_HZ)
    peak = ref_peak if ref_peak is not None else float(np.max(np.abs(x)))
    if peak <= 0.0:
        return x
    x = np.tanh(SOFT_CLIP_DRIVE * x / peak) / np.tanh(SOFT_CLIP_DRIVE)
    return x * db_to_gain(peak_db)


# ---------------------------------------------------------------------------
# ドラム(1打ぶんの波形を返す。曲中では使い回す)
# ---------------------------------------------------------------------------


def drum_kick(rng: np.random.Generator) -> np.ndarray:
    """キック: 高い音程から一気に下がるサイン波 + アタックのクリック."""
    n = int(0.35 * SAMPLE_RATE)
    t = np.arange(n) / SAMPLE_RATE
    freq = 48.0 + 130.0 * np.exp(-t / 0.028)
    body = osc_sine(freq, n) * exp_decay(n, 0.17)
    click = highpass(osc_noise(n, rng), 2000.0) * exp_decay(n, 0.003)
    return np.tanh(1.5 * (body + 0.35 * click))


def drum_snare(rng: np.random.Generator) -> np.ndarray:
    """スネア: 胴鳴りのトーン + 帯域を絞ったノイズ(響き線)."""
    n = int(0.25 * SAMPLE_RATE)
    t = np.arange(n) / SAMPLE_RATE
    tone = osc_triangle(185.0 + 60.0 * np.exp(-t / 0.01), n) * exp_decay(n, 0.045)
    noise = bandpass(osc_noise(n, rng), 1200.0, 8000.0) * exp_decay(n, 0.09)
    return 0.55 * tone + 1.1 * noise


def drum_hat(rng: np.random.Generator, open_hat: bool = False) -> np.ndarray:
    """ハイハット: 高域ノイズ。open_hatで余韻を伸ばす."""
    length, tau = (0.3, 0.12) if open_hat else (0.07, 0.018)
    n = int(length * SAMPLE_RATE)
    return highpass(osc_noise(n, rng), 7000.0, order=4) * exp_decay(n, tau)


def drum_crash(rng: np.random.Generator) -> np.ndarray:
    """クラッシュシンバル: 長く伸びる高域ノイズ(セクションの頭の合図)."""
    n = int(1.8 * SAMPLE_RATE)
    return highpass(osc_noise(n, rng), 4000.0, order=2) * exp_decay(n, 0.55)


def drum_tom(rng: np.random.Generator) -> np.ndarray:
    """タム: キックより高く短い胴鳴り(フィルイン用)."""
    n = int(0.25 * SAMPLE_RATE)
    t = np.arange(n) / SAMPLE_RATE
    return osc_sine(110.0 + 80.0 * np.exp(-t / 0.04), n) * exp_decay(n, 0.12)


def riser(n: int, rng: np.random.Generator, low_hz: float = 300.0, high_hz: float = 9000.0) -> np.ndarray:
    """ノイズが上昇していく「シュワー」という盛り上げ音(イントロ→本編の橋渡し)."""
    noise = osc_noise(n, rng)
    swept = sweep_lowpass(noise, low_hz, high_hz)
    env = np.linspace(0.0, 1.0, n) ** 2
    return swept * env
