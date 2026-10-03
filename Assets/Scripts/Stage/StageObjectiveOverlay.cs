using System.Globalization;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// ステージのクリア条件と進み具合（残り時間・残り討伐数・ボスの HP）を画面の上中央に表示する UI。
/// ECS 側のステージの状態を読み取って表示するだけで、ECS 側の値は変更しない。
/// UI はコードで生成するため、Prefab やシーンの設定は不要。
/// </summary>
[DefaultExecutionOrder(210)]
public sealed class StageObjectiveOverlay : MonoBehaviour
{
    private const string OverlayObjectName = "Stage Objective Overlay";
    private const string CanvasObjectName = "Stage Objective Overlay Canvas";
    private const string BuiltInFontResourceName = "LegacyRuntime.ttf";
    private const float CanvasScaleMatch = 0.5f;

    private static readonly Vector2 RootSize = new Vector2(720f, 96f);
    private static readonly Vector2 BossBarSize = new Vector2(560f, 18f);
    private static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);
    private static readonly Color PanelColor = new Color(0f, 0f, 0f, 0.35f);
    private static readonly Color BossBackColor = new Color(0.18f, 0.08f, 0.04f, 0.95f);
    private static readonly Color BossFrontColor = new Color(0.78f, 0.12f, 0.08f, 1f);

    [Tooltip("表示先 Canvas。未指定なら ScreenSpaceOverlay の Canvas を自動生成する。")]
    [SerializeField]
    private Canvas TargetCanvas;

    private World queryWorld;
    private EntityManager entityManager;
    private EntityQuery timedStageQuery;
    private EntityQuery killCountStageQuery;
    private EntityQuery bossStageQuery;
    private RectTransform root;
    private Text objectiveText;
    private RectTransform bossRoot;
    private Text bossLabel;
    private Image bossFrontImage;
    private bool hasQueries;
    private bool ownsCanvas;

    /// <summary>
    /// シーンに手動で置かなくても、表示役を自動で 1 つだけ作る。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateOverlay()
    {
        if (Object.FindAnyObjectByType<StageObjectiveOverlay>() != null)
        {
            return;
        }

        var gameObject = new GameObject(OverlayObjectName);

        Object.DontDestroyOnLoad(gameObject);
        gameObject.AddComponent<StageObjectiveOverlay>();
    }

    private void OnEnable()
    {
        EnsureCanvas();
        EnsureUi();
    }

    private void OnDestroy()
    {
        DisposeQueries();

        if (ownsCanvas && TargetCanvas != null)
        {
            Destroy(TargetCanvas.gameObject);
        }
    }

    /// <summary>
    /// ステージの種類に応じて表示を切り替える（ボス → 生き残り → 討伐 の順に確認し、最初に見つかったものを表示する）。
    /// </summary>
    private void LateUpdate()
    {
        if (!EnsureCanvas() || !EnsureUi() || !TryCreateQueries())
        {
            SetVisible(false);
            return;
        }

        if (TryShowBossStage())
        {
            return;
        }

        if (TryShowTimedStage())
        {
            return;
        }

        if (TryShowKillCountStage())
        {
            return;
        }

        SetVisible(false);
    }

    /// <summary>
    /// ステージの状態を読むための Query を作る。World が作り直されていたら Query も作り直す。
    /// </summary>
    private bool TryCreateQueries()
    {
        var world = World.DefaultGameObjectInjectionWorld;

        if (world == null || !world.IsCreated)
        {
            DisposeQueries();
            return false;
        }

        if (hasQueries && queryWorld == world)
        {
            return true;
        }

        DisposeQueries();
        queryWorld = world;
        entityManager = world.EntityManager;
        timedStageQuery = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<TimedSurvivalStageProgress>(),
            ComponentType.ReadOnly<StageClearState>(),
            ComponentType.Exclude<Prefab>());
        killCountStageQuery = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<KillCountStageProgress>(),
            ComponentType.ReadOnly<StageClearState>(),
            ComponentType.Exclude<Prefab>());
        bossStageQuery = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<BossHealthStageProgress>(),
            ComponentType.ReadOnly<StageClearState>(),
            ComponentType.Exclude<Prefab>());
        hasQueries = true;

        return true;
    }

    private void DisposeQueries()
    {
        if (!hasQueries)
        {
            return;
        }

        if (queryWorld != null && queryWorld.IsCreated)
        {
            timedStageQuery.Dispose();
            killCountStageQuery.Dispose();
            bossStageQuery.Dispose();
        }

        hasQueries = false;
        queryWorld = null;
        entityManager = default;
    }

    /// <summary>
    /// 生き残りステージなら、残り時間を表示する。
    /// </summary>
    private bool TryShowTimedStage()
    {
        if (timedStageQuery.IsEmpty)
        {
            return false;
        }

        var stage = timedStageQuery.GetSingleton<TimedSurvivalStageProgress>();
        var clearState = timedStageQuery.GetSingleton<StageClearState>();
        var remainingSeconds = StageObjectiveOverlayMath.CalculateRemainingSeconds(
            stage.TimeLimitSeconds,
            stage.ElapsedSeconds);

        SetVisible(true);
        SetBossVisible(false);
        objectiveText.text = clearState.IsCleared != 0
            ? "CLEAR"
            : $"残り時間 {StageObjectiveOverlayMath.FormatRemainingSeconds(remainingSeconds)}";

        return true;
    }

    /// <summary>
    /// 討伐ステージなら、残りの討伐数を表示する。
    /// </summary>
    private bool TryShowKillCountStage()
    {
        if (killCountStageQuery.IsEmpty)
        {
            return false;
        }

        var stage = killCountStageQuery.GetSingleton<KillCountStageProgress>();
        var clearState = killCountStageQuery.GetSingleton<StageClearState>();
        var remainingKillCount = StageObjectiveOverlayMath.CalculateRemainingKillCount(
            stage.TargetKillCount,
            stage.CurrentKillCount);

        SetVisible(true);
        SetBossVisible(false);
        objectiveText.text = clearState.IsCleared != 0
            ? "CLEAR"
            : $"討伐残り {remainingKillCount.ToString(CultureInfo.InvariantCulture)}";

        return true;
    }

    /// <summary>
    /// ボスステージなら、ボスの HP バーを表示する。
    /// </summary>
    private bool TryShowBossStage()
    {
        if (bossStageQuery.IsEmpty)
        {
            return false;
        }

        var stage = bossStageQuery.GetSingleton<BossHealthStageProgress>();
        var clearState = bossStageQuery.GetSingleton<StageClearState>();

        SetVisible(true);
        SetBossVisible(true);
        objectiveText.text = string.Empty;
        bossLabel.text = clearState.IsCleared != 0 ? "BOSS CLEAR" : "BOSS";
        bossFrontImage.fillAmount = StageObjectiveOverlayMath.CalculateBossHpFillAmount(
            stage.CurrentHp,
            stage.MaxHp);

        return true;
    }

    /// <summary>
    /// 表示先の Canvas がなければ自動で作る。
    /// </summary>
    private bool EnsureCanvas()
    {
        if (TargetCanvas != null)
        {
            return true;
        }

        var canvasObject = new GameObject(CanvasObjectName, typeof(RectTransform));
        var canvas = canvasObject.AddComponent<Canvas>();
        var canvasScaler = canvasObject.AddComponent<CanvasScaler>();

        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 110;
        ConfigureCanvasScaler(canvasScaler);
        Object.DontDestroyOnLoad(canvasObject);

        TargetCanvas = canvas;
        ownsCanvas = true;

        return true;
    }

    /// <summary>
    /// 表示に使う UI（テキストとボスの HP バー）を生成する。シーン切り替えなどで破棄されていたら作り直す。
    /// </summary>
    private bool EnsureUi()
    {
        if (IsUiReady())
        {
            return true;
        }

        ClearUiReferences();

        if (TargetCanvas == null)
        {
            return false;
        }

        root = CreateRect("StageObjectiveRoot", TargetCanvas.transform);
        root.anchorMin = new Vector2(0.5f, 1f);
        root.anchorMax = new Vector2(0.5f, 1f);
        root.pivot = new Vector2(0.5f, 1f);
        root.anchoredPosition = new Vector2(0f, -18f);
        root.sizeDelta = RootSize;

        var panel = CreateImage("panel", root, PanelColor).rectTransform;

        panel.anchorMin = new Vector2(0.5f, 1f);
        panel.anchorMax = new Vector2(0.5f, 1f);
        panel.pivot = new Vector2(0.5f, 1f);
        panel.anchoredPosition = Vector2.zero;
        panel.sizeDelta = new Vector2(360f, 36f);

        objectiveText = CreateText("objective-text", panel);
        objectiveText.alignment = TextAnchor.MiddleCenter;
        objectiveText.fontSize = 24;
        objectiveText.fontStyle = FontStyle.Bold;

        bossRoot = CreateRect("boss-root", root);
        bossRoot.anchorMin = new Vector2(0.5f, 1f);
        bossRoot.anchorMax = new Vector2(0.5f, 1f);
        bossRoot.pivot = new Vector2(0.5f, 1f);
        bossRoot.anchoredPosition = Vector2.zero;
        bossRoot.sizeDelta = new Vector2(620f, 58f);

        bossLabel = CreateText("boss-label", bossRoot);
        bossLabel.alignment = TextAnchor.MiddleCenter;
        bossLabel.fontSize = 22;
        bossLabel.fontStyle = FontStyle.Bold;
        bossLabel.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        bossLabel.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        bossLabel.rectTransform.pivot = new Vector2(0.5f, 1f);
        bossLabel.rectTransform.anchoredPosition = Vector2.zero;
        bossLabel.rectTransform.sizeDelta = new Vector2(420f, 26f);

        var bossBarBackground = CreateImage("boss-bar-background", bossRoot, BossBackColor);

        bossBarBackground.rectTransform.anchorMin = new Vector2(0.5f, 1f);
        bossBarBackground.rectTransform.anchorMax = new Vector2(0.5f, 1f);
        bossBarBackground.rectTransform.pivot = new Vector2(0.5f, 1f);
        bossBarBackground.rectTransform.anchoredPosition = new Vector2(0f, -30f);
        bossBarBackground.rectTransform.sizeDelta = BossBarSize;

        bossFrontImage = CreateImage("boss-bar-front", bossBarBackground.transform, BossFrontColor);
        bossFrontImage.type = Image.Type.Filled;
        bossFrontImage.fillMethod = Image.FillMethod.Horizontal;
        bossFrontImage.fillOrigin = 0;

        SetVisible(false);

        return true;
    }

    private bool IsUiReady()
    {
        return root != null &&
               objectiveText != null &&
               bossRoot != null &&
               bossLabel != null &&
               bossFrontImage != null;
    }

    private void ClearUiReferences()
    {
        if (root != null)
        {
            root.gameObject.SetActive(false);
            Destroy(root.gameObject);
        }

        root = null;
        objectiveText = null;
        bossRoot = null;
        bossLabel = null;
        bossFrontImage = null;
    }

    private static RectTransform CreateRect(string name, Transform parent)
    {
        var gameObject = new GameObject(name, typeof(RectTransform));

        gameObject.transform.SetParent(parent, false);

        return gameObject.GetComponent<RectTransform>();
    }

    private static Image CreateImage(string name, Transform parent, Color color)
    {
        var gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));

        gameObject.transform.SetParent(parent, false);

        var rectTransform = gameObject.GetComponent<RectTransform>();

        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = Vector2.zero;
        rectTransform.offsetMax = Vector2.zero;

        var image = gameObject.GetComponent<Image>();

        image.color = color;
        image.raycastTarget = false;

        return image;
    }

    private static Text CreateText(string name, Transform parent)
    {
        var gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));

        gameObject.transform.SetParent(parent, false);

        var rectTransform = gameObject.GetComponent<RectTransform>();

        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = Vector2.zero;
        rectTransform.offsetMax = Vector2.zero;

        var text = gameObject.GetComponent<Text>();

        text.font = Resources.GetBuiltinResource<Font>(BuiltInFontResourceName);
        text.color = Color.white;
        text.raycastTarget = false;

        return text;
    }

    /// <summary>
    /// 画面サイズに合わせて UI を拡大縮小し、どの解像度でも画面に対して同じ比率で表示されるようにする。
    /// </summary>
    private static void ConfigureCanvasScaler(CanvasScaler canvasScaler)
    {
        canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasScaler.referenceResolution = ReferenceResolution;
        canvasScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        canvasScaler.matchWidthOrHeight = CanvasScaleMatch;
    }

    private void SetVisible(bool visible)
    {
        if (root != null)
        {
            root.gameObject.SetActive(visible);
        }
    }

    /// <summary>
    /// ボスの HP バーと通常のテキストを切り替える（どちらか一方だけを表示する）。
    /// </summary>
    private void SetBossVisible(bool visible)
    {
        if (bossRoot != null)
        {
            bossRoot.gameObject.SetActive(visible);
        }

        if (objectiveText != null)
        {
            objectiveText.gameObject.SetActive(!visible);
        }
    }
}

/// <summary>
/// StageObjectiveOverlay の表示に使う計算。Unity のオブジェクトに依存しないので、単体テストできる。
/// </summary>
public static class StageObjectiveOverlayMath
{
    /// <summary>
    /// 残り時間（秒）を返す。0 未満にはならない。
    /// </summary>
    public static float CalculateRemainingSeconds(float timeLimitSeconds, float elapsedSeconds)
    {
        return Mathf.Max(0f, timeLimitSeconds - Mathf.Max(0f, elapsedSeconds));
    }

    /// <summary>
    /// 残りの討伐数を返す。0 未満にはならない。
    /// </summary>
    public static int CalculateRemainingKillCount(int targetKillCount, int currentKillCount)
    {
        return Mathf.Max(0, targetKillCount - Mathf.Max(0, currentKillCount));
    }

    /// <summary>
    /// ボスの HP バーの長さ（0〜1）を返す。
    /// </summary>
    public static float CalculateBossHpFillAmount(int currentHp, int maxHp)
    {
        if (maxHp <= 0)
        {
            return 0f;
        }

        return Mathf.Clamp01((float)currentHp / maxHp);
    }

    /// <summary>
    /// 残り時間を「分:秒」（例：04:59）の文字列にする。
    /// </summary>
    public static string FormatRemainingSeconds(float remainingSeconds)
    {
        var safeSeconds = Mathf.Max(0, Mathf.FloorToInt(remainingSeconds));
        var minutes = safeSeconds / 60;
        var seconds = safeSeconds % 60;

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}",
            minutes,
            seconds);
    }
}
