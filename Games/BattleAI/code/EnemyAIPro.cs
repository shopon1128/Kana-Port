using UnityEngine;

/// <summary>
/// AIの頭脳(ユーティリティAI)。
/// 状況から各戦術の有効度を採点し、最も有効な戦術を選んで実行する。
/// 公平性ルール: 戦術の変更は判断チック(0.25秒間隔)でのみ行い、フレーム単位の機械的な即応はしない。
/// 弾回避も既存AIと同じ観測時間ベース(反応遅延あり)のみで、緊急ダッシュ回避のような理不尽な動きはしない。
///
/// このファイルは「知覚・採点・選択」を持つ。選んだ戦術の実行は EnemyAIPro.Tactics.cs にある
/// </summary>
[RequireComponent(typeof(Enemy))]
public partial class EnemyAIPro : MonoBehaviour
{
    private const float CONTACT_FRESH_DURATION = 1.5f;//「直前まで見られていた」とみなす時間(フェイントの機会判定)
    private const float OBS_DIST_NORM = 500f;//観測ベクトルの距離の正規化スケール
    private const float OBS_SINCE_SEEN_NORM = 5f;//観測ベクトルの「最終視認からの経過」正規化スケール(秒)
    private const float LOW_EP_RETREAT_RATE = 0.25f;//このEP割合を下回ったら離脱を検討する
    private const float ASSAULT_MIN_DISTANCE_RATE = 0.8f;//射程のこの割合より遠いときだけ強襲する(近ければ普通に撃てばよい)
    private const float FEINT_FAILED_ROLL_COOLDOWN = 1f;//フェイント抽選に外れたときの再抽選待ち(連続抽選で確率が形骸化するのを防ぐ)
    private const float SEEN_DEBOUNCE_TIME = 0.2f;//視線判定の明滅を無視する時間。この時間続いた変化だけを離脱の計画に使う

    //戦術の採点(手調整の効用値)。高いほど優先され、SCORE_DISABLEDは「その戦術は今は選べない」を表す
    private const float SCORE_DISABLED = 0f;
    private const float SCORE_SEARCH_WITH_CONTACT = 5f;//位置に心当たりがあるなら、他の戦術に譲る
    private const float SCORE_SEARCH_NO_CONTACT = 30f;//位置の心当たりが無いときの基本行動
    private const float SCORE_APPROACH = 40f;
    private const float SCORE_AMBUSH = 55f;
    private const float SCORE_ASSAULT = 60f;
    private const float SCORE_FEINT = 70f;
    private const float SCORE_RETREAT_LOW_EP = 70f;//EPが減ってきた(離脱の突入) / 回復しきるまで続ける(離脱の継続)
    private const float SCORE_ATTACK = 80f;
    private const float SCORE_RETREAT_NO_EP = 90f;//1発も撃てない
    private const float SCORE_FEINT_RUNNING = 95f;//実行中のフェイントは完走する(離脱の緊急時のみ中断)
    private const float SCORE_RETREAT_INVINCIBLE = 100f;//無敵中の相手に付き合わず即離脱(カウンター回避)

    public static EnemyAIPro Instance { get; private set; }

    //スポナーが生成前に指定するデータの差し替え(タイトルのAI選択用)。Awakeで一度だけ消費される
    public static EnemyAiProData PendingAiData;
    //学習した戦術選択方策。設定されている間は ScoreTactic のargmaxの代わりにこれで選ぶ
    public static TacticPolicy PendingPolicy;

    [SerializeField] private EnemyAiProData _aiData;
    [SerializeField] private MapData _mapData;
    [SerializeField] private CharaData _playerData;//プレイヤーのダッシュ検知・速度予測用
    [SerializeField] private BulletData _bulletData;//偏差射撃の弾速参照用(敵通常弾のデータ)

    //部品
    private Enemy _enemy;
    private CharaData _data;
    private GridPathMover _mover;
    private BulletDodgeSensor _dodgeSensor;
    private StuckWatcher _stuckWatcher;
    private StuckEscape _stuckEscape;
    private DangerMemory _dangerMemory;
    private TacticPolicy _policy;

    //判断
    private Tactic _tactic = Tactic.Search;
    private float _decisionTimer;
    private readonly float[] _tacticScores = new float[TacticCatalog.Count];//直近の判断での各戦術の生スコア(有効マスク・ログ用)
    private readonly float[] _policyScores = new float[TacticCatalog.Count];
    private int _logTick;//この試合の判断チック番号(ログ用)

    //知覚・記憶
    private bool _canSeePlayer;
    private bool _seenDebounced;//視線判定の明滅を均した「見られている」判定(離脱の計画用)
    private float _seenFlipTimer;
    private Vector3 _lastKnownPlayerPos;
    private bool _hasContact;//プレイヤーの位置に心当たりがあるか
    private float _lastSeenTime = float.NegativeInfinity;
    private float _lastHp;//被弾検知用(HPの減少を監視する)

    //プレイヤー速度の観測値。入力ではなく実際の変位から算出する
    //(壁に阻まれて動けない移動入力に予測が騙されない+入力という内部情報を読まない=公平)
    private Vector2 _observedPlayerVelocity;
    private Vector2 _prevPlayerPosition;
    private bool _hasPrevPlayerPosition;

    //見失いエピソード(視界を切ってから再発見されるまで)の計測。フェイントの効果測定に使う
    private bool _hiddenEpisodeActive;
    private float _hiddenEpisodeStart;
    private bool _hiddenEpisodeUsedFeint;

    //危険地帯の記憶(可視化用に公開)
    public DangerMemory Danger => _dangerMemory;

    /// <summary>現在の戦術の記録用ラベル(英字の安定キー)</summary>
    public string CurrentTacticLabel => TacticCatalog.Label(_tactic);

    /// <summary>現在の戦術の画面表示名</summary>
    public string CurrentTacticDisplayName => TacticCatalog.DisplayName(CurrentTacticLabel);

    void Awake()
    {
        //データの差し替えは、以降の部品生成より先に行うこと
        if (PendingAiData != null)
        {
            _aiData = PendingAiData;
            PendingAiData = null;
        }
        //学習方策の受け取り(Learnedブレイン用)。設定されていれば選択をこれに委ねる
        _policy = PendingPolicy;
        PendingPolicy = null;
        //最適化の試行中はさらにパラメータを上書きする
        _aiData = OptimizationRunner.ApplyTrialParams(_aiData);

        Instance = this;
        _enemy = GetComponent<Enemy>();
        _data = _enemy.Data;
        _dangerMemory = new DangerMemory(_mapData, _aiData.DangerMemoryDuration, _aiData.DangerCellCost);
        _mover = new GridPathMover(_mapData, _aiData.RepathInterval, _aiData.WaypointReachDistance, _dangerMemory.GetCost);
        _dodgeSensor = new BulletDodgeSensor(_aiData.BulletDetectRange, _aiData.BulletReactionTime, _aiData.BulletDodgeWidth, Layers.WallMask);
        _stuckWatcher = new StuckWatcher(_mapData);
        _stuckEscape = new StuckEscape(_mapData);
        _lastHp = _data.nowHp;
    }

    void OnDestroy()
    {
        //シーン再読込(試合の切り替え)で溜まったログを取りこぼさないよう書き出す
        if (TacticLogger.Enabled)
        {
            TacticLogger.Flush();
        }
    }

    //判断は固定時間刻みで行う(フレームレートやタイムスケールでゲームバランスが変わらないように)
    void FixedUpdate()
    {
        if (!GameState.IsGameStarted || Player.PlayerTransform == null)
        {
            return;
        }

        ObservePlayerVelocity();

        //被弾検知: 撃たれた場所を危険地帯として記憶する(スタン中でも記録する)
        if (_data.nowHp < _lastHp)
        {
            _dangerMemory.MarkArea(_mapData.WorldToCell(transform.position), _aiData.DangerHitMarkRadius);
        }
        _lastHp = _data.nowHp;

        if (_enemy.IsStunned)
        {
            return;
        }
        if (_enemy.IsDead || (Player.Instance != null && Player.Instance.IsDead))
        {
            //決着時: 開いたままの見失いエピソードを打ち切りとして記録する(長い成功エピソードほど消える偏りを防ぐ)
            FlushHiddenEpisode();
            _data.moveInput = Vector2.zero;
            _data.isDashing = false;
            return;
        }

        UpdatePerception();

        //メトリクス: 現在戦術の滞在時間を集計する
        if (MatchMetrics.Instance != null)
        {
            MatchMetrics.Instance.ReportTacticTime(CurrentTacticLabel, Time.deltaTime);
        }

        //戦術の切り替えは判断チックのタイミングでのみ行う
        _decisionTimer += Time.deltaTime;
        if (_decisionTimer >= _aiData.DecisionTickInterval)
        {
            _decisionTimer = 0f;
            ChooseTactic();
        }

        _data.isDashing = false;//必要な戦術だけが毎フレーム明示的にオンにする
        TickTactic();
        ApplyBulletDodge();
        //生の移動(交戦・フェイント等)で壁に嵌まったら、通れる隣のセルへ一定時間逃がす
        if (_stuckEscape.TryGetEscapeInput(_enemy.CenterPosition, _data.moveInput, out Vector2 escapeInput))
        {
            _data.moveInput = escapeInput;
        }
        //最終的な移動入力に対して「動けているか」を監視する(引っかかり調査用。通常は無効)
        _stuckWatcher.Tick(CurrentTacticLabel, _enemy.CenterPosition, _data.moveInput);
    }

    //---- 知覚 ----

    //プレイヤーの実変位から速度を観測する(偏差射撃・先回り予測用)
    private void ObservePlayerVelocity()
    {
        Vector2 observedPlayerPos = Player.PlayerTransform.position;
        if (_hasPrevPlayerPosition)
        {
            _observedPlayerVelocity = (observedPlayerPos - _prevPlayerPosition) / Time.fixedDeltaTime;
        }
        _prevPlayerPosition = observedPlayerPos;
        _hasPrevPlayerPosition = true;
    }

    private void UpdatePerception()
    {
        //視線は対称なので「見える=見られている」
        _canSeePlayer = LineOfSight.Instance.IsVisible(transform.position);
        if (_canSeePlayer)
        {
            _lastSeenTime = Time.time;
            OnPlayerSighted();
        }

        //視線判定は境界で細かく明滅するため、一定時間続いた変化だけを反映したデバウンス値も持つ(離脱の計画用)
        if (_canSeePlayer == _seenDebounced)
        {
            _seenFlipTimer = 0f;
        }
        else
        {
            _seenFlipTimer += Time.deltaTime;
            if (_seenFlipTimer >= SEEN_DEBOUNCE_TIME)
            {
                _seenDebounced = _canSeePlayer;
                _seenFlipTimer = 0f;
                OnSeenStateChanged(_seenDebounced);
            }
        }
        if (_canSeePlayer || _playerData.isDashing)
        {
            _lastKnownPlayerPos = Player.PlayerTransform.position;
            _hasContact = true;
        }
    }

    //「見られている」状態の確定変化(デバウンス済み)。見失いエピソードの計測に使う
    private void OnSeenStateChanged(bool a_isSeen)
    {
        if (!a_isSeen)
        {
            //視界を切った: 見失いエピソード開始。
            //フェイントは生の視線判定で発動するため、エピソード開始(デバウンス確定)より先に始まっていることがある。
            //その場合もフラグを引き継ぐ(falseで初期化するとフェイント付きエピソードが「なし」に化ける)
            _hiddenEpisodeActive = true;
            _hiddenEpisodeStart = Time.time;
            _hiddenEpisodeUsedFeint = _isFeintRunning;
        }
        else
        {
            FlushHiddenEpisode();
        }
    }

    /// <summary>
    /// 開いている見失いエピソードを閉じて記録する(再発見時と、決着時の打ち切りに使う)。
    /// 決着時はサマリ構築より先に呼ばれる必要があるため、MatchMetrics側からも呼べるように公開している
    /// </summary>
    public void FlushHiddenEpisode()
    {
        if (!_hiddenEpisodeActive)
        {
            return;
        }
        _hiddenEpisodeActive = false;
        if (MatchMetrics.Instance != null)
        {
            MatchMetrics.Instance.ReportHiddenEpisode(Time.time - _hiddenEpisodeStart, _hiddenEpisodeUsedFeint);
        }
    }

    private float DistanceToPlayer()
    {
        return Vector3.Distance(transform.position, Player.PlayerTransform.position);
    }

    private bool IsPlayerInvincible()
    {
        return Player.Instance != null && Player.Instance.IsInvincible;
    }

    //弾の太さでプレイヤーまで通るか(至近距離は判定を省略して必ず可)
    private bool HasClearShot(float a_distance)
    {
        if (a_distance <= _aiData.PointBlankRange)
        {
            return true;
        }
        return IsShotPathClear(_enemy.CenterPosition, Player.PlayerTransform.position);
    }

    //発射位置から目標点まで弾が通るか。起点は必ず実際の発射位置(コライダー中心)を使う
    private bool IsShotPathClear(Vector2 a_fireOrigin, Vector2 a_target)
    {
        Vector2 diff = a_target - a_fireOrigin;
        return !Physics2D.CircleCast(a_fireOrigin, _aiData.ShotClearanceRadius, diff.normalized, diff.magnitude, Layers.WallMask);
    }

    //強襲に必要なEP(ダッシュで詰める分+攻撃数発分)が残っているか(EP会計)
    private bool HasAssaultBudget(float a_distance)
    {
        float dashSpeed = _data.Speed * _data.DashSpeedMultiplier;
        float dashTime = a_distance / dashSpeed;
        float requiredEp = _data.DashEpCostPerSecond * dashTime + _data.ShotEpCost * _aiData.AssaultShotBudget;
        return _data.nowEp >= requiredEp;
    }

    //---- 戦術の採点と選択 ----

    private void ChooseTactic()
    {
        //各戦術の生スコアと有効マスクは常に計算する(手書きの発動可能条件を流用)
        for (int i = 0; i < TacticCatalog.Count; i++)
        {
            _tacticScores[i] = ScoreTactic(TacticCatalog.ALL[i]);
        }

        int bestIndex = _policy != null ? ChooseByPolicy() : ChooseByHandScores();

        //v4の教師データ収集: この判断(観測→選択)を記録する(学習方策で動作中は記録しない)
        if (TacticLogger.Enabled && _policy == null)
        {
            LogDecision(bestIndex);
        }

        Tactic best = TacticCatalog.ALL[bestIndex];
        if (best != _tactic)
        {
            EnterTactic(best);
        }
    }

    //その戦術が今選べるか(手書きの発動可能条件を満たすか)
    private bool IsTacticEnabled(int a_index)
    {
        return _tacticScores[a_index] > SCORE_DISABLED;
    }

    //手書きの効用: 生スコア + 維持ボーナスの argmax
    private int ChooseByHandScores()
    {
        int bestIndex = TacticCatalog.IndexOf(_tactic);
        float bestScore = float.MinValue;
        for (int i = 0; i < TacticCatalog.Count; i++)
        {
            float adjusted = TacticCatalog.ALL[i] == _tactic ? _tacticScores[i] + _aiData.TacticKeepBonus : _tacticScores[i];
            if (adjusted > bestScore)
            {
                bestScore = adjusted;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    //学習方策: 学習スコアの argmax を「有効 or 現在の戦術」の中から選ぶ
    private int ChooseByPolicy()
    {
        //無敵→強制離脱は公平性ルールとして手書きのまま残す(学習対象外)
        int retreatIndex = TacticCatalog.IndexOf(Tactic.Retreat);
        if (IsPlayerInvincible() && IsTacticEnabled(retreatIndex))
        {
            return retreatIndex;
        }

        _policy.Score(BuildObservation(), _policyScores);
        int bestIndex = -1;
        float bestScore = float.MinValue;
        for (int i = 0; i < TacticCatalog.Count; i++)
        {
            //有効な戦術か、現在の戦術(維持は常に許可=手書き脳のヒステリシスと同じ)だけを候補にする
            bool selectable = IsTacticEnabled(i) || TacticCatalog.ALL[i] == _tactic;
            if (selectable && _policyScores[i] > bestScore)
            {
                bestScore = _policyScores[i];
                bestIndex = i;
            }
        }
        //万一どれも選べない場合は現在の戦術を維持する
        return bestIndex >= 0 ? bestIndex : TacticCatalog.IndexOf(_tactic);
    }

    //現在の判断を「観測ベクトル + 有効マスク + 選ばれた戦術」としてTacticLoggerへ渡す
    private void LogDecision(int a_chosenIndex)
    {
        bool[] valid = new bool[TacticCatalog.Count];
        for (int i = 0; i < TacticCatalog.Count; i++)
        {
            valid[i] = IsTacticEnabled(i);
        }
        TacticLogger.Log(_logTick++, BuildObservation(), valid, a_chosenIndex);
    }

    //観測ベクトル(手書き脳が見ている情報だけ)。並びはTacticLogger.OBS_NAMESと一致させること
    private float[] BuildObservation()
    {
        float distance = DistanceToPlayer();
        return new float[]
        {
            Mathf.Clamp01(distance / OBS_DIST_NORM),
            Mathf.Clamp01(_data.nowEp / Mathf.Max(1f, _data.MaxEp)),
            Mathf.Clamp01((Time.time - _lastSeenTime) / OBS_SINCE_SEEN_NORM),//初期は+∞→1にクランプ
            Mathf.Clamp01(_retreatSeenTimer / Mathf.Max(0.01f, _aiData.CorneredTime)),
            _canSeePlayer ? 1f : 0f,
            IsPlayerInvincible() ? 1f : 0f,
            _hasContact ? 1f : 0f,
            HasClearShot(distance) ? 1f : 0f,
            HasAssaultBudget(distance) ? 1f : 0f,
            Time.time < _feintCooldownUntil ? 1f : 0f,
            _isFeintRunning ? 1f : 0f,
            _tactic == Tactic.Search ? 1f : 0f,
            _tactic == Tactic.Approach ? 1f : 0f,
            _tactic == Tactic.Assault ? 1f : 0f,
            _tactic == Tactic.Ambush ? 1f : 0f,
            _tactic == Tactic.Attack ? 1f : 0f,
            _tactic == Tactic.Feint ? 1f : 0f,
            _tactic == Tactic.Retreat ? 1f : 0f,
        };
    }

    private float ScoreTactic(Tactic a_tactic)
    {
        float distance = DistanceToPlayer();
        bool invincible = IsPlayerInvincible();

        switch (a_tactic)
        {
            case Tactic.Search:
                return _hasContact ? SCORE_SEARCH_WITH_CONTACT : SCORE_SEARCH_NO_CONTACT;

            case Tactic.Approach:
                if (!_canSeePlayer || invincible || distance <= _aiData.AttackRange)
                {
                    return SCORE_DISABLED;
                }
                return SCORE_APPROACH;

            case Tactic.Assault:
                if (!_canSeePlayer || invincible || distance <= _aiData.AttackRange * ASSAULT_MIN_DISTANCE_RATE)
                {
                    return SCORE_DISABLED;
                }
                return HasAssaultBudget(distance) ? SCORE_ASSAULT : SCORE_DISABLED;

            case Tactic.Ambush:
                //見つかっていない+心当たりあり→遮蔽伝いに忍び寄る
                if (_canSeePlayer || !_hasContact || invincible)
                {
                    return SCORE_DISABLED;
                }
                return SCORE_AMBUSH;

            case Tactic.Attack:
                if (!_canSeePlayer || invincible || distance > _aiData.AttackRange || _data.nowEp < _data.ShotEpCost)
                {
                    return SCORE_DISABLED;
                }
                return HasClearShot(distance) ? SCORE_ATTACK : SCORE_DISABLED;

            case Tactic.Feint:
                return ScoreFeint(invincible);

            case Tactic.Retreat:
                return ScoreRetreat(invincible);
        }
        return SCORE_DISABLED;
    }

    private float ScoreFeint(bool a_invincible)
    {
        if (_isFeintRunning)
        {
            return SCORE_FEINT_RUNNING;
        }
        if (_canSeePlayer || !_hasContact || a_invincible || Time.time < _feintCooldownUntil)
        {
            return SCORE_DISABLED;
        }
        //「直前まで見られていた」直後だけ、囮の残像に意味がある
        if (Time.time - _lastSeenTime > CONTACT_FRESH_DURATION)
        {
            return SCORE_DISABLED;
        }
        if (Random.value >= _aiData.FeintChance)
        {
            _feintCooldownUntil = Time.time + FEINT_FAILED_ROLL_COOLDOWN;//外れたらしばらく再抽選しない
            return SCORE_DISABLED;
        }
        return SCORE_FEINT;
    }

    private float ScoreRetreat(bool a_invincible)
    {
        if (a_invincible)
        {
            return SCORE_RETREAT_INVINCIBLE;
        }
        if (_tactic == Tactic.Retreat)
        {
            //応戦: 振り切れないまま一定時間が経ち、最低限のEPが戻っているなら、逃げに徹するのをやめて戦う
            bool canFightBack = _data.nowEp >= _data.ShotEpCost * _aiData.FightBackShotCount;
            if (_retreatSeenTimer >= _aiData.CorneredTime && canFightBack)
            {
                return SCORE_DISABLED;
            }
            //応戦条件を満たさない間は、十分回復するまで離脱を続ける
            return _data.nowEp < _data.MaxEp * _aiData.RecoverEpRate ? SCORE_RETREAT_LOW_EP : SCORE_DISABLED;
        }
        //離脱への突入条件
        if (_data.nowEp < _data.ShotEpCost)
        {
            return SCORE_RETREAT_NO_EP;
        }
        return _data.nowEp < _data.MaxEp * LOW_EP_RETREAT_RATE ? SCORE_RETREAT_LOW_EP : SCORE_DISABLED;
    }
}
