using UnityEngine;

/// <summary>
/// AIの頭脳(ユーティリティAI)。
/// 状況から各戦術の有効度を採点し、最も有効な戦術を選んで実行する。
/// 公平性ルール: 戦術の変更は判断チック(0.25秒間隔)でのみ行い、フレーム単位の機械的な即応はしない。
/// 弾回避も既存AIと同じ観測時間ベース(反応遅延あり)のみで、緊急ダッシュ回避のような理不尽な動きはしない。
/// </summary>
[RequireComponent(typeof(Enemy))]
public class EnemyAIPro : MonoBehaviour
{
    private enum Tactic
    {
        Search,   //索敵: プレイヤーの位置が分からないので歩き回る
        Approach, //接近: 視界内のプレイヤーへ歩いて近づく(EP温存)
        Assault,  //強襲: EPに余裕がある時、ダッシュで一気に攻撃位置へ詰める
        Ambush,   //隠密接近: 見つかっていない間、遮蔽の陰を通って忍び寄る(残像が出るダッシュは使わない)
        Attack,   //攻撃: 射撃しつつ不規則ストレイフ
        Feint,    //フェイント: 視界外で囮ダッシュの残像だけ見せ、別方向へ歩いて撹乱する
        Retreat,  //離脱: 視界を切ってEP回復を図る
    }

    private static readonly Tactic[] ALL_TACTICS =
    {
        Tactic.Search, Tactic.Approach, Tactic.Assault, Tactic.Ambush, Tactic.Attack, Tactic.Feint, Tactic.Retreat,
    };

    private const float CONTACT_FRESH_DURATION = 1.5f;//「直前まで見られていた」とみなす時間(フェイントの機会判定)
    private const float OBS_DIST_NORM = 500f;//観測ベクトルの距離の正規化スケール
    private const float OBS_SINCE_SEEN_NORM = 5f;//観測ベクトルの「最終視認からの経過」正規化スケール(秒)
    private const float LOW_EP_RETREAT_RATE = 0.25f;//このEP割合を下回ったら離脱を検討する
    private const int RETREAT_CANDIDATE_COUNT = 20;//離脱先の候補セル数
    private const float FEINT_FAILED_ROLL_COOLDOWN = 1f;//フェイント抽選に外れたときの再抽選待ち(連続抽選で確率が形骸化するのを防ぐ)
    private const float SEEN_DEBOUNCE_TIME = 0.2f;//視線判定の明滅を無視する時間。この時間続いた変化だけを離脱の計画に使う
    private const float HIDING_REPICK_INTERVAL = 0.6f;//隠れ場所を選び直す最短間隔(毎フレーム選び直すと向きが揺れ続ける)

    public static EnemyAIPro Instance { get; private set; }

    [SerializeField] private EnemyAiProData _aiData;
    [SerializeField] private MapData _mapData;
    [SerializeField] private CharaData _playerData;//プレイヤーのダッシュ検知・速度予測用
    [SerializeField] private BulletData _bulletData;//偏差射撃の弾速参照用(敵通常弾のデータ)

    private Enemy _enemy;
    private CharaData _data;
    private int _wallLayerMask;
    private GridPathMover _mover;
    private BulletDodgeSensor _dodgeSensor;
    private StuckWatcher _stuckWatcher;
    private StuckEscape _stuckEscape;
    private DangerMemory _dangerMemory;
    private float _lastHp;//被弾検知用(HPの減少を監視する)

    //危険地帯の記憶(可視化用に公開)
    public DangerMemory Danger => _dangerMemory;

    private Tactic _tactic = Tactic.Search;
    private float _decisionTimer;

    //知覚・記憶
    private bool _canSeePlayer;
    private bool _seenDebounced;//視線判定の明滅を均した「見られている」判定(離脱の計画用)
    private float _seenFlipTimer;
    private Vector3 _lastKnownPlayerPos;
    private bool _hasContact;//プレイヤーの位置に心当たりがあるか
    private float _lastSeenTime = float.NegativeInfinity;

    //戦術ごとの作業変数
    private Vector2Int _roamGoal;
    private bool _hasRoamGoal;
    private Vector2Int _retreatGoal;
    private bool _hasRetreatGoal;
    private bool _headingToHidingSpot;//隠れ場所へ走り込んでいる最中か(着くまで計画を変えないコミット用)
    private float _retreatSeenTimer;//離脱中、視線が通ったままの経過時間(振り切れない判定用)
    private float _hidingRepickTimer;//隠れ場所を選び直してからの経過時間
    private float _shotTimer;
    private float _strafeTimer;
    private int _strafeSign = 1;
    private bool _strafeDashing;
    private float _feintTimer;
    private Vector2 _feintDashDirection;
    private Vector2Int _feintWalkGoal;
    private bool _isFeintRunning;
    private float _feintCooldownUntil;

    //見失いエピソード(視界を切ってから再発見されるまで)の計測。フェイントの効果測定に使う
    private bool _hiddenEpisodeActive;
    private float _hiddenEpisodeStart;
    private bool _hiddenEpisodeUsedFeint;
    private float _ambushTimer;//隠密接近の経過時間(忍耐の期限判定用)
    private Vector2Int _flankGoal;//回り込みの経由地
    private bool _hasFlankGoal;
    private bool _flankRolled;//このエピソード(見失い区間)で回り込みの抽選を済ませたか

    //戦術ごとの名前。Labelは記録用の安定キーで、DisplayNameは画面表示用(ここだけ日本語にする)
    private static readonly (Tactic Tactic, string Label, string DisplayName)[] TACTIC_NAMES =
    {
        (Tactic.Search,   "SEARCH",   "索敵"),
        (Tactic.Approach, "APPROACH", "接近"),
        (Tactic.Assault,  "ASSAULT",  "強襲"),
        (Tactic.Ambush,   "AMBUSH",   "隠密接近"),
        (Tactic.Attack,   "ATTACK",   "攻撃"),
        (Tactic.Feint,    "FEINT",    "フェイント"),
        (Tactic.Retreat,  "RETREAT",  "離脱"),
    };
    private const string UNKNOWN_TACTIC_LABEL = "-";

    /// <summary>現在の戦術の記録用ラベル(英字の安定キー)</summary>
    public string CurrentTacticLabel
    {
        get
        {
            foreach ((Tactic tactic, string label, string _) in TACTIC_NAMES)
            {
                if (tactic == _tactic)
                {
                    return label;
                }
            }
            return UNKNOWN_TACTIC_LABEL;
        }
    }

    /// <summary>現在の戦術の画面表示名</summary>
    public string CurrentTacticDisplayName => TacticDisplayName(CurrentTacticLabel);

    /// <summary>
    /// 記録用ラベルを画面表示名に変換する。
    /// メトリクスは集計をラベルで持っているので、表示の直前にここで日本語へ直す
    /// </summary>
    public static string TacticDisplayName(string a_label)
    {
        foreach ((Tactic _, string label, string displayName) in TACTIC_NAMES)
        {
            if (label == a_label)
            {
                return displayName;
            }
        }
        return a_label;
    }

    //スポナーが生成前に指定するデータの差し替え(タイトルのAI選択用)。Awakeで一度だけ消費される
    public static EnemyAiProData PendingAiData;
    //学習した戦術選択方策。設定されている間は ScoreTactic のargmaxの代わりにこれで選ぶ
    public static TacticPolicy PendingPolicy;
    private TacticPolicy _policy;
    private readonly float[] _policyScores = new float[7];

    void OnDestroy()
    {
        //シーン再読込(試合の切り替え)で溜まったログを取りこぼさないよう書き出す
        if (TacticLogger.Enabled)
        {
            TacticLogger.Flush();
        }
    }

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
        _wallLayerMask = 1 << LayerMask.NameToLayer("Wall");
        _dangerMemory = new DangerMemory(_mapData, _aiData.DangerMemoryDuration, _aiData.DangerCellCost);
        _mover = new GridPathMover(_mapData, _aiData.RepathInterval, _aiData.WaypointReachDistance, _dangerMemory.GetCost);
        _dodgeSensor = new BulletDodgeSensor(_aiData.BulletDetectRange, _aiData.BulletReactionTime, _aiData.BulletDodgeWidth, _wallLayerMask);
        _stuckWatcher = new StuckWatcher(_mapData);
        _stuckEscape = new StuckEscape(_mapData);
        _lastHp = _data.nowHp;
    }

    //プレイヤー速度の観測値。入力ではなく実際の変位から算出する
    //(壁に阻まれて動けない移動入力に予測が騙されない+入力という内部情報を読まない=公平)
    private Vector2 _observedPlayerVelocity;
    private Vector2 _prevPlayerPosition;
    private bool _hasPrevPlayerPosition;
    private readonly float[] _tacticScores = new float[7];//直近の判断での各戦術の生スコア(ログ用)
    private int _logTick;//この試合の判断チック番号(ログ用)

    //判断は固定時間刻みで行う(フレームレートやタイムスケールでゲームバランスが変わらないように)
    void FixedUpdate()
    {
        bool isGameStarted = StartSequenceManager.Instance == null || StartSequenceManager.Instance.IsGameStarted;
        if (!isGameStarted || Player.PlayerTransform == null)
        {
            return;
        }

        //プレイヤーの実変位から速度を観測する(偏差射撃・先回り予測用)
        Vector2 observedPlayerPos = Player.PlayerTransform.position;
        if (_hasPrevPlayerPosition)
        {
            _observedPlayerVelocity = (observedPlayerPos - _prevPlayerPosition) / Time.fixedDeltaTime;
        }
        _prevPlayerPosition = observedPlayerPos;
        _hasPrevPlayerPosition = true;

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

    private void UpdatePerception()
    {
        //視線は対称なので「見える=見られている」
        _canSeePlayer = LineOfSight.Instance.IsVisible(transform.position);
        if (_canSeePlayer)
        {
            _lastSeenTime = Time.time;
            _ambushTimer = 0f;//接敵できたので忍耐をリセット(次に見失ってからまた6秒だけ忍ぶ)
            _hasFlankGoal = false;//回り込み計画も破棄
            _flankRolled = false;//次の見失いエピソードで再抽選できるようにする
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
        return !Physics2D.CircleCast(a_fireOrigin, _aiData.ShotClearanceRadius, diff.normalized, diff.magnitude, _wallLayerMask);
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
        for (int i = 0; i < ALL_TACTICS.Length; i++)
        {
            _tacticScores[i] = ScoreTactic(ALL_TACTICS[i]);
        }

        int bestIndex = _policy != null ? ChooseByPolicy() : ChooseByHandScores();

        //v4の教師データ収集: この判断(観測→選択)を記録する(学習方策で動作中は記録しない)
        if (TacticLogger.Enabled && _policy == null)
        {
            LogDecision(bestIndex);
        }

        Tactic best = ALL_TACTICS[bestIndex];
        if (best != _tactic)
        {
            EnterTactic(best);
        }
    }

    //手書きの効用: 生スコア + 維持ボーナスの argmax
    private int ChooseByHandScores()
    {
        int bestIndex = System.Array.IndexOf(ALL_TACTICS, _tactic);
        float bestScore = float.MinValue;
        for (int i = 0; i < ALL_TACTICS.Length; i++)
        {
            float adjusted = ALL_TACTICS[i] == _tactic ? _tacticScores[i] + _aiData.TacticKeepBonus : _tacticScores[i];
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
        int retreatIndex = System.Array.IndexOf(ALL_TACTICS, Tactic.Retreat);
        if (IsPlayerInvincible() && _tacticScores[retreatIndex] > 0f)
        {
            return retreatIndex;
        }

        _policy.Score(BuildObservation(), _policyScores);
        int bestIndex = -1;
        float bestScore = float.MinValue;
        for (int i = 0; i < ALL_TACTICS.Length; i++)
        {
            //有効な戦術か、現在の戦術(維持は常に許可=手書き脳のヒステリシスと同じ)だけを候補にする
            bool selectable = _tacticScores[i] > 0f || ALL_TACTICS[i] == _tactic;
            if (selectable && _policyScores[i] > bestScore)
            {
                bestScore = _policyScores[i];
                bestIndex = i;
            }
        }
        //万一どれも選べない場合は現在の戦術を維持する
        return bestIndex >= 0 ? bestIndex : System.Array.IndexOf(ALL_TACTICS, _tactic);
    }

    //現在の判断を「観測18次元 + 有効マスク7 + 選ばれた戦術」としてTacticLoggerへ渡す
    private void LogDecision(int a_chosenIndex)
    {
        bool[] valid = new bool[ALL_TACTICS.Length];
        for (int i = 0; i < ALL_TACTICS.Length; i++)
        {
            valid[i] = _tacticScores[i] > 0f;//手書きの発動可能条件(不可なら0点)
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
                //位置の心当たりが無いときの基本行動
                return _hasContact ? 5f : 30f;

            case Tactic.Approach:
                if (!_canSeePlayer || invincible || distance <= _aiData.AttackRange)
                {
                    return 0f;
                }
                return 40f;

            case Tactic.Assault:
                if (!_canSeePlayer || invincible || distance <= _aiData.AttackRange * 0.8f)
                {
                    return 0f;
                }
                return HasAssaultBudget(distance) ? 60f : 0f;

            case Tactic.Ambush:
                //見つかっていない+心当たりあり→遮蔽伝いに忍び寄る
                if (_canSeePlayer || !_hasContact || invincible)
                {
                    return 0f;
                }
                return 55f;

            case Tactic.Attack:
                if (!_canSeePlayer || invincible || distance > _aiData.AttackRange || _data.nowEp < _data.ShotEpCost)
                {
                    return 0f;
                }
                return HasClearShot(distance) ? 80f : 0f;

            case Tactic.Feint:
                if (_isFeintRunning)
                {
                    return 95f;//実行中は完走する(離脱の緊急時のみ中断)
                }
                if (_canSeePlayer || !_hasContact || invincible || Time.time < _feintCooldownUntil)
                {
                    return 0f;
                }
                //「直前まで見られていた」直後だけ、囮の残像に意味がある
                if (Time.time - _lastSeenTime > CONTACT_FRESH_DURATION)
                {
                    return 0f;
                }
                if (Random.value >= _aiData.FeintChance)
                {
                    _feintCooldownUntil = Time.time + FEINT_FAILED_ROLL_COOLDOWN;//外れたらしばらく再抽選しない
                    return 0f;
                }
                return 70f;

            case Tactic.Retreat:
                if (invincible)
                {
                    return 100f;//無敵中の相手に付き合わず即離脱(カウンター回避)
                }
                if (_tactic == Tactic.Retreat)
                {
                    //応戦: 振り切れないまま一定時間が経ち、最低限のEPが戻っているなら、逃げに徹するのをやめて戦う
                    bool canFightBack = _data.nowEp >= _data.ShotEpCost * _aiData.FightBackShotCount;
                    if (_retreatSeenTimer >= _aiData.CorneredTime && canFightBack)
                    {
                        return 0f;
                    }
                    //応戦条件を満たさない間は、十分回復するまで離脱を続ける
                    return _data.nowEp < _data.MaxEp * _aiData.RecoverEpRate ? 70f : 0f;
                }
                //離脱への突入条件
                if (_data.nowEp < _data.ShotEpCost)
                {
                    return 90f;
                }
                return _data.nowEp < _data.MaxEp * LOW_EP_RETREAT_RATE ? 70f : 0f;
        }
        return 0f;
    }

    private void EnterTactic(Tactic a_tactic)
    {
        _tactic = a_tactic;
        _mover.Clear();
        _data.moveInput = Vector2.zero;

        switch (a_tactic)
        {
            case Tactic.Search:
                _hasRoamGoal = false;
                break;
            case Tactic.Attack:
                _shotTimer = _aiData.ShotInterval;//突入時に即1発(通常版と同じ意図的な仕様)
                _strafeTimer = 0f;
                _strafeSign = Random.value < 0.5f ? -1 : 1;
                _strafeDashing = false;
                break;
            case Tactic.Feint:
                BeginFeint();
                break;
            case Tactic.Retreat:
                _hasRetreatGoal = false;
                _headingToHidingSpot = false;
                _retreatSeenTimer = 0f;
                break;
        }
    }

    private void TickTactic()
    {
        switch (_tactic)
        {
            case Tactic.Search: TickSearch(); break;
            case Tactic.Approach: TickApproach(); break;
            case Tactic.Assault: TickAssault(); break;
            case Tactic.Ambush: TickAmbush(); break;
            case Tactic.Attack: TickAttack(); break;
            case Tactic.Feint: TickFeint(); break;
            case Tactic.Retreat: TickRetreat(); break;
        }
    }

    //---- 各戦術の実行 ----

    private void TickSearch()
    {
        if (!_hasRoamGoal || _mover.IsFinished)
        {
            _roamGoal = MapGenerator.Instance.GetRandomWalkableCell();
            _hasRoamGoal = true;
        }
        _data.moveInput = _mover.GetMoveInput(_enemy.CenterPosition, _roamGoal, null, 0);
    }

    //逃げる相手の未来位置へ先回りする目標セル(視線が通っている時のみ速度が読める)
    private Vector2Int GetPursuitGoalCell()
    {
        Vector2 playerPosition = Player.PlayerTransform.position;
        if (!_canSeePlayer)
        {
            return _mapData.WorldToCell(_lastKnownPlayerPos);
        }

        Vector2 predicted = playerPosition + _observedPlayerVelocity * _aiData.PursuitLeadTime;
        Vector2Int predictedCell = _mapData.WorldToCell(predicted);
        return MapGenerator.Instance.IsWalkable(predictedCell) ? predictedCell : _mapData.WorldToCell(playerPosition);
    }

    private void TickApproach()
    {
        Vector2 move = _mover.GetMoveInput(_enemy.CenterPosition, GetPursuitGoalCell(), null, 0);

        //視線が通っている(=撃たれ得る)間はジグザグで狙いを絞らせない
        if (_canSeePlayer && move.sqrMagnitude > 0.01f)
        {
            Vector2 side = Vector2.Perpendicular(move);
            float wave = Mathf.Sin(Time.time * _aiData.WeaveFrequency * 2f * Mathf.PI);
            move = (move + side * (wave * _aiData.WeaveStrength)).normalized;
        }
        _data.moveInput = move;

        //最後の心当たり位置に着いても見つからなければ見失った
        if (!_canSeePlayer && _mover.IsFinished)
        {
            _hasContact = false;
        }
    }

    private void TickAssault()
    {
        //EP会計は採点側で済んでいるので、ここでは全力で先回り位置へ距離を詰める
        _data.isDashing = true;
        _data.moveInput = _mover.GetMoveInput(_enemy.CenterPosition, GetPursuitGoalCell(), null, 0);
    }

    private void TickAmbush()
    {
        _ambushTimer += Time.deltaTime;
        TryPickFlankGoal();

        //回り込み中はまず側面の経由地へ、着いたら本来の目標へ(接近ルートを読まれにくくする)
        Vector2Int viewerCell = _mapData.WorldToCell(_lastKnownPlayerPos);
        Vector2Int goalCell = _hasFlankGoal ? _flankGoal : viewerCell;

        //最後に確認した位置から見えないセルを縫って接近する。
        //ただし目標が射程内に迫ったら詰めは大胆に直行し、時間をかけすぎた場合も隠密を諦めて直行する(臆病さの上限)
        float distanceToGoal = Vector3.Distance(transform.position, _lastKnownPlayerPos);
        bool sneaking = distanceToGoal > _aiData.AttackRange && _ambushTimer < _aiData.AmbushPatience;

        _data.moveInput = sneaking
            ? _mover.GetMoveInput(_enemy.CenterPosition, goalCell, viewerCell, _aiData.SeenCellCost)
            : _mover.GetMoveInput(_enemy.CenterPosition, goalCell, null, 0);

        if (_hasFlankGoal && _mover.IsFinished)
        {
            _hasFlankGoal = false;//経由地に到達。次は本来の目標へ
        }

        //露出区間(相手側から見通せる場所)にいる間は、EPに余裕があればダッシュで駆け抜けて置き撃ちの猶予を与えない
        bool exposed = MapGenerator.Instance.IsCellLineClear(_mapData.WorldToCell(transform.position), viewerCell);
        _data.isDashing = exposed && _data.nowEp > _data.MaxEp * _aiData.CrossingDashMinEpRate;

        if (!_canSeePlayer && !_hasFlankGoal && _mover.IsFinished)
        {
            _hasContact = false;//着いても居なければ見失った
        }
    }

    //確率で「側面へ回り込む」経由地を選ぶ(見失いエピソードごとに1回だけ抽選)。直行ルートを読んで置き撃ちする戦法への対抗
    private void TryPickFlankGoal()
    {
        if (_flankRolled || _hasFlankGoal)
        {
            return;
        }
        _flankRolled = true;
        if (Random.value >= _aiData.FlankChance)
        {
            return;
        }

        Vector2 myPosition = transform.position;
        Vector2 toGoal = (Vector2)_lastKnownPlayerPos - myPosition;
        if (toGoal.sqrMagnitude < 1f)
        {
            return;
        }

        Vector2 side = Vector2.Perpendicular(toGoal.normalized);
        Vector2 midpoint = myPosition + toGoal * 0.5f;
        float firstSign = Random.value < 0.5f ? 1f : -1f;
        for (int i = 0; i < 2; i++)
        {
            float sign = i == 0 ? firstSign : -firstSign;
            Vector2Int cell = _mapData.WorldToCell(midpoint + side * (_aiData.FlankOffset * sign));
            if (MapGenerator.Instance.IsWalkable(cell))
            {
                _flankGoal = cell;
                _hasFlankGoal = true;
                return;
            }
        }
    }

    private void TickAttack()
    {
        float distance = DistanceToPlayer();
        TickStrafe(distance);

        _shotTimer += Time.deltaTime;
        if (_shotTimer >= _aiData.ShotInterval && _data.nowEp >= _data.ShotEpCost)
        {
            _shotTimer = 0f;
            Vector2 fireOrigin = _enemy.CenterPosition;
            Vector2 direction = (PredictAimPosition(fireOrigin) - fireOrigin).normalized;
            _data.nowEp -= _data.ShotEpCost;
            BulletManager.Instance.GetEnemyNormal(fireOrigin, direction);
        }
    }

    //偏差射撃: 弾の到達時間ぶんだけプレイヤーの移動先を予測して狙う。
    //単純な線形予測+リード率で不完全にしてあり、進行方向を変えれば外れる(公平性: 追尾はしない)
    private Vector2 PredictAimPosition(Vector2 a_fireOrigin)
    {
        Vector2 playerPosition = Player.PlayerTransform.position;
        if (_bulletData == null)
        {
            return playerPosition;
        }

        float travelTime = Vector2.Distance(a_fireOrigin, playerPosition) / _bulletData.Speed;
        Vector2 predicted = playerPosition + _observedPlayerVelocity * (travelTime * _aiData.AimLeadRate);
        //偏差先が壁の向こうなら偏差を諦めて直射する。狙いの甘さではなく「壁に撃つ」のは
        //ただEPを捨てるだけで駆け引きにならないため(偏差量は距離に比例するので遠距離型ほど起きやすい)
        return IsShotPathClear(a_fireOrigin, predicted) ? predicted : playerPosition;
    }

    //撃ちながらの横移動。切り替えごとにダッシュを混ぜるか抽選し、リズムを不規則にする
    private void TickStrafe(float a_distance)
    {
        _strafeTimer += Time.deltaTime;
        if (_strafeTimer >= _aiData.StrafeSwitchInterval)
        {
            _strafeTimer = 0f;
            _strafeSign = Random.value < 0.5f ? -1 : 1;
            _strafeDashing = Random.value < _aiData.StrafeDashChance;
        }

        Vector2 toPlayer = ((Vector2)Player.PlayerTransform.position - (Vector2)transform.position).normalized;
        Vector2 side = Vector2.Perpendicular(toPlayer) * _strafeSign;

        //射程の8割より遠ければ寄り、4割より近ければ離れる
        Vector2 radial = Vector2.zero;
        if (a_distance > _aiData.AttackRange * 0.8f)
        {
            radial = toPlayer;
        }
        else if (a_distance < _aiData.AttackRange * 0.4f)
        {
            radial = -toPlayer;
        }

        _data.moveInput = (side + radial).normalized;
        _data.isDashing = _strafeDashing && _data.nowEp > _data.MaxEp * _aiData.StrafeDashMinEpRate;
    }

    private void BeginFeint()
    {
        _isFeintRunning = true;
        _feintTimer = 0f;
        _hiddenEpisodeUsedFeint = true;//このエピソードの隠密時間は「フェイントあり」として集計される
        if (MatchMetrics.Instance != null)
        {
            MatchMetrics.Instance.CountFeintAttempt();
        }

        //囮方向: プレイヤー方向に対する左右どちらか(いかにも回り込みそうな向きを残像で見せる)
        Vector2 toPlayer = ((Vector2)_lastKnownPlayerPos - (Vector2)transform.position).normalized;
        Vector2 side = Vector2.Perpendicular(toPlayer) * (Random.value < 0.5f ? 1f : -1f);
        _feintDashDirection = side;

        //本当の行き先: 囮と反対側のセル(歩けない場所ならランダムで代替)
        Vector3 walkTarget = transform.position - (Vector3)(side * 60f);
        Vector2Int walkCell = _mapData.WorldToCell(walkTarget);
        if (!MapGenerator.Instance.IsWalkable(walkCell))
        {
            walkCell = MapGenerator.Instance.GetRandomWalkableCell();
        }
        _feintWalkGoal = walkCell;
    }

    private void TickFeint()
    {
        _feintTimer += Time.deltaTime;

        if (_feintTimer <= _aiData.FeintDashDuration)
        {
            //囮ダッシュ: 視界外でも見える残像だけを相手に見せる
            _data.isDashing = true;
            _data.moveInput = _feintDashDirection;
            return;
        }
        if (_feintTimer <= _aiData.FeintDashDuration + _aiData.FeintWalkDuration)
        {
            //残像を出さない歩きで、本当の方向へ隠密移動
            _data.moveInput = _mover.GetMoveInput(_enemy.CenterPosition, _feintWalkGoal, _mapData.WorldToCell(_lastKnownPlayerPos), _aiData.SeenCellCost);
            return;
        }

        if (_isFeintRunning)
        {
            _isFeintRunning = false;
            _feintCooldownUntil = Time.time + _aiData.FeintCooldown;
        }
    }

    private void TickRetreat()
    {
        //視線が通ったままの時間を計る(途切れたらリセット=振り切れた)
        _retreatSeenTimer = _canSeePlayer ? _retreatSeenTimer + Time.deltaTime : 0f;
        _hidingRepickTimer += Time.deltaTime;

        //計画の判断にはデバウンス済みの「見られている」を使う(生の判定の明滅に釣られて右往左往しない)
        if (_seenDebounced)
        {
            //見つかっている: 深い死角へ走り込む。着いたのにまだ見られている場合の選び直しは一定間隔まで
            bool needRepick = !_headingToHidingSpot || (_mover.IsFinished && _hidingRepickTimer >= HIDING_REPICK_INTERVAL);
            if (needRepick)
            {
                PickHidingSpot();
                _headingToHidingSpot = true;
                _hidingRepickTimer = 0f;
            }
        }
        else if (_headingToHidingSpot && _mover.IsFinished)
        {
            //走り込み完了(視界も切れた)。ここから隠れ待機へ
            _headingToHidingSpot = false;
            _hasRetreatGoal = false;
        }

        //走り込み中は、視界が切れても着くまで計画を変えない(視界境界での右往左往防止)。
        //移動していない瞬間はダッシュを立てない(その場でEPを燃やすだけ)
        if (_headingToHidingSpot)
        {
            _data.moveInput = _mover.GetMoveInput(_enemy.CenterPosition, _retreatGoal, null, 0);
            _data.isDashing = !_mover.IsFinished;
            return;
        }

        //隠れた後: 現在地が死角で相手の気配も遠いなら、動かずにEPを回復して次の手を待つ
        Vector2Int myCell = _mapData.WorldToCell(transform.position);
        Vector2Int viewerCell = _mapData.WorldToCell(_lastKnownPlayerPos);
        bool inCover = !MapGenerator.Instance.IsCellLineClear(viewerCell, myCell);
        bool threatNear = Vector3.Distance(transform.position, _lastKnownPlayerPos) <= _aiData.RelocateDistance;

        if (inCover && !threatNear)
        {
            _data.isDashing = false;
            _data.moveInput = Vector2.zero;
            _hasRetreatGoal = false;//次に動くときは目的地を選び直す
            return;
        }

        //露出している、または相手が近づいてきた: 歩きの隠密経路で距離を取り直す
        if (!_hasRetreatGoal || _mover.IsFinished)
        {
            _retreatGoal = PickFarCell();
            _hasRetreatGoal = true;
        }
        _data.isDashing = false;
        _data.moveInput = _mover.GetMoveInput(_enemy.CenterPosition, _retreatGoal, viewerCell, _aiData.SeenCellCost);
    }

    //境界すれすれではない深めの死角を選ぶ(見つからなければ遠くのセルで妥協)
    private void PickHidingSpot()
    {
        Vector2Int myCell = _mapData.WorldToCell(transform.position);
        Vector2Int viewerCell = _mapData.WorldToCell(Player.PlayerTransform.position);

        //まず危険地帯(撃たれた場所・射線)を避けて探し、無ければ危険を許容して探し直す
        if (!MapGenerator.Instance.TryFindNearestHiddenCell(myCell, viewerCell, out _retreatGoal, _dangerMemory.IsDangerous)
            && !MapGenerator.Instance.TryFindNearestHiddenCell(myCell, viewerCell, out _retreatGoal))
        {
            _retreatGoal = PickFarCell();
        }
        _hasRetreatGoal = true;
    }

    //候補セルの中からプレイヤーの最終確認位置に最も遠いセルを選ぶ(危険地帯でない候補を優先)
    private Vector2Int PickFarCell()
    {
        Vector2Int bestAny = MapGenerator.Instance.GetRandomWalkableCell();
        float bestAnyDistance = -1f;
        Vector2Int bestSafe = bestAny;
        float bestSafeDistance = -1f;

        for (int i = 0; i < RETREAT_CANDIDATE_COUNT; i++)
        {
            Vector2Int candidate = MapGenerator.Instance.GetRandomWalkableCell();
            float distance = Vector3.Distance(_mapData.CellToWorldPosition(candidate.x, candidate.y), _lastKnownPlayerPos);
            if (distance > bestAnyDistance)
            {
                bestAnyDistance = distance;
                bestAny = candidate;
            }
            if (distance > bestSafeDistance && !_dangerMemory.IsDangerous(candidate))
            {
                bestSafeDistance = distance;
                bestSafe = candidate;
            }
        }
        return bestSafeDistance >= 0f ? bestSafe : bestAny;
    }

    //---- 弾回避(観測時間ベース) ----

    private void ApplyBulletDodge()
    {
        _dodgeSensor.Observe(transform.position);

        //初めて視認した弾の射線を「見張られている通路」として記憶し、以後の経路計算で避ける
        foreach (Bullet bullet in _dodgeSensor.NewlySeenBullets)
        {
            _dangerMemory.MarkBulletLane(bullet.transform.position, bullet.Velocity, _aiData.BulletDetectRange);
        }

        if (_dodgeSensor.TryGetDodgeDirection(transform.position, out Vector2 dodgeDirection))
        {
            _data.moveInput = (_data.moveInput + dodgeDirection * _aiData.DodgeStrength).normalized;
        }
    }
}
