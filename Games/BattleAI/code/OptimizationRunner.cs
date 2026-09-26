using System;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Optuna最適化の受け口。Python側とファイルで連携する:
///   1. ml/bridge/params.json に新しい試行(trial番号)が書かれるのを待つ
///   2. パラメータをEnemyAiProDataの実行時コピーに適用し、指定試合数の自動対戦を回す
///   3. 勝率を ml/bridge/result.json に書き出し、次の試行を待つ
/// 元のアセットは変更しない。
/// 使い方: Managersにアタッチ。Optimize On Play=ONでループ開始。
///         Trial Modeはparams.jsonを1回だけ適用する手動テスト用
/// </summary>
public class OptimizationRunner : MonoBehaviour
{
    private const string PARAMS_FILE_NAME = "params.json";
    private const string RESULT_FILE_NAME = "result.json";
    private const float POLL_INTERVAL = 0.5f;//params.jsonを確認する間隔(実時間の秒)
    private const float GUI_LABEL_X = 10f;
    private const float GUI_LABEL_Y_RUNNING = 40f;//連戦中はAUTO BATTLE表示(AutoBattleRunner)の下に出す
    private const float GUI_LABEL_Y_IDLE = 15f;
    private const float GUI_LABEL_WIDTH = 600f;
    private const float GUI_LABEL_HEIGHT = 30f;

    public static OptimizationRunner Instance { get; private set; }

    //設定はOptionSetting.assetに集約(ON/OFFの切り忘れを1か所で見渡せるように)
    [SerializeField] private OptionSetting _optionSetting;

    private bool TrialMode => _optionSetting != null && _optionSetting.trialMode;
    private bool OptimizeOnPlay => _optionSetting != null && _optionSetting.optimizeOnPlay;

    //ループの進行状態(シーン再読込を跨いで保持する)
    private enum OptState
    {
        Idle,//ループしていない
        Waiting,//params.jsonの新しい試行を待っている(ゲームは停止)
        Running,//自動対戦で試行中
    }
    private static OptState _state;
    private static int _runningTrial;//実行中の試行番号
    private static int _runningMatches;//実行中の試行の試合数
    private static EnemyAiType? _runningAiType;//実行中の試行が指定した敵AI(A/B対戦用。未指定ならnull)

    /// <summary>
    /// 試行がAIの種類を指定していればそれを返す(A/B対戦で交互に切り替えるため)。
    /// 未指定ならnullで、呼び出し側はOptionSettingの設定を使う
    /// </summary>
    public static EnemyAiType? TrialAiTypeOverride => _state == OptState.Running ? _runningAiType : null;

    private TrialParamsFile _applied;//適用済みの内容(画面表示用)
    private float _pollTimer;

    private static string BridgeDirectory => ProjectPaths.BridgeDirectory;

    //試行パラメータで戦っている間はその試行番号(メトリクスのai_type用)。未適用ならnull
    public static string AppliedTrialLabel => Instance != null && Instance._applied != null ? $"trial_{Instance._applied.trial}" : null;

    //JsonUtilityで読み書きするためのファイル形式定義
    [Serializable]
    private class TrialParamsFile
    {
        public int trial;//試行番号(Python側が増やす)
        public int matches;//この試行で戦う試合数
        public TrialParam[] values;
        public string ai_type;//任意: 試行ごとに敵AIを切り替える(A/B対戦用)。空ならOptionSettingに従う
    }

    [Serializable]
    private class TrialParam
    {
        public string name;//EnemyAiProData/EnemyAiDataのフィールド名(例: "AIM_LEAD_RATE")
        public float value;
    }

    [Serializable]
    private class TrialResultFile
    {
        public int trial;
        public int matches;
        public int enemy_wins;
        public int draws;//時間切れ引き分け。分母に含まれる=敵の負け扱い(膠着は低評価)
        public float enemy_win_rate;
    }

    void Awake()
    {
        Instance = this;
        if (OptimizeOnPlay && _state == OptState.Idle)
        {
            _state = OptState.Waiting;
            Debug.Log($"[Opt] 最適化ループ開始: {BridgeDirectory} の params.json を待ちます");
        }
    }

    void Update()
    {
        switch (_state)
        {
            case OptState.Waiting:
                TickWaiting();
                break;
            case OptState.Running:
                //連戦の完了を監視する(進行自体はAutoBattleRunnerに任せる)
                if (!AutoBattleRunner.IsRunning)
                {
                    WriteResult();
                    _state = OptState.Waiting;
                }
                break;
        }
    }

    //次の試行を待つ。スタート演出が終わり次第ゲームを止め、params.jsonの更新を確認し続ける
    private void TickWaiting()
    {
        if (GameState.IsGameStarted)
        {
            Time.timeScale = 0f;//無意味な試合が進まないように停止(演出中に止めると演出と衝突するため開始後に)
        }

        _pollTimer += Time.unscaledDeltaTime;
        if (_pollTimer < POLL_INTERVAL)
        {
            return;
        }
        _pollTimer = 0f;

        if (!TryReadNewTrial(out TrialParamsFile paramsFile))
        {
            return;
        }
        _runningTrial = paramsFile.trial;
        _runningMatches = Mathf.Max(1, paramsFile.matches);
        _runningAiType = ParseAiType(paramsFile.ai_type);
        _state = OptState.Running;
        AutoBattleRunner.BeginRun(_runningMatches);
        Debug.Log($"[Opt] 試行#{_runningTrial} 開始: {_runningMatches}試合{(_runningAiType.HasValue ? $" / AI={_runningAiType.Value}" : "")}");
        ResultManager.Instance.Retry();//シーンを読み直すと新パラメータで敵が生まれる
    }

    //「まだ結果を出していない試行」がparams.jsonにあればそれを返す。
    //試行番号をresult.jsonと比較する方式なので、Unityを再起動しても続きから再開できる
    private bool TryReadNewTrial(out TrialParamsFile a_paramsFile)
    {
        a_paramsFile = null;
        string paramsPath = Path.Combine(BridgeDirectory, PARAMS_FILE_NAME);
        if (!File.Exists(paramsPath))
        {
            return false;
        }
        try
        {
            a_paramsFile = JsonUtility.FromJson<TrialParamsFile>(File.ReadAllText(paramsPath));
        }
        catch (Exception)
        {
            return false;//Python側が書き込み中の可能性。次の確認まで待つ
        }
        //valuesは空でも可(段階2の方策評価: パラメータは適用せず、指定試合数を回すだけの試行)。
        //nullは書き込み途中などの異常とみなす
        if (a_paramsFile == null || a_paramsFile.values == null)
        {
            return false;
        }

        string resultPath = Path.Combine(BridgeDirectory, RESULT_FILE_NAME);
        if (!File.Exists(resultPath))
        {
            return true;//まだ一度も結果を出していない
        }
        TrialResultFile lastResult = JsonUtility.FromJson<TrialResultFile>(File.ReadAllText(resultPath));
        return lastResult == null || lastResult.trial != a_paramsFile.trial;
    }

    //試行の成績をresult.jsonへ書き出す(Python側が読んでOptunaに報告する)
    private void WriteResult()
    {
        var result = new TrialResultFile
        {
            trial = _runningTrial,
            matches = AutoBattleRunner.MatchesDone,
            enemy_wins = AutoBattleRunner.EnemyWins,
            draws = AutoBattleRunner.Draws,
            enemy_win_rate = AutoBattleRunner.MatchesDone > 0 ? (float)AutoBattleRunner.EnemyWins / AutoBattleRunner.MatchesDone : 0f,
        };

        //書き込み途中のファイルをPython側が読まないよう、別名で書いてから置き換える
        string tempPath = Path.Combine(BridgeDirectory, RESULT_FILE_NAME + ".tmp");
        string resultPath = Path.Combine(BridgeDirectory, RESULT_FILE_NAME);
        File.WriteAllText(tempPath, JsonUtility.ToJson(result, true));
        if (File.Exists(resultPath))
        {
            File.Delete(resultPath);
        }
        File.Move(tempPath, resultPath);
        Debug.Log($"[Opt] 試行#{_runningTrial} 完了: 敵{result.enemy_wins}/{result.matches}勝 (勝率{result.enemy_win_rate:P0}) → result.json");
    }

    /// <summary>
    /// params.jsonの値を上書きした実行時コピーを返す。
    /// 最適化中でも手動テスト(Trial Mode)でもなければ元のデータをそのまま返す
    /// </summary>
    public static EnemyAiProData ApplyTrialParams(EnemyAiProData a_original)
    {
        if (Instance == null || (!Instance.TrialMode && _state == OptState.Idle))
        {
            return a_original;
        }
        return Instance.Apply(a_original);
    }

    private EnemyAiProData Apply(EnemyAiProData a_original)
    {
        string path = Path.Combine(BridgeDirectory, PARAMS_FILE_NAME);
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[Trial] {path} が無いため、元のパラメータで動作します");
            return a_original;
        }

        TrialParamsFile file = JsonUtility.FromJson<TrialParamsFile>(File.ReadAllText(path));
        if (file == null || file.values == null || file.values.Length == 0)
        {
            //空valuesは正当(方策評価: EnemyAiProDataは変えず、方策ファイルtactic_policy.jsonで動く)
            return a_original;
        }

        //元アセットを守るため、コピーに対して上書きする
        EnemyAiProData copy = Instantiate(a_original);
        var log = new StringBuilder($"[Trial] #{file.trial} パラメータ適用:");
        foreach (TrialParam param in file.values)
        {
            if (ScriptableFieldWriter.TrySetNumber(copy, param.name, param.value))
            {
                log.Append($" {param.name}={param.value}");
            }
            else
            {
                Debug.LogError($"[Trial] フィールド '{param.name}' が見つかりません(名前の綴りを確認)");
            }
        }
        Debug.Log(log.ToString());
        _applied = file;
        return copy;
    }

    //試行が指定したAI名を解釈する。CSVラベル(例: "pro_learned_v5")かenum名を受け付ける
    private static EnemyAiType? ParseAiType(string a_name)
    {
        if (string.IsNullOrEmpty(a_name))
        {
            return null;
        }
        //まずAiCatalogのCSVラベルで探す(Python側はCSVと同じ名前で書ける)
        foreach (AiInfo info in AiCatalog.All)
        {
            if (info.CsvLabel == a_name)
            {
                return info.Type;
            }
        }
        if (Enum.TryParse(a_name, out EnemyAiType parsed))
        {
            return parsed;
        }
        Debug.LogError($"[Opt] ai_type '{a_name}' を解釈できません(CSVラベルかenum名で指定してください)");
        return null;
    }

    //試行パラメータで動作中であることを画面に明示する(素の設定と取り違えないように)
    void OnGUI()
    {
        string label = _state switch
        {
            OptState.Waiting => "OPT WAITING: params.json の新しい試行を待機中...",
            OptState.Running => $"OPT TRIAL #{_runningTrial}",
            _ => _applied != null ? $"TRIAL #{_applied.trial}  (params: {_applied.values.Length})" : null,
        };
        if (label != null)
        {
            float y = _state == OptState.Running ? GUI_LABEL_Y_RUNNING : GUI_LABEL_Y_IDLE;
            GUI.Label(new Rect(GUI_LABEL_X, y, GUI_LABEL_WIDTH, GUI_LABEL_HEIGHT), label);
        }
    }
}
