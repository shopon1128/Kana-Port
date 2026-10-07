using System.Collections;
using UnityEngine;

/// <summary>
/// プレイヤーと敵に共通する「体」。移動・ダッシュ・EP・被弾(スタン+無敵)・やられ演出・歩行アニメを受け持つ。
/// 何をするかは入力値(CharaData.moveInput / isDashing)で受け取り、入力を書くのは派生クラスや頭脳(AI・ボット)の役目
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public abstract class Character : MonoBehaviour
{
    private const float DASH_TRAIL_INTERVAL = 0.05f;//残像を生成する間隔(秒)
    private const float DASH_TRAIL_FADE_DURATION = 0.3f;//残像が消えるまでの時間(秒)
    private const int DASH_TRAIL_SORTING_ORDER = 2;//フォグ(1)より手前・本体(3)より奥に描画し、視界外でも見えるようにする
    private const float STUN_DURATION = 0.5f;//被弾時のスタン時間(秒)
    private const float INVINCIBLE_DURATION_AFTER_STUN = 1f;//スタン解除後の無敵時間(秒)
    private const float STUN_SHAKE_AMPLITUDE = 1.5f;//スタン中の震え幅(ワールド単位)
    private const float INVINCIBLE_BLINK_SPEED = 4f;//無敵中の点滅速度
    private const float INVINCIBLE_MIN_ALPHA = 0.5f;//点滅時の最小不透明度
    private const float DEATH_BLINK_SPEED = 12f;//やられ演出の点滅速度(回/秒)
    private const float DEATH_BLINK_DIM_ALPHA = 0.3f;//やられ点滅の暗い側の不透明度
    private const float MAX_STEP_DELTA_TIME = 1f / 30f;//1フレームの経過時間の上限(タイムスケール変更直後の1フレームだけ演出が飛ぶのを防ぐ)
    private static readonly Color DEFAULT_TRAIL_COLOR = new(1f, 1f, 1f, 0.6f);//派生クラスが色を指定しない場合の残像色

    /// <summary>移動入力の各軸がこれ以下なら「その軸には動いていない」とみなす</summary>
    protected const float MOVE_AXIS_EPSILON = 0.01f;
    /// <summary>移動入力の二乗長がこれ未満なら「止まっている」とみなす</summary>
    protected const float MIN_MOVE_SQR_MAGNITUDE = 0.01f;

    [SerializeField] protected CharaData _data;
    [SerializeField] protected PlAnimeData _animeData;
    protected Rigidbody2D _rb;
    protected SpriteRenderer _spriteRenderer;
    protected Collider2D _collider;
    protected Vector2 _facingDirection = Vector2.down;//現在向いている方向
    protected bool _facingHorizontal;//向きの軸が横かどうか
    private bool _isDead;
    private float _dashTrailTimer;
    private float _stunTimer;
    private float _invincibleTimer;
    private Vector2 _stunCenter;//スタン開始位置(震えの基準点)
    private PlAnimeData.WalkSprites _currentWalk;
    private float _animTimer;
    private bool _isFootR;

    public CharaData Data => _data;
    public bool IsStunned => _stunTimer > 0f;
    public bool IsInvincible => _invincibleTimer > 0f;
    public bool IsDead => _isDead;

    //コライダーの中心位置。壁密着時でも弾がめり込まないよう、弾はここから発射する
    //(コライダーにはオフセットがあり、transform.positionは壁との間に弾の半径分の余裕を保証できない)
    public Vector2 CenterPosition => _collider != null ? _collider.bounds.center : transform.position;

    //ダッシュ残像の色(派生クラスで自陣営の色に差し替える)
    protected virtual Color DashTrailColor => DEFAULT_TRAIL_COLOR;

    protected virtual void Init()
    {
        _data.SetUp();
        _rb = GetComponent<Rigidbody2D>();
        _spriteRenderer = GetComponent<SpriteRenderer>();
        _collider = GetComponent<Collider2D>();

        if (_animeData != null && _spriteRenderer != null)
        {
            _currentWalk = _animeData.down;
            _facingDirection = Vector2.down;
            _spriteRenderer.sprite = _currentWalk.stop;
        }
    }

    void Start()
    {
        Init();
    }

    void FixedUpdate()
    {
        if (_isDead)
        {
            //やられ演出中は一切の行動・状態更新を止める
            return;
        }
        if (GameState.IsGameDecided)
        {
            //決着後は勝者側も立ち止まる
            _rb.linearVelocity = Vector2.zero;
            return;
        }

        UpdateStunAndInvincibility();
        if (IsStunned)
        {
            //スタン中は移動・ダッシュ・EP回復を含め一切行動できない
            return;
        }

        if (_data.isDashing)
        {
            //EPが切れてもダッシュは継続できるが、倍率が下がる(EP自然回復は止まったまま)
            bool hasEp = _data.nowEp > 0f;
            _data.nowEp = Mathf.Max(_data.nowEp - _data.DashEpCostPerSecond * Time.fixedDeltaTime, 0f);
            _data.nowSpeed = _data.Speed * (hasEp ? _data.DashSpeedMultiplier : _data.ExhaustedDashSpeedMultiplier);
        }
        else
        {
            _data.nowEp = Mathf.Min(_data.nowEp + _data.EpRegenRate * Time.fixedDeltaTime, _data.MaxEp);
            _data.nowSpeed = _data.Speed;
        }

        //壁との接触で物理エンジンに速度を削られても、毎物理フレーム入力値から速度を再設定することで復帰させる
        Move();

        UpdateDashTrail(_data.isDashing);
    }

    private void Move()
    {
        _rb.linearVelocity = _data.moveInput * _data.nowSpeed;
    }

    /// <summary>
    /// ダメージを与える。無敵中などで通らなかった場合はfalseを返す
    /// </summary>
    public bool ApplyDamage(float a_damage)
    {
        if (_isDead || IsInvincible)
        {
            return false;
        }

        _data.nowHp -= a_damage;

        //被弾音も命中の記録と同じく被弾側で鳴らす(プレイヤー/敵のどちらが受けても1か所で済む)
        AudioManager.PlaySE(AudioManager.SEType.Damage);

        //命中の記録は被弾側で行う(弾側で数えるとDie→リザルトのログ出力が先に走り、決着の一撃が漏れる)
        if (MatchMetrics.Instance != null)
        {
            MatchMetrics.Instance.CountHit(this is Player);
        }

        if (_data.nowHp <= 0f)
        {
            _data.nowHp = 0f;
            _isDead = true;
            Die();
            return true;
        }

        //被弾リアクション開始(スタン+無敵)
        _stunTimer = STUN_DURATION;
        _invincibleTimer = STUN_DURATION + INVINCIBLE_DURATION_AFTER_STUN;
        _stunCenter = _rb.position;
        _rb.linearVelocity = Vector2.zero;
        return true;
    }
    /// <summary>HPが0になった瞬間に呼ばれる(勝敗の通知は陣営ごとに異なるので派生クラスが行う)</summary>
    protected abstract void Die();

    /// <summary>
    /// やられ演出(高速点滅しながらフェードアウト)を開始する。スローモーション中でも実時間で進む
    /// </summary>
    public void PlayDeathEffect(float a_duration)
    {
        _rb.linearVelocity = Vector2.zero;

        //視界外で倒れた場合でも演出が見えるよう、視界による非表示を解除する
        if (TryGetComponent(out VisibilityByLineOfSight visibility))
        {
            visibility.enabled = false;
        }
        if (_spriteRenderer != null)
        {
            _spriteRenderer.enabled = true;
            StartCoroutine(DeathEffectRoutine(a_duration));
        }
    }

    private IEnumerator DeathEffectRoutine(float a_duration)
    {
        float timer = 0f;
        while (timer < a_duration)
        {
            timer += Mathf.Min(Time.unscaledDeltaTime, MAX_STEP_DELTA_TIME);

            //徐々に薄くなる包絡線 × 高速点滅
            float envelope = Mathf.Clamp01(1f - timer / a_duration);
            bool isBrightPhase = Mathf.Repeat(Time.unscaledTime * DEATH_BLINK_SPEED, 1f) < 0.5f;
            Color color = _spriteRenderer.color;
            color.a = envelope * (isBrightPhase ? 1f : DEATH_BLINK_DIM_ALPHA);
            _spriteRenderer.color = color;
            yield return null;
        }

        Color finalColor = _spriteRenderer.color;
        finalColor.a = 0f;
        _spriteRenderer.color = finalColor;

        //本体が完全に消えたら、影も一緒に消す
        foreach (SpriteRenderer child in GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (child != _spriteRenderer)
            {
                child.enabled = false;
            }
        }
    }

    private void UpdateStunAndInvincibility()
    {
        if (_stunTimer > 0f)
        {
            _stunTimer -= Time.fixedDeltaTime;

            //基準点の周りで小刻みに揺らして被弾を表現する
            _rb.linearVelocity = Vector2.zero;
            _rb.position = _stunCenter + Random.insideUnitCircle * STUN_SHAKE_AMPLITUDE;
            if (_stunTimer <= 0f)
            {
                _rb.position = _stunCenter;
            }
        }

        if (_invincibleTimer > 0f)
        {
            _invincibleTimer -= Time.fixedDeltaTime;
            if (_spriteRenderer != null)
            {
                //無敵中は不透明度を最小値〜1の間で点滅させる。終了時に1へ戻す
                Color color = _spriteRenderer.color;
                color.a = _invincibleTimer > 0f
                    ? INVINCIBLE_MIN_ALPHA + Mathf.PingPong(Time.time * INVINCIBLE_BLINK_SPEED, 1f - INVINCIBLE_MIN_ALPHA)
                    : 1f;
                _spriteRenderer.color = color;
            }
        }
    }

    /// <summary>
    /// 歩行アニメーション。_facingHorizontalの軸を優先しつつ、移動入力に合わせてスプライトを切り替える
    /// </summary>
    protected void Animate()
    {
        if (_animeData == null || _spriteRenderer == null)
        {
            return;
        }

        Vector2 input = _data.moveInput;
        if (input.sqrMagnitude < MIN_MOVE_SQR_MAGNITUDE)
        {
            _animTimer = 0f;
            _isFootR = false;
            _spriteRenderer.sprite = _currentWalk.stop;
            return;
        }

        //向きの軸が動いていない場合は、動いているもう一方の軸を向く
        bool hasX = Mathf.Abs(input.x) > MOVE_AXIS_EPSILON;
        bool hasY = Mathf.Abs(input.y) > MOVE_AXIS_EPSILON;
        bool useHorizontal = (_facingHorizontal && hasX) || !hasY;

        _currentWalk = useHorizontal
            ? (input.x > 0 ? _animeData.right : _animeData.left)
            : (input.y > 0 ? _animeData.up : _animeData.down);
        _facingDirection = useHorizontal
            ? (input.x > 0 ? Vector2.right : Vector2.left)
            : (input.y > 0 ? Vector2.up : Vector2.down);

        _animTimer += Time.deltaTime;
        if (_animTimer >= _animeData.animInterval)
        {
            _animTimer = 0f;
            _isFootR = !_isFootR;
        }

        _spriteRenderer.sprite = _isFootR ? _currentWalk.footR : _currentWalk.footL;
    }

    private void UpdateDashTrail(bool a_isDashing)
    {
        if (!a_isDashing || _spriteRenderer == null)
        {
            _dashTrailTimer = 0f;
            return;
        }

        _dashTrailTimer += Time.fixedDeltaTime;
        if (_dashTrailTimer < DASH_TRAIL_INTERVAL)
        {
            return;
        }
        _dashTrailTimer = 0f;
        SpawnDashTrailGhost();
    }

    private void SpawnDashTrailGhost()
    {
        GameObject ghost = new("DashTrailGhost");
        ghost.transform.SetPositionAndRotation(transform.position, transform.rotation);
        ghost.transform.localScale = transform.localScale;

        SpriteRenderer ghostRenderer = ghost.AddComponent<SpriteRenderer>();
        ghostRenderer.sprite = _spriteRenderer.sprite;
        ghostRenderer.flipX = _spriteRenderer.flipX;
        ghostRenderer.color = DashTrailColor;
        ghostRenderer.sortingOrder = DASH_TRAIL_SORTING_ORDER;

        ghost.AddComponent<DashTrailGhost>().Init(DASH_TRAIL_FADE_DURATION);
    }
}
