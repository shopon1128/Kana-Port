using UnityEngine;
using UnityEngine.UI;

public class TitleAnimationManager : MonoBehaviour
{
    [System.Serializable]
    public struct VirusArray
    {
        public GameObject obj;//プレハブ
        public float rate;//生成比率
    }
    [SerializeField] private VirusArray[] _virus;

    [SerializeField] private Transform _spawnPoint;
    [SerializeField] private float _spawnRange;
    [SerializeField] private float _spawnMaxInterval = 2f;
    [SerializeField] private SceneChange _sceneChange;
    [SerializeField] private Transform _rogo;
    private Vector3 _rogoDefPos;
    [SerializeField] float _rogoMoveRange = 0.5f;
    [SerializeField] float _rogoMoveTime = 8f;//1往復するのにかかる時間
    private float _rogoMoveElapsed = 0f;

    // 白転解除用
    [SerializeField] private Image _whiteImage; // 白いUI Image
    [SerializeField] private float _whiteFadeTime = 0.5f; // 白転解除にかかる時間
    private float _whiteFadeTimer = 0f;
    private bool _isWhiteFading = true; // 起動直後は白転解除中とする
    [SerializeField] private float _yoinTime = 2f;//フェード終わってから他のアニメーションが動くのを待つ
    private bool _inYoin = false;

    private float _spawnTimer;

    // 前回のフレームの InTitle の状態を記憶しておく変数
    private bool _wasInTitle = false;

    void Start()
    {
        // SoundManager.Instance.BGM((int)SoundManager.BgmType.Title,loop:false);
        SoundManager.Instance.BGMSeamless((int)SoundManager.BgmType.Title, (int)SoundManager.BgmType.TitleLoop);
        _rogoDefPos = _rogo.transform.position;
        if (_whiteImage != null)
        {
            _whiteImage.gameObject.SetActive(true);
            Color c = _whiteImage.color;
            c.a = 1f;
            _whiteImage.color = c;
        }
        _inYoin = false;

        _spawnTimer = _spawnMaxInterval;
    }

    void Update()
    {
        //白転解除中は、これ以外の処理を一切行わずreturnする
        if (_isWhiteFading)
        {
            UpdateWhiteFade();
            return;
        }
        // InTitle が true から false に切り替わった瞬間を検知
        if (!_sceneChange.InTitle && _wasInTitle)
        {
            ClearAllTitleViruses();
        }
        // 現在の状態を保存しておく
        _wasInTitle = _sceneChange.InTitle;

        // タイトル画面ではないなら何もしない
        if (!_sceneChange.InTitle) return;

        _rogoMoveElapsed += Time.deltaTime;
        MoveRogo();

        _spawnTimer += Time.deltaTime;

        if (_spawnTimer >= _spawnMaxInterval)
        {
            _spawnTimer -= Random.Range(0f, _spawnMaxInterval);
            SpawnVirus();
        }
    }

    private void SpawnVirus()
    {
        float randomX = Random.Range(_spawnPoint.position.x - _spawnRange, _spawnPoint.position.x + _spawnRange);
        Vector3 spawnPosition = new Vector3(randomX, _spawnPoint.position.y, _spawnPoint.position.z);
        GameObject selectedPrefab = ChooseVirusPrefabByRate();
        if (selectedPrefab != null)
        {
            GameObject spawnedVirus = Instantiate(selectedPrefab, spawnPosition, Quaternion.identity);
            spawnedVirus.name += "_Title"; // 先に名前を変えておく

            // VirusManagerを取得してVirusStartを呼ぶ
            if (spawnedVirus.TryGetComponent<VirusManager>(out var virusManager))
            {
                virusManager.VirusStart(null, VirusManager.WhoVirus.PlayerOne); // 引数は使われないのでダミーでOK
            }
        }
    }
    private void MoveRogo()
    {
        // 角速度 = 2π / 周期。Time.time を使うことで一定周期のsin波になる
        float angularSpeed = (2f * Mathf.PI) / _rogoMoveTime;
        float offsetX = Mathf.Sin(_rogoMoveElapsed * angularSpeed) * _rogoMoveRange;

        Vector3 newPos = _rogoDefPos;
        newPos.x += offsetX;
        _rogo.position = newPos;
    }
    // 白転を0.5秒かけて解除する
    private void UpdateWhiteFade()
    {
        float t;
        _whiteFadeTimer += Time.deltaTime;
        if (!_inYoin)
        {
            t = Mathf.Clamp01(_whiteFadeTimer / _whiteFadeTime);


            Color c = _whiteImage.color;
            c.a = Mathf.Lerp(1f, 0f, t);
            _whiteImage.color = c;

            if (t >= 1f)
            {
                _inYoin = true;
                _whiteFadeTimer = 0f;//使い回す
            }

        }
        else
        {
            t = Mathf.Clamp01(_whiteFadeTimer / _yoinTime);
            if (t >= 1f)
            {
                _inYoin = false;
                _isWhiteFading = false;
                _whiteImage.gameObject.SetActive(false);
            }
        }
    }

    /// <summary>
    /// 画面内にあるタイトル用のウイルスをすべて探して消去する
    /// </summary>
    private void ClearAllTitleViruses()
    {
        // ヒエラルキー上のすべての GameObject を検索
        GameObject[] allObjects = GameObject.FindObjectsByType<GameObject>(FindObjectsSortMode.None);

        foreach (GameObject obj in allObjects)
        {
            // 名前が "_Title" で終わる（タイトル用に生成された）オブジェクトを消去
            if (obj.name.EndsWith("_Title"))
            {
                Destroy(obj);
            }
        }
    }

    private GameObject ChooseVirusPrefabByRate()
    {
        float totalRate = 0f;
        foreach (var v in _virus) totalRate += v.rate;

        float randomPoint = Random.Range(0f, totalRate);
        float currentSum = 0f;
        foreach (var v in _virus)
        {
            currentSum += v.rate;
            if (randomPoint <= currentSum) return v.obj;
        }
        return _virus[0].obj;
    }

    private void OnDrawGizmosSelected()
    {
        if (_spawnPoint == null) return;
        Gizmos.color = Color.yellow;
        Vector3 left = new Vector3(_spawnPoint.position.x - _spawnRange, _spawnPoint.position.y, _spawnPoint.position.z);
        Vector3 right = new Vector3(_spawnPoint.position.x + _spawnRange, _spawnPoint.position.y, _spawnPoint.position.z);
        Gizmos.DrawLine(left, right);
    }
}
