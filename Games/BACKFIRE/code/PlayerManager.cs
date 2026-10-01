using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Users;
using System.Collections;
using LitMotion;
using LitMotion.Extensions;

public class PlayerManager : MonoBehaviour
{
    [SerializeField] private int _playerId;//1P -> 1,2P -> 2
    //HP関連の変数、定数
    [SerializeField] private float PLAYER_MAX_HP = 500;//最大HP
    private float _playerHP;//現在HP
    public float PlayerMaxHP => PLAYER_MAX_HP;//外から最大HPを参照する用
    public float PlayerHP => _playerHP;//外から現在HPを参照する用

    ///<summary>
    /// プレイヤーの表情の状態を管理する列挙型
    /// </summary>
    public enum PlayerState
    {
        GENKI,//HP満タン(ゲーム開始時)
        AYASI,//HPが半分切った時
        KIKEN,//HPが25%切った時
        HAIBOKU//HPが0になった時(負け)
    }
    private PlayerState _playerState = PlayerState.GENKI;
    public PlayerState PlState => _playerState;//外から_playerStateを参照する用

    //回転関連の変数、定数
    private int _playerInputV;
    [SerializeField] private int _playerRotateSpeed = 140;//回転速度 140°/s,チャージショット中は半減
    private float _playerAngule;//プレイヤーの角度 中心0°
    private const float PLAYER_ANGULE_RANGE = 50f;//角度の振れ幅 ± 50°

    //発射関係の変数、定数
    [SerializeField] private int PLAYER_SHOT_NUM = 5;//一回のボタン入力で発射される弾の数
    [SerializeField] private float PLAYER_SHOT_DRAY = 0.075f;//連射時の一発一発の発射間隔(秒)。0.3秒で5発撃つ。
    private float _playerShotAngule;//ショットの射出角度 Shot()の引数などに使用。
    private int _playerShotZandan;//発射時にこの値を1減らす。ボタンが押された時にPLAYER_SHOT_NUMを代入し、連射を継続させる。
    private bool _playerShootingFlag;//連射中か否か。コルーチン呼び出しのフラグに使用。
    private bool _playerChargeFlag;//チャージ状態か否か
    private bool _shotHeld;//ショットボタンが今押されているか。OnShotはこの状態を記録するだけにする。
    private bool _prevShotHeld;//前フレームの_shotHeld。押した/離した瞬間の検出に使う。
    [SerializeField] private float PLAYER_CHARGE_COUNT = 0.7f;//チャージショットに必要な時間
    private float _playerChargeCount;//チャージの維持時間
    [SerializeField] private float PLAYER_SHOT_CT = 0.5f;//チャージショット後のクールタイム
    private float _playerShotCtCount;//クールタイムカウント用
    private bool _playerShotCtFlag;//クールタイムか否か
    [SerializeField] private float PLAYER_MAX_SHAKE = 0.5f;//チャージショット後の震えの最大値
    [SerializeField] private float PLAYER_SHAKE_RANGE = 2f;//反動で後ろへ行く大きさ(CT中に行って帰ってする)
    private float _playerShakeMoved = 0f;//反動でどれだけ後ろにいってるか

    // ……(抜粋外)……

    void Update()
    {
        if (MainGameManager.Instance.InGame == true)
        {
            //ショットボタンを押した瞬間・離した瞬間を算出する。
            bool shotDown = _shotHeld && !_prevShotHeld;
            bool shotUp = !_shotHeld && _prevShotHeld;
            _prevShotHeld = _shotHeld;

            //回転処理
            Rotation(_playerInputV);

            //押下・離しの処理。CT中は受け付けない。
            if (_playerShotCtFlag == false)
            {
                //押されたとき
                if (shotDown)
                {
                    Shot();
                    StartCharge();
                }
                //離されたとき
                else if (shotUp)
                {
                    StopCharge();
                    //チャージが満たされていたらチャージショット
                    if (_playerChargeCount >= PLAYER_CHARGE_COUNT)
                    {
                        ChargeShot();
                    }
                    // ……(チームメンバー担当のため省略)……
                }
            }

            //チャージ処理
            if (_playerChargeFlag)
            {
                _playerChargeCount += Time.deltaTime;
                var mainModule = _chargeEffect.main;
                if (_playerChargeCount <= PLAYER_CHARGE_COUNT)
                {
                    mainModule.startColor = CHARGE_EFFECT_COLOR;
                    //チャージが一定割合を超えてからエフェクトを出す
                    if (_chargeEffect.isPlaying == false && _playerChargeCount >= PLAYER_CHARGE_COUNT * CHARGE_EFFECT_SHOW_RATE)
                    {
                        _chargeEffect.Play();
                    }
                    // ……(チームメンバー担当のため省略)……
                }
                else
                {
                    // ……(チームメンバー担当のため省略)……
                }
            }
            else
            {
                _playerChargeCount = 0f;
                // ……(チームメンバー担当のため省略)……
            }
            //クールタイム処理
            if (_playerShotCtFlag)
            {
                if (MainGameManager.Instance.IsPaused)
                {
                    //ポーズ中は震え(乱数)を止める
                    Cursor.transform.localPosition = _cursorDefPos + new Vector3(0f, _playerShakeMoved, 0f);
                }
                else
                {
                    _playerShotCtCount += Time.deltaTime;
                    CtShake();
                    if (_playerShotCtCount >= PLAYER_SHOT_CT)
                    {
                        _playerShotCtFlag = false;
                        _playerShotCtCount = 0f;
                        Cursor.transform.localPosition = _cursorDefPos;//元の位置へ
                        _playerShakeMoved = 0f;

                        //CT中もボタンを押し続けていたらCT明けからチャージを始める。
                        if (_shotHeld)
                        {
                            StartCharge();
                        }
                    }
                }
            }
        }
        else
        {//リザルトで変にならない様にいろいろ止めたりなど
            _playerShotCtFlag = false;
            Cursor.transform.localPosition = _cursorDefPos;//元の位置へ
            StopCharge();
            //再開時に押しっぱなしが「押した瞬間」と誤検出されないよう同期しておく
            _prevShotHeld = _shotHeld;
        }
    }
    // ……(抜粋外)……

    //スティック入力の受け取り
    void OnMove(InputValue inv)
    {
        if (inv.Get<Vector2>().x != 0)
        {
            //符号取得
            _playerInputV = (int)Mathf.Sign(inv.Get<Vector2>().x);
        }
        else
        {
            _playerInputV = 0;
        }
    }

    //ボタン入力の受け取り
    //押した瞬間・離した瞬間の判定はUpdateで行う
    void OnShot(InputValue inv)
    {
        _shotHeld = inv.isPressed;
    }
    // ……(抜粋外)……

    ///<summary>
    /// CT中の震えを実現するためのメソッド
    /// </summary>
    private void CtShake()
    {
        //震えの強さ(徐々に0へ)
        float power = Mathf.Lerp(PLAYER_MAX_SHAKE, 0, _playerShotCtCount / PLAYER_SHOT_CT);

        float x = Random.Range(-1f, 1f) * power;
        float y = Random.Range(-1f, 1f) * power;

        if (_playerShotCtCount < PLAYER_SHOT_CT / 2f)
        {//後ろに下がる
            _playerShakeMoved -= PLAYER_SHAKE_RANGE * Time.deltaTime;
        }
        else
        {//戻ってくる
            _playerShakeMoved += PLAYER_SHAKE_RANGE * Time.deltaTime;
        }
        y += _playerShakeMoved;
        Cursor.transform.localPosition = _cursorDefPos + new Vector3(x, y, 0);
    }
    ///<summary>
    /// 被弾処理
    /// </summary>
    public void Damage(float damage)
    {
        // ……(チームメンバー担当のため省略)……
        //HP1より大きいときは、致命傷を受けても1耐える
        if (_playerHP > 1 && _playerHP <= damage)
        {
            _playerHP = 1;
        }
        else
        {
            _playerHP -= damage;
        }
        StateManage();
    }
    ///<summary>
    /// 状態管理とHPバー管理
    /// ゲーム開始時の初期化やHP変動時に呼び出す。
    /// </summary>
    private void StateManage()
    {

        //現在のHP割合に応じた_playerStateに変更
        if (_playerHP >= PLAYER_MAX_HP * 0.5f)
        {//50%以上
            _playerState = PlayerState.GENKI;
        }
        else if (_playerHP >= PLAYER_MAX_HP * 0.25f)
        {//25%以上
            _playerState = PlayerState.AYASI;
        }
        else if (_playerHP > 0)
        {//25%未満
            _playerState = PlayerState.KIKEN;
        }
        else
        {//0以下
            _playerState = PlayerState.HAIBOKU;
        }

        PlayerHpManager.Instance.ReflectGaugeAndFace(_playerId - 1);//顔処理 と HPゲージ反映
        // ……(チームメンバー担当のため省略)……
    }

    ///<summary>
    /// プレイヤーの回転処理
    /// 引数に回転量 プラス:右方向  マイナス:左方向
    /// </summary>
    private void Rotation(int v)
    {
        Vector3 nowAngule = Cursor.transform.localEulerAngles;//カーソルの現在の角度を取得
        float addAngle = -v * _playerRotateSpeed * Time.deltaTime;
        if (_playerChargeFlag)
        {
            addAngle /= 2;//チャージ中なら回転量を半減する。
        }
        _playerAngule += addAngle;

        //振れ幅の上下限を超えないようにする処理
        if (_playerAngule > PLAYER_ANGULE_RANGE)
        {
            _playerAngule = PLAYER_ANGULE_RANGE;
            nowAngule.z = PLAYER_ANGULE_RANGE;
        }
        else if (_playerAngule < -PLAYER_ANGULE_RANGE)
        {
            _playerAngule = -PLAYER_ANGULE_RANGE;
            nowAngule.z = -PLAYER_ANGULE_RANGE;
        }
        else
        {
            nowAngule.z += addAngle;
        }

        Cursor.transform.localEulerAngles = nowAngule;
    }

    ///<summary>
    /// チャージ開始。
    /// </summary>
    private void StartCharge()
    {
        _playerChargeFlag = true;
        _playerChargeCount = 0f;
        //チャージ完了時に色が_defChargeEffectColorへ変わるので、開始時に戻す
        var mainModule = _chargeEffect.main;
        mainModule.startColor = CHARGE_EFFECT_COLOR;
        // ……(チームメンバー担当のため省略)……
    }

    ///<summary>
    /// チャージ終了。エフェクトと音を止めてチャージ状態を解除する。
    /// </summary>
    private void StopCharge()
    {
        _playerChargeFlag = false;
        _chargeEffect.Stop();
        // ……(チームメンバー担当のため省略)……
        StopChargeSounds();
    }

    // ……(抜粋外)……
}
