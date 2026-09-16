"""ProAIパラメータのOptuna最適化ドライバ.

Unity側(OptimizationRunner)とファイルで連携する:
    1. ml/bridge/params.json にパラメータ一式と試行番号を書く
    2. Unityが自動対戦を回し、ml/bridge/result.json に勝率を書くのを待つ
    3. 勝率をOptunaに報告し、次の提案を受け取る … 繰り返し

使い方:
    1. UnityでOptimize On PlayをONにしてPlay(「OPT WAITING」表示になる)
    2. ml/.venv/bin/python ml/optimize.py --trials 3 --matches 5   # まず小さく
    3. 本番は --trials 100 --matches 15 など

学習履歴は ml/study.db に保存され、中断しても同じコマンドで続きから再開できる。
"""

import argparse
import json
import os
import time
from pathlib import Path

import optuna

BRIDGE_DIR = Path(__file__).parent / "bridge"
PARAMS_PATH = BRIDGE_DIR / "params.json"
RESULT_PATH = BRIDGE_DIR / "result.json"
STORAGE_URL = f"sqlite:///{Path(__file__).parent / 'study.db'}"

POLL_INTERVAL_SEC = 1.0  # result.jsonを確認する間隔
SAMPLER_SEED = 42  # 提案の再現性のためシードを固定

# === v3探索空間(3層方式) ===
# 層1・公平性の固定: SHOT_INTERVAL / DECISION_TICK_INTERVAL / BULLET_REACTION_TIME は
#   理不尽な強さに直結するため探索しない(値はプレハブ参照アセット=v1を引き継ぐ)。
# 層2・製品判断の固定: 見せ場を事後の上書きではなく事前の制約にする。
#   固定値と共適応した脇役パラメータを探させるのがv3の狙い。
FIXED_PARAMS: dict[str, float] = {
    "FEINT_CHANCE": 0.5,  # フェイントは見せ場。2回の最適化で2回削られた=放置すれば必ず削られる
    "AIM_LEAD_RATE": 0.7,  # 重要度が2回とも最下位圏。対人間では現行値に実績あり
}

# 層3・探索対象(12次元 = 固定2つを外し、新規2つと入れ替え)。形式: (フィールド名, 下限, 上限, 整数かどうか)
SEARCH_SPACE: list[tuple[str, float, float, bool]] = [
    ("ATTACK_RANGE", 100.0, 300.0, False),
    ("TACTIC_KEEP_BONUS", 0.0, 40.0, False),  # v1は25、v2研究は低値 — 係争中の次元
    ("PURSUIT_LEAD_TIME", 0.0, 1.5, False),
    ("STRAFE_DASH_CHANCE", 0.0, 1.0, False),  # 同じく係争中(0.8 vs 0.45)
    ("FLANK_CHANCE", 0.0, 1.0, False),
    ("SEEN_CELL_COST", 0, 8, True),
    ("DANGER_CELL_COST", 6, 15, True),  # 2環境で一貫して高値だったため低域を切り捨て
    ("ASSAULT_SHOT_BUDGET", 2, 5, True),  # 見せ場の床: 6以上は突撃が消滅するため範囲外
    ("RECOVER_EP_RATE", 0.2, 1.0, False),
    ("AMBUSH_PATIENCE", 2.0, 12.0, False),
    ("STRAFE_SWITCH_INTERVAL", 0.4, 1.2, False),  # v3新規: 攻撃中のリズム(不規則さのもう半分)
    ("DODGE_STRENGTH", 0.3, 1.0, False),  # v3新規: 弾回避の逸れ幅(反応時間0.3秒は公平性の枠のまま)
]


def write_params(
    trial_number: int, params: dict[str, float], matches: int, ai_type: str | None = None
) -> None:
    """パラメータをUnityが読む形式でbridgeフォルダへ書き出す.

    書きかけのファイルをUnityが読まないよう、一時ファイルに書いてから置き換える。

    ai_type を渡すと、その試行だけ敵AIを切り替える(A/B対戦で方策型とパラメータ型を
    交互に戦わせるため)。省略時はUnity側のOptionSettingの設定が使われる。
    """
    payload = {
        "trial": trial_number,
        "matches": matches,
        "values": [{"name": name, "value": float(value)} for name, value in params.items()],
        "ai_type": ai_type or "",
    }
    tmp_path = PARAMS_PATH.with_suffix(".tmp")
    tmp_path.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    os.replace(tmp_path, PARAMS_PATH)


def wait_for_result(trial_number: int, timeout_sec: float) -> dict:
    """Unityが該当試行のresult.jsonを書くまで待って、その内容を返す."""
    start = time.time()
    while True:
        if time.time() - start > timeout_sec:
            raise TimeoutError(
                f"試行#{trial_number}の結果が{timeout_sec:.0f}秒待っても届きません。"
                "UnityがPlay中でOptimize On PlayがONか確認してください"
            )
        if RESULT_PATH.exists():
            try:
                result = json.loads(RESULT_PATH.read_text(encoding="utf-8"))
            except json.JSONDecodeError:
                result = None  # Unityが書き込み中。次の確認まで待つ
            if result is not None and result.get("trial") == trial_number:
                return result
        time.sleep(POLL_INTERVAL_SEC)


def make_objective(matches: int, timeout_sec: float):
    """試合数などの設定を焼き込んだ目的関数を作る."""

    def objective(trial: optuna.Trial) -> float:
        #固定パラメータも毎回明示的に送る(プレハブ参照アセットが変わっても前提が崩れないように)
        params: dict[str, float] = dict(FIXED_PARAMS)
        for name, low, high, is_int in SEARCH_SPACE:
            if is_int:
                params[name] = trial.suggest_int(name, int(low), int(high))
            else:
                params[name] = trial.suggest_float(name, low, high)

        write_params(trial.number, params, matches)
        print(f"[trial #{trial.number}] パラメータを送信、{matches}試合の結果を待機中...")
        result = wait_for_result(trial.number, timeout_sec)

        win_rate = float(result["enemy_win_rate"])
        draws = int(result.get("draws", 0))
        note = f"、引き分け{draws}" if draws > 0 else ""
        print(f"[trial #{trial.number}] 敵の勝率 {win_rate:.0%} ({result['enemy_wins']}/{result['matches']}{note})")
        return win_rate

    return objective


def main() -> None:
    parser = argparse.ArgumentParser(description="ProAIパラメータのOptuna最適化")
    parser.add_argument("--trials", type=int, default=3, help="試行回数(まず3で動作確認)")
    parser.add_argument("--matches", type=int, default=25, help="1試行あたりの試合数(v3から25: TPEに渡すスコアのノイズを減らす)")
    parser.add_argument("--timeout", type=float, default=1200.0, help="1試行の結果を待つ上限秒数")
    args = parser.parse_args()

    BRIDGE_DIR.mkdir(parents=True, exist_ok=True)
    # 前回の残骸をUnityが誤って処理しないよう消しておく
    RESULT_PATH.unlink(missing_ok=True)
    PARAMS_PATH.unlink(missing_ok=True)

    study = optuna.create_study(
        study_name="proai_params",
        storage=STORAGE_URL,
        direction="maximize",  # 敵(ProAI)の勝率を最大化する
        sampler=optuna.samplers.TPESampler(seed=SAMPLER_SEED),
        load_if_exists=True,  # 中断しても続きから再開できる
    )
    done = len(study.trials)
    if done > 0:
        print(f"既存のスタディを再開します(完了済み: {done}試行)")

    study.optimize(make_objective(args.matches, args.timeout), n_trials=args.trials)

    print("\n=== ここまでのベスト ===")
    print(f"敵の勝率: {study.best_value:.0%}")
    for name, value in study.best_params.items():
        print(f"  {name} = {value:.3f}" if isinstance(value, float) else f"  {name} = {value}")


if __name__ == "__main__":
    main()
