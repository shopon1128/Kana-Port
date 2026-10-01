using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class ResultManager : MonoBehaviour
{
    // ……(チームメンバー担当のため省略)……

    [Header("結果データ")]
    [SerializeField] private ResultData _resultData;

    [Header("演出用UI参照")]
    [SerializeField] private GameObject _srCanvas;
    [SerializeField] private GameObject[] _frames = new GameObject[2];
    [SerializeField] private GameObject[] _texts = new GameObject[2];
    // ……(チームメンバー担当のため省略)……
    [SerializeField] private GameObject[] _charaC = new GameObject[2];
    [SerializeField] private Sprite[] _winFace = new Sprite[2];
    [SerializeField] private Sprite[] _loseFace = new Sprite[2];
    // ……(チームメンバー担当のため省略)……
    [SerializeField] private GameObject[] _plLabelsB = new GameObject[2];
    [SerializeField] private float _plLabelDefPosY; // 移動前の位置Y

    [Header("スコア表示用テキスト")]
    [SerializeField] private TextMeshProUGUI _pl1ConboText;
    [SerializeField] private TextMeshProUGUI[] _pl1ScoreText = new TextMeshProUGUI[5];
    [SerializeField] private TextMeshProUGUI _pl1SumText;
    [SerializeField] private TextMeshProUGUI _pl2ConboText;
    [SerializeField] private TextMeshProUGUI[] _pl2ScoreText = new TextMeshProUGUI[5];
    [SerializeField] private TextMeshProUGUI _pl2SumText;

    [Header("開始タイミング")]
    [SerializeField] private float ENTER_VISIBLE_TIME = 0.05f;//滑り込みのうち、ウィルスが弾け始めてからの分(s)。

    [Header("勝者キャラ演出(MoveChara)")]
    [SerializeField] private float CHARA_MOVE_TIME = 2f;//演出全体の尺(s)
    [SerializeField] private float CHARA_ENTER_TIME = 0.1f;//滑り込みにかける時間(s)
    [SerializeField] private float CHARA_EXIT_TIME = 0.2f;//画面外へ抜ける時間(s)。
    [SerializeField] private float CHARA_ENTER_FROM_X = 1500f;//主役の立ち絵の滑り込み開始X
    [SerializeField] private float CHARA_START_X = 0f;//滑り込み終わりのX
    [SerializeField] private float CHARA_SLIDE_X = -400f;//ゆっくり流れていく先のX
    [SerializeField] private float CHARA_EXIT_X = -1500f;//退場後のX
    [SerializeField] private float CHARA_POS_Y = -353f;//主役の立ち絵の表示Y
    [SerializeField] private float CHARA_C_ENTER_FROM_X = -1500f;//背後の立ち絵の滑り込み開始X
    [SerializeField] private float CHARA_C_START_X = -960f;//滑り込み終わりのX
    [SerializeField] private float CHARA_C_SLIDE_X = -560f;//ゆっくり流れていく先のX
    [SerializeField] private float CHARA_C_EXIT_X = 1500f;//退場後のX
    [SerializeField] private float LABEL_ENTER_EXTRA_TIME = 0.9f;//CHARA_ENTER_TIMEに上乗せする時間(s)。キャラがゆっくり区間に入ってもラベルが滑り込みを続けるのに必要
    [SerializeField] private float LABEL_ENTER_DISTANCE = 1900f;//定位置の手前どれだけ離れた所から滑り込むか
    [SerializeField] private float LABEL_EXIT_DISTANCE = 150f;//退場でどれだけ動くか
    [SerializeField] private float LABEL_EXIT_FADE_TIME = 0.1f;//退場開始から消え切るまでの時間(s)。

    [Header("枠表示演出(ShowFrame)")]
    [SerializeField] private float _needShowFrame = 0.8f;//枠拡大・ラベル移動にかける時間(s)
    [SerializeField] private float CHARA_B_MOVE_DELAY = 0.4f;//枠表示開始から何秒後にキャラを動かすか(s)
    [SerializeField] private float CHARA_B_MOVE_TIME = 0.4f;//キャラの移動にかける時間(s)
    [SerializeField] private float[] CHARA_B_START_X = { -1200f, 1190f };//移動前のX(0:1P, 1:2P)
    [SerializeField] private float[] CHARA_B_END_X = { -800f, 790f };//移動後のX(0:1P, 1:2P)

    [Header("シーン遷移関係")]
    [SerializeField] private string _mainSceneName = "Main";
    [SerializeField] private string _titleSceneName = "Title";
    [SerializeField] private Canvas _btnCanvas;
    [SerializeField] private Button _btnRetry;
    [SerializeField] private Button _btnTitle;
    [SerializeField] private Image _antenPanel;//リトライ選択時に使う暗転パネル
    [SerializeField] private float ANTEN_TIME = 0.3f;//暗転にかける時間(s)

    private Vector3 defScale1p, defScale2p;
    private Vector3 targetPos1, targetPos2;
    private Vector3 _plLabelAStopPos;
    private Image _saveTextImage;

    private bool _scoreSkipRequested = false;
    private bool _inTransition = false;//シーン遷移を開始したか

    /// <summary>勝者(0:1P, 1:2P)</summary>
    private int WinIndex => _resultData.WhoWin - 1;

    /// <summary>X座標は1P勝利時を基準に持っているので、2Pのときはこの係数で左右反転させる</summary>
    private float Mirror => _resultData.WhoWin == 2 ? -1f : 1f;

    void Start()
    {
        int i =0;
        if(WinIndex == 0) i = 1;
        _charaB[WinIndex].GetComponent<Image>().sprite = _winFace[WinIndex];
        _charaB[i].GetComponent<Image>().sprite = _loseFace[i];
        StartCoroutine(RevealSequence());
    }

    IEnumerator RevealSequence()
    {
        // ウィルスで画面が覆われている間にスコア画面を準備し、そのまま演出も始める
        SetupScoreCanvas();
        ApplyMatchResultData();
        Coroutine scoreStage = StartCoroutine(PlayScoreStage());

        // 滑り込みの終わり際だけが見えるように、その分を残して覆ったままにしておく
        float burstDelay = Mathf.Max(0f, CHARA_ENTER_TIME - ENTER_VISIBLE_TIME);
        if (burstDelay > 0f) yield return new WaitForSecondsRealtime(burstDelay);

        // ウィルスが散り出すのと同時にBGMを鳴らす
        SoundManager.Instance.BGM((int)SoundManager.BgmType.Result, loop:false);

        // 弾ける演出は待たずに並行させる（散った時点で立ち絵が既に流れている状態にするため）
        VirusTransitionController.Instance.StartCoroutine(VirusTransitionController.Instance.PlayBurst());
        yield return WaitForProceedOrCoroutine(scoreStage, () => _scoreSkipRequested = true);

        // 入力待ち
        yield return WaitForProceed();

        yield return ShowBtnCanvas();
    }

    ///<summary>
    /// スコア画面の初期状態を整える。
    /// ウィルスで画面が埋め尽くされている間に裏で呼ばれる想定。
    ///</summary>
    private void SetupScoreCanvas()
    {
        _srCanvas.SetActive(true);
        _texts[0].SetActive(false);
        _texts[1].SetActive(false);

        defScale1p = _frames[0].transform.localScale;
        defScale2p = _frames[1].transform.localScale;

        _frames[0].transform.localScale = new Vector3(defScale1p.x, 0f, defScale1p.z);
        _frames[1].transform.localScale = new Vector3(defScale2p.x, 0f, defScale2p.z);

        targetPos1 = _plLabelsB[0].transform.localPosition;
        targetPos2 = _plLabelsB[1].transform.localPosition;

        Vector3 p1 = targetPos1; p1.y = _plLabelDefPosY;
        Vector3 p2 = targetPos2; p2.y = _plLabelDefPosY;
        _plLabelsB[0].transform.localPosition = p1;
        _plLabelsB[1].transform.localPosition = p2;

        _plLabelsA[WinIndex].enabled = true;
        _plLabelsA[WinIndex].color = Color.white;

        // 滑り込みの開始位置(画面外)へ置いておく。ここからの移動はウィルスの裏で消化される
        _charaA[WinIndex].transform.localPosition = new Vector3(CHARA_ENTER_FROM_X * Mirror, CHARA_POS_Y, 0f);

        _saveTextImage = _saveText.GetComponent<Image>();
        _saveTextImage.color = Color.white;

        // ラベルは定位置を覚えてから、Cと同じ向きに滑り込めるよう手前へ下げておく
        Transform plLabelTf = _plLabelsA[WinIndex].transform;
        _plLabelAStopPos = plLabelTf.localPosition;
        plLabelTf.localPosition = new Vector3(_plLabelAStopPos.x - LABEL_ENTER_DISTANCE * Mirror, _plLabelAStopPos.y, _plLabelAStopPos.z);

        for (int i = 0; i < _charaC.Length; i++)
        {
            if (_charaC[i] == null) continue;

            bool isWinner = i == WinIndex;
            if (isWinner)
            {
                float posY = _charaC[i].transform.localPosition.y;//Yはシーン側の設定をそのまま使う
                _charaC[i].transform.localPosition = new Vector3(CHARA_C_ENTER_FROM_X * Mirror, posY, 0f);
            }
            _charaC[i].SetActive(isWinner);
        }
    }

    ///<summary>
    /// ResultDataの内容をスコア表示に反映する。
    ///</summary>
    private void ApplyMatchResultData()
    {
        _pl1ConboText.text = _resultData.Pl1Score.MaxCombo.ToString();
        _pl2ConboText.text = _resultData.Pl2Score.MaxCombo.ToString();

        for(int i = 0; i < 5; i++)
        {
            _pl1ScoreText[i].text = _resultData.Pl1Score.KillCount[i].ToString();
            _pl2ScoreText[i].text = _resultData.Pl2Score.KillCount[i].ToString();
        }

        _pl1SumText.text = _resultData.Pl1Score.SumKillCount().ToString();
        _pl2SumText.text = _resultData.Pl2Score.SumKillCount().ToString();
    }

    ///<summary>枠拡大・キャラ移動</summary>
    IEnumerator PlayScoreStage()
    {
        // ……(チームメンバー担当のため省略)……

        yield return ShowFrame();
        _texts[0].SetActive(true);
        _texts[1].SetActive(true);
    }

    // ……(チームメンバー担当のため省略)……
    IEnumerator MoveChara()
    {
        float multiple = Mirror;
        int index  = WinIndex;

        // ゆっくり流れる区間と退場開始時刻は、全体の尺から逆算する
        float slideTime = CHARA_MOVE_TIME - CHARA_ENTER_TIME - CHARA_EXIT_TIME;
        float exitStartTime = CHARA_MOVE_TIME - CHARA_EXIT_TIME;

        // 背後を流れる立ち絵。表示と開始位置はSetupScoreCanvas()で済ませてある
        GameObject charaC = index < _charaC.Length ? _charaC[index] : null;
        if (charaC == null) Debug.LogWarning("ResultManager: _charaCが未設定です。背後の立ち絵演出をスキップします。");
        float charaCposY = charaC != null ? charaC.transform.localPosition.y : 0f;//Yはシーン側の設定をそのまま使う

        // 定位置からの相対で入口と出口を決める
        Image plLabel = _plLabelsA[index];
        float labelEnterFromX = _plLabelAStopPos.x - LABEL_ENTER_DISTANCE * multiple;
        float labelExitToX = _plLabelAStopPos.x + LABEL_EXIT_DISTANCE * multiple;
        // キャラが滑り込み終わった後も、上乗せ分だけラベルは滑り込みを続ける
        float labelEnterTime = CHARA_ENTER_TIME + LABEL_ENTER_EXTRA_TIME;

        // ……(チームメンバー担当のため省略)……
        while (t < CHARA_MOVE_TIME && !_scoreSkipRequested)
        {
            t += Time.unscaledDeltaTime;

            float charaX, charaCx;
            float saveTextAlpha = 1f;

            if (t < CHARA_ENTER_TIME)
            {
                float dt = Progress(t, CHARA_ENTER_TIME);
                charaX = Mathf.Lerp(CHARA_ENTER_FROM_X, CHARA_START_X, dt);
                charaCx = Mathf.Lerp(CHARA_C_ENTER_FROM_X, CHARA_C_START_X, dt);
            }

            else if (t < exitStartTime)
            {
                float dt = Progress(t - CHARA_ENTER_TIME, slideTime);
                charaX = Mathf.Lerp(CHARA_START_X, CHARA_SLIDE_X, dt);
                charaCx = Mathf.Lerp(CHARA_C_START_X, CHARA_C_SLIDE_X, dt);
            }

            else
            {
                float dt = Progress(t - exitStartTime, CHARA_EXIT_TIME);
                charaX = Mathf.Lerp(CHARA_SLIDE_X, CHARA_EXIT_X, dt);
                charaCx = Mathf.Lerp(CHARA_C_SLIDE_X, CHARA_C_EXIT_X, dt);
                saveTextAlpha = 1f - dt;//退場に合わせてその場で消える
            }

            float labelX;
            float labelAlpha = 1f;

            if (t < labelEnterTime)
            {
                labelX = Mathf.Lerp(labelEnterFromX, _plLabelAStopPos.x, Progress(t, labelEnterTime));
            }
            else if (t < exitStartTime)
            {
                labelX = _plLabelAStopPos.x;//ゆっくり区間の間は定位置で止まる
            }
            else
            {
                float dt = t - exitStartTime;
                labelX = Mathf.Lerp(_plLabelAStopPos.x, labelExitToX, Progress(dt, CHARA_EXIT_TIME));
                labelAlpha = 1f - Progress(dt, LABEL_EXIT_FADE_TIME);
            }

            _charaA[index].transform.localPosition = new Vector3(charaX * multiple, CHARA_POS_Y, 0f);
            if (_saveTextImage != null) _saveTextImage.color = new Color(1f, 1f, 1f, saveTextAlpha);
            if (charaC != null)
            {
                charaC.transform.localPosition = new Vector3(charaCx * multiple, charaCposY, 0f);
            }
            plLabel.transform.localPosition = new Vector3(labelX, _plLabelAStopPos.y, _plLabelAStopPos.z);
            plLabel.color = new Color(1f, 1f, 1f, labelAlpha);

            // ……(チームメンバー担当のため省略)……
        }

        _charaA[index].transform.localPosition = new Vector3(CHARA_EXIT_X * multiple, CHARA_POS_Y, 0f);
        if (_saveTextImage != null) _saveTextImage.color = new Color(1f, 1f, 1f, 0f);
        if (charaC != null)
        {
            charaC.transform.localPosition = new Vector3(CHARA_C_EXIT_X * multiple, charaCposY, 0f);
            charaC.SetActive(false);//退場後は枠の横に絵が残るので消す
        }
        plLabel.transform.localPosition = new Vector3(labelExitToX, _plLabelAStopPos.y, _plLabelAStopPos.z);
        plLabel.color = new Color(1f, 1f, 1f, 0f);
    }

    ///<summary>枠の表示と、ラベルの移動</summary>
    IEnumerator ShowFrame()
    {
        float t = 0f;

        Vector3 startPos1 = _plLabelsB[0].transform.localPosition;
        Vector3 startPos2 = _plLabelsB[1].transform.localPosition;

        // ……(チームメンバー担当のため省略)……

        int charaBCount = Mathf.Min(_charaB.Length, Mathf.Min(CHARA_B_START_X.Length, CHARA_B_END_X.Length));

        float stageTime = Mathf.Max(_needShowFrame, CHARA_B_MOVE_DELAY + CHARA_B_MOVE_TIME);

        while (t < stageTime && !_scoreSkipRequested)
        {
            t += Time.unscaledDeltaTime;
            float dt = Progress(t, _needShowFrame);
            _plLabelsB[0].transform.localPosition = Vector3.Lerp(startPos1, targetPos1, dt);
            _plLabelsB[1].transform.localPosition = Vector3.Lerp(startPos2, targetPos2, dt);
            _frames[0].transform.localScale = new Vector3(defScale1p.x, Mathf.Lerp(0f, defScale1p.y, dt), defScale1p.z);
            _frames[1].transform.localScale = new Vector3(defScale2p.x, Mathf.Lerp(0f, defScale2p.y, dt), defScale2p.z);

            if (t >= CHARA_B_MOVE_DELAY)
            {
                float charaDt = Progress(t - CHARA_B_MOVE_DELAY, CHARA_B_MOVE_TIME);

                for (int i = 0; i < charaBCount; i++)
                {
                    float x = Mathf.Lerp(CHARA_B_START_X[i], CHARA_B_END_X[i], charaDt);
                    _charaB[i].transform.localPosition = new Vector3(x, _charaB[i].transform.localPosition.y, 0f);
                }
            }

            yield return null;
        }
        _plLabelsB[0].transform.localPosition = targetPos1;
        _plLabelsB[1].transform.localPosition = targetPos2;
        _frames[0].transform.localScale = defScale1p;
        _frames[1].transform.localScale = defScale2p;

        for (int i = 0; i < charaBCount; i++)
        {
            _charaB[i].transform.localPosition = new Vector3(CHARA_B_END_X[i], _charaB[i].transform.localPosition.y, 0f);
        }
    }

    /// <summary>
    /// 0除算を避けつつ経過時間を0〜1の進捗に変換する。
    /// </summary>
    private static float Progress(float a_elapsed, float a_duration)
    {
        if (a_duration <= 0f) return 1f;
        return Mathf.Clamp01(a_elapsed / a_duration);
    }

    /// <summary>
    /// Proceed入力があった場合はonSkipを一度だけ呼ぶ
    /// コルーチンは強制停止せず、フラグを見て内部で短縮完了させる
    /// </summary>
    IEnumerator WaitForProceedOrCoroutine(Coroutine target, System.Action onSkip)
    {
        var plList = ResultPlayerInputBridge.Instance != null ? ResultPlayerInputBridge.Instance.PlList : null;

        bool skipTriggered = false;
        System.Action<InputAction.CallbackContext> onProceed = _ =>
        {
            if (skipTriggered) return;
            skipTriggered = true;
            onSkip?.Invoke();
        };

        if (plList != null)
        {
            foreach (var pl in plList)
            {
                if (pl == null) continue;
                pl.actions.FindActionMap("Result")["Proceed"].performed += onProceed;
            }
        }

        if (target != null)
        {
            yield return target;
        }

        if (plList != null)
        {
            foreach (var pl in plList)
            {
                if (pl == null) continue;
                pl.actions.FindActionMap("Result")["Proceed"].performed -= onProceed;
            }
        }
    }

    /// <summary>Proceedが押されるまで待つ</summary>
    IEnumerator WaitForProceed()
    {
        var plList = ResultPlayerInputBridge.Instance != null ? ResultPlayerInputBridge.Instance.PlList : null;
        if (plList == null)
        {
            Debug.LogWarning("ScoreResultSceneManager: 引き継がれたPlayerInputが見つからない。");
            yield break;
        }

        bool inputReceived = false;
        System.Action<InputAction.CallbackContext> onProceed = _ => inputReceived = true;

        foreach (var pl in plList)
        {
            if (pl == null) continue;
            pl.actions.FindActionMap("Result")["Proceed"].performed += onProceed;
        }

        while (!inputReceived)
            yield return null;

        foreach (var pl in plList)
        {
            if (pl == null) continue;
            pl.actions.FindActionMap("Result")["Proceed"].performed -= onProceed;
        }
    }

    /// <summary>
    /// リトライかタイトルかの選択キャンバスの表示
    /// </summary>
    IEnumerator ShowBtnCanvas()
    {
        _antenPanel.gameObject.SetActive(false);

        _btnCanvas.gameObject.SetActive(true);
        SoundManager.Instance.CanSelectSound = false;

        _btnRetry.Select();
        yield return null;
    }

    public void ToRetry()
    {
        // ……(チームメンバー担当のため省略)……

        if (_inTransition) return;//暗転中の押し直しを無視する
        _inTransition = true;

        // ……(チームメンバー担当のため省略)……
        StartCoroutine(RetryRoutine());
    }

    /// <summary>暗転させてからMainシーンへ戻る</summary>
    IEnumerator RetryRoutine()
    {
        yield return FadeToBlack();

        // Mainシーン側でPlayerInputを作り直すため、引き継いだ分は破棄しないと砲台が二重になる
        if (ResultPlayerInputBridge.Instance != null)
        {
            ResultPlayerInputBridge.Instance.ReleaseAndDestroy();
        }
        Time.timeScale = 1f;
        SceneManager.LoadScene(_mainSceneName);
    }

    /// <summary>暗転パネルを透明から不透明にする</summary>
    IEnumerator FadeToBlack()
    {
        Color panelColor = _antenPanel.color;
        _antenPanel.color = new Color(panelColor.r, panelColor.g, panelColor.b, 0f);
        _antenPanel.gameObject.SetActive(true);

        float t = 0f;
        while (t < ANTEN_TIME)
        {
            t += Time.unscaledDeltaTime;
            _antenPanel.color = new Color(panelColor.r, panelColor.g, panelColor.b, Progress(t, ANTEN_TIME));
            yield return null;
        }
        _antenPanel.color = new Color(panelColor.r, panelColor.g, panelColor.b, 1f);
    }

    // ……(チームメンバー担当のため省略)……
    private IEnumerator OpenTitle()
    {
        // ……(チームメンバー担当のため省略)……

        _inTransition = true;

        if (ResultPlayerInputBridge.Instance != null)
        {
            ResultPlayerInputBridge.Instance.ReleaseAndDestroy();
        }
        Time.timeScale = 1f;
        // ……(チームメンバー担当のため省略)……
        SoundManager.Instance.StopBGM();
        SoundManager.Instance.CanSelectSound = false;

        // ……(チームメンバー担当のため省略)……
        SceneManager.LoadScene(_titleSceneName);
    }
}
