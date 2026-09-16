"""戦術選択方策をCMA-ESで進化させ、Champを超える立ち回りを探す.

模倣学習で得た重み(=Champ級)を起点(ウォームスタート)に、
方策の重みベクトル(126+7=133次元)をCMA-ESで最適化する。
適応度=その重みでLearned脳が自動対戦したときの勝率。

仕組み(既存のfile-bridgeを流用):
    1. 候補の重みを ml/bridge/tactic_policy.json に書く(Learned脳が毎試合読む)
    2. params.json を空valuesで書いて試行を起動(パラメータは変えず試合だけ回す)
    3. Unityが自動対戦N試合 → result.json に勝率
    4. CMA-ESへ -勝率 を返す(cmaは最小化なので符号反転)… 繰り返し

事前準備(Unity):
    - OptionSetting: Enemy Ai Type = Learned, Optimize On Play = ON, Auto Battle Time Scale = 8
    - Play(「OPT WAITING」表示)

使い方:
    ml/.venv/bin/python ml/evolve_policy.py --generations 30 --matches 15
"""

import argparse
import json
import os
import pickle
from pathlib import Path

import cma
import numpy as np

from optimize import PARAMS_PATH, RESULT_PATH, wait_for_result, write_params

POLICY_PATH = Path(__file__).parent / "bridge" / "tactic_policy.json"
BEST_PATH = Path(__file__).parent / "tactic_policy_best.json"
#OOMでUnityが落ちても続きから再開できるよう、CMA-ESの状態を毎世代ここへ保存する
CHECKPOINT_PATH = Path(__file__).parent / "cma_checkpoint.pkl"
#全評価の履歴(追記式)。最後に上位を複数追試して真のベストを選ぶ(勝者の呪い対策)
HISTORY_PATH = Path(__file__).parent / "evolve_history.jsonl"

N_FEATURES = 18
N_TACTICS = 7
N_WEIGHTS = N_FEATURES * N_TACTICS  # 126
DIM = N_WEIGHTS + N_TACTICS  # 133(重み126 + バイアス7)

# 進化評価の試行ID帯(Optuna=0〜, 追試=20000〜, A/B=30000〜 と衝突しない)
EVOLVE_TRIAL_BASE = 50000


def load_base() -> tuple[np.ndarray, dict]:
    """模倣学習の方策を起点ベクトルとして読み込む(メタ情報も保持)."""
    meta = json.loads(POLICY_PATH.read_text(encoding="utf-8"))
    x0 = np.array(list(meta["weightsFlat"]) + list(meta["biases"]), dtype=np.float64)
    if x0.size != DIM:
        raise SystemExit(f"次元不一致: {x0.size} != {DIM}。tactic_policy.jsonを確認")
    return x0, meta


def write_policy(a_vec: np.ndarray, a_meta: dict) -> None:
    """重みベクトルを Unity(Learned脳)が読む形式で書き出す(アトミック)."""
    payload = dict(a_meta)
    payload["weightsFlat"] = a_vec[:N_WEIGHTS].tolist()
    payload["biases"] = a_vec[N_WEIGHTS:].tolist()
    tmp = POLICY_PATH.with_suffix(".tmp")
    tmp.write_text(json.dumps(payload), encoding="utf-8")
    os.replace(tmp, POLICY_PATH)


def evaluate(a_vec: np.ndarray, a_meta: dict, a_trial: int, a_matches: int, a_timeout: float) -> float:
    """候補の重みで自動対戦し、敵(Learned脳)の勝率を返す."""
    write_policy(a_vec, a_meta)
    #空valuesの試行=パラメータは変えず試合だけ回す(方策はtactic_policy.jsonで効く)
    write_params(a_trial, {}, a_matches)
    result = wait_for_result(a_trial, a_timeout)
    return float(result["enemy_win_rate"])


def save_best(a_vec: np.ndarray, a_meta: dict, a_win_rate: float) -> None:
    BEST_PATH.write_text(json.dumps({**a_meta,
        "weightsFlat": a_vec[:N_WEIGHTS].tolist(),
        "biases": a_vec[N_WEIGHTS:].tolist(),
        "win_rate": a_win_rate}), encoding="utf-8")


def save_checkpoint(a_state: dict) -> None:
    tmp = CHECKPOINT_PATH.with_suffix(".tmp")
    with open(tmp, "wb") as f:
        pickle.dump(a_state, f)
    os.replace(tmp, CHECKPOINT_PATH)


def validate_best(a_matches: int, a_timeout: float) -> None:
    """保存済みのベスト方策を多試合で追試する(n=15の上振れを見抜くため)."""
    if not BEST_PATH.exists():
        raise SystemExit("tactic_policy_best.json がありません。先に進化を実行してください")
    meta = json.loads(BEST_PATH.read_text(encoding="utf-8"))
    vec = np.array(list(meta["weightsFlat"]) + list(meta["biases"]), dtype=np.float64)
    RESULT_PATH.unlink(missing_ok=True)
    print(f"ベスト方策(探索時 {meta.get('win_rate', 0):.0%})を{a_matches}試合で追試中...")
    wr = evaluate(vec, meta, EVOLVE_TRIAL_BASE + 900000, a_matches, a_timeout)
    print(f"追試勝率: {wr:.0%}  (Champ通算 約72% が基準)")


def revalidate_top(a_top: int, a_matches: int, a_timeout: float) -> None:
    """履歴の上位K候補を多試合で追試し直し、真のベストを選ぶ(選抜と評価の分離)."""
    if not HISTORY_PATH.exists():
        raise SystemExit("evolve_history.jsonl がありません。進化を実行してから使ってください")
    meta = json.loads(BEST_PATH.read_text(encoding="utf-8"))  # features/tactics/teacherを流用
    records = [json.loads(line) for line in HISTORY_PATH.read_text(encoding="utf-8").splitlines() if line.strip()]
    #探索時の勝率が高い順に上位K件(重複重みは除く)
    seen, candidates = set(), []
    for rec in sorted(records, key=lambda r: r["win_rate"], reverse=True):
        key = round(sum(rec["weightsFlat"][:5]), 4)  # 簡易な重複判定
        if key in seen:
            continue
        seen.add(key)
        candidates.append(rec)
        if len(candidates) >= a_top:
            break

    RESULT_PATH.unlink(missing_ok=True)
    results = []
    for i, rec in enumerate(candidates):
        vec = np.array(rec["weightsFlat"] + rec["biases"], dtype=np.float64)
        wr = evaluate(vec, meta, EVOLVE_TRIAL_BASE + 800000 + i, a_matches, a_timeout)
        results.append((rec["trial"], rec["win_rate"], wr, vec))
        print(f"  trial#{rec['trial']} 探索時 {rec['win_rate']:.0%} → 追試 {wr:.0%}")

    results.sort(key=lambda r: r[2], reverse=True)
    print("\n=== 追試ランキング(この数字で選ぶ) ===")
    for trial, screened, validated, _ in results:
        print(f"  trial#{trial}  探索時 {screened:.0%} → 追試 {validated:.0%}")
    #追試1位を最終ベストとして保存し直す
    best = results[0]
    save_best(best[3], meta, best[2])
    print(f"\n追試1位(追試{best[2]:.0%})を {BEST_PATH} に保存しました")


FINAL_PATH = Path(__file__).parent / "final_validation.json"


def final_validate(a_trials: list[int], a_matches: int, a_sets: int, a_timeout: float) -> None:
    """指定した候補を「1セットa_matches試合 × a_setsセット」でがっつり追試する.

    セットごとに結果を保存し、再実行で未完了セットの続きから再開する(OOMを跨げる)。
    """
    meta = json.loads(BEST_PATH.read_text(encoding="utf-8"))  # features/tactics/teacherを流用
    history = {r["trial"]: r for r in
               (json.loads(l) for l in HISTORY_PATH.read_text(encoding="utf-8").splitlines() if l.strip())}
    for t in a_trials:
        if t not in history:
            raise SystemExit(f"trial#{t} が履歴に見つかりません。番号を確認してください")

    #これまでの進捗(候補ごとのセット別勝利数)。再開時はここから続ける
    state = json.loads(FINAL_PATH.read_text(encoding="utf-8")) if FINAL_PATH.exists() else {}

    for t in a_trials:
        key = str(t)
        state.setdefault(key, {"sets": []})  # sets: [[wins, matches], ...]
        vec = np.array(history[t]["weightsFlat"] + history[t]["biases"], dtype=np.float64)
        while len(state[key]["sets"]) < a_sets:
            set_no = len(state[key]["sets"]) + 1
            RESULT_PATH.unlink(missing_ok=True)
            write_policy(vec, meta)
            trial_id = EVOLVE_TRIAL_BASE + 700000 + t % 1000 * 10 + set_no
            write_params(trial_id, {}, a_matches)
            result = wait_for_result(trial_id, a_timeout)
            wins = int(result["enemy_wins"])
            state[key]["sets"].append([wins, int(result["matches"])])
            FINAL_PATH.write_text(json.dumps(state, indent=2), encoding="utf-8")
            print(f"trial#{t} セット{set_no}/{a_sets}: {wins}/{result['matches']} ({wins / result['matches']:.0%})")

    print("\n=== 最終追試ランキング(累計) ===")
    ranked = []
    for t in a_trials:
        sets = state[str(t)]["sets"]
        wins = sum(s[0] for s in sets)
        total = sum(s[1] for s in sets)
        ci = 1.96 * (wins / total * (1 - wins / total) / total) ** 0.5 if total else 0
        ranked.append((t, wins, total, ci))
    for t, wins, total, ci in sorted(ranked, key=lambda r: r[1] / r[2], reverse=True):
        sets_str = " ".join(f"{s[0]}/{s[1]}" for s in state[str(t)]["sets"])
        print(f"  trial#{t}: 累計 {wins}/{total} ({wins / total:.0%} ±{ci:.0%})   セット[{sets_str}]")
    print("\n基準: Champ通算 約72%。有意に上回る候補があればv4昇格の検討へ")


def main() -> None:
    parser = argparse.ArgumentParser(description="戦術選択方策のCMA-ES進化")
    parser.add_argument("--generations", type=int, default=30, help="世代数")
    parser.add_argument("--matches", type=int, default=15, help="1評価あたりの試合数")
    parser.add_argument("--sigma", type=float, default=0.5, help="初期ステップ幅")
    parser.add_argument("--popsize", type=int, default=0, help="集団サイズ(0=CMA-ES既定)")
    parser.add_argument("--timeout", type=float, default=1800.0, help="1評価の結果待ち上限秒")
    parser.add_argument("--validate-best", action="store_true", help="保存済みベストを多試合で追試して終了")
    parser.add_argument("--revalidate-top", type=int, default=0, help="履歴の上位K候補を多試合で追試して終了")
    parser.add_argument("--final-trials", type=int, nargs="+", help="指定候補をセット分割でがっつり追試(再開可)")
    parser.add_argument("--sets", type=int, default=4, help="--final-trialsの1候補あたりのセット数")
    parser.add_argument("--fresh", action="store_true", help="チェックポイントを無視して最初からやり直す")
    args = parser.parse_args()

    if args.validate_best:
        validate_best(args.matches, args.timeout)
        return
    if args.revalidate_top > 0:
        revalidate_top(args.revalidate_top, args.matches, args.timeout)
        return
    if args.final_trials:
        final_validate(args.final_trials, args.matches, args.sets, args.timeout)
        return

    #再開: チェックポイントがあれば探索の続きから(OOMでUnityが落ちても軌跡を失わない)
    if CHECKPOINT_PATH.exists() and not args.fresh:
        state = pickle.loads(CHECKPOINT_PATH.read_bytes())
        es, meta = state["es"], state["meta"]
        base_wr, best_wr, best_vec = state["base_wr"], state["best_wr"], state["best_vec"]
        trial, done_gen = state["trial"], state["generation"]
        print(f"チェックポイントから再開: {done_gen}世代完了済み / 通算ベスト {best_wr:.0%}(起点 {base_wr:.0%})")
    else:
        x0, meta = load_base()
        RESULT_PATH.unlink(missing_ok=True)
        trial = EVOLVE_TRIAL_BASE
        base_wr = evaluate(x0, meta, trial, args.matches, args.timeout)
        trial += 1
        print(f"起点(模倣)勝率: {base_wr:.0%}\n")
        opts = {"seed": 42}
        if args.popsize > 0:
            opts["popsize"] = args.popsize
        es = cma.CMAEvolutionStrategy(x0.tolist(), args.sigma, opts)
        best_wr, best_vec, done_gen = base_wr, x0.copy(), 0
        save_best(best_vec, meta, best_wr)

    #この呼び出しで進める世代数ぶんだけ回す(チャンク実行: Unity再起動でメモリをクリアしつつ再開)
    target_gen = done_gen + args.generations
    generation = done_gen
    while generation < target_gen and not es.stop():
        generation += 1
        solutions = es.ask()
        win_rates = []
        for vec in solutions:
            vec = np.asarray(vec)
            wr = evaluate(vec, meta, trial, args.matches, args.timeout)
            #全候補を履歴に追記(後で上位を複数追試するため。追記式なのでチャンクを跨いでも残る)
            with open(HISTORY_PATH, "a", encoding="utf-8") as f:
                f.write(json.dumps({"trial": trial, "generation": generation, "win_rate": wr,
                                    "weightsFlat": vec[:N_WEIGHTS].tolist(),
                                    "biases": vec[N_WEIGHTS:].tolist()}) + "\n")
            trial += 1
            win_rates.append(wr)
            if wr > best_wr:
                best_wr, best_vec = wr, vec.copy()
                save_best(best_vec, meta, best_wr)
        es.tell(solutions, [-wr for wr in win_rates])  # cmaは最小化なので符号反転
        save_checkpoint({"es": es, "meta": meta, "base_wr": base_wr, "best_wr": best_wr,
                         "best_vec": best_vec, "trial": trial, "generation": generation})
        print(f"世代{generation:2d}: この世代の最高 {max(win_rates):.0%} / 通算ベスト {best_wr:.0%} (起点 {base_wr:.0%})")

    print(f"\n=== ここまでの結果 ===")
    print(f"起点(模倣) {base_wr:.0%} → 進化ベスト {best_wr:.0%}(探索時の数字)")
    print(f"ベスト重み: {BEST_PATH} / 再開情報: {CHECKPOINT_PATH}")
    print("次: さらに回すなら同じコマンド(自動で続きから)。採用前に --validate-best で追試すること")


if __name__ == "__main__":
    main()
