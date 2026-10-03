#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using System.Globalization;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Physics;
using UnityEngine.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// ゲーム中に表示・操作できるデバッグ用ウィンドウ（Esc キーで開閉）。エディタと開発ビルドでだけ有効になる。
/// できること：
/// ・FPS、プレイヤーのレベル、スキルスロットの状態の表示
/// ・スキルレベルの上げ下げ
/// ・プレイヤーの当たり判定、スキルのターゲット範囲・攻撃範囲の可視化
/// パラメータの調整や不具合の確認を、ゲームを止めずに素早く行えるようにするためのツール。
/// </summary>
public sealed class DebugOptionsOverlay : MonoBehaviour
{
    private const string ObjectName = "Debug Options Overlay";
    private const string BuiltInFontResourceName = "LegacyRuntime.ttf";
    private const float CanvasScaleMatch = 0.5f;
    private const float Margin = 16f;
    private const float TitleBarHeight = 22f;
    private const float ResizeHandleSize = 18f;
    private const float FpsRefreshSeconds = 0.25f;
    private const float FpsToggleY = 72f;
    private const float LevelToggleY = 100f;
    private const float SkillSlotToggleY = 128f;
    private const float SkillLevelControlY = 156f;
    private const float PlayerColliderToggleY = 188f;
    private const float SkillTargetRangeToggleY = 216f;
    private const float SkillAttackRangeToggleY = 244f;
    private const float WindowFpsY = 272f;
    private const float SkillLevelButtonSize = 22f;
    private const float SkillLevelMinusButtonX = 168f;
    private const float SkillLevelPlusButtonX = 198f;
    private const float FpsOverlayX = 12f;
    private const float FpsOverlayY = 12f;
    private const float FpsOverlayWidth = 104f;
    private const float FpsOverlayHeight = 28f;
    private const float LevelOverlayX = 12f;
    private const float LevelOverlayY = 58f;
    private const float LevelOverlayWidth = 184f;
    private const float LevelOverlayHeight = 64f;
    private const float SlotOverlayX = 12f;
    private const float SlotOverlayY = 130f;
    private const float SlotOverlayWidth = 144f;
    private const float SlotOverlayHeight = 52f;
    private const float SlotRowLabelX = 6f;
    private const float SlotAttackRowY = 4f;
    private const float SlotBuffRowY = 28f;
    private const float SlotIconStartX = 24f;
    private const float SlotIconSize = 18f;
    private const float SlotIconGap = 4f;

    private static readonly Vector2 DefaultWindowSize = new Vector2(320f, 308f);
    private static readonly Vector2 MinimumWindowSize = new Vector2(260f, 248f);
    private static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);
    private static readonly Color WindowBackgroundColor = new Color(0f, 0f, 0f, 0.36f);
    private static readonly Color CheckboxOnColor = new Color(0.92f, 0.92f, 0.92f, 1f);
    private static readonly Color CheckboxOffColor = new Color(0.06f, 0.06f, 0.06f, 1f);
    private static readonly Color ButtonColor = new Color(0.18f, 0.34f, 0.52f, 0.95f);
    private static readonly Color ButtonDisabledColor = new Color(0.08f, 0.08f, 0.08f, 0.75f);
    private static readonly Color TextColor = Color.white;
    private static readonly Color SlotEmptyColor = new Color(0.05f, 0.05f, 0.05f, 0.72f);
    private static readonly Color SlotReadyColor = new Color(0.08f, 0.38f, 0.18f, 0.92f);
    private static readonly Color SlotCooltimeColor = new Color(0.42f, 0.28f, 0.08f, 0.92f);
    private static readonly Color SlotTriggeredColor = new Color(0.18f, 0.28f, 0.62f, 0.92f);
    private static readonly Color SlotBuffColor = new Color(0.30f, 0.16f, 0.42f, 0.92f);

    private Rect windowRect;
    private Canvas overlayCanvas;
    private RectTransform windowRoot;
    private RectTransform fpsRoot;
    private RectTransform levelRoot;
    private RectTransform slotRoot;
    private RectTransform resizeHandleRect;
    private Text fpsOverlayText;
    private Text levelOverlayText;
    private Text slotAttackLabelText;
    private Text slotBuffLabelText;
    private Text skillLevelControlText;
    private Text windowFpsText;
    private Image fpsCheckboxImage;
    private Image levelCheckboxImage;
    private Image slotCheckboxImage;
    private Image playerColliderCheckboxImage;
    private Image skillTargetRangeCheckboxImage;
    private Image skillAttackRangeCheckboxImage;
    private Image skillLevelMinusButtonImage;
    private Image skillLevelPlusButtonImage;
    private bool isWindowOpen;
    private bool isFpsVisible;
    private bool isLevelVisible;
    private bool isSkillSlotVisible;
    private bool isPlayerColliderWireframeVisible;
    private bool isSkillTargetRangeVisible;
    private bool isSkillAttackRangeVisible;
    private bool isResizing;
    private bool isDragging;
    private bool ownsOverlayCanvas;
    private bool hasWindowRect;
    private Vector2 resizeMouseStart;
    private Vector2 resizeSizeStart;
    private Vector2 dragMouseStart;
    private Vector2 dragWindowStart;
    private float fps;
    private float fpsElapsedSeconds;
    private int fpsFrameCount;
    private readonly SkillSlotIconData[] slotIconBuffer = new SkillSlotIconData[PlayerCombatConstants.MaxSkillCount];
    private readonly Image[] slotIconImages = new Image[PlayerCombatConstants.MaxSkillCount];
    private readonly Text[] slotIconTexts = new Text[PlayerCombatConstants.MaxSkillCount];

    /// <summary>
    /// スキルスロット 1 つ分のアイコンの表示内容。
    /// </summary>
    private struct SkillSlotIconData
    {
        public string Text;
        public Color Color;
    }

    /// <summary>
    /// 装備中のスキルのレベルの集計（最小・最大と、上げ下げできるか）。
    /// </summary>
    private struct SkillLevelSummary
    {
        public int MinLevel;
        public int MaxLevel;
        public byte HasAnySkill;
        public byte CanDecrease;
        public byte CanIncrease;
    }

    /// <summary>
    /// シーンに手動で置かなくても、デバッグ用ウィンドウを自動で 1 つだけ作る。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateOverlay()
    {
        DebugOptionsRuntimeDrawer.Dispose();

        if (Object.FindAnyObjectByType<DebugOptionsOverlay>() != null)
        {
            return;
        }

        var gameObject = new GameObject(ObjectName);

        Object.DontDestroyOnLoad(gameObject);
        gameObject.hideFlags = HideFlags.DontSave;
        gameObject.AddComponent<DebugOptionsOverlay>();
    }

#if UNITY_EDITOR
    /// <summary>
    /// Play モードの終了時に、デバッグ表示用に作ったオブジェクトを片付けるよう登録する。
    /// </summary>
    [UnityEditor.InitializeOnLoadMethod]
    private static void RegisterEditorCleanup()
    {
        UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        DebugOptionsRuntimeDrawer.Dispose();
    }

    private static void OnPlayModeStateChanged(UnityEditor.PlayModeStateChange stateChange)
    {
        if (stateChange != UnityEditor.PlayModeStateChange.ExitingPlayMode &&
            stateChange != UnityEditor.PlayModeStateChange.EnteredEditMode)
        {
            return;
        }

        DebugOptionsRuntimeDrawer.Dispose();
    }
#endif

    /// <summary>
    /// 重複して作られた場合は、自分を削除して 1 つだけにする。
    /// </summary>
    private void Awake()
    {
        var overlays = Object.FindObjectsByType<DebugOptionsOverlay>(FindObjectsInactive.Exclude);

        if (overlays.Length > 1)
        {
            Destroy(gameObject);
            return;
        }
    }

    private void OnDisable()
    {
        SetRuntimeUiVisible(false);
        CleanupDebugObjects();
    }

    private void OnDestroy()
    {
        CleanupDebugObjects();
        DestroyRuntimeUi();
    }

    /// <summary>
    /// Esc キーでウィンドウを開閉し、FPS の計測・マウス操作・UI の更新を行う。
    /// </summary>
    private void Update()
    {
        var keyboard = Keyboard.current;

        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        {
            isWindowOpen = !isWindowOpen;
        }

        UpdateFps(Time.unscaledDeltaTime);

        EnsureRuntimeUi();
        ProcessPointerInput();
        UpdateRuntimeUi();
    }

    private static void CleanupDebugObjects()
    {
        DebugOptionsRuntimeDrawer.Dispose();
    }

    /// <summary>
    /// 有効になっている可視化オプション（当たり判定・スキル範囲）を描画する。
    /// すべて無効なら、描画用のオブジェクトを非表示にする。
    /// </summary>
    private void LateUpdate()
    {
        if (!DebugOptionsRuntimeDrawer.HasAnyOptionEnabled(
                isPlayerColliderWireframeVisible,
                isSkillTargetRangeVisible,
                isSkillAttackRangeVisible))
        {
            SkillAttackRangeDebugEvents.SetRecordingEnabled(false);
            DebugOptionsRangeRenderer.HideSkillRanges();
            return;
        }

        DebugOptionsRuntimeDrawer.Draw(
            isPlayerColliderWireframeVisible,
            isSkillTargetRangeVisible,
            isSkillAttackRangeVisible);
    }

    /// <summary>
    /// FPS を計測する。一定時間（FpsRefreshSeconds）ごとに平均をとり、表示が細かく揺れすぎないようにする。
    /// </summary>
    private void UpdateFps(float deltaTime)
    {
        var instantFps = DebugOptionsOverlayMath.CalculateInstantFps(deltaTime);

        if (instantFps <= 0f)
        {
            return;
        }

        fpsElapsedSeconds += deltaTime;
        fpsFrameCount++;

        if (fps <= 0f)
        {
            fps = instantFps;
        }

        if (fpsElapsedSeconds < FpsRefreshSeconds)
        {
            return;
        }

        fps = fpsFrameCount / fpsElapsedSeconds;
        fpsElapsedSeconds = 0f;
        fpsFrameCount = 0;
    }

    /// <summary>
    /// デバッグ用の UI をすべて生成する。1 つでも欠けていれば、いったん破棄して作り直す。
    /// </summary>
    private bool EnsureRuntimeUi()
    {
        if (overlayCanvas != null &&
            windowRoot != null &&
            fpsRoot != null &&
            levelRoot != null &&
            slotRoot != null &&
            resizeHandleRect != null &&
            fpsOverlayText != null &&
            levelOverlayText != null &&
            slotAttackLabelText != null &&
            slotBuffLabelText != null &&
            skillLevelControlText != null &&
            AreSkillSlotIconsReady() &&
            windowFpsText != null &&
            fpsCheckboxImage != null &&
            levelCheckboxImage != null &&
            slotCheckboxImage != null &&
            playerColliderCheckboxImage != null &&
            skillTargetRangeCheckboxImage != null &&
            skillAttackRangeCheckboxImage != null &&
            skillLevelMinusButtonImage != null &&
            skillLevelPlusButtonImage != null)
        {
            return true;
        }

        DestroyRuntimeUi();

        var canvasObject = new GameObject("Debug Options Canvas", typeof(RectTransform));
        var canvas = canvasObject.AddComponent<Canvas>();
        var canvasScaler = canvasObject.AddComponent<CanvasScaler>();

        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;
        ConfigureCanvasScaler(canvasScaler);
        Object.DontDestroyOnLoad(canvasObject);

        overlayCanvas = canvas;
        ownsOverlayCanvas = true;

        fpsRoot = CreateRect("fps-root", overlayCanvas.transform);
        CreateImage("fps-background", fpsRoot, WindowBackgroundColor);
        fpsOverlayText = CreateText("fps-text", fpsRoot, 20, FontStyle.Normal);

        levelRoot = CreateRect("level-root", overlayCanvas.transform);
        CreateImage("level-background", levelRoot, WindowBackgroundColor);
        levelOverlayText = CreateText("level-text", levelRoot, 18, FontStyle.Normal);

        slotRoot = CreateRect("slot-root", overlayCanvas.transform);
        CreateImage("slot-background", slotRoot, WindowBackgroundColor);
        slotAttackLabelText = CreateText("slot-attack-label", slotRoot, 11, FontStyle.Bold);
        slotAttackLabelText.alignment = TextAnchor.MiddleCenter;
        slotAttackLabelText.text = "A";
        slotBuffLabelText = CreateText("slot-buff-label", slotRoot, 11, FontStyle.Bold);
        slotBuffLabelText.alignment = TextAnchor.MiddleCenter;
        slotBuffLabelText.text = "B";
        CreateSkillSlotIcons();

        windowRoot = CreateRect("window-root", overlayCanvas.transform);
        CreateImage("window-background", windowRoot, WindowBackgroundColor);

        var titleText = CreateText("title", windowRoot, 18, FontStyle.Bold);

        SetTopLeftRect(titleText.rectTransform, 0f, 0f, DefaultWindowSize.x, TitleBarHeight);
        titleText.alignment = TextAnchor.MiddleCenter;
        titleText.text = "Debug Options";

        var optionsText = CreateText("options-label", windowRoot, 17, FontStyle.Normal);

        SetTopLeftRect(optionsText.rectTransform, 18f, 42f, 220f, 24f);
        optionsText.text = "Options";

        fpsCheckboxImage = CreateCheckboxRow(windowRoot, FpsToggleY, "FPS 表示");
        levelCheckboxImage = CreateCheckboxRow(windowRoot, LevelToggleY, "Level 表示");
        slotCheckboxImage = CreateCheckboxRow(windowRoot, SkillSlotToggleY, "スキルスロット表示");
        skillLevelControlText = CreateText("skill-level-control", windowRoot, 17, FontStyle.Normal);
        SetTopLeftRect(skillLevelControlText.rectTransform, 44f, SkillLevelControlY, 118f, 24f);
        skillLevelControlText.text = "スキルLv --";
        skillLevelMinusButtonImage = CreateSmallButton(windowRoot, "skill-level-minus-button", "-");
        skillLevelPlusButtonImage = CreateSmallButton(windowRoot, "skill-level-plus-button", "+");
        playerColliderCheckboxImage = CreateCheckboxRow(
            windowRoot,
            PlayerColliderToggleY,
            "Player Collider Wireframe");
        skillTargetRangeCheckboxImage = CreateCheckboxRow(
            windowRoot,
            SkillTargetRangeToggleY,
            "Skill Target Range");
        skillAttackRangeCheckboxImage = CreateCheckboxRow(
            windowRoot,
            SkillAttackRangeToggleY,
            "Skill Attack Range");

        windowFpsText = CreateText("window-fps", windowRoot, 17, FontStyle.Normal);
        SetTopLeftRect(windowFpsText.rectTransform, 18f, WindowFpsY, 180f, 24f);

        var resizeHandle = CreateImage("resize-handle", windowRoot, new Color(1f, 1f, 1f, 0.28f));

        resizeHandleRect = resizeHandle.rectTransform;

        SetTopLeftRect(
            resizeHandleRect,
            DefaultWindowSize.x - ResizeHandleSize,
            DefaultWindowSize.y - ResizeHandleSize,
            ResizeHandleSize,
            ResizeHandleSize);

        return true;
    }

    private bool AreSkillSlotIconsReady()
    {
        for (var iconIndex = 0; iconIndex < slotIconImages.Length; iconIndex++)
        {
            if (slotIconImages[iconIndex] == null || slotIconTexts[iconIndex] == null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// スキルスロットの数だけアイコンを生成する。
    /// </summary>
    private void CreateSkillSlotIcons()
    {
        for (var iconIndex = 0; iconIndex < slotIconImages.Length; iconIndex++)
        {
            var iconImage = CreateImage($"slot-icon-{iconIndex}", slotRoot, SlotEmptyColor);
            var iconText = CreateText($"slot-icon-{iconIndex}-text", iconImage.transform, 10, FontStyle.Bold);

            iconText.alignment = TextAnchor.MiddleCenter;
            iconText.horizontalOverflow = HorizontalWrapMode.Overflow;
            iconText.verticalOverflow = VerticalWrapMode.Overflow;
            iconText.text = "-";

            slotIconImages[iconIndex] = iconImage;
            slotIconTexts[iconIndex] = iconText;
        }
    }

    /// <summary>
    /// ウィンドウ上のマウス操作を処理する（リサイズ・ボタン・チェックボックス・タイトルバーのドラッグ）。
    /// </summary>
    private void ProcessPointerInput()
    {
        if (!isWindowOpen)
        {
            isDragging = false;
            isResizing = false;
            return;
        }

        var mouse = Mouse.current;

        if (mouse == null)
        {
            return;
        }

        var canvasSize = GetCanvasSize();

        EnsureWindowRect(canvasSize);

        var mousePosition = ToCanvasTopLeftMousePosition(mouse.position.ReadValue());

        if (mouse.leftButton.wasPressedThisFrame)
        {
            if (GetResizeHandleRect().Contains(mousePosition))
            {
                isResizing = true;
                resizeMouseStart = mousePosition;
                resizeSizeStart = new Vector2(windowRect.width, windowRect.height);
                return;
            }

            if (TryHandleSkillLevelButton(mousePosition))
            {
                return;
            }

            if (TryToggleClickedOption(mousePosition))
            {
                return;
            }

            if (GetTitleBarRect().Contains(mousePosition))
            {
                isDragging = true;
                dragMouseStart = mousePosition;
                dragWindowStart = new Vector2(windowRect.x, windowRect.y);
            }
        }

        if (mouse.leftButton.wasReleasedThisFrame)
        {
            isDragging = false;
            isResizing = false;
            return;
        }

        if (!mouse.leftButton.isPressed)
        {
            isDragging = false;
            isResizing = false;
            return;
        }

        if (isResizing)
        {
            var delta = mousePosition - resizeMouseStart;

            windowRect.width = resizeSizeStart.x + delta.x;
            windowRect.height = resizeSizeStart.y + delta.y;
            windowRect = DebugOptionsOverlayMath.ClampWindowRect(
                windowRect,
                canvasSize.x,
                canvasSize.y,
                MinimumWindowSize);
            return;
        }

        if (isDragging)
        {
            var delta = mousePosition - dragMouseStart;

            windowRect.x = dragWindowStart.x + delta.x;
            windowRect.y = dragWindowStart.y + delta.y;
            windowRect = DebugOptionsOverlayMath.ClampWindowRect(
                windowRect,
                canvasSize.x,
                canvasSize.y,
                MinimumWindowSize);
        }
    }

    /// <summary>
    /// スキルレベルの「-」「+」ボタンが押されたら、装備中のスキルのレベルを変える。
    /// </summary>
    private bool TryHandleSkillLevelButton(Vector2 mousePosition)
    {
        if (GetSkillLevelMinusButtonRect().Contains(mousePosition))
        {
            TryAdjustPlayerSkillSlotLevels(-1);
            return true;
        }

        if (GetSkillLevelPlusButtonRect().Contains(mousePosition))
        {
            TryAdjustPlayerSkillSlotLevels(1);
            return true;
        }

        return false;
    }

    /// <summary>
    /// クリックされたチェックボックスのオン・オフを切り替える。
    /// </summary>
    private bool TryToggleClickedOption(Vector2 mousePosition)
    {
        if (GetToggleRect(FpsToggleY).Contains(mousePosition))
        {
            isFpsVisible = !isFpsVisible;
            return true;
        }

        if (GetToggleRect(LevelToggleY).Contains(mousePosition))
        {
            isLevelVisible = !isLevelVisible;
            return true;
        }

        if (GetToggleRect(SkillSlotToggleY).Contains(mousePosition))
        {
            isSkillSlotVisible = !isSkillSlotVisible;
            return true;
        }

        if (GetToggleRect(PlayerColliderToggleY).Contains(mousePosition))
        {
            isPlayerColliderWireframeVisible = !isPlayerColliderWireframeVisible;
            return true;
        }

        if (GetToggleRect(SkillTargetRangeToggleY).Contains(mousePosition))
        {
            isSkillTargetRangeVisible = !isSkillTargetRangeVisible;
            return true;
        }

        if (GetToggleRect(SkillAttackRangeToggleY).Contains(mousePosition))
        {
            isSkillAttackRangeVisible = !isSkillAttackRangeVisible;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 現在の設定とゲームの状態に合わせて、UI の表示内容と配置を更新する。
    /// </summary>
    private void UpdateRuntimeUi()
    {
        if (!EnsureRuntimeUi())
        {
            return;
        }

        var canvasSize = GetCanvasSize();

        EnsureWindowRect(canvasSize);

        windowRect = DebugOptionsOverlayMath.ClampWindowRect(
            windowRect,
            canvasSize.x,
            canvasSize.y,
            MinimumWindowSize);

        SetTopLeftRect(windowRoot, windowRect.x, windowRect.y, windowRect.width, windowRect.height);
        SetTopLeftRect(fpsRoot, FpsOverlayX, FpsOverlayY, FpsOverlayWidth, FpsOverlayHeight);
        SetTopLeftRect(levelRoot, LevelOverlayX, LevelOverlayY, LevelOverlayWidth, LevelOverlayHeight);
        SetTopLeftRect(slotRoot, SlotOverlayX, SlotOverlayY, SlotOverlayWidth, SlotOverlayHeight);
        UpdateSkillSlotIconLayout();
        SetTopLeftRect(
            resizeHandleRect,
            windowRect.width - ResizeHandleSize,
            windowRect.height - ResizeHandleSize,
            ResizeHandleSize,
            ResizeHandleSize);
        SetTopLeftRect(skillLevelControlText.rectTransform, 44f, SkillLevelControlY, 118f, 24f);
        SetTopLeftRect(
            skillLevelMinusButtonImage.rectTransform,
            SkillLevelMinusButtonX,
            SkillLevelControlY + 1f,
            SkillLevelButtonSize,
            SkillLevelButtonSize);
        SetTopLeftRect(
            skillLevelPlusButtonImage.rectTransform,
            SkillLevelPlusButtonX,
            SkillLevelControlY + 1f,
            SkillLevelButtonSize,
            SkillLevelButtonSize);

        var fpsText = fps.ToString("0.0", CultureInfo.InvariantCulture);
        var hasSkillLevel = false;
        var skillLevelSummary = default(SkillLevelSummary);

        if (isWindowOpen)
        {
            hasSkillLevel = ReadPlayerSkillLevelSummary(out skillLevelSummary);
        }

        fpsRoot.gameObject.SetActive(isFpsVisible);
        fpsOverlayText.text = $"FPS {fpsText}";
        levelRoot.gameObject.SetActive(isLevelVisible);
        levelOverlayText.text = ReadPlayerExperience(out var playerExperience)
            ? DebugOptionsOverlayMath.FormatLevelText(playerExperience)
            : "Lv --  EXP -- / --";
        slotRoot.gameObject.SetActive(isSkillSlotVisible);
        if (isSkillSlotVisible)
        {
            ReadPlayerSkillSlotIcons(slotIconBuffer);
            UpdateSkillSlotIcons(slotIconBuffer);
        }
        windowRoot.gameObject.SetActive(isWindowOpen);
        windowFpsText.gameObject.SetActive(isFpsVisible);
        windowFpsText.text = $"FPS: {fpsText}";
        skillLevelControlText.text = hasSkillLevel
            ? DebugOptionsOverlayMath.FormatSkillLevelSummary(
                skillLevelSummary.MinLevel,
                skillLevelSummary.MaxLevel)
            : "スキルLv --";
        skillLevelMinusButtonImage.color = hasSkillLevel && skillLevelSummary.CanDecrease != 0
            ? ButtonColor
            : ButtonDisabledColor;
        skillLevelPlusButtonImage.color = hasSkillLevel && skillLevelSummary.CanIncrease != 0
            ? ButtonColor
            : ButtonDisabledColor;

        UpdateCheckbox(fpsCheckboxImage, isFpsVisible);
        UpdateCheckbox(levelCheckboxImage, isLevelVisible);
        UpdateCheckbox(slotCheckboxImage, isSkillSlotVisible);
        UpdateCheckbox(playerColliderCheckboxImage, isPlayerColliderWireframeVisible);
        UpdateCheckbox(skillTargetRangeCheckboxImage, isSkillTargetRangeVisible);
        UpdateCheckbox(skillAttackRangeCheckboxImage, isSkillAttackRangeVisible);
    }

    private void UpdateSkillSlotIconLayout()
    {
        SetTopLeftRect(slotAttackLabelText.rectTransform, SlotRowLabelX, SlotAttackRowY, 14f, SlotIconSize);
        SetTopLeftRect(slotBuffLabelText.rectTransform, SlotRowLabelX, SlotBuffRowY, 14f, SlotIconSize);

        for (var slotIndex = 0; slotIndex < PlayerCombatConstants.MaxAttackSkillCount; slotIndex++)
        {
            var x = SlotIconStartX + slotIndex * (SlotIconSize + SlotIconGap);

            SetTopLeftRect(slotIconImages[slotIndex].rectTransform, x, SlotAttackRowY, SlotIconSize, SlotIconSize);
            SetTopLeftRect(slotIconTexts[slotIndex].rectTransform, 0f, 0f, SlotIconSize, SlotIconSize);

            var buffIconIndex = PlayerCombatConstants.MaxAttackSkillCount + slotIndex;

            SetTopLeftRect(slotIconImages[buffIconIndex].rectTransform, x, SlotBuffRowY, SlotIconSize, SlotIconSize);
            SetTopLeftRect(slotIconTexts[buffIconIndex].rectTransform, 0f, 0f, SlotIconSize, SlotIconSize);
        }
    }

    private void UpdateSkillSlotIcons(SkillSlotIconData[] icons)
    {
        for (var iconIndex = 0; iconIndex < slotIconImages.Length; iconIndex++)
        {
            var icon = iconIndex < icons.Length ? icons[iconIndex] : CreateEmptySkillSlotIcon();
            var text = string.IsNullOrEmpty(icon.Text) ? "-" : icon.Text;

            slotIconImages[iconIndex].color = icon.Color;
            slotIconTexts[iconIndex].text = text;
        }
    }

    private void SetRuntimeUiVisible(bool visible)
    {
        if (windowRoot != null)
        {
            windowRoot.gameObject.SetActive(visible && isWindowOpen);
        }

        if (fpsRoot != null)
        {
            fpsRoot.gameObject.SetActive(visible && isFpsVisible);
        }

        if (levelRoot != null)
        {
            levelRoot.gameObject.SetActive(visible && isLevelVisible);
        }

        if (slotRoot != null)
        {
            slotRoot.gameObject.SetActive(visible && isSkillSlotVisible);
        }
    }

    private void DestroyRuntimeUi()
    {
        if (ownsOverlayCanvas && overlayCanvas != null)
        {
            Destroy(overlayCanvas.gameObject);
        }

        overlayCanvas = null;
        windowRoot = null;
        fpsRoot = null;
        levelRoot = null;
        slotRoot = null;
        resizeHandleRect = null;
        fpsOverlayText = null;
        levelOverlayText = null;
        slotAttackLabelText = null;
        slotBuffLabelText = null;
        skillLevelControlText = null;
        ClearSkillSlotIconReferences();
        windowFpsText = null;
        fpsCheckboxImage = null;
        levelCheckboxImage = null;
        slotCheckboxImage = null;
        playerColliderCheckboxImage = null;
        skillTargetRangeCheckboxImage = null;
        skillAttackRangeCheckboxImage = null;
        skillLevelMinusButtonImage = null;
        skillLevelPlusButtonImage = null;
        ownsOverlayCanvas = false;
    }

    /// <summary>
    /// ウィンドウの位置と大きさを初期化する（初回は画面右側に置く）。画面の外にはみ出さないようにする。
    /// </summary>
    private void EnsureWindowRect(Vector2 canvasSize)
    {
        if (hasWindowRect)
        {
            return;
        }

        windowRect = DebugOptionsOverlayMath.CalculateDefaultWindowRect(
            canvasSize.x,
            canvasSize.y,
            DefaultWindowSize,
            Margin);
        hasWindowRect = true;
    }

    private Vector2 GetCanvasSize()
    {
        if (overlayCanvas != null && overlayCanvas.transform is RectTransform canvasRect)
        {
            var size = canvasRect.rect.size;

            if (size.x > 0f && size.y > 0f)
            {
                return size;
            }
        }

        return new Vector2(
            Mathf.Max(1f, Screen.width),
            Mathf.Max(1f, Screen.height));
    }

    private void ClearSkillSlotIconReferences()
    {
        for (var iconIndex = 0; iconIndex < slotIconImages.Length; iconIndex++)
        {
            slotIconImages[iconIndex] = null;
            slotIconTexts[iconIndex] = null;
        }
    }

    /// <summary>
    /// ECS からプレイヤーの経験値とレベルを読み取る。
    /// </summary>
    private static bool ReadPlayerExperience(out ExperienceComponent experience)
    {
        experience = default;

        var world = World.DefaultGameObjectInjectionWorld;

        if (world == null || !world.IsCreated)
        {
            return false;
        }

        using var query = world.EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<PlayerTag>(),
            ComponentType.ReadOnly<ExperienceComponent>(),
            ComponentType.Exclude<Prefab>());

        if (query.IsEmpty)
        {
            return false;
        }

        experience = query.GetSingleton<ExperienceComponent>();
        return true;
    }

    /// <summary>
    /// ECS からプレイヤーが装備しているスキルのレベルを集計する。
    /// </summary>
    private static bool ReadPlayerSkillLevelSummary(out SkillLevelSummary summary)
    {
        summary = new SkillLevelSummary
        {
            MinLevel = PlayerCombatConstants.MaxSkillLevel,
            MaxLevel = PlayerCombatConstants.MinSkillLevel,
            HasAnySkill = 0,
            CanDecrease = 0,
            CanIncrease = 0
        };

        var world = World.DefaultGameObjectInjectionWorld;

        if (world == null || !world.IsCreated)
        {
            return false;
        }

        var entityManager = world.EntityManager;

        if (!TryReadPlayerEntity(entityManager, out var playerEntity))
        {
            return false;
        }

        ReadAttackSkillLevelSummary(entityManager, playerEntity, ref summary);
        ReadBuffSkillLevelSummary(entityManager, playerEntity, ref summary);
        return summary.HasAnySkill != 0;
    }

    /// <summary>
    /// プレイヤーが装備しているすべてのスキルのレベルを delta だけ変える（上限・下限の範囲内）。
    /// </summary>
    private static bool TryAdjustPlayerSkillSlotLevels(int delta)
    {
        var world = World.DefaultGameObjectInjectionWorld;

        if (world == null || !world.IsCreated)
        {
            return false;
        }

        var entityManager = world.EntityManager;

        if (!TryReadPlayerEntity(entityManager, out var playerEntity))
        {
            return false;
        }

        var changedCount = 0;

        changedCount += AdjustAttackSkillSlotLevels(entityManager, playerEntity, delta);
        changedCount += AdjustBuffSkillSlotLevels(entityManager, playerEntity, delta);
        return changedCount > 0;
    }

    /// <summary>
    /// ECS からプレイヤーのスキルスロットの状態を読み取り、アイコンの表示内容を作る。
    /// </summary>
    private static void ReadPlayerSkillSlotIcons(SkillSlotIconData[] icons)
    {
        ResetSkillSlotIcons(icons);

        var world = World.DefaultGameObjectInjectionWorld;

        if (world == null || !world.IsCreated)
        {
            return;
        }

        var entityManager = world.EntityManager;

        if (!TryReadPlayerEntity(entityManager, out var playerEntity))
        {
            return;
        }

        ReadAttackSkillSlots(entityManager, playerEntity, icons);
        ReadBuffSkillSlots(entityManager, playerEntity, icons);
    }

    private static void ResetSkillSlotIcons(SkillSlotIconData[] icons)
    {
        for (var iconIndex = 0; iconIndex < icons.Length; iconIndex++)
        {
            icons[iconIndex] = CreateEmptySkillSlotIcon();
        }
    }

    private static SkillSlotIconData CreateEmptySkillSlotIcon()
    {
        return new SkillSlotIconData
        {
            Text = "-",
            Color = SlotEmptyColor
        };
    }

    private static SkillSlotIconData CreateAttackSkillSlotIcon(
        bool isEquipped,
        AttackSkillConfig config,
        AttackSkillState state)
    {
        return new SkillSlotIconData
        {
            Text = DebugOptionsOverlayMath.FormatAttackSkillSlotIconText(isEquipped, config),
            Color = CalculateAttackSkillSlotColor(isEquipped, state)
        };
    }

    private static SkillSlotIconData CreateBuffSkillSlotIcon(bool isEquipped, BuffSkillConfig config)
    {
        return new SkillSlotIconData
        {
            Text = DebugOptionsOverlayMath.FormatBuffSkillSlotIconText(isEquipped, config),
            Color = isEquipped ? SlotBuffColor : SlotEmptyColor
        };
    }

    /// <summary>
    /// 攻撃スキルの状態（未装備・発動予約・クールタイム中・発動可能）に応じたアイコンの色を返す。
    /// </summary>
    private static Color CalculateAttackSkillSlotColor(bool isEquipped, AttackSkillState state)
    {
        if (!isEquipped)
        {
            return SlotEmptyColor;
        }

        if (state.IsTriggered != 0)
        {
            return SlotTriggeredColor;
        }

        if (state.IsCooltime != 0)
        {
            return SlotCooltimeColor;
        }

        return SlotReadyColor;
    }

    private static bool TryReadPlayerEntity(EntityManager entityManager, out Entity playerEntity)
    {
        var query = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<PlayerTag>(),
            ComponentType.Exclude<Prefab>());
        var entities = query.ToEntityArray(Allocator.Temp);

        if (entities.Length <= 0)
        {
            entities.Dispose();
            query.Dispose();
            playerEntity = Entity.Null;
            return false;
        }

        playerEntity = entities[0];
        entities.Dispose();
        query.Dispose();
        return true;
    }

    private static void ReadAttackSkillLevelSummary(
        EntityManager entityManager,
        Entity playerEntity,
        ref SkillLevelSummary summary)
    {
        var query = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<SkillSlotComponent>(),
            ComponentType.ReadOnly<AttackSkillSlotTag>(),
            ComponentType.ReadOnly<EquippedSkillTag>(),
            ComponentType.ReadOnly<AttackSkillState>(),
            ComponentType.Exclude<Prefab>());
        var entities = query.ToEntityArray(Allocator.Temp);
        var slots = query.ToComponentDataArray<SkillSlotComponent>(Allocator.Temp);

        for (var slotEntityIndex = 0; slotEntityIndex < entities.Length; slotEntityIndex++)
        {
            var slot = slots[slotEntityIndex];

            if (slot.Owner != playerEntity ||
                slot.SlotIndex < 0)
            {
                continue;
            }

            var skillState = entityManager.GetComponentData<AttackSkillState>(entities[slotEntityIndex]);

            AddSkillLevelToSummary(skillState.Level, ref summary);
        }

        slots.Dispose();
        entities.Dispose();
        query.Dispose();
    }

    private static void ReadBuffSkillLevelSummary(
        EntityManager entityManager,
        Entity playerEntity,
        ref SkillLevelSummary summary)
    {
        var query = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<SkillSlotComponent>(),
            ComponentType.ReadOnly<BuffSkillSlotTag>(),
            ComponentType.ReadOnly<EquippedSkillTag>(),
            ComponentType.ReadOnly<BuffSkillState>(),
            ComponentType.Exclude<Prefab>());
        var entities = query.ToEntityArray(Allocator.Temp);
        var slots = query.ToComponentDataArray<SkillSlotComponent>(Allocator.Temp);

        for (var slotEntityIndex = 0; slotEntityIndex < entities.Length; slotEntityIndex++)
        {
            var slot = slots[slotEntityIndex];

            if (slot.Owner != playerEntity ||
                slot.SlotIndex < 0)
            {
                continue;
            }

            var skillState = entityManager.GetComponentData<BuffSkillState>(entities[slotEntityIndex]);

            AddSkillLevelToSummary(skillState.Level, ref summary);
        }

        slots.Dispose();
        entities.Dispose();
        query.Dispose();
    }

    private static void AddSkillLevelToSummary(int level, ref SkillLevelSummary summary)
    {
        var safeLevel = DebugOptionsOverlayMath.ApplySkillLevelDelta(level, 0);

        summary.MinLevel = math.min(summary.MinLevel, safeLevel);
        summary.MaxLevel = math.max(summary.MaxLevel, safeLevel);
        summary.HasAnySkill = 1;

        if (safeLevel > PlayerCombatConstants.MinSkillLevel)
        {
            summary.CanDecrease = 1;
        }

        if (safeLevel < PlayerCombatConstants.MaxSkillLevel)
        {
            summary.CanIncrease = 1;
        }
    }

    private static int AdjustAttackSkillSlotLevels(
        EntityManager entityManager,
        Entity playerEntity,
        int delta)
    {
        var query = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<SkillSlotComponent>(),
            ComponentType.ReadOnly<AttackSkillSlotTag>(),
            ComponentType.ReadOnly<EquippedSkillTag>(),
            ComponentType.ReadWrite<AttackSkillState>(),
            ComponentType.Exclude<Prefab>());
        var entities = query.ToEntityArray(Allocator.Temp);
        var slots = query.ToComponentDataArray<SkillSlotComponent>(Allocator.Temp);
        var changedCount = 0;

        for (var slotEntityIndex = 0; slotEntityIndex < entities.Length; slotEntityIndex++)
        {
            var slot = slots[slotEntityIndex];

            if (slot.Owner != playerEntity ||
                slot.SlotIndex < 0)
            {
                continue;
            }

            var entity = entities[slotEntityIndex];
            var skillState = entityManager.GetComponentData<AttackSkillState>(entity);
            var nextLevel = DebugOptionsOverlayMath.ApplySkillLevelDelta(skillState.Level, delta);

            if (nextLevel == skillState.Level)
            {
                continue;
            }

            skillState.Level = nextLevel;
            entityManager.SetComponentData(entity, skillState);
            changedCount++;
        }

        slots.Dispose();
        entities.Dispose();
        query.Dispose();
        return changedCount;
    }

    private static int AdjustBuffSkillSlotLevels(
        EntityManager entityManager,
        Entity playerEntity,
        int delta)
    {
        var query = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<SkillSlotComponent>(),
            ComponentType.ReadOnly<BuffSkillSlotTag>(),
            ComponentType.ReadOnly<EquippedSkillTag>(),
            ComponentType.ReadWrite<BuffSkillState>(),
            ComponentType.Exclude<Prefab>());
        var entities = query.ToEntityArray(Allocator.Temp);
        var slots = query.ToComponentDataArray<SkillSlotComponent>(Allocator.Temp);
        var changedCount = 0;

        for (var slotEntityIndex = 0; slotEntityIndex < entities.Length; slotEntityIndex++)
        {
            var slot = slots[slotEntityIndex];

            if (slot.Owner != playerEntity ||
                slot.SlotIndex < 0)
            {
                continue;
            }

            var entity = entities[slotEntityIndex];
            var skillState = entityManager.GetComponentData<BuffSkillState>(entity);
            var nextLevel = DebugOptionsOverlayMath.ApplySkillLevelDelta(skillState.Level, delta);

            if (nextLevel == skillState.Level)
            {
                continue;
            }

            skillState.Level = nextLevel;
            entityManager.SetComponentData(entity, skillState);
            changedCount++;
        }

        slots.Dispose();
        entities.Dispose();
        query.Dispose();
        return changedCount;
    }

    private static void ReadAttackSkillSlots(
        EntityManager entityManager,
        Entity playerEntity,
        SkillSlotIconData[] icons)
    {
        var query = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<SkillSlotComponent>(),
            ComponentType.ReadOnly<AttackSkillSlotTag>(),
            ComponentType.Exclude<Prefab>());
        var entities = query.ToEntityArray(Allocator.Temp);
        var slots = query.ToComponentDataArray<SkillSlotComponent>(Allocator.Temp);

        for (var slotEntityIndex = 0; slotEntityIndex < entities.Length; slotEntityIndex++)
        {
            var slot = slots[slotEntityIndex];

            if (slot.Owner != playerEntity ||
                slot.SlotIndex < 0 ||
                slot.SlotIndex >= PlayerCombatConstants.MaxAttackSkillCount ||
                slot.SlotIndex >= icons.Length)
            {
                continue;
            }

            var entity = entities[slotEntityIndex];
            var isEquipped = entityManager.HasComponent<EquippedSkillTag>(entity) &&
                             entityManager.HasComponent<AttackSkillConfig>(entity) &&
                             entityManager.HasComponent<AttackSkillState>(entity);

            icons[slot.SlotIndex] = CreateAttackSkillSlotIcon(
                isEquipped,
                isEquipped ? entityManager.GetComponentData<AttackSkillConfig>(entity) : default,
                isEquipped ? entityManager.GetComponentData<AttackSkillState>(entity) : default);
        }

        slots.Dispose();
        entities.Dispose();
        query.Dispose();
    }

    private static void ReadBuffSkillSlots(
        EntityManager entityManager,
        Entity playerEntity,
        SkillSlotIconData[] icons)
    {
        var query = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<SkillSlotComponent>(),
            ComponentType.ReadOnly<BuffSkillSlotTag>(),
            ComponentType.Exclude<Prefab>());
        var entities = query.ToEntityArray(Allocator.Temp);
        var slots = query.ToComponentDataArray<SkillSlotComponent>(Allocator.Temp);

        for (var slotEntityIndex = 0; slotEntityIndex < entities.Length; slotEntityIndex++)
        {
            var slot = slots[slotEntityIndex];

            if (slot.Owner != playerEntity ||
                slot.SlotIndex < 0 ||
                slot.SlotIndex >= PlayerCombatConstants.MaxBuffSkillCount)
            {
                continue;
            }

            var entity = entities[slotEntityIndex];
            var iconIndex = PlayerCombatConstants.MaxAttackSkillCount + slot.SlotIndex;

            if (iconIndex < 0 || iconIndex >= icons.Length)
            {
                continue;
            }

            var isEquipped = entityManager.HasComponent<EquippedSkillTag>(entity) &&
                             entityManager.HasComponent<BuffSkillConfig>(entity);

            icons[iconIndex] = CreateBuffSkillSlotIcon(
                isEquipped,
                isEquipped ? entityManager.GetComponentData<BuffSkillConfig>(entity) : default);
        }

        slots.Dispose();
        entities.Dispose();
        query.Dispose();
    }

    private Rect GetTitleBarRect()
    {
        return new Rect(windowRect.x, windowRect.y, windowRect.width - ResizeHandleSize, TitleBarHeight);
    }

    private Rect GetResizeHandleRect()
    {
        return new Rect(
            windowRect.xMax - ResizeHandleSize,
            windowRect.yMax - ResizeHandleSize,
            ResizeHandleSize,
            ResizeHandleSize);
    }

    private Rect GetToggleRect(float rowY)
    {
        return new Rect(windowRect.x + 18f, windowRect.y + rowY, windowRect.width - 36f, 24f);
    }

    private Rect GetSkillLevelMinusButtonRect()
    {
        return GetSkillLevelButtonRect(SkillLevelMinusButtonX);
    }

    private Rect GetSkillLevelPlusButtonRect()
    {
        return GetSkillLevelButtonRect(SkillLevelPlusButtonX);
    }

    private Rect GetSkillLevelButtonRect(float buttonX)
    {
        return new Rect(
            windowRect.x + buttonX,
            windowRect.y + SkillLevelControlY + 1f,
            SkillLevelButtonSize,
            SkillLevelButtonSize);
    }

    /// <summary>
    /// マウス座標（画面左下が原点）を、Canvas の左上を原点とした座標に変換する。
    /// CanvasScaler で UI が拡大縮小されていても、クリック判定がずれないようにするため。
    /// </summary>
    private Vector2 ToCanvasTopLeftMousePosition(Vector2 bottomLeftMousePosition)
    {
        if (overlayCanvas != null &&
            overlayCanvas.transform is RectTransform canvasRect &&
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRect,
                bottomLeftMousePosition,
                null,
                out var localPosition))
        {
            var canvasArea = canvasRect.rect;

            return new Vector2(
                localPosition.x - canvasArea.xMin,
                canvasArea.yMax - localPosition.y);
        }

        return new Vector2(bottomLeftMousePosition.x, Screen.height - bottomLeftMousePosition.y);
    }

    /// <summary>
    /// 画面サイズに合わせて UI を拡大縮小するように設定する。
    /// </summary>
    private static void ConfigureCanvasScaler(CanvasScaler canvasScaler)
    {
        canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasScaler.referenceResolution = ReferenceResolution;
        canvasScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        canvasScaler.matchWidthOrHeight = CanvasScaleMatch;
    }

    private static void UpdateCheckbox(Image checkboxImage, bool isChecked)
    {
        if (checkboxImage != null)
        {
            checkboxImage.color = isChecked ? CheckboxOnColor : CheckboxOffColor;
        }
    }

    private static Image CreateCheckboxRow(RectTransform parent, float rowY, string label)
    {
        var checkbox = CreateImage("checkbox", parent, CheckboxOffColor);

        SetTopLeftRect(checkbox.rectTransform, 18f, rowY + 3f, 16f, 16f);

        var labelText = CreateText(label, parent, 17, FontStyle.Normal);

        SetTopLeftRect(labelText.rectTransform, 44f, rowY, 260f, 24f);
        labelText.text = label;

        return checkbox;
    }

    private static Image CreateSmallButton(RectTransform parent, string name, string label)
    {
        var buttonImage = CreateImage(name, parent, ButtonColor);
        var buttonText = CreateText($"{name}-label", buttonImage.transform, 16, FontStyle.Bold);

        buttonText.alignment = TextAnchor.MiddleCenter;
        buttonText.text = label;
        SetTopLeftRect(buttonText.rectTransform, 0f, 0f, SkillLevelButtonSize, SkillLevelButtonSize);
        return buttonImage;
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

    private static Text CreateText(string name, Transform parent, int fontSize, FontStyle fontStyle)
    {
        var gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));

        gameObject.transform.SetParent(parent, false);

        var text = gameObject.GetComponent<Text>();

        text.font = Resources.GetBuiltinResource<Font>(BuiltInFontResourceName);
        text.color = TextColor;
        text.fontSize = fontSize;
        text.fontStyle = fontStyle;
        text.alignment = TextAnchor.MiddleLeft;
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

/// <summary>
/// デバッグ用の可視化（当たり判定・スキル範囲）を描画する。
/// ECS のデータを読み取って線で描くだけで、ゲームの状態は変更しない。
/// </summary>
public static class DebugOptionsRuntimeDrawer
{
    private const int CircleSegments = 48;
    private const float DefaultLineSeconds = 0.02f;
    private const float RangeLineHeightOffset = 0.08f;
    public const float AttackRangeFadeSeconds = 5f;

    private static readonly Color CollisionRadiusColor = new Color(1f, 0.85f, 0.15f, 1f);
    private static readonly Color GroundSensorColor = new Color(0.1f, 0.85f, 1f, 1f);
    private static readonly Color PhysicsColliderColor = new Color(0.2f, 1f, 0.2f, 1f);
    private static readonly Color SkillTargetRangeColor = new Color(0.2f, 0.45f, 1f, 1f);
    private static readonly Color SkillAttackRangeColor = new Color(1f, 0.15f, 0.15f, 1f);

    /// <summary>
    /// 可視化オプションが 1 つでも有効かを返す。
    /// </summary>
    public static bool HasAnyOptionEnabled(
        bool showPlayerColliderWireframe,
        bool showSkillTargetRange,
        bool showSkillAttackRange)
    {
        return showPlayerColliderWireframe ||
               showSkillTargetRange ||
               showSkillAttackRange;
    }

    /// <summary>
    /// 有効になっているオプションの可視化を描画する。
    /// </summary>
    public static void Draw(
        bool showPlayerColliderWireframe,
        bool showSkillTargetRange,
        bool showSkillAttackRange)
    {
        SkillAttackRangeDebugEvents.SetRecordingEnabled(showSkillAttackRange);

        if (!TryGetEntityManager(out var entityManager))
        {
            return;
        }

        if (showPlayerColliderWireframe)
        {
            DrawPlayerColliders(entityManager);
        }

        if (showSkillTargetRange || showSkillAttackRange)
        {
            DrawSkillRanges(entityManager, showSkillTargetRange, showSkillAttackRange);
            return;
        }

        DebugOptionsRangeRenderer.HideSkillRanges();
    }

    /// <summary>
    /// 描画に使ったデータとオブジェクトをすべて片付ける。
    /// </summary>
    public static void Dispose()
    {
        SkillAttackRangeDebugEvents.Clear();
        DebugOptionsRangeRenderer.DisposeAll();
    }

    private static bool TryGetEntityManager(out EntityManager entityManager)
    {
        var world = World.DefaultGameObjectInjectionWorld;

        if (world == null || !world.IsCreated)
        {
            entityManager = default;
            return false;
        }

        entityManager = world.EntityManager;
        return true;
    }

    /// <summary>
    /// プレイヤーの当たり判定（ゲーム用の半径・接地判定の球・物理 Collider）を描画する。
    /// </summary>
    private static void DrawPlayerColliders(EntityManager entityManager)
    {
        DrawPlayerCollisionRadiusWireframes(entityManager);
        DrawPlayerGroundSensorWireframes(entityManager);
        DrawPlayerPhysicsColliderAabbs(entityManager);
    }

    private static void DrawPlayerCollisionRadiusWireframes(EntityManager entityManager)
    {
        var query = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<PlayerTag>(),
            ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadOnly<CollisionRadius>());
        var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var radii = query.ToComponentDataArray<CollisionRadius>(Allocator.Temp);

        for (var entityIndex = 0; entityIndex < transforms.Length; entityIndex++)
        {
            var radius = radii[entityIndex].Value;

            if (radius <= 0f)
            {
                continue;
            }

            DrawCircleXZ(transforms[entityIndex].Position, radius, CollisionRadiusColor);
        }

        radii.Dispose();
        transforms.Dispose();
        query.Dispose();
    }

    private static void DrawPlayerGroundSensorWireframes(EntityManager entityManager)
    {
        var query = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<PlayerTag>(),
            ComponentType.ReadOnly<LocalTransform>(),
            ComponentType.ReadOnly<GroundSensor>());
        var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
        var sensors = query.ToComponentDataArray<GroundSensor>(Allocator.Temp);

        for (var entityIndex = 0; entityIndex < transforms.Length; entityIndex++)
        {
            var transform = transforms[entityIndex];
            var sensor = sensors[entityIndex];

            if (sensor.Radius <= 0f)
            {
                continue;
            }

            var center = transform.Position +
                         math.rotate(transform.Rotation, sensor.LocalCenter * transform.Scale);
            var radius = sensor.Radius * transform.Scale;

            DrawCircle(center, radius, new float3(1f, 0f, 0f), new float3(0f, 0f, 1f), GroundSensorColor);
            DrawCircle(center, radius, new float3(1f, 0f, 0f), new float3(0f, 1f, 0f), GroundSensorColor);
            DrawCircle(center, radius, new float3(0f, 1f, 0f), new float3(0f, 0f, 1f), GroundSensorColor);
        }

        sensors.Dispose();
        transforms.Dispose();
        query.Dispose();
    }

    private static void DrawPlayerPhysicsColliderAabbs(EntityManager entityManager)
    {
        var query = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<PlayerTag>(),
            ComponentType.ReadOnly<PhysicsCollider>(),
            ComponentType.ReadOnly<LocalToWorld>());
        var colliders = query.ToComponentDataArray<PhysicsCollider>(Allocator.Temp);
        var transforms = query.ToComponentDataArray<LocalToWorld>(Allocator.Temp);

        for (var entityIndex = 0; entityIndex < colliders.Length; entityIndex++)
        {
            if (!colliders[entityIndex].Value.IsCreated)
            {
                continue;
            }

            DrawAabb(
                CalculateWorldAabb(colliders[entityIndex].Value, transforms[entityIndex].Value),
                PhysicsColliderColor);
        }

        transforms.Dispose();
        colliders.Dispose();
        query.Dispose();
    }

    /// <summary>
    /// スキルのターゲット範囲（青）と攻撃範囲（赤）を描画する。
    /// </summary>
    private static void DrawSkillRanges(
        EntityManager entityManager,
        bool showSkillTargetRange,
        bool showSkillAttackRange)
    {
        if (showSkillTargetRange)
        {
            DrawSkillTargetRanges(entityManager);
        }
        else
        {
            DebugOptionsRangeRenderer.FinishTargetFrame(0);
        }

        if (showSkillAttackRange)
        {
            DrawSkillAttackRangeEvents();
        }
        else
        {
            DebugOptionsRangeRenderer.FinishAttackFrame(0);
        }
    }

    /// <summary>
    /// 装備中の攻撃スキルの、ターゲットを探す範囲を描画する。
    /// </summary>
    private static void DrawSkillTargetRanges(EntityManager entityManager)
    {
        var attackQuery = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<AttackSkillConfig>(),
            ComponentType.ReadOnly<AttackSkillState>(),
            ComponentType.ReadOnly<SkillSlotComponent>(),
            ComponentType.ReadOnly<EquippedSkillTag>(),
            ComponentType.ReadOnly<AttackSkillSlotTag>());
        var buffQuery = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<BuffSkillConfig>(),
            ComponentType.ReadOnly<SkillSlotComponent>(),
            ComponentType.ReadOnly<EquippedSkillTag>(),
            ComponentType.ReadOnly<BuffSkillSlotTag>());
        var attackConfigs = attackQuery.ToComponentDataArray<AttackSkillConfig>(Allocator.Temp);
        var attackSlots = attackQuery.ToComponentDataArray<SkillSlotComponent>(Allocator.Temp);
        var buffSkills = buffQuery.ToComponentDataArray<BuffSkillConfig>(Allocator.Temp);
        var buffSlots = buffQuery.ToComponentDataArray<SkillSlotComponent>(Allocator.Temp);
        var renderedRangeCount = 0;

        for (var skillIndex = 0; skillIndex < attackConfigs.Length; skillIndex++)
        {
            var owner = attackSlots[skillIndex].Owner;

            if (owner == Entity.Null ||
                !entityManager.HasComponent<PlayerTag>(owner) ||
                !TryGetEntityPosition(entityManager, owner, out var ownerPosition))
            {
                continue;
            }

            var buffs = SkillSystemUtility.CreateBuffAccumulatorForOwner(
                owner,
                buffSkills,
                buffSlots);
            var targetRange = SkillMath.CalculateEffectiveTargetRange(attackConfigs[skillIndex], buffs);

            if (targetRange > 0f)
            {
                ownerPosition.y += RangeLineHeightOffset;
                DebugOptionsRangeRenderer.DrawTargetCircle(
                    renderedRangeCount,
                    ownerPosition,
                    targetRange,
                    SkillTargetRangeColor);
                renderedRangeCount++;
            }
        }

        DebugOptionsRangeRenderer.FinishTargetFrame(renderedRangeCount);
        buffSlots.Dispose();
        buffSkills.Dispose();
        attackSlots.Dispose();
        attackConfigs.Dispose();
        buffQuery.Dispose();
        attackQuery.Dispose();
    }

    /// <summary>
    /// 直近に発生した攻撃範囲を、時間とともに薄くしながら描画する。
    /// </summary>
    private static void DrawSkillAttackRangeEvents()
    {
        SkillAttackRangeDebugEvents.DrawAndUpdate(
            Time.unscaledTime,
            AttackRangeFadeSeconds,
            SkillAttackRangeColor);
    }

    private static bool TryGetEntityPosition(
        EntityManager entityManager,
        Entity entity,
        out float3 position)
    {
        if (entityManager.HasComponent<LocalToWorld>(entity))
        {
            position = entityManager.GetComponentData<LocalToWorld>(entity).Position;
            return true;
        }

        if (entityManager.HasComponent<LocalTransform>(entity))
        {
            position = entityManager.GetComponentData<LocalTransform>(entity).Position;
            return true;
        }

        position = float3.zero;
        return false;
    }

    private static Aabb CalculateWorldAabb(
        BlobAssetReference<Unity.Physics.Collider> collider,
        float4x4 localToWorld)
    {
        var position = localToWorld.c3.xyz;
        var right = localToWorld.c0.xyz;
        var up = localToWorld.c1.xyz;
        var forward = localToWorld.c2.xyz;
        var scale = math.max(
            math.length(right),
            math.max(math.length(up), math.length(forward)));

        if (scale <= 0.0001f)
        {
            scale = 1f;
        }

        var rotation = quaternion.LookRotationSafe(
            math.normalizesafe(forward, new float3(0f, 0f, 1f)),
            math.normalizesafe(up, new float3(0f, 1f, 0f)));

        return collider.Value.CalculateAabb(
            new RigidTransform(rotation, position),
            scale);
    }

    private static void DrawCircleXZ(float3 center, float radius, Color color)
    {
        DrawCircle(
            center,
            radius,
            new float3(1f, 0f, 0f),
            new float3(0f, 0f, 1f),
            color);
    }

    private static void DrawCircle(
        float3 center,
        float radius,
        float3 axisA,
        float3 axisB,
        Color color)
    {
        if (radius <= 0f)
        {
            return;
        }

        var previousPoint = center + axisA * radius;

        for (var segmentIndex = 1; segmentIndex <= CircleSegments; segmentIndex++)
        {
            var angle = segmentIndex * math.PI * 2f / CircleSegments;
            var nextPoint = center +
                            (math.cos(angle) * axisA + math.sin(angle) * axisB) * radius;

            Debug.DrawLine(
                (Vector3)previousPoint,
                (Vector3)nextPoint,
                color,
                DefaultLineSeconds,
                false);
            previousPoint = nextPoint;
        }
    }

    private static void DrawAabb(Aabb aabb, Color color)
    {
        var min = aabb.Min;
        var max = aabb.Max;
        var p000 = new float3(min.x, min.y, min.z);
        var p001 = new float3(min.x, min.y, max.z);
        var p010 = new float3(min.x, max.y, min.z);
        var p011 = new float3(min.x, max.y, max.z);
        var p100 = new float3(max.x, min.y, min.z);
        var p101 = new float3(max.x, min.y, max.z);
        var p110 = new float3(max.x, max.y, min.z);
        var p111 = new float3(max.x, max.y, max.z);

        DrawLine(p000, p001, color);
        DrawLine(p001, p101, color);
        DrawLine(p101, p100, color);
        DrawLine(p100, p000, color);
        DrawLine(p010, p011, color);
        DrawLine(p011, p111, color);
        DrawLine(p111, p110, color);
        DrawLine(p110, p010, color);
        DrawLine(p000, p010, color);
        DrawLine(p001, p011, color);
        DrawLine(p100, p110, color);
        DrawLine(p101, p111, color);
    }

    private static void DrawLine(float3 from, float3 to, Color color)
    {
        Debug.DrawLine(
            (Vector3)from,
            (Vector3)to,
            color,
            DefaultLineSeconds,
            false);
    }
}

/// <summary>
/// デバッグ表示用に記録した、1 回分の攻撃範囲。
/// </summary>
public struct SkillAttackRangeDebugEvent
{
    /// <summary>攻撃範囲の中心。</summary>
    public float3 Center;

    /// <summary>攻撃範囲の半径。</summary>
    public float Radius;

    /// <summary>記録した時刻（秒）。時間とともに表示を薄くするために使う。</summary>
    public float StartedAtSeconds;
}

/// <summary>
/// SkillLogicSystem からデバッグ表示へ、攻撃範囲の発生位置を渡すためのデバッグ専用の記録。
/// 表示が有効なときだけ記録し、無効なときは何もしないので、通常のプレイには影響しない。
/// </summary>
public static class SkillAttackRangeDebugEvents
{
    private const float RangeLineHeightOffset = 0.12f;
    private const int MaxEventCount = 128;

    private static readonly List<SkillAttackRangeDebugEvent> Events = new List<SkillAttackRangeDebugEvent>(32);
    private static bool isRecordingEnabled;

    /// <summary>
    /// 記録するかどうかを切り替える。無効にしたら記録を消す。
    /// </summary>
    public static void SetRecordingEnabled(bool enabled)
    {
        if (isRecordingEnabled == enabled)
        {
            return;
        }

        isRecordingEnabled = enabled;

        if (!enabled)
        {
            Clear();
        }
    }

    /// <summary>
    /// 攻撃範囲を 1 つ記録する。記録数が上限を超えたら古いものから消す。
    /// </summary>
    public static void Record(float3 center, float radius)
    {
        if (!isRecordingEnabled || radius <= 0f)
        {
            return;
        }

        var now = Time.unscaledTime;

        PruneExpiredEvents(now, DebugOptionsRuntimeDrawer.AttackRangeFadeSeconds);

        while (Events.Count >= MaxEventCount)
        {
            Events.RemoveAt(0);
        }

        center.y += RangeLineHeightOffset;
        Events.Add(new SkillAttackRangeDebugEvent
        {
            Center = center,
            Radius = radius,
            StartedAtSeconds = now
        });
    }

    /// <summary>
    /// 記録をすべて消す。
    /// </summary>
    public static void Clear()
    {
        Events.Clear();
        DebugOptionsRangeRenderer.FinishAttackFrame(0);
    }

    /// <summary>
    /// 記録された攻撃範囲を描画し、表示時間を過ぎたものを消す。
    /// </summary>
    public static void DrawAndUpdate(
        float currentTimeSeconds,
        float lifetimeSeconds,
        Color baseColor)
    {
        if (Events.Count == 0)
        {
            DebugOptionsRangeRenderer.FinishAttackFrame(0);
            return;
        }

        var renderedRangeCount = 0;

        for (var eventIndex = Events.Count - 1; eventIndex >= 0; eventIndex--)
        {
            var rangeEvent = Events[eventIndex];

            if (!SkillAttackRangeDebugMath.IsEventAlive(
                    rangeEvent.StartedAtSeconds,
                    currentTimeSeconds,
                    lifetimeSeconds))
            {
                Events.RemoveAt(eventIndex);
                continue;
            }

            var alpha = SkillAttackRangeDebugMath.CalculateFadeAlpha(
                currentTimeSeconds - rangeEvent.StartedAtSeconds,
                lifetimeSeconds);
            var color = new Color(baseColor.r, baseColor.g, baseColor.b, baseColor.a * alpha);

            DebugOptionsRangeRenderer.DrawAttackCircle(
                renderedRangeCount,
                rangeEvent.Center,
                rangeEvent.Radius,
                color);
            renderedRangeCount++;
        }

        DebugOptionsRangeRenderer.FinishAttackFrame(renderedRangeCount);
    }

    /// <summary>
    /// 表示時間を過ぎた記録を消す。
    /// </summary>
    private static void PruneExpiredEvents(float currentTimeSeconds, float lifetimeSeconds)
    {
        for (var eventIndex = Events.Count - 1; eventIndex >= 0; eventIndex--)
        {
            if (SkillAttackRangeDebugMath.IsEventAlive(
                    Events[eventIndex].StartedAtSeconds,
                    currentTimeSeconds,
                    lifetimeSeconds))
            {
                continue;
            }

            Events.RemoveAt(eventIndex);
        }
    }
}

/// <summary>
/// スキル範囲の円を Game ビューに表示するための LineRenderer の管理（使い回し用のプール）。
/// Gizmos は Game ビューやビルドでは表示されないため、LineRenderer で描画している。
/// </summary>
public static class DebugOptionsRangeRenderer
{
    private const int CircleSegments = 96;
    private const float LineWidth = 0.16f;

    private static readonly List<LineRenderer> TargetRangeLines = new List<LineRenderer>(8);
    private static readonly List<LineRenderer> AttackRangeLines = new List<LineRenderer>(16);

    private static Transform root;
    private static UnityEngine.Material lineMaterial;

    /// <summary>
    /// ターゲット範囲の円を描画する。
    /// </summary>
    public static void DrawTargetCircle(
        int rendererIndex,
        float3 center,
        float radius,
        Color color)
    {
        DrawCircle(TargetRangeLines, "Debug Target Range", rendererIndex, center, radius, color);
    }

    /// <summary>
    /// 攻撃範囲の円を描画する。
    /// </summary>
    public static void DrawAttackCircle(
        int rendererIndex,
        float3 center,
        float radius,
        Color color)
    {
        DrawCircle(AttackRangeLines, "Debug Attack Range", rendererIndex, center, radius, color);
    }

    /// <summary>
    /// このフレームで使わなかったターゲット範囲用の LineRenderer を非表示にする。
    /// </summary>
    public static void FinishTargetFrame(int usedCount)
    {
        DisableUnused(TargetRangeLines, usedCount);
    }

    /// <summary>
    /// このフレームで使わなかった攻撃範囲用の LineRenderer を非表示にする。
    /// </summary>
    public static void FinishAttackFrame(int usedCount)
    {
        DisableUnused(AttackRangeLines, usedCount);
    }

    /// <summary>
    /// スキル範囲の表示をすべて非表示にする。
    /// </summary>
    public static void HideSkillRanges()
    {
        DisableUnused(TargetRangeLines, 0);
        DisableUnused(AttackRangeLines, 0);
    }

    /// <summary>
    /// 作成した LineRenderer とマテリアルをすべて破棄する。
    /// </summary>
    public static void DisposeAll()
    {
        DestroyRenderers(TargetRangeLines);
        DestroyRenderers(AttackRangeLines);

        if (root != null)
        {
            DestroyDebugObject(root.gameObject);
            root = null;
        }

        if (lineMaterial != null)
        {
            DestroyDebugObject(lineMaterial);
            lineMaterial = null;
        }
    }

    private static void DrawCircle(
        List<LineRenderer> renderers,
        string objectName,
        int rendererIndex,
        float3 center,
        float radius,
        Color color)
    {
        if (rendererIndex < 0 || radius <= 0f)
        {
            return;
        }

        var lineRenderer = GetLineRenderer(renderers, objectName, rendererIndex);

        lineRenderer.enabled = true;
        lineRenderer.gameObject.SetActive(true);
        lineRenderer.startColor = color;
        lineRenderer.endColor = color;
        lineRenderer.positionCount = CircleSegments;

        for (var segmentIndex = 0; segmentIndex < CircleSegments; segmentIndex++)
        {
            lineRenderer.SetPosition(
                segmentIndex,
                DebugOptionsRangeRendererMath.CalculateCirclePoint(
                    center,
                    radius,
                    segmentIndex,
                    CircleSegments));
        }
    }

    /// <summary>
    /// プールから LineRenderer を取り出す。足りなければ新しく作る。
    /// </summary>
    private static LineRenderer GetLineRenderer(
        List<LineRenderer> renderers,
        string objectName,
        int rendererIndex)
    {
        while (renderers.Count <= rendererIndex)
        {
            renderers.Add(CreateLineRenderer($"{objectName} {renderers.Count}"));
        }

        return renderers[rendererIndex];
    }

    /// <summary>
    /// 円の描画用の LineRenderer を作る。
    /// </summary>
    private static LineRenderer CreateLineRenderer(string objectName)
    {
        var gameObject = new GameObject(objectName);

        gameObject.hideFlags = HideFlags.HideAndDontSave;
        gameObject.transform.SetParent(GetRoot(), false);

        var lineRenderer = gameObject.AddComponent<LineRenderer>();

        lineRenderer.hideFlags = HideFlags.HideAndDontSave;
        lineRenderer.useWorldSpace = true;
        lineRenderer.loop = true;
        lineRenderer.widthMultiplier = LineWidth;
        lineRenderer.numCornerVertices = 2;
        lineRenderer.numCapVertices = 0;
        lineRenderer.alignment = LineAlignment.View;
        lineRenderer.textureMode = LineTextureMode.Stretch;
        lineRenderer.shadowCastingMode = ShadowCastingMode.Off;
        lineRenderer.receiveShadows = false;
        lineRenderer.allowOcclusionWhenDynamic = false;
        lineRenderer.sortingOrder = short.MaxValue;

        var material = GetLineMaterial();

        if (material != null)
        {
            lineRenderer.sharedMaterial = material;
        }

        return lineRenderer;
    }

    private static Transform GetRoot()
    {
        if (root != null)
        {
            return root;
        }

        var gameObject = new GameObject("Debug Options Range Renderer");

        gameObject.hideFlags = HideFlags.HideAndDontSave;
        Object.DontDestroyOnLoad(gameObject);
        root = gameObject.transform;
        return root;
    }

    /// <summary>
    /// 線の描画に使うマテリアルを返す（初回だけ作成する）。
    /// </summary>
    private static UnityEngine.Material GetLineMaterial()
    {
        if (lineMaterial != null)
        {
            return lineMaterial;
        }

        var shader = Shader.Find("Hidden/Internal-Colored");

        if (shader == null)
        {
            shader = Shader.Find("Sprites/Default");
        }

        if (shader == null)
        {
            return null;
        }

        lineMaterial = new UnityEngine.Material(shader)
        {
            hideFlags = HideFlags.HideAndDontSave
        };

        lineMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        lineMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        lineMaterial.SetInt("_Cull", (int)CullMode.Off);
        lineMaterial.SetInt("_ZWrite", 0);
        lineMaterial.SetInt("_ZTest", (int)CompareFunction.Always);

        return lineMaterial;
    }

    /// <summary>
    /// 使わなかった LineRenderer を非表示にする。
    /// </summary>
    private static void DisableUnused(List<LineRenderer> renderers, int usedCount)
    {
        var safeUsedCount = math.max(0, usedCount);

        for (var rendererIndex = safeUsedCount; rendererIndex < renderers.Count; rendererIndex++)
        {
            if (renderers[rendererIndex] == null)
            {
                continue;
            }

            renderers[rendererIndex].enabled = false;
            renderers[rendererIndex].gameObject.SetActive(false);
        }
    }

    private static void DestroyRenderers(List<LineRenderer> renderers)
    {
        for (var rendererIndex = 0; rendererIndex < renderers.Count; rendererIndex++)
        {
            var lineRenderer = renderers[rendererIndex];

            if (lineRenderer == null)
            {
                continue;
            }

            DestroyDebugObject(lineRenderer.gameObject);
        }

        renderers.Clear();
    }

    private static void DestroyDebugObject(UnityEngine.Object target)
    {
        if (target == null)
        {
            return;
        }

#if UNITY_EDITOR
        UnityEngine.Object.DestroyImmediate(target);
#else
        UnityEngine.Object.Destroy(target);
#endif
    }
}

/// <summary>
/// スキル範囲の円の描画に使う計算。Unity のオブジェクトに依存しないので、単体テストできる。
/// </summary>
public static class DebugOptionsRangeRendererMath
{
    /// <summary>
    /// 円周を segmentCount 等分したときの、segmentIndex 番目の点の座標（XZ 平面）を返す。
    /// </summary>
    public static Vector3 CalculateCirclePoint(
        float3 center,
        float radius,
        int segmentIndex,
        int segmentCount)
    {
        var safeSegmentCount = math.max(1, segmentCount);
        var angle = segmentIndex * math.PI * 2f / safeSegmentCount;
        var point = center + new float3(
            math.cos(angle) * radius,
            0f,
            math.sin(angle) * radius);

        return (Vector3)point;
    }
}

/// <summary>
/// 攻撃範囲のデバッグ表示に使う計算。
/// </summary>
public static class SkillAttackRangeDebugMath
{
    /// <summary>
    /// 経過時間に応じた表示の不透明度（1 → 0）を返す。
    /// </summary>
    public static float CalculateFadeAlpha(float elapsedSeconds, float lifetimeSeconds)
    {
        if (lifetimeSeconds <= 0f)
        {
            return 0f;
        }

        return math.saturate(1f - math.max(0f, elapsedSeconds) / lifetimeSeconds);
    }

    /// <summary>
    /// 記録がまだ表示時間内かを返す。
    /// </summary>
    public static bool IsEventAlive(
        float startedAtSeconds,
        float currentTimeSeconds,
        float lifetimeSeconds)
    {
        return CalculateFadeAlpha(currentTimeSeconds - startedAtSeconds, lifetimeSeconds) > 0f;
    }
}

/// <summary>
/// デバッグ用ウィンドウの計算と表示用の文字列の作成。Unity のオブジェクトに依存しないので、単体テストできる。
/// </summary>
public static class DebugOptionsOverlayMath
{
    /// <summary>
    /// 1 フレームの経過時間から、その瞬間の FPS を計算する。
    /// </summary>
    /// <param name="deltaTime">前のフレームからの経過時間（秒）。</param>
    /// <returns>deltaTime が正の場合は FPS。それ以外は 0。</returns>
    public static float CalculateInstantFps(float deltaTime)
    {
        if (deltaTime <= 0f)
        {
            return 0f;
        }

        return 1f / deltaTime;
    }

    /// <summary>
    /// ウィンドウの初期位置（画面の右側）と大きさを決める。
    /// </summary>
    /// <param name="screenWidth">画面の幅。</param>
    /// <param name="screenHeight">画面の高さ。</param>
    /// <param name="size">ウィンドウの初期サイズ。</param>
    /// <param name="margin">画面の端からの余白。</param>
    /// <returns>画面内に収まるウィンドウの位置と大きさ。</returns>
    public static Rect CalculateDefaultWindowRect(
        float screenWidth,
        float screenHeight,
        Vector2 size,
        float margin)
    {
        var rect = new Rect(
            screenWidth - size.x - margin,
            margin,
            size.x,
            size.y);

        return ClampWindowRect(rect, screenWidth, screenHeight, size);
    }

    /// <summary>
    /// ウィンドウが最小サイズより小さくならず、画面の外にはみ出さないように補正する。
    /// </summary>
    /// <param name="rect">現在のウィンドウの位置と大きさ。</param>
    /// <param name="screenWidth">画面の幅。</param>
    /// <param name="screenHeight">画面の高さ。</param>
    /// <param name="minimumSize">最小サイズ。</param>
    /// <returns>補正後のウィンドウの位置と大きさ。</returns>
    public static Rect ClampWindowRect(
        Rect rect,
        float screenWidth,
        float screenHeight,
        Vector2 minimumSize)
    {
        var safeScreenWidth = Mathf.Max(1f, screenWidth);
        var safeScreenHeight = Mathf.Max(1f, screenHeight);
        var width = Mathf.Clamp(rect.width, minimumSize.x, safeScreenWidth);
        var height = Mathf.Clamp(rect.height, minimumSize.y, safeScreenHeight);
        var maxX = Mathf.Max(0f, safeScreenWidth - width);
        var maxY = Mathf.Max(0f, safeScreenHeight - height);
        var x = Mathf.Clamp(rect.x, 0f, maxX);
        var y = Mathf.Clamp(rect.y, 0f, maxY);

        return new Rect(x, y, width, height);
    }

    /// <summary>
    /// プレイヤーのレベルと経験値の表示用の文字列を作る。
    /// </summary>
    /// <param name="experience">プレイヤーの経験値の状態。</param>
    /// <returns>レベルと経験値を表す文字列。</returns>
    public static string FormatLevelText(ExperienceComponent experience)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "Lv {0}  EXP {1} / {2}",
            Mathf.Max(1, experience.Level),
            Mathf.Max(0, experience.CurrentExperience),
            Mathf.Max(0, experience.RequiredExperience));
    }

    /// <summary>
    /// 攻撃スキルのスロットのアイコンに表示する文字を作る。
    /// </summary>
    /// <param name="isEquipped">装備中なら true。</param>
    /// <param name="config">装備中のスキルの設定。</param>
    /// <returns>未装備なら "-"、装備中ならスキルの ID。</returns>
    public static string FormatAttackSkillSlotIconText(bool isEquipped, AttackSkillConfig config)
    {
        if (!isEquipped)
        {
            return "-";
        }

        return FormatSkillSlotIconId(config.Id);
    }

    /// <summary>
    /// スキルレベルを増減し、通常の上限・下限の範囲に収める。
    /// </summary>
    public static int ApplySkillLevelDelta(int currentLevel, int delta)
    {
        return Mathf.Clamp(
            currentLevel + delta,
            PlayerCombatConstants.MinSkillLevel,
            PlayerCombatConstants.MaxSkillLevel);
    }

    /// <summary>
    /// 装備中のスキル全体のレベルの範囲を、表示用の文字列にする（すべて同じならその値、違えば「最小-最大」）。
    /// </summary>
    public static string FormatSkillLevelSummary(int minLevel, int maxLevel)
    {
        var safeMinLevel = ApplySkillLevelDelta(minLevel, 0);
        var safeMaxLevel = ApplySkillLevelDelta(maxLevel, 0);

        if (safeMinLevel == safeMaxLevel)
        {
            return string.Format(CultureInfo.InvariantCulture, "スキルLv {0}", safeMinLevel);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "スキルLv {0}-{1}",
            Mathf.Min(safeMinLevel, safeMaxLevel),
            Mathf.Max(safeMinLevel, safeMaxLevel));
    }

    /// <summary>
    /// バフスキルのスロットのアイコンに表示する文字を作る。
    /// </summary>
    public static string FormatBuffSkillSlotIconText(bool isEquipped, BuffSkillConfig config)
    {
        if (!isEquipped)
        {
            return "-";
        }

        return FormatSkillSlotIconId(config.Id);
    }

    private static string FormatSkillSlotIconId(int id)
    {
        if (id < 0)
        {
            return "?";
        }

        return id <= 99
            ? id.ToString(CultureInfo.InvariantCulture)
            : "99+";
    }
}
#endif
