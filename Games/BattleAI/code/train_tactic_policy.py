"""段階1: 戦術選択層の模倣学習(behavioral cloning).

tactic_log.csv の (観測18次元 → 選ばれた戦術) を教師データとして、
線形softmax(多項ロジスティック回帰)で「教師AIの判断」を再現する方策を学習する。

出力の重みは ml/bridge/tactic_policy.json に書き出し、Unity側(段階1b)が読んで
LearnedブレインとしてChampを模倣する。まずは「手書き脳を再現できるか」を精度で測るのが目的。

使い方:
    ml/.venv/bin/python ml/train_tactic_policy.py --teacher pro_optimized_v1
"""

import argparse
import json
from pathlib import Path

import numpy as np
import pandas as pd
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import accuracy_score, classification_report
from sklearn.model_selection import train_test_split

LOG_PATH = Path(__file__).parent.parent / "MetricsLogs" / "tactic_log.csv"
OUT_PATH = Path(__file__).parent / "bridge" / "tactic_policy.json"

# 観測18次元(TacticLogger.OBS_NAMES と同順)
FEATURES = [
    "dist", "ep", "since_seen", "cornered",
    "can_see", "invincible", "has_contact", "clear_shot", "assault_budget", "feint_cd", "feint_running",
    "cur_search", "cur_approach", "cur_assault", "cur_ambush", "cur_attack", "cur_feint", "cur_retreat",
]
# 戦術7種(C#側 TacticCatalog.ALL と同順)。この順序が方策の出力インデックスになる
TACTICS = ["search", "approach", "assault", "ambush", "attack", "feint", "retreat"]

SEED = 42


def main() -> None:
    parser = argparse.ArgumentParser(description="戦術選択の模倣学習(線形softmax)")
    parser.add_argument("--teacher", default="pro_optimized_v1", help="模倣する教師のai_type")
    parser.add_argument("--test-size", type=float, default=0.2, help="検証用に取り分ける割合")
    args = parser.parse_args()

    df = pd.read_csv(LOG_PATH)
    df = df[df["ai_type"] == args.teacher]
    if df.empty:
        raise SystemExit(f"教師 '{args.teacher}' の行がありません。ai_typeを確認してください")
    print(f"教師 {args.teacher}: {len(df)}行 / {df['match'].nunique()}試合")

    x = df[FEATURES].to_numpy(dtype=np.float32)
    #戦術名を固定インデックス(TACTICS順)へ。教師に出現しない戦術があっても順序を保つ
    y = df["chosen"].map({t: i for i, t in enumerate(TACTICS)}).to_numpy()

    x_train, x_test, y_train, y_test = train_test_split(
        x, y, test_size=args.test_size, random_state=SEED, stratify=y
    )

    #多項ロジスティック回帰=線形softmax。特徴は概ね[0,1]なので標準化なしでそのまま学習する
    model = LogisticRegression(max_iter=2000, C=1.0)
    model.fit(x_train, y_train)

    train_acc = accuracy_score(y_train, model.predict(x_train))
    test_acc = accuracy_score(y_test, model.predict(x_test))
    #「常に最頻の戦術を答える」だけのベースライン(これを超えて初めて意味がある)
    majority = np.bincount(y_train, minlength=len(TACTICS)).argmax()
    baseline = accuracy_score(y_test, np.full_like(y_test, majority))

    print(f"\n=== 模倣精度 ===")
    print(f"  訓練 {train_acc:.1%} / 検証 {test_acc:.1%}  (最頻ベースライン {baseline:.1%})")
    print("\n=== 戦術別(検証) ===")
    present = sorted(set(y_test))
    print(classification_report(
        y_test, model.predict(x_test),
        labels=present, target_names=[TACTICS[i] for i in present], zero_division=0, digits=2,
    ))

    #Unityが読む形へ。sklearnの2値扱いに備え、常に7クラス分の重み行列に整える
    weights, biases = expand_to_all_classes(model)
    #UnityのJsonUtilityは多次元配列を扱えないため、行優先(tactic t, feature f → t*18+f)で平坦化する
    weights_flat = [w for row in weights for w in row]
    OUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    OUT_PATH.write_text(json.dumps({
        "teacher": args.teacher,
        "features": FEATURES,
        "tactics": TACTICS,
        "weightsFlat": weights_flat,  # 長さ 7*18=126, row-major
        "biases": biases,             # 長さ 7
    }, indent=2), encoding="utf-8")
    print(f"重みを書き出し: {OUT_PATH}")


def expand_to_all_classes(a_model: LogisticRegression):
    """学習に出なかった戦術も含め、常に TACTICS 全7クラス分の (重み, バイアス) を返す.

    出現クラスが2種だけだと sklearn は係数を1行しか持たないため、
    出力インデックス=TACTICS順の7行に整える(欠けたクラスは強い負バイアスで実質選ばれない)。
    """
    n_features = len(FEATURES)
    weights = [[0.0] * n_features for _ in TACTICS]
    biases = [-1e9] * len(TACTICS)  # 未学習クラスは選ばれないように

    classes = list(a_model.classes_)
    if len(classes) == 2:
        #2クラスは coef_ が1行。クラス1が +、クラス0が - になる線形識別
        w = a_model.coef_[0].tolist()
        b = float(a_model.intercept_[0])
        c0, c1 = classes
        weights[c1], biases[c1] = w, b
        weights[c0], biases[c0] = [-v for v in w], -b
    else:
        for row, cls in enumerate(classes):
            weights[cls] = a_model.coef_[row].tolist()
            biases[cls] = float(a_model.intercept_[row])
    return weights, biases


if __name__ == "__main__":
    main()
