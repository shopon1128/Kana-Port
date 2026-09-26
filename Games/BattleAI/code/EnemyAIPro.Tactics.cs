using UnityEngine;

/// <summary>
/// EnemyAIProのうち「選んだ戦術の実行」を担う部分。
/// 戦術に入るとき(EnterTactic)の初期化と、毎物理フレームの移動入力・射撃(Tick*)をまとめる
/// </summary>
public partial class EnemyAIPro
{
    private const int RETREAT_CANDIDATE_COUNT = 20;//離脱先の候補セル数
    private const float HIDING_REPICK_INTERVAL = 0.6f;//隠れ場所を選び直す最短間隔(毎フレーム選び直すと向きが揺れ続ける)
    private const float FEINT_WALK_DISTANCE = 60f;//フェイントで本当に向かう先までの距離(囮と反対側、ワールド単位)
    private const float FLANK_MIDPOINT_RATE = 0.5f;//回り込みの経由地を、目標までの道のりのどこに取るか
    private const float MIN_FLANK_DISTANCE_SQR = 1f;//目標がこれより近ければ回り込まない(方向が定まらないため)

    //索敵
    private Vector2Int _roamGoal;
    private bool _hasRoamGoal;

    //隠密接近
    private float _ambushTimer;//隠密接近の経過時間(忍耐の期限判定用)
    private Vector2Int _flankGoal;//回り込みの経由地
    private bool _hasFlankGoal;
    private bool _flankRolled;//このエピソード(見失い区間)で回り込みの抽選を済ませたか

    //攻撃
    private float _shotTimer;
    private float _strafeTimer;
    private int _strafeSign = 1;
    private bool _strafeDashing;

    //フェイント
    private float _feintTimer;
    private Vector2 _feintDashDirection;
    private Vector2Int _feintWalkGoal;
    private bool _isFeintRunning;
    private float _feintCooldownUntil;

    //離脱
    private Vector2Int _retreatGoal;
    private bool _hasRetreatGoal;
    private bool _headingToHidingSpot;//隠れ場所へ走り込んでいる最中か(着くまで計画を変えないコミット用)
    private float _retreatSeenTimer;//離脱中、視線が通ったままの経過時間(振り切れない判定用)
    private float _hidingRepickTimer;//隠れ場所を選び直してからの経過時間

    //プレイヤーを視認できたときに、見失い中だけ使う計画をリセットする
    private void OnPlayerSighted()
    {
        _ambushTimer = 0f;//接敵できたので忍耐をリセット(次に見失ってからまた期限いっぱい忍ぶ)
        _hasFlankGoal = false;//回り込み計画も破棄
        _flankRolled = false;//次の見失いエピソードで再抽選できるようにする
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
                _strafeSign = AiSteering.RandomSign();
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

    //経路探索で目的地へ向かう(隠密なし)
    private Vector2 MoveToward(Vector2Int a_goal)
    {
        return _mover.GetMoveInput(_enemy.CenterPosition, a_goal, null, 0);
    }

    //経路探索で目的地へ向かう。a_viewerCellから見えるセルを避ける隠密経路にする
    private Vector2 SneakToward(Vector2Int a_goal, Vector2Int a_viewerCell)
    {
        return _mover.GetMoveInput(_enemy.CenterPosition, a_goal, a_viewerCell, _aiData.SeenCellCost);
    }

    //---- 索敵 ----

    private void TickSearch()
    {
        if (!_hasRoamGoal || _mover.IsFinished)
        {
            _roamGoal = MapGenerator.Instance.GetRandomWalkableCell();
            _hasRoamGoal = true;
        }
        _data.moveInput = MoveToward(_roamGoal);
    }

    //---- 接近・強襲 ----

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
        Vector2 move = MoveToward(GetPursuitGoalCell());

        //視線が通っている(=撃たれ得る)間はジグザグで狙いを絞らせない
        if (_canSeePlayer && move.sqrMagnitude > AiSteering.MIN_MOVE_SQR_MAGNITUDE)
        {
            move = AiSteering.Weave(move, _aiData.WeaveFrequency, _aiData.WeaveStrength);
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
        _data.moveInput = MoveToward(GetPursuitGoalCell());
    }

    //---- 隠密接近 ----

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

        _data.moveInput = sneaking ? SneakToward(goalCell, viewerCell) : MoveToward(goalCell);

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
        if (toGoal.sqrMagnitude < MIN_FLANK_DISTANCE_SQR)
        {
            return;
        }

        //左右どちらかを先に試し、歩けなければ反対側を試す
        Vector2 side = Vector2.Perpendicular(toGoal.normalized);
        Vector2 midpoint = myPosition + toGoal * FLANK_MIDPOINT_RATE;
        int firstSign = AiSteering.RandomSign();
        foreach (int sign in new[] { firstSign, -firstSign })
        {
            Vector2Int cell = _mapData.WorldToCell(midpoint + side * (_aiData.FlankOffset * sign));
            if (MapGenerator.Instance.IsWalkable(cell))
            {
                _flankGoal = cell;
                _hasFlankGoal = true;
                return;
            }
        }
    }

    //---- 攻撃 ----

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
            _strafeSign = AiSteering.RandomSign();
            _strafeDashing = Random.value < _aiData.StrafeDashChance;
        }

        Vector2 toPlayer = ((Vector2)Player.PlayerTransform.position - (Vector2)transform.position).normalized;
        _data.moveInput = AiSteering.Strafe(toPlayer, _strafeSign, a_distance, _aiData.AttackRange);
        _data.isDashing = _strafeDashing && _data.nowEp > _data.MaxEp * _aiData.StrafeDashMinEpRate;
    }

    //---- フェイント ----

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
        Vector2 side = Vector2.Perpendicular(toPlayer) * AiSteering.RandomSign();
        _feintDashDirection = side;

        //本当の行き先: 囮と反対側のセル(歩けない場所ならランダムで代替)
        Vector3 walkTarget = transform.position - (Vector3)(side * FEINT_WALK_DISTANCE);
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
            _data.moveInput = SneakToward(_feintWalkGoal, _mapData.WorldToCell(_lastKnownPlayerPos));
            return;
        }

        if (_isFeintRunning)
        {
            _isFeintRunning = false;
            _feintCooldownUntil = Time.time + _aiData.FeintCooldown;
        }
    }

    //---- 離脱 ----

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
            _data.moveInput = MoveToward(_retreatGoal);
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
        _data.moveInput = SneakToward(_retreatGoal, viewerCell);
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
