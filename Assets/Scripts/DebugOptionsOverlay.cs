#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Editor と Development Build だけで生成される runtime debug option window。
/// </summary>
public sealed class DebugOptionsOverlay : MonoBehaviour
{
    private const string ObjectName = "Debug Options Overlay";
    private const int WindowId = 0x6401;
    private const float Margin = 16f;
    private const float TitleBarHeight = 22f;
    private const float ResizeHandleSize = 18f;
    private const float FpsRefreshSeconds = 0.25f;

    private static readonly Vector2 DefaultWindowSize = new Vector2(320f, 220f);
    private static readonly Vector2 MinimumWindowSize = new Vector2(260f, 160f);

    private Rect windowRect;
    private bool isWindowOpen;
    private bool isFpsVisible;
    private bool isResizing;
    private Vector2 resizeMouseStart;
    private Vector2 resizeSizeStart;
    private float fps;
    private float fpsElapsedSeconds;
    private int fpsFrameCount;

    /// <summary>
    /// Scene に手動配置しなくても debug overlay を 1 つだけ作る。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateOverlay()
    {
        if (Object.FindAnyObjectByType<DebugOptionsOverlay>() != null)
        {
            return;
        }

        var gameObject = new GameObject(ObjectName);

        Object.DontDestroyOnLoad(gameObject);
        gameObject.hideFlags = HideFlags.DontSave;
        gameObject.AddComponent<DebugOptionsOverlay>();
    }

    private void Awake()
    {
        var overlays = Object.FindObjectsByType<DebugOptionsOverlay>(FindObjectsInactive.Exclude);

        if (overlays.Length > 1)
        {
            Destroy(gameObject);
            return;
        }

        windowRect = DebugOptionsOverlayMath.CalculateDefaultWindowRect(
            Screen.width,
            Screen.height,
            DefaultWindowSize,
            Margin);
    }

    private void Update()
    {
        var keyboard = Keyboard.current;

        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        {
            isWindowOpen = !isWindowOpen;
        }

        UpdateFps(Time.unscaledDeltaTime);
    }

    private void OnGUI()
    {
        windowRect = DebugOptionsOverlayMath.ClampWindowRect(
            windowRect,
            Screen.width,
            Screen.height,
            MinimumWindowSize);

        HandleResizeInput(Event.current);

        if (isFpsVisible)
        {
            DrawFps();
        }

        if (!isWindowOpen)
        {
            return;
        }

        windowRect = GUI.Window(WindowId, windowRect, DrawWindow, "Debug Options");
        windowRect = DebugOptionsOverlayMath.ClampWindowRect(
            windowRect,
            Screen.width,
            Screen.height,
            MinimumWindowSize);
    }

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

    private void DrawFps()
    {
        var fpsText = fps.ToString("0.0", CultureInfo.InvariantCulture);

        GUI.Box(new Rect(12f, 12f, 104f, 28f), $"FPS {fpsText}");
    }

    private void DrawWindow(int windowId)
    {
        GUILayout.Space(4f);
        GUILayout.Label("Options");
        isFpsVisible = GUILayout.Toggle(isFpsVisible, "FPS 表示");

        if (isFpsVisible)
        {
            GUILayout.Label($"FPS: {fps.ToString("0.0", CultureInfo.InvariantCulture)}");
        }

        GUILayout.FlexibleSpace();
        DrawResizeHandle();
        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - ResizeHandleSize, TitleBarHeight));
    }

    private void DrawResizeHandle()
    {
        var handleRect = new Rect(
            windowRect.width - ResizeHandleSize,
            windowRect.height - ResizeHandleSize,
            ResizeHandleSize,
            ResizeHandleSize);

        GUI.Box(handleRect, string.Empty);
    }

    private void HandleResizeInput(Event currentEvent)
    {
        if (!isWindowOpen || currentEvent == null)
        {
            return;
        }

        var handleRect = new Rect(
            windowRect.xMax - ResizeHandleSize,
            windowRect.yMax - ResizeHandleSize,
            ResizeHandleSize,
            ResizeHandleSize);

        if (currentEvent.type == EventType.MouseDown &&
            currentEvent.button == 0 &&
            handleRect.Contains(currentEvent.mousePosition))
        {
            isResizing = true;
            resizeMouseStart = currentEvent.mousePosition;
            resizeSizeStart = new Vector2(windowRect.width, windowRect.height);
            currentEvent.Use();
            return;
        }

        if (!isResizing)
        {
            return;
        }

        if (currentEvent.type == EventType.MouseDrag)
        {
            var delta = currentEvent.mousePosition - resizeMouseStart;

            windowRect.width = resizeSizeStart.x + delta.x;
            windowRect.height = resizeSizeStart.y + delta.y;
            windowRect = DebugOptionsOverlayMath.ClampWindowRect(
                windowRect,
                Screen.width,
                Screen.height,
                MinimumWindowSize);
            currentEvent.Use();
            return;
        }

        if (currentEvent.type == EventType.MouseUp)
        {
            isResizing = false;
            currentEvent.Use();
        }
    }
}

/// <summary>
/// DebugOptionsOverlay の入力に依存しない計算。
/// </summary>
public static class DebugOptionsOverlayMath
{
    /// <summary>
    /// 1 frame の経過秒数から瞬間 FPS を計算する。
    /// </summary>
    /// <param name="deltaTime">前 frame からの経過秒数。</param>
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
    /// 画面右側に置く初期 window 矩形を作る。
    /// </summary>
    /// <param name="screenWidth">現在の Game View 幅。</param>
    /// <param name="screenHeight">現在の Game View 高さ。</param>
    /// <param name="size">初期 window サイズ。</param>
    /// <param name="margin">画面端からの余白。</param>
    /// <returns>画面内に収まる初期 window 矩形。</returns>
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
    /// window 矩形を最小サイズ以上かつ画面内へ制限する。
    /// </summary>
    /// <param name="rect">現在の window 矩形。</param>
    /// <param name="screenWidth">現在の Game View 幅。</param>
    /// <param name="screenHeight">現在の Game View 高さ。</param>
    /// <param name="minimumSize">縮小できる最小サイズ。</param>
    /// <returns>制限後の window 矩形。</returns>
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
}
#endif
