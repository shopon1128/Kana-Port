using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// スマホ向けの画面上UIでプレイヤーを操作する。
/// ・移動: ジョイスティック(入力はコード側で8方向へ量子化する)
/// ・ショット: 4方向ボタン(タップ)、ダッシュ: ボタン(押しっぱなし)、ポーズ: ボタン(タップ)
/// 表示可否はOptionSetting.showPhoneUiで切り替える(既定は非表示)。ポーズ中の設定からも切り替わるので、
/// 自身は常に有効なまま子オブジェクトの表示だけを出し入れする(非アクティブだと切り替え指示を受け取れないため)。
/// PlayerBotと同じくPlayer.Instanceへ直接入力を流し込む方式。
/// </summary>
public class PhoneUIController : MonoBehaviour
{
    private const float DEAD_ZONE = 0.2f;//これ未満の傾きは移動しない(中央付近の微振動対策)
    private const float DIR_STEP_DEG = 45f;//8方向スナップの角度刻み

    [SerializeField] private OptionSetting _optionSetting;
    [Header("移動(8方向スティック)")]
    [SerializeField] private Joystick _moveJoystick;//Fixed/Floatingどちらでも可(基底型で受ける)
    [Header("ダッシュ(押しっぱなし)")]
    [SerializeField] private TouchHoldButton _dash;
    [Header("ショット(タップ)")]
    [SerializeField] private TouchHoldButton _shotUp;
    [SerializeField] private TouchHoldButton _shotDown;
    [SerializeField] private TouchHoldButton _shotLeft;
    [SerializeField] private TouchHoldButton _shotRight;
    [Header("ポーズ(タップ)")]
    [SerializeField] private TouchHoldButton _pause;//キーのポーズと同じ働き

    /// <summary>ポーズ中の設定から表示を切り替えるために公開する</summary>
    public static PhoneUIController Instance { get; private set; }

    private Image _background;
    private bool _isVisible;

    void Awake()
    {
        Instance = this;
        TryGetComponent(out _background);
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    void Start()
    {
        //ジョイスティックを8方向スナップ+デッドゾーンに強制設定する。
        //Inspectorのチェックに依存せず、ハンドルが8方向へカクカク飛ぶ挙動を確実に有効化する
        if (_moveJoystick != null)
        {
            _moveJoystick.SnapX = true;
            _moveJoystick.SnapY = true;
            _moveJoystick.DeadZone = DEAD_ZONE;
        }

        _shotUp.Pressed += () => Fire(Vector2.up);
        _shotDown.Pressed += () => Fire(Vector2.down);
        _shotLeft.Pressed += () => Fire(Vector2.left);
        _shotRight.Pressed += () => Fire(Vector2.right);
        _pause.Pressed += TogglePause;

        SetVisible(_optionSetting != null && _optionSetting.showPhoneUi);
    }

    /// <summary>
    /// 操作UIの表示を切り替える。
    /// 自身は無効化せず見た目だけ消す(無効化すると次に表示へ戻す指示を受け取れなくなるため)
    /// </summary>
    public void SetVisible(bool a_visible)
    {
        _isVisible = a_visible;
        if (_background != null)
        {
            _background.enabled = a_visible;
        }
        foreach (Transform child in transform)
        {
            child.gameObject.SetActive(a_visible);
        }
    }

    //移動とダッシュは押下状態を毎物理フレーム反映する(ゲーム進行のゲートはPlayer/Character側が持つ)
    void FixedUpdate()
    {
        //非表示の間は流し込まない(スティックの0がキーボード入力を打ち消してしまうため)
        if (!_isVisible)
        {
            return;
        }
        //ボット操作中は入力元が競合するため流し込まない
        if (_optionSetting.usePlayerBot || Player.Instance == null || Player.Instance.IsDead)
        {
            return;
        }

        Vector2 raw = _moveJoystick != null ? _moveJoystick.Direction : Vector2.zero;
        Player.Instance.ApplyMoveInput(SnapTo8(raw));
        Player.Instance.SetDashing(_dash.IsHeld);
    }

    //スティックの生入力を等速の8方向(上下左右+斜め)へ量子化する。
    //キーボードと同じ等速8方向にそろえ、AI対戦のフェアネスを保つ(斜めが速くならないよう単位ベクトル化)
    private static Vector2 SnapTo8(Vector2 a_raw)
    {
        if (a_raw.magnitude < DEAD_ZONE)
        {
            return Vector2.zero;
        }
        float angle = Mathf.Atan2(a_raw.y, a_raw.x) * Mathf.Rad2Deg;
        float snapped = Mathf.Round(angle / DIR_STEP_DEG) * DIR_STEP_DEG * Mathf.Deg2Rad;
        return new Vector2(Mathf.Cos(snapped), Mathf.Sin(snapped));//45°刻みのcos/sinは既に単位ベクトル
    }

    private void Fire(Vector2 a_direction)
    {
        if (Player.Instance != null)
        {
            Player.Instance.FireBullet(a_direction);
        }
    }

    private void TogglePause()
    {
        if (PauseManager.Instance != null)
        {
            PauseManager.Instance.TogglePause();
        }
    }
}
