using UnityEngine;
using System;
/// <summary>
/// Mainシーン→Resultシーンへ結果データを引き継ぐためのScriptableObject。
/// </summary>
[CreateAssetMenu(fileName = "ResultData", menuName = "Scriptable Objects/ResultData")]
public class ResultData : ScriptableObject
{
    [Header("勝敗（1 or 2）")]
    public int WhoWin;

    [Header("プレイヤーごとのスコア詳細")]
    public PlayerScoreData Pl1Score = new PlayerScoreData();
    public PlayerScoreData Pl2Score = new PlayerScoreData();

    /// <summary>
    /// 両プレイヤーのスコアを初期化
    /// </summary>
    public void ScoreReset()
    {
        Pl1Score.ScoreReset();
        Pl2Score.ScoreReset();
    }

    /// <summary>
    /// キル数のカウントを加算
    /// 引数: who -> 撃破したプレイヤー(1 or 2) , lv -> 撃破されたウィルスのレベル(1 ~ 5)
    /// </summary>
    public void AddKillCount(int who,int lv)
    {
        switch (who)
        {
            case 1:
                Pl1Score.AddKillCount(lv);
                break;
            case 2:
                Pl2Score.AddKillCount(lv);
                break;
            default:
                Debug.LogWarning("AddKillCount()の引数:whoが不正");
                break;
        }
    }

    /// <summary>
    /// 最大コンボ数の更新
    /// 引数: who -> プレイヤー(1 or 2) , combo -> コンボ数
    /// </summary>
    public void UpdateMaxCombo(int who,int combo)
    {
        switch (who)
        {
            case 1:
                Pl1Score.UpdateMaxConbo(combo);
                break;
            case 2:
                Pl2Score.UpdateMaxConbo(combo);
                break;
            default:
                Debug.LogWarning("UpdateMaxCombo()の引数:whoが不正");
                break;
        }
    }
}

/// <summary>
/// プレイヤー1人分のスコア詳細。
/// </summary>
[System.Serializable]
public class PlayerScoreData
{
    public int MaxCombo;
    public int[] KillCount = new int[5];//倒したウィルスの数。lv - 1 に対応するlv毎の撃破数を保持

    /// <summary>
    /// スコアの初期化
    /// </summary>
    public void ScoreReset()
    {
        MaxCombo = 0;
        Array.Clear(KillCount, 0, KillCount.Length);
    }

    /// <summary>
    /// 撃破数の合計を返す。
    /// </summary>
    public int SumKillCount()
    {
        int sum = 0;
        foreach (int count in KillCount)
        {
        sum += count;
        }
        return sum;
    }
    /// <summary>
    /// 対象の倒した数のカウントを1進める。
    /// 引数 : lv -> 撃破されたウィルスのレベル (1 ~ 5)
    /// </summary>
    public void AddKillCount(int lv)
    {
        if(lv > KillCount.Length)
        {
            Debug.LogWarning("AddKillCount()の引数:lvが不正");
            return;
        }
        KillCount[lv - 1]++;
    }

    /// <summary>
    /// 引数のコンボ数が今のMaxComboより大きいなら更新
    /// </summary>
    public void UpdateMaxConbo(int conbo)
    {
        if(MaxCombo < conbo) MaxCombo = conbo;
    }

}
