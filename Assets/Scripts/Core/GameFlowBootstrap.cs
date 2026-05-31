using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// ゲーム開始、ゲーム本編、リザルトの最小 scene flow を管理する。
/// </summary>
[DefaultExecutionOrder(320)]
public sealed class GameFlowBootstrap : MonoBehaviour
{
    public const string StartSceneName = "GameStartScene";
    public const string GameplaySceneName = "DebugScene";
    public const string ResultSceneName = "ResultScene";

    private const string BootstrapObjectName = "Game Flow Bootstrap";
    private const string CanvasObjectName = "Game Flow Canvas";
    private const string BuiltInFontResourceName = "LegacyRuntime.ttf";

    private static bool hasGameSessionStarted;

    private static readonly Vector2 ButtonSize = new Vector2(300f, 62f);
    private static readonly Color BackgroundColor = new Color(0.04f, 0.05f, 0.06f, 0.96f);
    private static readonly Color PanelColor = new Color(0.08f, 0.09f, 0.11f, 0.88f);
    private static readonly Color ButtonColor = new Color(0.18f, 0.34f, 0.52f, 0.96f);
    private static readonly Color ButtonHoverColor = new Color(0.24f, 0.45f, 0.68f, 0.96f);
    private static readonly Color TextColor = new Color(0.92f, 0.94f, 0.96f, 1f);
    private static readonly Color MutedTextColor = new Color(0.70f, 0.75f, 0.80f, 1f);

    private Canvas canvas;
    private RectTransform startRoot;
    private RectTransform resultRoot;
    private Text startTitleText;
    private Text startSubtitleText;
    private Image startButtonImage;
    private Text startButtonText;
    private Text resultTitleText;
    private Text resultSubtitleText;
    private Image resultButtonImage;
    private Text resultButtonText;
    private Rect startButtonRect;
    private Rect resultButtonRect;
    private World queryWorld;
    private EntityQuery stageClearQuery;
    private bool hasStageClearQuery;
    private bool hasLoadedResultForCurrentScene;
    private int framesSinceSceneLoaded;

    /// <summary>
    /// スタート画面を経由していない本編 / リザルト Scene を開始 Scene へ戻すかを判定する。
    /// </summary>
    /// <param name="sceneName">現在の Scene 名。</param>
    /// <param name="hasGameplayLaunchPermission">スタート画面からゲーム開始済みなら true。</param>
    /// <returns>開始 Scene へ戻す必要があれば true。</returns>
    public static bool ShouldRedirectToStartScene(string sceneName, bool hasGameplayLaunchPermission)
    {
        if (sceneName == StartSceneName)
        {
            return false;
        }

        if (sceneName == GameplaySceneName || sceneName == ResultSceneName)
        {
            return !hasGameplayLaunchPermission;
        }

        return false;
    }

    /// <summary>
    /// Domain Reload 無効時でも Play 開始ごとに開始 Scene から始めるための初期化。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetSessionState()
    {
        hasGameSessionStarted = false;
    }

    /// <summary>
    /// Scene に手動配置しなくても game flow bootstrap を 1 つだけ作る。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateBootstrap()
    {
        if (Object.FindAnyObjectByType<GameFlowBootstrap>() != null)
        {
            return;
        }

        var gameObject = new GameObject(BootstrapObjectName);

        Object.DontDestroyOnLoad(gameObject);
        gameObject.AddComponent<GameFlowBootstrap>();
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        RefreshSceneUi();
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnDestroy()
    {
        DisposeStageClearQuery();

        if (canvas != null)
        {
            Destroy(canvas.gameObject);
        }
    }

    private void Update()
    {
        var sceneName = SceneManager.GetActiveScene().name;

        if (ShouldRedirectToStartScene(sceneName, hasGameSessionStarted))
        {
            SceneManager.LoadScene(StartSceneName);
            return;
        }

        RefreshSceneUi();

        if (sceneName == StartSceneName)
        {
            UpdateMenuButtonState(startButtonImage, startButtonRect);

            if (WasPrimaryActionPressed(startButtonRect))
            {
                hasGameSessionStarted = true;
                SceneManager.LoadScene(GameplaySceneName);
            }

            return;
        }

        if (sceneName == ResultSceneName)
        {
            UpdateMenuButtonState(resultButtonImage, resultButtonRect);

            if (WasPrimaryActionPressed(resultButtonRect))
            {
                hasGameSessionStarted = false;
                SceneManager.LoadScene(StartSceneName);
            }

            return;
        }

        if (sceneName == GameplaySceneName)
        {
            if (framesSinceSceneLoaded < 2)
            {
                framesSinceSceneLoaded++;
                return;
            }

            LoadResultSceneWhenStageCleared();
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        hasLoadedResultForCurrentScene = false;
        framesSinceSceneLoaded = 0;
        RefreshSceneUi();
    }

    private void LoadResultSceneWhenStageCleared()
    {
        if (hasLoadedResultForCurrentScene || !TryCreateStageClearQuery() || stageClearQuery.IsEmpty)
        {
            return;
        }

        if (stageClearQuery.GetSingleton<StageClearState>().IsCleared == 0)
        {
            return;
        }

        hasLoadedResultForCurrentScene = true;
        SceneManager.LoadScene(ResultSceneName);
    }

    private bool TryCreateStageClearQuery()
    {
        var world = World.DefaultGameObjectInjectionWorld;

        if (world == null || !world.IsCreated)
        {
            DisposeStageClearQuery();
            return false;
        }

        if (hasStageClearQuery && queryWorld == world)
        {
            return true;
        }

        DisposeStageClearQuery();
        queryWorld = world;
        stageClearQuery = world.EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<StageClearState>(),
            ComponentType.Exclude<Prefab>());
        hasStageClearQuery = true;

        return true;
    }

    private void DisposeStageClearQuery()
    {
        if (!hasStageClearQuery)
        {
            return;
        }

        if (queryWorld != null && queryWorld.IsCreated)
        {
            stageClearQuery.Dispose();
        }

        hasStageClearQuery = false;
        queryWorld = null;
    }

    private void RefreshSceneUi()
    {
        var sceneName = SceneManager.GetActiveScene().name;
        var showStartUi = sceneName == StartSceneName;
        var showResultUi = sceneName == ResultSceneName;

        if (!showStartUi && !showResultUi)
        {
            SetSceneUiVisible(false, false);
            return;
        }

        EnsureCanvas();
        EnsureSceneUi();
        UpdateSceneUiLayout();
        SetSceneUiVisible(showStartUi, showResultUi);
    }

    private void EnsureCanvas()
    {
        if (canvas != null)
        {
            return;
        }

        var canvasObject = new GameObject(CanvasObjectName, typeof(RectTransform));

        canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;

        Object.DontDestroyOnLoad(canvasObject);
    }

    private void EnsureSceneUi()
    {
        if (startRoot != null && resultRoot != null)
        {
            return;
        }

        startRoot = CreateFullScreenRoot("start-root", canvas.transform);
        CreateImage("background", startRoot, BackgroundColor);

        var startPanel = CreateImage("panel", startRoot, PanelColor).rectTransform;

        startTitleText = CreateText("title", startPanel, 48, FontStyle.Bold, TextAnchor.MiddleCenter, TextColor);
        startTitleText.text = "ECS Sample";
        startSubtitleText = CreateText("subtitle", startPanel, 22, FontStyle.Normal, TextAnchor.MiddleCenter, MutedTextColor);
        startSubtitleText.text = "Enter / Space / Click";
        startButtonImage = CreateImage("start-button", startPanel, ButtonColor);
        startButtonText = CreateText("start-button-label", startButtonImage.transform, 24, FontStyle.Bold, TextAnchor.MiddleCenter, TextColor);
        startButtonText.text = "ゲーム開始";

        resultRoot = CreateFullScreenRoot("result-root", canvas.transform);
        CreateImage("background", resultRoot, BackgroundColor);

        var resultPanel = CreateImage("panel", resultRoot, PanelColor).rectTransform;

        resultTitleText = CreateText("title", resultPanel, 48, FontStyle.Bold, TextAnchor.MiddleCenter, TextColor);
        resultTitleText.text = "ステージクリア";
        resultSubtitleText = CreateText("subtitle", resultPanel, 22, FontStyle.Normal, TextAnchor.MiddleCenter, MutedTextColor);
        resultSubtitleText.text = "Result";
        resultButtonImage = CreateImage("result-button", resultPanel, ButtonColor);
        resultButtonText = CreateText("result-button-label", resultButtonImage.transform, 24, FontStyle.Bold, TextAnchor.MiddleCenter, TextColor);
        resultButtonText.text = "スタートへ戻る";
    }

    private void UpdateSceneUiLayout()
    {
        var width = Mathf.Max(1, Screen.width);
        var height = Mathf.Max(1, Screen.height);
        var panelWidth = Mathf.Clamp(width - 48f, 240f, 680f);
        var panelHeight = 300f;
        var panelX = (width - panelWidth) * 0.5f;
        var panelY = Mathf.Max(80f, (height - panelHeight) * 0.5f);

        UpdateMenuLayout(startRoot, startTitleText, startSubtitleText, startButtonImage, panelX, panelY, panelWidth, panelHeight);
        UpdateMenuLayout(resultRoot, resultTitleText, resultSubtitleText, resultButtonImage, panelX, panelY, panelWidth, panelHeight);

        startButtonRect = CalculateButtonRect(panelX, panelY, panelWidth);
        resultButtonRect = startButtonRect;
    }

    private static void UpdateMenuLayout(
        RectTransform root,
        Text titleText,
        Text subtitleText,
        Image buttonImage,
        float panelX,
        float panelY,
        float panelWidth,
        float panelHeight)
    {
        var panel = (RectTransform)titleText.transform.parent;

        SetTopLeftRect(root, 0f, 0f, Screen.width, Screen.height);
        SetTopLeftRect(panel, panelX, panelY, panelWidth, panelHeight);
        SetTopLeftRect(titleText.rectTransform, 32f, 34f, panelWidth - 64f, 70f);
        SetTopLeftRect(subtitleText.rectTransform, 32f, 116f, panelWidth - 64f, 38f);
        SetTopLeftRect(buttonImage.rectTransform, (panelWidth - ButtonSize.x) * 0.5f, panelHeight - 96f, ButtonSize.x, ButtonSize.y);
    }

    private static Rect CalculateButtonRect(float panelX, float panelY, float panelWidth)
    {
        return new Rect(
            panelX + (panelWidth - ButtonSize.x) * 0.5f,
            panelY + 204f,
            ButtonSize.x,
            ButtonSize.y);
    }

    private void SetSceneUiVisible(bool showStartUi, bool showResultUi)
    {
        if (canvas == null || startRoot == null || resultRoot == null)
        {
            return;
        }

        canvas.gameObject.SetActive(showStartUi || showResultUi);
        startRoot.gameObject.SetActive(showStartUi);
        resultRoot.gameObject.SetActive(showResultUi);
    }

    private static bool WasPrimaryActionPressed(Rect buttonRect)
    {
        var keyboard = Keyboard.current;

        if (keyboard != null &&
            (keyboard.enterKey.wasPressedThisFrame || keyboard.spaceKey.wasPressedThisFrame))
        {
            return true;
        }

        var mouse = Mouse.current;

        if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
        {
            return false;
        }

        var position = mouse.position.ReadValue();
        var topLeftPosition = new Vector2(position.x, Screen.height - position.y);

        return buttonRect.Contains(topLeftPosition);
    }

    private static void UpdateMenuButtonState(Image buttonImage, Rect buttonRect)
    {
        if (buttonImage == null)
        {
            return;
        }

        var mouse = Mouse.current;

        if (mouse == null)
        {
            buttonImage.color = ButtonColor;
            return;
        }

        var position = mouse.position.ReadValue();
        var topLeftPosition = new Vector2(position.x, Screen.height - position.y);

        buttonImage.color = buttonRect.Contains(topLeftPosition)
            ? ButtonHoverColor
            : ButtonColor;
    }

    private static RectTransform CreateFullScreenRoot(string name, Transform parent)
    {
        var root = CreateRect(name, parent);

        root.anchorMin = Vector2.zero;
        root.anchorMax = Vector2.one;
        root.offsetMin = Vector2.zero;
        root.offsetMax = Vector2.zero;

        return root;
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

    private static Text CreateText(
        string name,
        Transform parent,
        int fontSize,
        FontStyle fontStyle,
        TextAnchor alignment,
        Color color)
    {
        var gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));

        gameObject.transform.SetParent(parent, false);

        var text = gameObject.GetComponent<Text>();

        text.font = Resources.GetBuiltinResource<Font>(BuiltInFontResourceName);
        text.color = color;
        text.fontSize = fontSize;
        text.fontStyle = fontStyle;
        text.alignment = alignment;
        text.raycastTarget = false;

        return text;
    }

    private static void SetTopLeftRect(RectTransform rectTransform, float x, float y, float width, float height)
    {
        rectTransform.anchorMin = new Vector2(0f, 1f);
        rectTransform.anchorMax = new Vector2(0f, 1f);
        rectTransform.pivot = new Vector2(0f, 1f);
        rectTransform.anchoredPosition = new Vector2(x, -y);
        rectTransform.sizeDelta = new Vector2(width, height);
    }
}
