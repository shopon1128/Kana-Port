using UnityEngine;
using System.Collections;
using TMPro;
using UnityEngine.UI;

public class EventManager : MonoBehaviour
{
    public enum EventType
    {
        VirusMassOutbreak, VirusMax, VirusAccele,VirusDivision
    }
    [System.Serializable]
    public struct EventData
    {
        public EventType type;
        public float rate;//発生比率
        public string message;//イベント発生時に表示されるテキスト
    }
    [SerializeField] private EventData[] EventList;//イベントのリスト

    [SerializeField] private GameObject _eventMessage;//メッセージを表示する領域的な(黒)
    [SerializeField] private GameObject _eventMessageGauge;//メッセージで時間を表現するゲージ(黄色)
    [SerializeField] private TextMeshProUGUI _eventMessagetext;//メッセージを表示するテキスト
    [SerializeField] private Image _emBackColor;//透明度いじる用
    [SerializeField] private Image _emGaugeColor;//透明度いじる用(ゲージ)
    private Color _emBackDefColor;//イベントメッセージの透明度保存用
    private Color _emGaugeDefColor;//イベントメッセージの透明度保存用(ゲージ)
    private Color _emTextDefColor;//イベントメッセージの透明度保存用(テキスト)
    private Vector3 _emGaugeDefScale;//ゲージの進行時に弄る用

    [SerializeField] private float _emShowNeedTime = 0.3f;//emの出し入れにかかる時間(s)
    private Coroutine _emShowCorutine;

    [SerializeField] private SpawnManager[] _spawnManagerList;//0 -> 1Pのスポーン 1 -> 2Pのスポーン
    [SerializeField] private VirusTeleportManager _virusTeleportManager;
    private bool _eventFlag = false;//イベント発生中か否か
    [SerializeField] private float EVENT_TIME = 10f;//イベントの継続時間(s)
    [SerializeField] private float EVENT_CT = 20f;//イベントのクールタイム(s)
    private bool _eventCtFlag = false;//イベントCT中か否か
    private float _eventCtCount = 0f;//クールタイムカウント用

    [SerializeField] private int MASS_OUTBREAK_NUM = 3;//大量発生イベントでの一度に生成されるウィルスの数

    [SerializeField] private float ACCELE_RATE = 2f;//ウィルスの速度増加率
    public static float AccelerationRate = 1f;//現在の速度増加率


    //ゲーム開始から1分経ってもどちらの体力も50%以下になっていないとき
    private bool _ElapsedTimeEventFlag = true;
    private float _timerCount = 0f;//ゲーム開始からどれだけ経ってるかのタイマー
    [SerializeField] private float ELAPSED_TIME = 60f;//ゲーム開始から何秒後に判定するか(s)
    [SerializeField] private float ELAPSED_CUTOFF_SCORE = 0.5f;//体力の基準値(0~1)

    //体力が25%を切ったとき
    [SerializeField] private float FALLBELOW_CUTOFF_SCORE = 0.25f;//体力の基準値(0~1)
    private bool _pl1FallBelowEventFlag = true;//1Pの体力が25%を切ったとき
    private bool _pl2FallBelowEventFlag = true;//2Pの体力が25%を切ったとき

    private bool _dangerBGM = false;//どちらかのHPが25%以下か

    //20秒間、どちらの体力も5%以上変化がないとき イベント中はカウントせず、イベント終了から0からカウント
    [SerializeField] private float DELTA_CUTOFF_SCORE = 0.05f;//体力の変化量の基準値(0~1)
    private float _pl1LastHpRate = 1f;//前回判定時の1PのHP割合
    private float _pl2LastHpRate = 1f;//前回判定時の2PのHP割合
    [SerializeField] private float BLANK_TIME = 20f;//前回イベントから何秒後に判定するか(s)
    private float _blankCount = 0f;//カウント用

    void Start()
    {
        //色々初期化


        _emBackDefColor = _emBackColor.color;
        _emGaugeDefColor = _emGaugeColor.color;
        _emTextDefColor = _eventMessagetext.color;

        _emGaugeDefScale = _eventMessageGauge.transform.localScale;
        _eventMessage.SetActive(false);

        _emShowCorutine = null;

        _timerCount = 0f;

        _eventFlag = false;
        _eventCtCount = 0f;
        _eventCtFlag = false;

        AccelerationRate = 1f;

        _ElapsedTimeEventFlag = true;
        _pl1FallBelowEventFlag = true;
        _pl2FallBelowEventFlag = true;

        _dangerBGM = false;

        _pl1LastHpRate = 1f;
        _pl2LastHpRate = 1f;

        _blankCount = 0f;

        MainGameManager.Instance._gameEndAction += OnGameEnd;
    }

    private void OnDestroy()
    {
        if (MainGameManager.Instance != null)
        {
            MainGameManager.Instance._gameEndAction -= OnGameEnd;
        }
    }

    void Update()
    {
        if (MainGameManager.Instance.InGame)
        {

            //クールタイム管理
            if (_eventCtFlag)
            {
                _eventCtCount += Time.deltaTime;
                if (_eventCtCount >= EVENT_CT)
                {
                    _eventCtFlag = false;
                    _eventCtCount = 0f;
                }
            }

            //ゲーム開始から1分経ってもどちらの体力も50%以下になっていないとき
            if (_ElapsedTimeEventFlag)
            {
                _timerCount += Time.deltaTime;
                if (_timerCount >= ELAPSED_TIME)
                {
                    if (PlayerHpManager.Instance.GetPlHpRate(0) > ELAPSED_CUTOFF_SCORE && PlayerHpManager.Instance.GetPlHpRate(1) > ELAPSED_CUTOFF_SCORE)
                    {
                        Debug.Log("ゲーム開始から1分経ってもどちらの体力も50%以下になっていないとき");
                        CallUpEvent();
                    }
                    _ElapsedTimeEventFlag = false;
                }
            }

            //1Pの体力が25%を切ったとき
            if (_pl1FallBelowEventFlag)
            {
                if (PlayerHpManager.Instance.GetPlHpRate(0) < FALLBELOW_CUTOFF_SCORE)
                {
                    _pl1FallBelowEventFlag = false;
                    Debug.Log("1Pの体力が25%を切ったとき");
                    if (!_dangerBGM)
                    {
                        _dangerBGM = true;
                        SoundManager.Instance.BGM((int)SoundManager.BgmType.Danger);
                    }
                    CallUpEvent();
                }
            }

            //2Pの体力が25%を切ったとき
            if (_pl2FallBelowEventFlag)
            {
                if (PlayerHpManager.Instance.GetPlHpRate(1) < FALLBELOW_CUTOFF_SCORE)
                {
                    _pl2FallBelowEventFlag = false;
                    Debug.Log("2Pの体力が25%を切ったとき");
                    if (!_dangerBGM)
                    {
                        _dangerBGM = true;
                        SoundManager.Instance.BGM((int)SoundManager.BgmType.Danger);
                    }
                    CallUpEvent();
                }
            }

            //20秒間、どちらの体力も5%以上変化がないとき
            if (!_eventFlag)
            {
                _blankCount += Time.deltaTime;
                if (_blankCount >= BLANK_TIME)
                {
                    float pl1NowHpRate = PlayerHpManager.Instance.GetPlHpRate(0);
                    float pl2NowHpRate = PlayerHpManager.Instance.GetPlHpRate(1);
                    if (_pl1LastHpRate - pl1NowHpRate < DELTA_CUTOFF_SCORE || _pl2LastHpRate - pl2NowHpRate < DELTA_CUTOFF_SCORE)
                    {
                        Debug.Log("20秒間、どちらか体力が5%以上変化がないとき");
                        CallUpEvent();
                        _blankCount = 0f;
                    }
                    else
                    {//不発時は、次のカウントを早める
                        _blankCount = BLANK_TIME/2;
                    }
                    _pl1LastHpRate = pl1NowHpRate;
                    _pl2LastHpRate = pl2NowHpRate;
                }
            }
            else
            {
                _blankCount = 0f;
            }
        }
    }

    ///<summary>
    /// イベントの呼び出し
    /// </summary>
    public void CallUpEvent()
    {
        if (!_eventFlag && !_eventCtFlag)
        {//イベントが重複発生しないように
            _eventFlag = true;
            // ……(チームメンバー担当のため省略)……

            //レートの合計値算出
            float totalRate = 0f;
            foreach (var ev in EventList)
            {
                totalRate += ev.rate;
            }
            //イベント抽選
            float sum = 0f;
            float point = Random.Range(0f, totalRate);
            EventData selectedEvent = EventList[0];
            foreach (var ev in EventList)
            {
                sum += ev.rate;
                if (point <= sum)
                {
                    //当たったイベント取得
                    selectedEvent = ev;
                    break;
                }
            }
            //当たったイベントをメッセージに表示
            _eventMessagetext.text = selectedEvent.message;
            _emShowCorutine = StartCoroutine(ShowEventMessage(true));

            //typeに応じてコルーチンを開始
            switch (selectedEvent.type)
            {
                case EventType.VirusMassOutbreak:
                    StartCoroutine(VirusMassOutbreakEvent());
                    break;

                case EventType.VirusMax:
                    StartCoroutine(VirusMaxEvent());
                    break;

                case EventType.VirusAccele:
                    StartCoroutine(VirusAcceleEvent());
                    break;
                case EventType.VirusDivision:
                    StartCoroutine(VirusDivisionEvent());
                    break;
            }
            StartCoroutine(EmGauge());//ゲージ進行開始
        }
    }
    ///<summary>イベントメッセージの出し入れ</summary>
    IEnumerator ShowEventMessage(bool isShow){

        float ba,ga,ta;
        float bda = _emBackDefColor.a /_emShowNeedTime;
        float gda = _emGaugeDefColor.a /_emShowNeedTime;
        float tda = _emTextDefColor.a / _emShowNeedTime;
        if (isShow)
        {//出す
            _eventMessage.SetActive(true);
            ba = 0f;
            ga = 0f;
            ta = 0f;

            _emBackColor.color = new Color(_emBackDefColor.r,_emBackDefColor.g,_emBackDefColor.b,ba);
            _emGaugeColor.color = new Color(_emGaugeDefColor.r,_emGaugeDefColor.g,_emGaugeDefColor.b,ga);
            _eventMessagetext.color = new Color(_emTextDefColor.r,_emTextDefColor.g,_emTextDefColor.b,ta);
            while (ba < _emBackDefColor.a && ga < _emGaugeDefColor.a && ta < _emTextDefColor.a)
            {
                ba += Mathf.Clamp01(bda * Time.deltaTime);
                ga += Mathf.Clamp01(gda * Time.deltaTime);
                ta += Mathf.Clamp01(tda * Time.deltaTime);
                _emBackColor.color = new Color(_emBackDefColor.r,_emBackDefColor.g,_emBackDefColor.b,ba);
                _emGaugeColor.color = new Color(_emGaugeDefColor.r,_emGaugeDefColor.g,_emGaugeDefColor.b,ga);
                _eventMessagetext.color = new Color(_emTextDefColor.r,_emTextDefColor.g,_emTextDefColor.b,ta);
                yield return null;
            }
            _emBackColor.color = _emBackDefColor;
            _emGaugeColor.color = _emGaugeDefColor;
            _eventMessagetext.color = _emTextDefColor;
        }
        else
        {//しまう
            ba = _emBackDefColor.a;
            ga = _emGaugeDefColor.a;
            ta = _emTextDefColor.a;
            _emBackColor.color = new Color(_emBackDefColor.r,_emBackDefColor.g,_emBackDefColor.b,ba);
            _emGaugeColor.color = new Color(_emGaugeDefColor.r,_emGaugeDefColor.g,_emGaugeDefColor.b,ga);
            _eventMessagetext.color = new Color(_emTextDefColor.r,_emTextDefColor.g,_emTextDefColor.b,ta);
            while (ba > 0f && ga > 0f && ta > 0f)
            {
                ba -= Mathf.Clamp01(bda * Time.deltaTime);
                ga -= Mathf.Clamp01(gda * Time.deltaTime);
                ta -= Mathf.Clamp01(tda * Time.deltaTime);
                _emBackColor.color = new Color(_emBackDefColor.r,_emBackDefColor.g,_emBackDefColor.b,ba);
                _emGaugeColor.color = new Color(_emGaugeDefColor.r,_emGaugeDefColor.g,_emGaugeDefColor.b,ga);
                _eventMessagetext.color = new Color(_emTextDefColor.r,_emTextDefColor.g,_emTextDefColor.b,ta);
                yield return null;
            }
            _eventMessage.SetActive(false);
        }

        _emShowCorutine = null;

    }

    /// <summary>
    /// イベントメッセージのゲージを管理
    /// </summary>
    IEnumerator EmGauge()
    {
        float t = 0f;
        _eventMessageGauge.transform.localScale = _emGaugeDefScale;
        while(t <= EVENT_TIME)
        {
            t += Time.deltaTime;
            float dx = Mathf.Lerp(_emGaugeDefScale.x,0f,t/EVENT_TIME);
            _eventMessageGauge.transform.localScale = new Vector3(dx,_emGaugeDefScale.y,_emGaugeDefScale.z);
            yield return null;
        }
        //確実に消えるように
        _eventMessageGauge.transform.localScale = new Vector3(0f,_emGaugeDefScale.y,_emGaugeDefScale.z);
    }

    ///<summary>
    /// 「ウイルス大量発生」イベント
    /// 10秒間、Lv1のウイルスを【0〜1秒の間隔】で1回の生成で同時に3つ以上追加する
    /// </summary>
    IEnumerator VirusMassOutbreakEvent()
    {
        Debug.Log("ウイルスが大量に発生した！");
        float evCount = 0f;//継続時間計測用
        while (evCount < EVENT_TIME)
        {
            for (int i = 0; i < MASS_OUTBREAK_NUM; i++)
            {
                _spawnManagerList[0].SpawnVirus();
                _spawnManagerList[1].SpawnVirus();
            }
            float randomCt = Random.Range(0f, 1f);//0~1秒のクールタイム
            yield return new WaitForSeconds(randomCt);
            evCount += randomCt;
        }
        EventEnd();
    }

    ///<summary>
    /// 「ウィルスが強くなった」イベント
    /// 10秒間、自然発生するウイルスがLv1ではなくLv5になる
    /// </summary>
    IEnumerator VirusMaxEvent()
    {
        Debug.Log("ウイルスがみんな強くなった！");

        _spawnManagerList[0]._virusMaxEventFlag = true;
        _spawnManagerList[1]._virusMaxEventFlag = true;

        yield return new WaitForSeconds(EVENT_TIME);

        _spawnManagerList[0]._virusMaxEventFlag = false;
        _spawnManagerList[1]._virusMaxEventFlag = false;

        EventEnd();
    }

    ///<summary>
    /// 「ウィルス加速」イベント
    /// 10秒間、ウイルスが素早くなる
    /// </summary>
    IEnumerator VirusAcceleEvent()
    {
        Debug.Log("ウイルスが素早くなった！");
        AccelerationRate = ACCELE_RATE;//ACCWLW_RATE倍速に
        yield return new WaitForSeconds(EVENT_TIME);
        AccelerationRate = 1f;//元の速度に
        EventEnd();
    }
    ///<summary>
    /// 「ウィルス分裂」イベント
    /// 10秒間、返したウイルスの生成数が2倍になる
    /// </summary>
    IEnumerator VirusDivisionEvent()
    {
        Debug.Log("ウイルスが分裂する！");
        _virusTeleportManager._virusDivisionEventFlag = true;
        yield return new WaitForSeconds(EVENT_TIME);
        _virusTeleportManager._virusDivisionEventFlag = false;
        EventEnd();
    }

    ///<summary>
    /// イベントが最後まで走り切った時の共通処理。
    /// メッセージをしまい、次のイベントまでのクールタイムを開始する。
    /// </summary>
    private void EventEnd()
    {
        _eventCtFlag = true;
        _eventFlag = false;

        if(_emShowCorutine != null) StopCoroutine(_emShowCorutine);//出す演出の途中だった場合に備えて止める
        StartCoroutine(ShowEventMessage(false));
        SoundManager.Instance.EventAlertStop();
    }

    ///<summary>
    /// 決着時の後片付け。
    /// </summary>
    private void OnGameEnd()
    {
        //走行中のイベント本体とメッセージ演出をすべて止める
        StopAllCoroutines();
        _emShowCorutine = null;
        _eventFlag = false;

        //コルーチンを止めた分、各イベントの終了処理が走らないのでここで元に戻す
        AccelerationRate = 1f;
        _spawnManagerList[0]._virusMaxEventFlag = false;
        _spawnManagerList[1]._virusMaxEventFlag = false;
        _virusTeleportManager._virusDivisionEventFlag = false;

        //リザルトに残らないよう、しまう演出は挟まずその場で消す
        _eventMessage.SetActive(false);
        // ……(チームメンバー担当のため省略)……
    }
}
