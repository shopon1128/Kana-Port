using UnityEngine;
using UnityEngine.UI;
using System.Collections;

public class PlayerHpManager : MonoBehaviour
{
    public static PlayerHpManager Instance { get; private set; }
    [SerializeField] private float[] _damageList = new float[5];//レベルごとの与ダメージリスト
    [SerializeField] private GameObject[] _hpGaugeList = new GameObject[2];//HPゲージ 0 -> 1P, 1 -> 2P
    private Image[] _gaugeImage = new Image[2];//ゲージのImageコンポーネント
    private Color[] _gaugeBaseColor = new Color[2];
    // ……(チームメンバー担当のため省略)……
    [SerializeField] private float _flashDuration = 0.1f;//ダメージ演出の点滅にかかる時間(s)
    [SerializeField] private float _flashDepth = 0.5f;//ダメージ演出の点滅時にどれくらい透明にするか
    [System.Serializable] private struct frameShakeLv
    {
        public float dur;//ダメージ演出の振動の時間(s)
        public float powor ;//ダメージ演出の振動の強さ
    }
    private int[] _nowShakeLv = new int[2];//現在の振動のレベル(0~4) 0 -> 1P, 1 -> 2P
    [SerializeField] private frameShakeLv[] _frameShakeLv;
    private Vector3[] _frameDefPos = new Vector3[2];
    [SerializeField] private GameObject[] _frames = new GameObject[2];

    [SerializeField] private Image[] _faceFieldList = new Image[2];//顔のImageコンポーネント 0 -> 1P, 1 -> 2P

    [SerializeField] private Sprite[] _1pFaceSpriteList = new Sprite[4];//顔のリスト　あとでこっちに差し替える
    [SerializeField] private Sprite[] _2pFaceSpriteList = new Sprite[4];//顔のリスト　あとでこっちに差し替える

    private Coroutine[] _flashCoroutines;//二人分の被弾演出(HP)を管理する用
    private Coroutine[] _frameShakeCors;//二人分の被弾演出(振動)を管理する用

    // ……(チームメンバー担当のため省略)……

    private void Awake()
    {
        // 自分自身を Instance に登録
        if (Instance == null)
        {
            Instance = this;
        }

        //顔の初期化
        _faceFieldList[0].sprite = _1pFaceSpriteList[0];//顔画像きたら差し替え
        _faceFieldList[1].sprite = _2pFaceSpriteList[0];

    }

    private void Start(){
        //ゲージのImageコンポーネント取得
        _gaugeImage[0] = _hpGaugeList[0].GetComponent<Image>();
        _gaugeImage[1] = _hpGaugeList[1].GetComponent<Image>();
        //ベースの色保持
        _gaugeBaseColor[0] = _gaugeImage[0].color;
        _gaugeBaseColor[1] = _gaugeImage[1].color;

        //元の位置取得
        _frameDefPos[0] = _frames[0].transform.localPosition;
        _frameDefPos[1] = _frames[1].transform.localPosition;

        //コルーチン初期化
        _flashCoroutines = new Coroutine[2];
        _frameShakeCors = new Coroutine[2];

        _nowShakeLv[0] = 0;
        _nowShakeLv[1] = 0;
    }

    ///<summary>
    /// 引数 0 -> 1P, 1 -> 2P, 他はnull
    /// </summary>
    private PlayerManager GetPlayer(int plId)
    {
        if (plId == 0 || plId == 1)
        {
            return GetComponent<MainGameManager>().PlList[plId].GetComponent<PlayerManager>();
        }
        else
        {
            return null;
        }
    }

    ///<summary>
    /// 引数 : Virusのレベル と (int)WhoVirus
    /// </summary>
    public void DamageCheck(int lv, int plId)
    {
        GetPlayer(plId).Damage(_damageList[lv - 1]);
        //被弾演出
        int shakeLv = lv - 1;
        bool f = shakeLv >= _nowShakeLv[plId];//自分が今の振動レベルより強いか
        if (_flashCoroutines[plId] != null)
        {
            StopCoroutine(_flashCoroutines[plId]); // すでに点滅中なら一回リセット
            _gaugeImage[plId].color = _gaugeBaseColor[plId];
        }
        if(GetPlayer(plId).PlayerHP > 0f) _flashCoroutines[plId] = StartCoroutine(FlashGaugeRoutine(plId));
        if (_frameShakeCors[plId] != null && f)
        {
            StopCoroutine(_frameShakeCors[plId]); // すでに振動中ならリセット
            _frames[plId].transform.localPosition = _frameDefPos[plId];
        }
        if (GetPlayer(plId).PlayerHP > 0f && f)
        {
            _nowShakeLv[plId] = shakeLv;//今回の強さを記録してから振動開始(弱い被弾では上書きされない)
            _frameShakeCors[plId] = StartCoroutine(FrameShake(plId));
        }
    }

    ///<summary>
    /// 被弾演出コルーチン
    /// 透明度50%までフェードしたりして点滅
    /// </summary>
    private IEnumerator FlashGaugeRoutine(int plId)
    {
        float duration = _flashDuration / 2f;//フェードアウトとフェードインの時間に分ける

        float t = 0f;
        //フェードアウト
        while (t < 1f)
        {
            // 時間の経過割合（ Time.deltaTime / 片道時間 ）
            t += Time.deltaTime / duration;
            float alpha = Mathf.Lerp(1.0f, _flashDepth, t);
            _gaugeImage[plId].color = new Color(_gaugeBaseColor[plId].r, _gaugeBaseColor[plId].g, _gaugeBaseColor[plId].b, alpha);
            yield return null;
        }

        //フェードイン
        t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / duration;
            float alpha = Mathf.Lerp(_flashDepth, 1.0f, t);
            _gaugeImage[plId].color = new Color(_gaugeBaseColor[plId].r, _gaugeBaseColor[plId].g, _gaugeBaseColor[plId].b, alpha);
            yield return null;
        }

        _gaugeImage[plId].color = _gaugeBaseColor[plId];
        _flashCoroutines[plId] = null;
    }

    /// <summary>
    /// 被弾時に陣地を揺らすコルーチン
    /// 陣地のオブジェクトを直で揺らす感じに
    /// </summary>
    private IEnumerator FrameShake(int plId)
    {
        float t = 0f;
        while (t<_frameShakeLv[_nowShakeLv[plId]].dur)
        {
            if(GetPlayer(0).PlayerHP <= 0f||GetPlayer(1).PlayerHP <= 0f) break;
            if (MainGameManager.Instance.IsPaused)
            {//ポーズ中は揺れを止めて定位置で待機する。
                _frames[plId].transform.localPosition = _frameDefPos[plId];
                yield return null;
                continue;
            }
            t += Time.deltaTime;
            //揺らす処理 
            float damper = 1f - t / _frameShakeLv[_nowShakeLv[plId]].dur;
            Vector2 shakeOffset = Random.insideUnitCircle * _frameShakeLv[_nowShakeLv[plId]].powor * damper;
            shakeOffset.y = 0f;
            _frames[plId].transform.localPosition = _frameDefPos[plId] + (Vector3)shakeOffset;
            yield return null;
        }
        //定位置にちゃんと戻す処理
        _frames[plId].transform.localPosition = _frameDefPos[plId];
        _nowShakeLv[plId] = 0;//振動レベルのリセット
    }

    ///<summary>
    /// プレイヤーの_playerStateを取得
    /// 引数 0 -> 1P, 1 -> 2P
    /// </summary>
    public PlayerManager.PlayerState GetPlState(int plId)
    {
        return GetPlayer(plId).PlState;
    }

    ///<summary>
    /// プレイヤーの_playerStateに合わせて顔の画像を変える
    /// HPゲージも反映させる
    /// 引数 0 -> 1P, 1 -> 2P
    /// </summary>
    public void ReflectGaugeAndFace(int plId)
    {
        _hpGaugeList[plId].GetComponent<Image>().fillAmount = GetPlHpRate(plId);//HPゲージ反映
        // ……(チームメンバー担当のため省略)……

        if(plId == 0) _faceFieldList[plId].sprite = _1pFaceSpriteList[(int)GetPlState(plId)]; //顔画像きたら差し替え
        if(plId == 1) _faceFieldList[plId].sprite = _2pFaceSpriteList[(int)GetPlState(plId)]; //顔画像きたら差し替え
    }
    ///<summary>
    /// プレイヤーのHP割合を取得
    /// 引数 0 -> 1P, 1 -> 2P
    /// </summary>
    public float GetPlHpRate(int plId)
    {
        PlayerManager pl = GetPlayer(plId);
        return pl.PlayerHP / pl.PlayerMaxHP;
    }
}
