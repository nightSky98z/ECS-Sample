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
    private bool isPlayerColliderWireframeVisible;
    private bool isSkillTargetRangeVisible;
    private bool isSkillAttackRangeVisible;
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

    private void OnDisable()
    {
        CleanupDebugObjects();
    }

    private void OnDestroy()
    {
        CleanupDebugObjects();
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

    private static void CleanupDebugObjects()
    {
        DebugOptionsRuntimeDrawer.Dispose();
    }

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
        isPlayerColliderWireframeVisible = GUILayout.Toggle(
            isPlayerColliderWireframeVisible,
            "Player Collider Wireframe");
        isSkillTargetRangeVisible = GUILayout.Toggle(
            isSkillTargetRangeVisible,
            "Skill Target Range");
        isSkillAttackRangeVisible = GUILayout.Toggle(
            isSkillAttackRangeVisible,
            "Skill Attack Range");

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
/// DebugOptionsOverlay の runtime ECS wireframe 描画。
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

    public static bool HasAnyOptionEnabled(
        bool showPlayerColliderWireframe,
        bool showSkillTargetRange,
        bool showSkillAttackRange)
    {
        return showPlayerColliderWireframe ||
               showSkillTargetRange ||
               showSkillAttackRange;
    }

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

    private static void DrawSkillTargetRanges(EntityManager entityManager)
    {
        var attackQuery = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<AttackSkillComponent>(),
            ComponentType.ReadOnly<SkillSlotComponent>(),
            ComponentType.ReadOnly<EquippedSkillTag>(),
            ComponentType.ReadOnly<AttackSkillSlotTag>());
        var buffQuery = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<BuffSkillComponent>(),
            ComponentType.ReadOnly<SkillSlotComponent>(),
            ComponentType.ReadOnly<EquippedSkillTag>(),
            ComponentType.ReadOnly<BuffSkillSlotTag>());
        var attackSkills = attackQuery.ToComponentDataArray<AttackSkillComponent>(Allocator.Temp);
        var attackSlots = attackQuery.ToComponentDataArray<SkillSlotComponent>(Allocator.Temp);
        var buffSkills = buffQuery.ToComponentDataArray<BuffSkillComponent>(Allocator.Temp);
        var buffSlots = buffQuery.ToComponentDataArray<SkillSlotComponent>(Allocator.Temp);
        var renderedRangeCount = 0;

        for (var skillIndex = 0; skillIndex < attackSkills.Length; skillIndex++)
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
            var targetRange = SkillMath.CalculateEffectiveTargetRange(attackSkills[skillIndex], buffs);

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
        attackSkills.Dispose();
        buffQuery.Dispose();
        attackQuery.Dispose();
    }

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
/// DebugOptions が描画する attack range の一時表示イベント。
/// </summary>
public struct SkillAttackRangeDebugEvent
{
    public float3 Center;
    public float Radius;
    public float StartedAtSeconds;
}

/// <summary>
/// SkillLogicSystem から DebugOptions へ attack range 発生位置を渡す debug 専用バッファ。
/// </summary>
public static class SkillAttackRangeDebugEvents
{
    private const float RangeLineHeightOffset = 0.12f;
    private const int MaxEventCount = 128;

    private static readonly List<SkillAttackRangeDebugEvent> Events = new List<SkillAttackRangeDebugEvent>(32);
    private static bool isRecordingEnabled;

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

    public static void Clear()
    {
        Events.Clear();
        DebugOptionsRangeRenderer.FinishAttackFrame(0);
    }

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
/// DebugOptions の range 円を Game View に直接出す LineRenderer pool。
/// </summary>
public static class DebugOptionsRangeRenderer
{
    private const int CircleSegments = 96;
    private const float LineWidth = 0.16f;

    private static readonly List<LineRenderer> TargetRangeLines = new List<LineRenderer>(8);
    private static readonly List<LineRenderer> AttackRangeLines = new List<LineRenderer>(16);

    private static Transform root;
    private static UnityEngine.Material lineMaterial;

    public static void DrawTargetCircle(
        int rendererIndex,
        float3 center,
        float radius,
        Color color)
    {
        DrawCircle(TargetRangeLines, "Debug Target Range", rendererIndex, center, radius, color);
    }

    public static void DrawAttackCircle(
        int rendererIndex,
        float3 center,
        float radius,
        Color color)
    {
        DrawCircle(AttackRangeLines, "Debug Attack Range", rendererIndex, center, radius, color);
    }

    public static void FinishTargetFrame(int usedCount)
    {
        DisableUnused(TargetRangeLines, usedCount);
    }

    public static void FinishAttackFrame(int usedCount)
    {
        DisableUnused(AttackRangeLines, usedCount);
    }

    public static void HideSkillRanges()
    {
        DisableUnused(TargetRangeLines, 0);
        DisableUnused(AttackRangeLines, 0);
    }

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
/// DebugOptions range renderer の入力に依存しない計算。
/// </summary>
public static class DebugOptionsRangeRendererMath
{
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
/// Skill range debug 表示の入力に依存しない計算。
/// </summary>
public static class SkillAttackRangeDebugMath
{
    public static float CalculateFadeAlpha(float elapsedSeconds, float lifetimeSeconds)
    {
        if (lifetimeSeconds <= 0f)
        {
            return 0f;
        }

        return math.saturate(1f - math.max(0f, elapsedSeconds) / lifetimeSeconds);
    }

    public static bool IsEventAlive(
        float startedAtSeconds,
        float currentTimeSeconds,
        float lifetimeSeconds)
    {
        return CalculateFadeAlpha(currentTimeSeconds - startedAtSeconds, lifetimeSeconds) > 0f;
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
