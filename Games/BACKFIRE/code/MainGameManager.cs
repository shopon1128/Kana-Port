using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Users;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using LitMotion;
using LitMotion.Extensions;
using System.Linq;

public class MainGameManager : MonoBehaviour
{
    // ……(抜粋外)……

    void Update()
    {
        if (!InGame) return;
        if (!InSuddenDeath)
        {
            _timeCounter += Time.deltaTime;
            if (_timeCounter >= TIME_LIMIT)
            {
                _timeCounter = TIME_LIMIT;
                if (_pl1Manager.PlState != PlayerManager.PlayerState.HAIBOKU && _pl2Manager.PlState != PlayerManager.PlayerState.HAIBOKU)
                {
                    InSuddenDeath = true;//サドンデスに移行
                    _suddenDeathMessage.SetActive(true);
                    _back.GetComponent<Image>().color = _suddenDeathColor;
                    _timerText.color = new Color(1f, 0f, 0f);
                    if (_pl1Manager.PlayerHP > 1f) _pl1Manager.Damage(10000f);
                    if (_pl2Manager.PlayerHP > 1f) _pl2Manager.Damage(10000f);
                }
            }
            TimerTextUpdate();
        }
    }

    // ……(抜粋外)……

    public void HPCheck()
    {
        //どちらかの状態が敗北になったら決着演出開始
        if (_pl2Manager.PlState == PlayerManager.PlayerState.HAIBOKU) StartCoroutine(KO(1));
        if (_pl1Manager.PlState == PlayerManager.PlayerState.HAIBOKU) StartCoroutine(KO(2));
    }

    IEnumerator KO(int whoWin)
    {
        //二重に呼ばれないように念の為
        if (KoStarted) yield break;
        KoStarted = true;

        Time.timeScale = 0f;

        //赤点滅の準備
        SpriteRenderer field1 = _field1P.GetComponent<SpriteRenderer>();
        SpriteRenderer field2 = _field2P.GetComponent<SpriteRenderer>();
        Color field1OriginColor = field1.color;
        Color field2OriginColor = field2.color;
        _field1P.SetActive(true);
        _field2P.SetActive(true);

        Vector3 camOriginPos = _camera.transform.localPosition;

        float elapsed = 0f;
        while (elapsed < _KOtime)
        {
            elapsed += Time.unscaledDeltaTime;

            //赤点滅
            float alpha = Mathf.PingPong(elapsed * 2f / BLINK_CYCLE_TIME, 1f) * BLINK_MAX_ALPHA;
            Color blinkColor = new Color(1f, 0f, 0f, alpha);
            field1.color = blinkColor;
            field2.color = blinkColor;

            //画面振動
            float damper = 1f - elapsed / _KOtime;
            Vector2 shakeOffset = Random.insideUnitCircle * _shakePower * damper;
            _camera.transform.localPosition = camOriginPos + (Vector3)shakeOffset;

            yield return null;
        }

        //戻す
        _camera.transform.localPosition = camOriginPos;
        field1.color = field1OriginColor;
        field2.color = field2OriginColor;
        // ……(チームメンバー担当のため省略)……
        //リザルトへ
        Result(whoWin);
    }
    // ……(抜粋外)……

    ///<summary>ScoreResultにいくための入力受け取り </summary>
    IEnumerator WaitForAnyInput()
    {
        yield return new WaitForSecondsRealtime(_stayButtonTime);

        foreach (var pl in PlList)
        {
            if (pl == null) continue;
            pl.SwitchCurrentActionMap("Result");
        }

        // 演出スタート待ち
        yield return WaitForProceed();

        // 降下 → 画面を埋め尽くす → 溜め
        bool virusSkipRequested = false;
        if (VirusTransitionController.Instance != null)
        {
            Coroutine virusStage = StartCoroutine(
                VirusTransitionController.Instance.PlayRainAndFill(() => virusSkipRequested)
            );
            yield return WaitForProceedOrCoroutine(virusStage, () => virusSkipRequested = true);
        }
        else
        {
            Debug.LogWarning("MainGameManager: VirusTransitionController.Instanceが見つかりません。");
        }

        // 結果データをScriptableObjectへ書き込み
        WriteMatchResultData();

        // PlayerInputをシーンをまたいで引き継ぐ
        ResultPlayerInputBridge.Instance.Carry(PlList);


        // 画面がウィルスで完全に覆われている状態でシーン遷移
        yield return SceneManager.LoadSceneAsync(_scoreResultSceneName);
    }

    // ……(抜粋外)……

    /// <summary>
    /// 対象のコルーチンが自然に完了するまで待つ。
    /// その間にProceed入力があった場合はonSkipを一度だけ呼ぶ
    /// コルーチンは強制停止せず、フラグを見て内部で短縮完了させる。
    /// </summary>
    IEnumerator WaitForProceedOrCoroutine(Coroutine target, System.Action onSkip)
    {
        bool skipTriggered = false;
        System.Action<InputAction.CallbackContext> onProceed = _ =>
        {
            if (skipTriggered) return;
            skipTriggered = true;
            onSkip?.Invoke();
        };

        foreach (var pl in PlList)
        {
            if (pl == null) continue;
            pl.actions.FindActionMap("Result")["Proceed"].performed += onProceed;
        }

        if (target != null)
        {
            yield return target;
        }

        foreach (var pl in PlList)
        {
            if (pl == null) continue;
            pl.actions.FindActionMap("Result")["Proceed"].performed -= onProceed;
        }
    }

    /// <summary>Proceedが押されるまで待つ</summary>
    IEnumerator WaitForProceed()
    {
        bool inputReceived = false;
        System.Action<InputAction.CallbackContext> onProceed = _ => inputReceived = true;

        foreach (var pl in PlList)
        {
            if (pl == null) continue;
            pl.actions.FindActionMap("Result")["Proceed"].performed += onProceed;
        }

        while (!inputReceived)
            yield return null;

        foreach (var pl in PlList)
        {
            if (pl == null) continue;
            pl.actions.FindActionMap("Result")["Proceed"].performed -= onProceed;
        }
    }

    // ……(抜粋外)……
}
