/// <summary>
/// AI(EnemyAIPro)の戦術。並び順は学習方策・戦術ログ・メトリクスCSVの列順と一致しているので、
/// 並べ替えや途中への挿入をしてはいけない(学習済みの重みとCSVの列がずれる)
/// </summary>
public enum Tactic
{
    Search,   //索敵: プレイヤーの位置が分からないので歩き回る
    Approach, //接近: 視界内のプレイヤーへ歩いて近づく(EP温存)
    Assault,  //強襲: EPに余裕がある時、ダッシュで一気に攻撃位置へ詰める
    Ambush,   //隠密接近: 見つかっていない間、遮蔽の陰を通って忍び寄る(残像が出るダッシュは使わない)
    Attack,   //攻撃: 射撃しつつ不規則ストレイフ
    Feint,    //フェイント: 視界外で囮ダッシュの残像だけ見せ、別方向へ歩いて撹乱する
    Retreat,  //離脱: 視界を切ってEP回復を図る
}

/// <summary>
/// 戦術の一覧と名前の単一情報源。
/// Labelは記録用の安定キーで、DisplayNameは画面表示用(ここだけ日本語にする)
/// </summary>
public static class TacticCatalog
{
    private const string UNKNOWN_LABEL = "-";

    private static readonly (Tactic Tactic, string Label, string DisplayName)[] ENTRIES =
    {
        (Tactic.Search,   "SEARCH",   "索敵"),
        (Tactic.Approach, "APPROACH", "接近"),
        (Tactic.Assault,  "ASSAULT",  "強襲"),
        (Tactic.Ambush,   "AMBUSH",   "隠密接近"),
        (Tactic.Attack,   "ATTACK",   "攻撃"),
        (Tactic.Feint,    "FEINT",    "フェイント"),
        (Tactic.Retreat,  "RETREAT",  "離脱"),
    };

    /// <summary>全戦術(enumの並び順)。添字は学習方策のスコアや戦術ログの列と対応する</summary>
    public static readonly Tactic[] ALL = BuildAll();

    /// <summary>戦術の数(学習方策の出力次元・戦術ログの列数)</summary>
    public static int Count => ENTRIES.Length;

    /// <summary>戦術の添字(ALLの中での位置)</summary>
    public static int IndexOf(Tactic a_tactic) => System.Array.IndexOf(ALL, a_tactic);

    /// <summary>記録用ラベル(英字の安定キー)</summary>
    public static string Label(Tactic a_tactic)
    {
        foreach ((Tactic tactic, string label, string _) in ENTRIES)
        {
            if (tactic == a_tactic)
            {
                return label;
            }
        }
        return UNKNOWN_LABEL;
    }

    /// <summary>
    /// 記録用ラベルを画面表示名に変換する。
    /// メトリクスは集計をラベルで持っているので、表示の直前にここで日本語へ直す(未知のラベルはそのまま返す)
    /// </summary>
    public static string DisplayName(string a_label)
    {
        foreach ((Tactic _, string label, string displayName) in ENTRIES)
        {
            if (label == a_label)
            {
                return displayName;
            }
        }
        return a_label;
    }

    private static Tactic[] BuildAll()
    {
        var all = new Tactic[ENTRIES.Length];
        for (int i = 0; i < ENTRIES.Length; i++)
        {
            all[i] = ENTRIES[i].Tactic;
        }
        return all;
    }
}
