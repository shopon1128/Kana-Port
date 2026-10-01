using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// リザルト→スコアリザルト間のウィルス降り注ぎ＆弾ける演出。
/// </summary>
public class VirusTransitionController : MonoBehaviour
{
    public static VirusTransitionController Instance { get; private set; }

    [Header("ウィルス画像")]
    [SerializeField] private Sprite[] _virusSprites = new Sprite[5];

    [Header("生成設定")]
    [SerializeField] private int _virusCount = 100;//生成するウィルスの総数
    [SerializeField] private float _virusBaseSize = 100f;//ウィルスの基本サイズ
    [SerializeField] private float _sizeRandomMin = 0.6f;//サイズの倍率(最小)
    [SerializeField] private float _sizeRandomMax = 1.4f;//サイズの倍率(最大)

    [Header("降下フェーズ")]
    [SerializeField] private float _spawnWindow = 1.0f;//生成を行う時間幅(先頭から)
    [SerializeField] private float _fallSpeedMin = 900f;//平均落下速度(最小)。
    [SerializeField] private float _fallSpeedMax = 1800f;//平均落下速度(最大)
    [SerializeField] private float _fallEasePower = 4f;//減速カーブの強さ(1で等速、大きいほど終盤の減速が強い)
    [SerializeField] private float _rotationSpeed = 400f;//落下中の回転速度(deg/s)
    [SerializeField] private float _rotationEasePower = 2.8f;//回転の収まりの早さ(0で減衰なし、大きいほど早く収まる)

    [Header("溜めフェーズ")]
    [SerializeField] private float _fillPause = 0.35f;//画面を埋めた後、シーン遷移までの間(s)
    [SerializeField] private float _skipFillTime = 0.1f;//スキップ時の溜め時間(s)

    [Header("弾けるフェーズ")]
    [SerializeField] private float _burstDuration = 0.5f;//弾ける演出の時間
    [SerializeField] private float _burstSpeedMin = 1500f;//弾ける速度(最小)
    [SerializeField] private float _burstSpeedMax = 3500f;//弾ける速度(最大)
    [SerializeField] private float _burstScaleMultiplier = 1.5f;//弾ける時の拡大倍率

    private RectTransform _canvasRect;
    private VirusInstance[] _instances;
    private GameObject _container;//演出用の親オブジェクト（ウィルス本体をまとめる）
    private System.Func<bool> _skipCheck;//ステージ1のスキップ判定用

    /// <summary>ウィルス個体ごとのデータ</summary>
    private class VirusInstance
    {
        public GameObject Go;
        public RectTransform Rt;
        public Image Img;
        public Vector2 TargetPos;
        public Vector2 StartPos;
        public float FallDuration;
        public float FallElapsed;
        public float RotSpeed;
        public bool Landed;
        // 弾ける用
        public Vector2 BurstDir;
        public float BurstSpeed;
    }

    void Awake()
    {
        // ……(チームメンバー担当のため省略)……
        DontDestroyOnLoad(gameObject);
    }

    // 降下 → 画面を埋め尽くす → 溜め
    /// <summary>
    /// ウィルスを降らせて画面を埋め尽くし、少し溜めるところまでを行う。
    /// </summary>
    public IEnumerator PlayRainAndFill(System.Func<bool> skipCheck = null)
    {
        _skipCheck = skipCheck;

        _canvasRect = GetComponent<RectTransform>();

        // 演出オブジェクトをまとめる
        _container = new GameObject("VirusContainer");
        _container.transform.SetParent(_canvasRect, false);
        RectTransform containerRt = _container.AddComponent<RectTransform>();
        containerRt.anchorMin = Vector2.zero;
        containerRt.anchorMax = Vector2.one;
        containerRt.offsetMin = Vector2.zero;
        containerRt.offsetMax = Vector2.zero;

        // 最前面に表示
        _container.transform.SetAsLastSibling();

        _instances = new VirusInstance[_virusCount];

        yield return RainPhase();

        // 溜め
        // スキップされていたら短く、それ以外は通常
        float pauseDuration = IsSkipRequested() ? _skipFillTime : _fillPause;
        float waited = 0f;
        while (waited < pauseDuration)
        {
            // 溜めている最中に新たにスキップされた場合も、残り時間を短縮する
            if (IsSkipRequested() && pauseDuration > _skipFillTime)
            {
                pauseDuration = _skipFillTime;
            }
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
    }

    private bool IsSkipRequested()
    {
        return _skipCheck != null && _skipCheck();
    }

    //  弾ける（ScoreResultシーン側から呼ぶ）
    /// <summary>
    /// 弾ける演出を再生し、終わったら後片付けする。
    /// onReadyToReveal は、まだウィルスで画面が隠れている状態のうちに一度だけ呼ばれる。
    /// </summary>
    public IEnumerator PlayBurst(System.Action onReadyToReveal = null)
    {
        onReadyToReveal?.Invoke();

        if (_container != null)
        {
            yield return BurstPhase();
        }

        Cleanup();
    }

    // ウィルスを上から降らせ、画面を埋め尽くす
    private IEnumerator RainPhase()
    {
        float canvasW = _canvasRect.rect.width;
        float canvasH = _canvasRect.rect.height;
        float halfW = canvasW / 2f;
        float halfH = canvasH / 2f;

        int spawned = 0;
        int landedCount = 0;
        float elapsed = 0f;
        float spawnInterval = _spawnWindow / _virusCount;

        // 落下先を画面全体に均等配置（グリッド＋ランダムずらし）
        int cols = Mathf.CeilToInt(Mathf.Sqrt(_virusCount * (canvasW / canvasH)));
        int rows = Mathf.CeilToInt((float)_virusCount / cols);
        float cellH = canvasH / rows;


        // 総数を各行へ均等に配り、行ごとに全幅へ敷き詰める
        int baseItemsPerRow = _virusCount / rows;
        int extraRows = _virusCount % rows;

        Vector2[] targetPositions = new Vector2[_virusCount];
        int index = 0;
        for (int row = 0; row < rows; row++)
        {
            int itemsInRow = baseItemsPerRow + (row < extraRows ? 1 : 0);
            float cellW = canvasW / itemsInRow;
            for (int col = 0; col < itemsInRow; col++)
            {
                float x = -halfW + cellW * (col + 0.5f) + Random.Range(-cellW * 0.3f, cellW * 0.3f);
                float y = -halfH + cellH * (row + 0.5f) + Random.Range(-cellH * 0.3f, cellH * 0.3f);
                targetPositions[index] = new Vector2(x, y);
                index++;
            }
        }

        // 生成順をランダムにする
        for (int i = targetPositions.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (targetPositions[i], targetPositions[j]) = (targetPositions[j], targetPositions[i]);
        }

        float nextSpawnTime = 0f;

        while (landedCount < _virusCount && !IsSkipRequested())
        {
            elapsed += Time.unscaledDeltaTime;

            //生成
            while (spawned < _virusCount && elapsed >= nextSpawnTime)
            {
                SpawnVirus(spawned, halfW, halfH, targetPositions[spawned]);
                spawned++;
                nextSpawnTime += spawnInterval;
            }

            //落下更新
            for (int i = 0; i < spawned; i++)
            {
                var v = _instances[i];
                if (v == null || v.Landed) continue;

                v.FallElapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(v.FallElapsed / v.FallDuration);

                // イーズアウト:徐々に減速し、着地の瞬間にちょうど速度0になるように
                float eased = 1f - Mathf.Pow(1f - t, _fallEasePower);
                v.Rt.anchoredPosition = Vector2.Lerp(v.StartPos, v.TargetPos, eased);

                // 回転も減衰させ、着地時に止まって見えるようにする
                // 指数が負だと無限大に発散しちゃうため、0以上にクランプする必要がある
                float rotFactor = Mathf.Pow(1f - t, Mathf.Max(0f, _rotationEasePower));
                v.Rt.Rotate(0f, 0f, v.RotSpeed * rotFactor * Time.unscaledDeltaTime);

                if (t >= 1f)
                {
                    v.Landed = true;
                    landedCount++;
                }
            }

            yield return null;
        }

        // スキップ時: 残りを即座に全て着地状態にする（未生成分も含めて生成し切る）
        for (int i = 0; i < _virusCount; i++)
        {
            if (_instances[i] == null || _instances[i].Rt == null)
            {
                SpawnVirus(i, halfW, halfH, targetPositions[i]);
            }
            _instances[i].Rt.anchoredPosition = _instances[i].TargetPos;
            _instances[i].Landed = true;
        }
    }

    private void SpawnVirus(int index, float halfW, float halfH, Vector2 targetPos)
    {
        GameObject go = new GameObject($"Virus_{index}");
        go.transform.SetParent(_container.transform, false);

        Image img = go.AddComponent<Image>();
        img.sprite = _virusSprites[Random.Range(0, _virusSprites.Length)];
        img.preserveAspect = true;
        img.raycastTarget = false;

        RectTransform rt = go.GetComponent<RectTransform>();
        float sizeMul = Random.Range(_sizeRandomMin, _sizeRandomMax);
        float size = _virusBaseSize * sizeMul;
        rt.sizeDelta = new Vector2(size, size);
        rt.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));

        // 画面上端の外からスタート
        float startX = targetPos.x + Random.Range(-30f, 30f);
        float startY = halfH + size;
        Vector2 startPos = new Vector2(startX, startY);
        rt.anchoredPosition = startPos;

        // 着地までの所要時間は[落下距離 ÷ 平均速度]で決める
        float fallSpeed = Random.Range(_fallSpeedMin, _fallSpeedMax);

        var inst = new VirusInstance
        {
            Go = go,
            Rt = rt,
            Img = img,
            TargetPos = targetPos,
            StartPos = startPos,
            FallDuration = (startY - targetPos.y) / fallSpeed,
            FallElapsed = 0f,
            RotSpeed = Random.Range(-_rotationSpeed, _rotationSpeed),
            Landed = false,
            BurstDir = Random.insideUnitCircle.normalized,
            BurstSpeed = Random.Range(_burstSpeedMin, _burstSpeedMax),
        };

        _instances[index] = inst;
    }

    //  全ウィルスが弾ける
    private IEnumerator BurstPhase()
    {
        float elapsed = 0f;

        Vector3[] startScales = new Vector3[_virusCount];
        Vector2[] startPositions = new Vector2[_virusCount];
        for (int i = 0; i < _virusCount; i++)
        {
            if (_instances[i] == null) continue;
            startScales[i] = _instances[i].Rt.localScale;
            startPositions[i] = _instances[i].Rt.anchoredPosition;
        }

        while (elapsed < _burstDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / _burstDuration);
            float eased = 1f - (1f - t) * (1f - t); // EaseOutQuad

            for (int i = 0; i < _virusCount; i++)
            {
                var v = _instances[i];
                if (v == null || v.Go == null) continue;

                Vector2 offset = v.BurstDir * v.BurstSpeed * eased;
                v.Rt.anchoredPosition = startPositions[i] + offset;

                float scale = Mathf.Lerp(1f, _burstScaleMultiplier, eased);
                v.Rt.localScale = startScales[i] * scale;

                Color c = v.Img.color;
                c.a = 1f - eased;
                v.Img.color = c;
            }

            yield return null;
        }
    }

    private void Cleanup()
    {
        if (_container != null)
        {
            Destroy(_container);
        }
        _instances = null;
    }
}
