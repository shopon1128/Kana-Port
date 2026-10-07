using TMPro;
using UnityEngine;

/// <summary>
/// 敵AIがこの試合で、どの戦術に偏って時間を使ったかをレーダーチャートで見せる。
/// 分析ページ(ai_final_project_2026のクラスタ別レーダー)と同じ読み方にそろえてある:
///  ・半径は「全試合平均に対する倍率」の対数(log2)。
///    割合をそのまま半径にすると、どの試合でも大きい索敵・離脱に形が支配される。
///    倍率をそのまま半径にすると、今度は平均が小さい接近・強襲(1〜2%)だけがトゲになる。
///    対数にすると「2倍」と「1/2倍」が基準円から等距離になり、どの軸も同じ重みで読める
///  ・破線の円が全試合平均(1倍)。外側ほどこの試合でその戦術に偏った
/// 軸ラベルには倍率ではなく実際の割合(%)を添え、形と数字の両方で読めるようにする
/// </summary>
public class TacticRadarView
{
    private const float MIN_LOG_RATIO = -2f;//中心 = 平均の1/4倍以下(使わなかった戦術もここ)
    private const float MAX_LOG_RATIO = 2f;//外周 = 平均の4倍以上(はみ出しは外周に張り付かせる)
    private const float REFERENCE_LOG_RATIO = 0f;//基準円 = 平均の1倍
    private static readonly float[] GRID_LOG_RATIOS = { -1f, 1f, 2f };//1/2倍・2倍・4倍(外周)

    private const float LABEL_GAP = 14f;//外周とラベルの間
    private const float LABEL_WIDTH = 200f;
    private const float LABEL_HEIGHT = 32f;
    private const float LABEL_FONT_SIZE = 26f;
    private const float LABEL_ALIGN_THRESHOLD = 0.3f;//軸の向きの横成分がこれより大きければ左右寄せにする
    private const string SHARE_SIZE_TAG = "<size=85%>";//割合は戦術名より一段小さく

    private static readonly Color GRID_COLOR = new(1f, 1f, 1f, 0.28f);
    private static readonly Color REFERENCE_COLOR = new(1f, 1f, 1f, 0.9f);

    private readonly RadarChartGraphic _chart;
    private readonly TMP_Text[] _labels;

    /// <summary>
    /// a_areaの上下中央に、レーダー(半径a_radius)と軸ラベルを組み立てる
    /// </summary>
    public TacticRadarView(RectTransform a_area, TMP_FontAsset a_font, float a_radius, Color a_textColor)
    {
        float chartTop = (a_area.rect.height - a_radius * 2f) * 0.5f;

        GameObject chartObject = new("TacticRadar", typeof(RectTransform));
        chartObject.transform.SetParent(a_area, false);
        _chart = chartObject.AddComponent<RadarChartGraphic>();
        _chart.raycastTarget = false;
        UiBuilder.SetTopCentered(_chart.rectTransform, chartTop, a_radius * 2f, a_radius * 2f);

        float[] gridLevels = new float[GRID_LOG_RATIOS.Length];
        for (int i = 0; i < GRID_LOG_RATIOS.Length; i++)
        {
            gridLevels[i] = ToRadiusRate(GRID_LOG_RATIOS[i]);
        }
        _chart.SetScale(gridLevels, ToRadiusRate(REFERENCE_LOG_RATIO), GRID_COLOR, REFERENCE_COLOR);

        _labels = new TMP_Text[TacticCatalog.Count];
        for (int i = 0; i < TacticCatalog.Count; i++)
        {
            _labels[i] = UiBuilder.CreateText(_chart.transform, $"Label_{TacticCatalog.Label(TacticCatalog.ALL[i])}", "",
                a_font, LABEL_FONT_SIZE, a_textColor, FontStyles.Bold);
            _labels[i].textWrappingMode = TextWrappingModes.NoWrap;
            PlaceLabel(_labels[i], i, a_radius + LABEL_GAP);
        }
    }

    public void Show(MatchResultSummary a_summary)
    {
        float[] values = new float[TacticCatalog.Count];
        for (int i = 0; i < TacticCatalog.Count; i++)
        {
            Tactic tactic = TacticCatalog.ALL[i];
            float share = a_summary.TacticShare(tactic);
            values[i] = ToRadiusRate(ToLogRatio(share, TacticBaseline.AverageShare(tactic)));

            string name = TacticCatalog.DisplayName(TacticCatalog.Label(tactic));
            _labels[i].text = $"{name} {SHARE_SIZE_TAG}{share * 100f:F0}%</size>";
        }
        _chart.SetValues(values);
    }

    public void SetDataColor(Color a_color)
    {
        _chart.DataColor = a_color;
    }

    //この試合の割合が平均の何倍かを、log2で返す(下限で打ち切るので、使わなかった戦術(0%)も中心に描ける)
    private static float ToLogRatio(float a_share, float a_averageShare)
    {
        //平均を持たない戦術は偏りを測れないので、平均と同じ(1倍)として基準円に置く
        if (a_averageShare <= 0f)
        {
            return REFERENCE_LOG_RATIO;
        }
        float minRatio = Mathf.Pow(2f, MIN_LOG_RATIO);
        return Mathf.Log(Mathf.Max(a_share / a_averageShare, minRatio), 2f);
    }

    //log2の倍率を、中心0〜外周1の半径の比率にする
    private static float ToRadiusRate(float a_logRatio)
    {
        return Mathf.InverseLerp(MIN_LOG_RATIO, MAX_LOG_RATIO, a_logRatio);
    }

    /// <summary>
    /// ラベルを軸の延長線上に置く。軸の向きに合わせて基準点(pivot)と文字寄せを変え、
    /// 右側の軸は左寄せ・左側の軸は右寄せ・上下の軸は中央寄せにして、文字がチャートに重ならないようにする
    /// </summary>
    private static void PlaceLabel(TMP_Text a_label, int a_index, float a_distance)
    {
        Vector2 dir = RadarChartGraphic.AxisDirection(a_index, TacticCatalog.Count);
        float pivotX;
        if (dir.x > LABEL_ALIGN_THRESHOLD)
        {
            pivotX = 0f;
            a_label.alignment = TextAlignmentOptions.MidlineLeft;
        }
        else if (dir.x < -LABEL_ALIGN_THRESHOLD)
        {
            pivotX = 1f;
            a_label.alignment = TextAlignmentOptions.MidlineRight;
        }
        else
        {
            pivotX = 0.5f;
            a_label.alignment = TextAlignmentOptions.Midline;
        }

        //縦は軸の向きに応じて連続的に寄せる(上の軸は下端、下の軸は上端が軸の延長点に来る)
        RectTransform rect = a_label.rectTransform;
        Vector2 center = new(0.5f, 0.5f);
        rect.anchorMin = center;
        rect.anchorMax = center;
        rect.pivot = new Vector2(pivotX, 0.5f - dir.y * 0.5f);
        rect.sizeDelta = new Vector2(LABEL_WIDTH, LABEL_HEIGHT);
        rect.anchoredPosition = dir * a_distance;
    }
}
