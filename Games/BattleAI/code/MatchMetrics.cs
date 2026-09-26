using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 1試合分の対戦メトリクス(命中率・戦術滞在時間・残HPなど)を集計し、決着時にConsoleへ出力する。
/// 同時にCSVへ1試合1行で追記し、試合をまたいだ分析・学習用データとして蓄積する
/// </summary>
public class MatchMetrics : MonoBehaviour
{
    //v2形式(ai_type列で3種の敵AIを区別)。旧matches.csvはアーカイブとして残し、以後は書かない
    private const string CSV_FILE_NAME = "battle_data.csv";
    //CSVの戦術列(EnemyAIProの戦術ラベルと一致させる。戦術を増やしたらここも更新する)
    private static readonly string[] CSV_TACTIC_COLUMNS = { "SEARCH", "APPROACH", "ASSAULT", "AMBUSH", "ATTACK", "FEINT", "RETREAT" };

    public static MatchMetrics Instance { get; private set; }

    [SerializeField] private OptionSetting _optionSetting;//CSV保存のON/OFF設定(未設定なら常に保存)

    private int _playerShots;
    private int _playerHits;
    private int _enemyShots;
    private int _enemyHits;
    private readonly Dictionary<string, float> _tacticTimes = new();
    private int _feintAttempts;
    private int _feintHiddenCount;//フェイントを使った見失いエピソードの数
    private float _feintHiddenTotal;//同・合計隠密時間
    private int _plainHiddenCount;//フェイント無しの見失いエピソードの数
    private float _plainHiddenTotal;//同・合計隠密時間
    private CharaData _enemyData;
    private bool _isLogged;

    //リザルト画面に表示する用のサマリ(日本語。戦術名は表示名に直してから出す)
    public string ScreenSummary { get; private set; } = "";

    void Awake()
    {
        Instance = this;
    }

    //敵の残HP表示用に、敵の実データを登録してもらう
    public void RegisterEnemy(CharaData a_enemyData)
    {
        _enemyData = a_enemyData;
    }

    public void CountShot(bool a_isEnemyBullet)
    {
        if (a_isEnemyBullet)
        {
            _enemyShots++;
        }
        else
        {
            _playerShots++;
        }
    }

    public void CountHit(bool a_isEnemyBullet)
    {
        if (a_isEnemyBullet)
        {
            _enemyHits++;
        }
        else
        {
            _playerHits++;
        }
    }

    public void ReportTacticTime(string a_tacticLabel, float a_deltaTime)
    {
        _tacticTimes.TryGetValue(a_tacticLabel, out float total);
        _tacticTimes[a_tacticLabel] = total + a_deltaTime;
    }

    public void CountFeintAttempt()
    {
        _feintAttempts++;
    }

    /// <summary>
    /// 見失いエピソード(視界を切ってから再発見されるまで)の記録。
    /// フェイントの効果は「あり/なしの平均隠密時間の差」として測る(単発の成否は因果を観測できないため判定しない)
    /// </summary>
    public void ReportHiddenEpisode(float a_duration, bool a_usedFeint)
    {
        if (a_usedFeint)
        {
            _feintHiddenCount++;
            _feintHiddenTotal += a_duration;
        }
        else
        {
            _plainHiddenCount++;
            _plainHiddenTotal += a_duration;
        }
    }

    public void LogMatchResult(bool a_isPlayerWin)
    {
        if (_isLogged)
        {
            return;
        }
        _isLogged = true;

        //開いたままの見失いエピソードを、サマリ構築より先に打ち切り記録してもらう
        if (EnemyAIPro.Instance != null)
        {
            EnemyAIPro.Instance.FlushHiddenEpisode();
        }

        float playerHp = Player.Instance != null ? Player.Instance.Data.nowHp : 0f;
        float enemyHp = _enemyData != null ? _enemyData.nowHp : 0f;

        List<KeyValuePair<string, float>> sortedTactics = new(_tacticTimes);
        sortedTactics.Sort((a, b) => b.Value.CompareTo(a.Value));

        //Console用(日本語)
        StringBuilder builder = new();
        builder.AppendLine($"[MatchMetrics] 決着: {(a_isPlayerWin ? "プレイヤーの勝ち" : "敵の勝ち")} | 試合時間 {Time.timeSinceLevelLoad:F1}秒 | 残HP: プレイヤー {playerHp:F0} / 敵 {enemyHp:F0}");
        builder.AppendLine($"  命中率: プレイヤー {_playerHits}/{_playerShots} ({ToRateText(_playerHits, _playerShots)}) | 敵 {_enemyHits}/{_enemyShots} ({ToRateText(_enemyHits, _enemyShots)})");
        if (sortedTactics.Count > 0)
        {
            builder.Append("  戦術滞在: ");
            AppendTacticTimes(builder, sortedTactics, false);
            builder.AppendLine();
        }
        if (_feintHiddenCount > 0 || _plainHiddenCount > 0)
        {
            builder.Append($"  見失い平均: フェイントあり {ToAverage(_feintHiddenTotal, _feintHiddenCount):F1}秒×{_feintHiddenCount}回 / なし {ToAverage(_plainHiddenTotal, _plainHiddenCount):F1}秒×{_plainHiddenCount}回 (フェイント試行 {_feintAttempts}回)");
        }
        Debug.Log(builder.ToString());

        //リザルト画面用
        StringBuilder screenBuilder = new();
        screenBuilder.AppendLine($"試合時間 {Time.timeSinceLevelLoad:F1}秒   残りHP  自分 {playerHp:F0} / 敵 {enemyHp:F0}");
        screenBuilder.AppendLine($"命中率  自分 {_playerHits}/{_playerShots} ({ToRateText(_playerHits, _playerShots)})   敵 {_enemyHits}/{_enemyShots} ({ToRateText(_enemyHits, _enemyShots)})");
        if (sortedTactics.Count > 0)
        {
            screenBuilder.Append("戦術  ");
            AppendTacticTimes(screenBuilder, sortedTactics, true);
            screenBuilder.AppendLine();
        }
        if (_feintHiddenCount > 0 || _plainHiddenCount > 0)
        {
            screenBuilder.Append($"見失い平均  フェイントあり {ToAverage(_feintHiddenTotal, _feintHiddenCount):F1}秒×{_feintHiddenCount}回  /  なし {ToAverage(_plainHiddenTotal, _plainHiddenCount):F1}秒×{_plainHiddenCount}回");
        }
        ScreenSummary = screenBuilder.ToString();

        AppendCsvRow(a_isPlayerWin, playerHp, enemyHp);
    }

    //敵AIの種類をCSV用の文字列にする(タイトルの選択を参照。設定が無い場合はPro有無だけで推定)
    private string GetAiTypeText()
    {
        //試行パラメータ適用中はメニュー選択ではなく試行番号を記録する(最適化・A/Bのアームを区別するため)
        string trialLabel = OptimizationRunner.AppliedTrialLabel;
        if (trialLabel != null)
        {
            return trialLabel;
        }
        //実際に出撃したAIを記録する(Shuffleの抽選結果やA/Bの上書きを反映済み)。
        if (EnemySpawner.SpawnedAi != null)
        {
            return EnemySpawner.SpawnedAi.CsvLabel;
        }
        if (_optionSetting != null)
        {
            AiInfo info = AiCatalog.Find(_optionSetting.enemyAiType);
            return info != null ? info.CsvLabel : "unknown";
        }
        return EnemyAIPro.Instance != null ? "pro_optimized_v1" : "base";
    }

    //対戦相手をCSV用の文字列にする。人間なら"player"、ボットならその性格名(例: "bot_standard")。
    private string GetOpponentText()
    {
        return PlayerBot.Instance != null ? PlayerBot.Instance.CsvLabel : "player";
    }

    //1試合1行でCSVに追記する
    private void AppendCsvRow(bool a_isPlayerWin, float a_playerHp, float a_enemyHp)
    {
        //タイトルの設定でCSV保存がOFFなら書かない
        if (_optionSetting != null && !_optionSetting.saveCsv)
        {
            return;
        }

        try
        {
            string directory = GetCsvDirectory();
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, CSV_FILE_NAME);

            if (!File.Exists(path))
            {
                StringBuilder header = new();
                header.Append("timestamp,player_win,duration_sec,player_hp,enemy_hp,");
                header.Append("player_shots,player_hits,enemy_shots,enemy_hits,");
                header.Append("feint_attempts,feint_hidden_count,feint_hidden_avg,plain_hidden_count,plain_hidden_avg");
                foreach (string tactic in CSV_TACTIC_COLUMNS)
                {
                    header.Append($",tactic_{tactic.ToLowerInvariant()}");
                }
                header.AppendLine(",ai_type,opponent");
                File.WriteAllText(path, header.ToString());
            }

            //小数点の書式が実行環境の言語設定に依存しないよう、必ずInvariantCultureで書く
            CultureInfo culture = CultureInfo.InvariantCulture;
            StringBuilder row = new();
            row.Append(System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", culture));
            row.Append($",{(a_isPlayerWin ? 1 : 0)}");
            row.Append($",{Time.timeSinceLevelLoad.ToString("F1", culture)}");
            row.Append($",{a_playerHp.ToString("F0", culture)},{a_enemyHp.ToString("F0", culture)}");
            row.Append($",{_playerShots},{_playerHits},{_enemyShots},{_enemyHits}");
            row.Append($",{_feintAttempts},{_feintHiddenCount},{ToAverage(_feintHiddenTotal, _feintHiddenCount).ToString("F2", culture)}");
            row.Append($",{_plainHiddenCount},{ToAverage(_plainHiddenTotal, _plainHiddenCount).ToString("F2", culture)}");
            foreach (string tactic in CSV_TACTIC_COLUMNS)
            {
                _tacticTimes.TryGetValue(tactic, out float time);
                row.Append($",{time.ToString("F1", culture)}");
            }
            row.AppendLine($",{GetAiTypeText()},{GetOpponentText()}");
            File.AppendAllText(path, row.ToString());
        }
        catch (System.Exception exception)
        {
            //WebGLなどファイル書き込みできない環境では諦める(ゲーム進行は止めない)
            Debug.LogWarning($"[MatchMetrics] CSV保存に失敗: {exception.Message}");
        }
    }

    private string GetCsvDirectory()
    {
#if UNITY_EDITOR
        //エディタではプロジェクト直下に置く(Pythonなどから読みやすい)
        return Path.Combine(Application.dataPath, "..", "MetricsLogs");
#else
        return Path.Combine(Application.persistentDataPath, "MetricsLogs");
#endif
    }

    private float ToAverage(float a_total, int a_count)
    {
        return a_count > 0 ? a_total / a_count : 0f;
    }

    //戦術ごとの滞在時間を並べる。
    private void AppendTacticTimes(StringBuilder a_builder, List<KeyValuePair<string, float>> a_sortedTactics, bool a_isForScreen)
    {
        for (int i = 0; i < a_sortedTactics.Count; i++)
        {
            string label = a_sortedTactics[i].Key;
            string name = a_isForScreen ? EnemyAIPro.TacticDisplayName(label) : label;
            string unit = a_isForScreen ? "秒" : "s";
            a_builder.Append($"{name} {a_sortedTactics[i].Value:F1}{unit}");
            if (i < a_sortedTactics.Count - 1)
            {
                a_builder.Append(" / ");
            }
        }
    }

    private string ToRateText(int a_hits, int a_shots)
    {
        return a_shots > 0 ? $"{(float)a_hits / a_shots:P1}" : "-";
    }
}
