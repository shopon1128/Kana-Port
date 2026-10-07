using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// フィールド(外周の壁と遮蔽物)をランダム生成し、セル単位の通行可否・経路探索・視線判定を提供する
/// </summary>
public class MapGenerator : MonoBehaviour
{
    //遮蔽物の形状定義(セル単位の相対オフセット)
    private static readonly Vector2Int[][] OBSTACLE_SHAPES =
    {
        //小型
        new Vector2Int[] { new(0, 0), new(1, 0), new(0, 1), new(1, 1) },//四角(2x2)
        new Vector2Int[] { new(0, 0), new(1, 0), new(2, 0) },//横長(1x3)
        new Vector2Int[] { new(0, 1), new(1, 1), new(2, 1), new(1, 0) },//T字(3x2)
        new Vector2Int[] { new(0, 0), new(0, 1), new(0, 2), new(1, 0) },//L字(2x3)
        //中型
        new Vector2Int[] { new(0, 0), new(1, 0), new(2, 0), new(0, 1), new(1, 1), new(2, 1), new(0, 2), new(1, 2), new(2, 2) },//四角(3x3)
        new Vector2Int[] { new(0, 0), new(1, 0), new(2, 0), new(3, 0), new(4, 0) },//横長(1x5)
        new Vector2Int[] { new(0, 0), new(0, 1), new(0, 2), new(0, 3), new(1, 0), new(2, 0) },//L字(3x4)
        new Vector2Int[] { new(1, 0), new(0, 1), new(1, 1), new(2, 1), new(1, 2) },//十字(3x3)
        //大型
        new Vector2Int[]
        {
            new(1, 0), new(2, 0), new(1, 1), new(2, 1), new(1, 2), new(2, 2), new(1, 3), new(2, 3), new(1, 4), new(2, 4),
            new(0, 5), new(1, 5), new(2, 5), new(3, 5), new(4, 5), new(0, 6), new(1, 6), new(2, 6), new(3, 6), new(4, 6),
        },//T字(5x7)
    };
    private const int MAX_PLACEMENT_ATTEMPTS = 100;//1つの形状につき配置を試みる最大回数
    private static readonly Vector2Int[] FOUR_DIRECTIONS = { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right };
    private static readonly Vector2Int[] FOUR_DIAGONALS = { new(1, 1), new(1, -1), new(-1, 1), new(-1, -1) };
    private const int NEAR_WALL_MOVE_COST = 2;//壁際セルの追加移動コスト。経路探索で開けた場所を通りたがるようにする
    private const int MAX_PATHFINDING_STEPS = 5000;//A*の探索打ち切り上限(安全弁)
    private const int RANDOM_CELL_ATTEMPTS = 100;//歩行可能セルの抽選を試みる最大回数

    public static MapGenerator Instance { get; private set; }

    [SerializeField] private MapData _mapData;
    [SerializeField] private Transform _playerSpawnPoint;//安全地帯の中心とするプレイヤーの出現地点
    [SerializeField] private Transform _enemySpawnPoint;//安全地帯の中心とする敵の出現地点(未設定なら無視)

    private readonly HashSet<Vector2Int> _wallCells = new();//壁が置かれたセル(経路探索用)
    private readonly HashSet<Vector2Int> _nearWallCells = new();//壁に隣接する歩行可能セル

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        GenerateBoundary();
        GenerateObstacles();
        FillUnreachableAreas();
        CacheNearWallCells();
    }

    private void GenerateBoundary()
    {
        int width = _mapData.FieldWidth;
        int height = _mapData.FieldHeight;

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                bool isEdge = x == 0 || x == width - 1 || y == 0 || y == height - 1;
                if (isEdge)
                {
                    SpawnWall(_mapData.BoundaryWallPrefab, x, y);
                }
            }
        }
    }

    private void GenerateObstacles()
    {
        //出現地点(プレイヤー・敵)の周辺セルは安全地帯として除外する
        List<Vector2Int> spawnCells = new() { _mapData.WorldToCell(_playerSpawnPoint.position) };
        if (_enemySpawnPoint != null)
        {
            spawnCells.Add(_mapData.WorldToCell(_enemySpawnPoint.position));
        }

        HashSet<Vector2Int> blockedCells = new();
        int obstacleCount = Random.Range(_mapData.MinObstacleCount, _mapData.MaxObstacleCount + 1);

        for (int i = 0; i < obstacleCount; i++)
        {
            TryPlaceObstacleShape(spawnCells, blockedCells);
        }
    }

    private void TryPlaceObstacleShape(List<Vector2Int> a_spawnCells, HashSet<Vector2Int> a_blockedCells)
    {
        for (int attempt = 0; attempt < MAX_PLACEMENT_ATTEMPTS; attempt++)
        {
            Vector2Int[] shape = OBSTACLE_SHAPES[Random.Range(0, OBSTACLE_SHAPES.Length)];
            int rotation = Random.Range(0, 4);
            Vector2Int anchor = new(Random.Range(1, _mapData.FieldWidth - 1), Random.Range(1, _mapData.FieldHeight - 1));

            if (TryResolveShapeCells(shape, rotation, anchor, a_spawnCells, a_blockedCells, out List<Vector2Int> cells))
            {
                foreach (Vector2Int cell in cells)
                {
                    SpawnWall(_mapData.ObstacleWallPrefab, cell.x, cell.y);
                }
                foreach (Vector2Int cell in cells)
                {
                    a_blockedCells.Add(cell);
                }
                return;
            }
        }
    }

    private bool TryResolveShapeCells(Vector2Int[] a_shape, int a_rotation, Vector2Int a_anchor, List<Vector2Int> a_spawnCells, HashSet<Vector2Int> a_blockedCells, out List<Vector2Int> a_cells)
    {
        a_cells = new List<Vector2Int>(a_shape.Length);

        foreach (Vector2Int offset in a_shape)
        {
            Vector2Int cell = a_anchor + RotateOffset(offset, a_rotation);
            //境界壁そのものの上には置けないが、壁に接するのは可(隙間を作らなければ通行の妨げにならない)
            bool insideField = cell.x >= 1 && cell.x <= _mapData.FieldWidth - 2
                && cell.y >= 1 && cell.y <= _mapData.FieldHeight - 2;
            bool inSafeZone = IsInSafeZone(cell, a_spawnCells);

            if (!insideField || inSafeZone || a_blockedCells.Contains(cell))
            {
                return false;
            }
            a_cells.Add(cell);
        }
        return !CreatesNarrowGap(a_cells, a_blockedCells) && !CreatesDiagonalPinch(a_cells, a_blockedCells);
    }

    /// <summary>
    /// 置いた結果、斜めの角だけで接する配置ができるか。
    /// 角同士が触れると、そこを orthogonal に通り抜けようとしたキャラが尖った角を擦って引っかかる
    /// 斜め方向が壁なら、間の縦横どちらかも壁であること=角が面で繋がっていることを求める
    /// </summary>
    private bool CreatesDiagonalPinch(List<Vector2Int> a_cells, HashSet<Vector2Int> a_occupiedCells)
    {
        foreach (Vector2Int cell in a_cells)
        {
            foreach (Vector2Int diagonal in FOUR_DIAGONALS)
            {
                if (!IsWallCell(cell + diagonal, a_cells, a_occupiedCells))
                {
                    continue;
                }
                bool connectedByFace = IsWallCell(cell + new Vector2Int(diagonal.x, 0), a_cells, a_occupiedCells)
                    || IsWallCell(cell + new Vector2Int(0, diagonal.y), a_cells, a_occupiedCells);
                if (!connectedByFace)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 置いた結果、幅1マスの隙間ができるか。
    /// 「2マス先が壁なのに間の1マスが空いている」=幅1の通路(または閉じた穴)ができる状態を禁じる。
    /// 幅1の通路は幾何的には通れる(実効18.19 > 体10.6)が、余裕が片側3.8しかなく
    /// AI・ボットが引っかかって抜け出せなくなる。逆に隙間0(密着)は問題ないので、
    /// 遮蔽同士がくっついて大きな塊になるのも、壁際に寄せるのも許される
    /// </summary>
    private bool CreatesNarrowGap(List<Vector2Int> a_cells, HashSet<Vector2Int> a_occupiedCells)
    {
        foreach (Vector2Int cell in a_cells)
        {
            foreach (Vector2Int direction in FOUR_DIRECTIONS)
            {
                Vector2Int middle = cell + direction;
                Vector2Int far = cell + direction * 2;
                if (IsWallCell(far, a_cells, a_occupiedCells) && !IsWallCell(middle, a_cells, a_occupiedCells))
                {
                    return true;
                }
            }
        }
        return false;
    }

    //そのセルが壁として塞がっているか(既存の遮蔽・これから置く遮蔽・外周の境界壁)
    private bool IsWallCell(Vector2Int a_cell, List<Vector2Int> a_cells, HashSet<Vector2Int> a_occupiedCells)
    {
        bool isBoundary = a_cell.x <= 0 || a_cell.y <= 0
            || a_cell.x >= _mapData.FieldWidth - 1 || a_cell.y >= _mapData.FieldHeight - 1;
        return isBoundary || a_occupiedCells.Contains(a_cell) || a_cells.Contains(a_cell);
    }

    private bool IsInSafeZone(Vector2Int a_cell, List<Vector2Int> a_spawnCells)
    {
        foreach (Vector2Int spawnCell in a_spawnCells)
        {
            if (Vector2Int.Distance(a_cell, spawnCell) <= _mapData.SafeZoneRadius)
            {
                return true;
            }
        }
        return false;
    }

    private Vector2Int RotateOffset(Vector2Int a_offset, int a_rotation)
    {
        Vector2Int result = a_offset;
        for (int i = 0; i < a_rotation; i++)
        {
            result = new Vector2Int(-result.y, result.x);
        }
        return result;
    }

    /// <summary>
    /// どこからも辿り着けない空洞を遮蔽で埋める。
    /// 遮蔽同士の密着を許したことで、輪のように囲まれた内側ができることがある。
    /// 幅1の隙間ではないのでルール上は合法だが、誰も入れない空洞は見た目に不自然で、
    /// 経路探索にとっても意味のない領域になるため塞ぐ
    /// </summary>
    private void FillUnreachableAreas()
    {
        //プレイヤーの出現地点から歩いて行ける範囲を塗り広げる(出現地点は安全地帯なので必ず床)
        Vector2Int start = _mapData.WorldToCell(_playerSpawnPoint.position);
        HashSet<Vector2Int> reachable = new();
        Queue<Vector2Int> frontier = new();
        reachable.Add(start);
        frontier.Enqueue(start);

        while (frontier.Count > 0)
        {
            Vector2Int current = frontier.Dequeue();
            foreach (Vector2Int direction in FOUR_DIRECTIONS)
            {
                Vector2Int next = current + direction;
                if (IsWalkable(next) && reachable.Add(next))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        //辿り着けなかった床を遮蔽で埋める(列挙中に_wallCellsを変えないよう、先に集めてから置く)
        List<Vector2Int> enclosed = new();
        for (int x = 1; x < _mapData.FieldWidth - 1; x++)
        {
            for (int y = 1; y < _mapData.FieldHeight - 1; y++)
            {
                Vector2Int cell = new(x, y);
                if (IsWalkable(cell) && !reachable.Contains(cell))
                {
                    enclosed.Add(cell);
                }
            }
        }
        foreach (Vector2Int cell in enclosed)
        {
            SpawnWall(_mapData.ObstacleWallPrefab, cell.x, cell.y);
        }
    }

    private void SpawnWall(Wall a_prefab, int a_cellX, int a_cellY)
    {
        Wall wall = Instantiate(a_prefab, _mapData.CellToWorldPosition(a_cellX, a_cellY), Quaternion.identity, transform);
        wall.SetSize(_mapData.CellSize);
        _wallCells.Add(new Vector2Int(a_cellX, a_cellY));
    }

    //---- ここから経路探索用の公開API ----

    public bool IsWalkable(Vector2Int a_cell)
    {
        bool insideField = a_cell.x > 0 && a_cell.x < _mapData.FieldWidth - 1 && a_cell.y > 0 && a_cell.y < _mapData.FieldHeight - 1;
        return insideField && !_wallCells.Contains(a_cell);
    }

    public Vector2Int GetRandomWalkableCell()
    {
        for (int i = 0; i < RANDOM_CELL_ATTEMPTS; i++)
        {
            Vector2Int cell = new(Random.Range(1, _mapData.FieldWidth - 1), Random.Range(1, _mapData.FieldHeight - 1));
            if (IsWalkable(cell))
            {
                return cell;
            }
        }
        return new Vector2Int(_mapData.FieldWidth / 2, _mapData.FieldHeight / 2);
    }

    /// <summary>
    /// A*でセル単位の経路を探索する。成功時はa_resultに開始セルを除いた経路が入る。
    /// a_extraCellCostで呼び出し側の事情(危険地帯の記憶など)による追加コストを差し込める
    /// </summary>
    public bool TryFindPath(Vector2Int a_start, Vector2Int a_goal, List<Vector2Int> a_result, Func<Vector2Int, int> a_extraCellCost = null)
    {
        return FindPath(a_start, a_goal, a_result, null, 0, a_extraCellCost);
    }

    /// <summary>
    /// 隠密経路探索。a_viewerCellから見えるセルに追加コストをかけ、なるべく遮蔽の陰を通る経路を返す
    /// </summary>
    public bool TryFindStealthPath(Vector2Int a_start, Vector2Int a_goal, Vector2Int a_viewerCell, int a_seenCellCost, List<Vector2Int> a_result, Func<Vector2Int, int> a_extraCellCost = null)
    {
        return FindPath(a_start, a_goal, a_result, a_viewerCell, a_seenCellCost, a_extraCellCost);
    }

    private bool FindPath(Vector2Int a_start, Vector2Int a_goal, List<Vector2Int> a_result, Vector2Int? a_viewerCell, int a_seenCellCost, Func<Vector2Int, int> a_extraCellCost)
    {
        a_result.Clear();
        if (!IsWalkable(a_start) || !IsWalkable(a_goal))
        {
            return false;
        }
        if (a_start == a_goal)
        {
            return true;
        }

        Dictionary<Vector2Int, int> gScores = new() { [a_start] = 0 };
        Dictionary<Vector2Int, Vector2Int> cameFrom = new();
        List<Vector2Int> openCells = new() { a_start };
        HashSet<Vector2Int> closedCells = new();

        for (int step = 0; step < MAX_PATHFINDING_STEPS && openCells.Count > 0; step++)
        {
            //fスコア最小のセルを取り出す(フィールドが小さいため線形走査で充分)
            int bestIndex = 0;
            int bestScore = int.MaxValue;
            for (int i = 0; i < openCells.Count; i++)
            {
                int score = gScores[openCells[i]] + Heuristic(openCells[i], a_goal);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }
            Vector2Int current = openCells[bestIndex];
            openCells.RemoveAt(bestIndex);

            if (current == a_goal)
            {
                BuildPath(cameFrom, a_start, a_goal, a_result);
                return true;
            }
            closedCells.Add(current);

            foreach (Vector2Int direction in FOUR_DIRECTIONS)
            {
                Vector2Int next = current + direction;
                if (!IsWalkable(next) || closedCells.Contains(next))
                {
                    continue;
                }
                //キャラのコライダーはセルより縦に大きいため、高さ1マスの隙間は横方向に通り抜けられない
                if (direction.y == 0 && !HasVerticalClearance(current, next))
                {
                    continue;
                }

                int moveCost = 1 + (_nearWallCells.Contains(next) ? NEAR_WALL_MOVE_COST : 0);
                if (a_viewerCell.HasValue && IsCellLineClear(a_viewerCell.Value, next))
                {
                    //隠密経路: 監視者から見えるセルは通行コストを上げて避けさせる
                    moveCost += a_seenCellCost;
                }
                if (a_extraCellCost != null)
                {
                    moveCost += a_extraCellCost(next);
                }
                int nextScore = gScores[current] + moveCost;
                if (gScores.TryGetValue(next, out int oldScore) && nextScore >= oldScore)
                {
                    continue;
                }

                gScores[next] = nextScore;
                cameFrom[next] = current;
                if (!openCells.Contains(next))
                {
                    openCells.Add(next);
                }
            }
        }
        return false;
    }

    private int Heuristic(Vector2Int a_from, Vector2Int a_to)
    {
        return Mathf.Abs(a_from.x - a_to.x) + Mathf.Abs(a_from.y - a_to.y);
    }

    //横移動時に体の縦幅を確保できるか。移動元・移動先の両方で、同じ側(上または下)に空きセルが連続している必要がある
    private bool HasVerticalClearance(Vector2Int a_from, Vector2Int a_to)
    {
        for (int dy = -1; dy <= 1; dy += 2)
        {
            Vector2Int fromSide = new(a_from.x, a_from.y + dy);
            Vector2Int toSide = new(a_to.x, a_to.y + dy);
            if (IsWalkable(fromSide) && IsWalkable(toSide))
            {
                return true;
            }
        }
        return false;
    }

    private void BuildPath(Dictionary<Vector2Int, Vector2Int> a_cameFrom, Vector2Int a_start, Vector2Int a_goal, List<Vector2Int> a_result)
    {
        Vector2Int current = a_goal;
        while (current != a_start)
        {
            a_result.Add(current);
            current = a_cameFrom[current];
        }
        a_result.Reverse();
    }

    /// <summary>
    /// 2つのセルの間に壁がないか(グリッド上の簡易視線判定。Bresenhamの線分走査)
    /// </summary>
    public bool IsCellLineClear(Vector2Int a_from, Vector2Int a_to)
    {
        int x = a_from.x;
        int y = a_from.y;
        int dx = Mathf.Abs(a_to.x - x);
        int dy = -Mathf.Abs(a_to.y - y);
        int stepX = x < a_to.x ? 1 : -1;
        int stepY = y < a_to.y ? 1 : -1;
        int error = dx + dy;

        while (true)
        {
            if (_wallCells.Contains(new Vector2Int(x, y)))
            {
                return false;
            }
            if (x == a_to.x && y == a_to.y)
            {
                return true;
            }
            int error2 = 2 * error;
            if (error2 >= dy)
            {
                error += dy;
                x += stepX;
            }
            if (error2 <= dx)
            {
                error += dx;
                y += stepY;
            }
        }
    }

    /// <summary>
    /// a_fromから最も近い「a_viewerCellから見えないセル」を幅優先で探す(視界切り離脱用)。
    /// 境界すれすれの死角はすぐ再発見されるため、隣接セルまで隠れている「深い死角」を優先する
    /// </summary>
    public bool TryFindNearestHiddenCell(Vector2Int a_from, Vector2Int a_viewerCell, out Vector2Int a_result, Func<Vector2Int, bool> a_avoid = null)
    {
        a_result = a_from;
        bool foundShallow = false;
        Vector2Int shallowResult = a_from;

        Queue<Vector2Int> queue = new();
        HashSet<Vector2Int> visited = new() { a_from };
        queue.Enqueue(a_from);

        for (int step = 0; step < MAX_PATHFINDING_STEPS && queue.Count > 0; step++)
        {
            Vector2Int current = queue.Dequeue();
            bool isCandidate = a_avoid == null || !a_avoid(current);//避けたいセル(危険地帯など)は候補にしない(探索は続ける)
            if (isCandidate && !IsCellLineClear(a_viewerCell, current))
            {
                if (IsDeepHiddenCell(current, a_viewerCell))
                {
                    a_result = current;
                    return true;
                }
                if (!foundShallow)
                {
                    //深い死角が見つからなかった場合の代替として、最初に見つけた浅い死角を覚えておく
                    foundShallow = true;
                    shallowResult = current;
                }
            }

            foreach (Vector2Int direction in FOUR_DIRECTIONS)
            {
                Vector2Int next = current + direction;
                if (IsWalkable(next) && !visited.Contains(next))
                {
                    visited.Add(next);
                    queue.Enqueue(next);
                }
            }
        }

        a_result = shallowResult;
        return foundShallow;
    }

    //歩行可能な隣接セルもすべて隠れている「深い死角」か(境界すれすれではないか)
    private bool IsDeepHiddenCell(Vector2Int a_cell, Vector2Int a_viewerCell)
    {
        foreach (Vector2Int direction in FOUR_DIRECTIONS)
        {
            Vector2Int next = a_cell + direction;
            if (IsWalkable(next) && IsCellLineClear(a_viewerCell, next))
            {
                return false;
            }
        }
        return true;
    }

    //壁に隣接する歩行可能セルを事前計算しておく(経路コスト算出用)
    private void CacheNearWallCells()
    {
        foreach (Vector2Int wall in _wallCells)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    Vector2Int cell = new(wall.x + dx, wall.y + dy);
                    if (IsWalkable(cell))
                    {
                        _nearWallCells.Add(cell);
                    }
                }
            }
        }
    }
}
