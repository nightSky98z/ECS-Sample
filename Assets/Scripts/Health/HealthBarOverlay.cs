using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// ECS の HP と座標を読み、GameObject UI として頭上 HP bar を表示する。
/// </summary>
[DefaultExecutionOrder(200)]
public sealed class HealthBarOverlay : MonoBehaviour
{
    private const string OverlayObjectName = "Health Bar Overlay";
    private const string CanvasObjectName = "Health Bar Overlay Canvas";
    private const string HealthBarResourceName = "HP_Bar";
    private const string FrontImageName = "front-image";

    private static readonly Vector2 DefaultBarSize = new Vector2(100f, 12f);
    private static readonly Color BackgroundColor = new Color(0.35f, 0.35f, 0.35f, 1f);
    private static readonly Color FrontColor = new Color(0.92f, 0.08f, 0.08f, 1f);

    [Tooltip("HP バーを投影するカメラ。未指定なら MainCamera を使う。")]
    [SerializeField]
    private Camera TargetCamera;

    [Tooltip("HP バーを配置する Canvas。未指定なら ScreenSpaceOverlay の Canvas を自動生成する。")]
    [SerializeField]
    private Canvas TargetCanvas;

    [Tooltip("頭上 HP バーとして複製する UI プレハブ。未指定なら Resources/HP_Bar を探す。")]
    [SerializeField]
    private GameObject HealthBarPrefab;

    [Tooltip("0 以下なら距離制限なし。")]
    [SerializeField]
    private float MaxVisibleDistance = 80f;

    [Tooltip("HP バー表示を有効にする。Debug や負荷確認時に無効化できる。")]
    [SerializeField]
    private bool ShowHealthBars = true;

    [Tooltip("起動時に事前生成する HP バー数。多いほど初回生成負荷を減らせる。")]
    [SerializeField]
    private int InitialPoolCapacity = 64;

    private readonly Dictionary<Entity, HealthBarView> activeBars = new Dictionary<Entity, HealthBarView>();
    private readonly HashSet<Entity> updatedEntities = new HashSet<Entity>();
    private readonly List<Entity> releaseEntities = new List<Entity>();
    private readonly Stack<HealthBarView> barPool = new Stack<HealthBarView>();

    private World queryWorld;
    private EntityManager entityManager;
    private EntityQuery healthQuery;
    private bool hasQueries;
    private bool ownsCanvas;
    private bool searchedResourcePrefab;

    /// <summary>
    /// Scene に手動配置しなくても HP bar overlay を 1 つだけ作る。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CreateOverlay()
    {
        if (Object.FindAnyObjectByType<HealthBarOverlay>() != null)
        {
            return;
        }

        var gameObject = new GameObject(OverlayObjectName);

        Object.DontDestroyOnLoad(gameObject);
        gameObject.AddComponent<HealthBarOverlay>();
    }

    private void OnEnable()
    {
        EnsureCanvas();
        PrewarmPool();
    }

    private void OnDestroy()
    {
        ReleaseAllBars();
        DestroyPooledBars();

        if (ownsCanvas && TargetCanvas != null)
        {
            Destroy(TargetCanvas.gameObject);
        }
    }

    private void LateUpdate()
    {
        if (!TryGetCamera(out var targetCamera))
        {
            ReleaseAllBars();
            return;
        }

        if (!EnsureCanvas())
        {
            ReleaseAllBars();
            return;
        }

        if (!TryCreateQueries())
        {
            ReleaseAllBars();
            return;
        }

        updatedEntities.Clear();

        if (ShowHealthBars)
        {
            UpdateHealthBars(healthQuery, targetCamera);
        }
        else
        {
            ReleaseAllBars();
        }

        ReleaseBarsNotUpdatedThisFrame();
    }

    private bool TryCreateQueries()
    {
        var world = World.DefaultGameObjectInjectionWorld;

        if (world == null || !world.IsCreated)
        {
            hasQueries = false;
            return false;
        }

        if (hasQueries && queryWorld == world)
        {
            return true;
        }

        ReleaseAllBars();

        queryWorld = world;
        entityManager = world.EntityManager;
        healthQuery = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<HealthComponent>(),
            ComponentType.ReadOnly<HealthBarAnchor>(),
            ComponentType.ReadOnly<LocalToWorld>(),
            ComponentType.Exclude<Prefab>());
        hasQueries = true;

        return true;
    }

    private void UpdateHealthBars(EntityQuery query, Camera targetCamera)
    {
        if (query.IsEmpty)
        {
            return;
        }

        using var entities = query.ToEntityArray(Allocator.Temp);
        using var healthValues = query.ToComponentDataArray<HealthComponent>(Allocator.Temp);
        using var anchors = query.ToComponentDataArray<HealthBarAnchor>(Allocator.Temp);
        using var transforms = query.ToComponentDataArray<LocalToWorld>(Allocator.Temp);

        for (var entityIndex = 0; entityIndex < entities.Length; entityIndex++)
        {
            var entity = entities[entityIndex];
            var health = healthValues[entityIndex];

            if (HealthMath.IsDead(health))
            {
                ReleaseBar(entity);
                continue;
            }

            var anchorPosition = CalculateAnchorPosition(
                transforms[entityIndex],
                anchors[entityIndex],
                out var barSize);

            if (!IsInsideVisibleDistance(anchorPosition, targetCamera.transform.position))
            {
                ReleaseBar(entity);
                continue;
            }

            var screenPoint = targetCamera.WorldToScreenPoint(anchorPosition);
            var screenSize = new Vector2(Screen.width, Screen.height);

            if (!HealthBarOverlayMath.IsScreenPointVisible(screenPoint, screenSize))
            {
                ReleaseBar(entity);
                continue;
            }

            var view = GetOrCreateBar(entity);

            if (!TryCalculateCanvasPosition(screenPoint, targetCamera, out var canvasPosition))
            {
                ReleaseBar(entity);
                continue;
            }

            updatedEntities.Add(entity);
            view.RectTransform.sizeDelta = barSize;
            view.RectTransform.anchoredPosition = canvasPosition;
            view.FrontImage.fillAmount = HealthBarOverlayMath.CalculateFillAmount(health);
        }
    }

    private Vector3 CalculateAnchorPosition(
        LocalToWorld localToWorld,
        HealthBarAnchor anchor,
        out Vector2 barSize)
    {
        barSize = new Vector2(
            Mathf.Max(1f, anchor.Size.x),
            Mathf.Max(1f, anchor.Size.y));

        return (Vector3)math.transform(localToWorld.Value, anchor.LocalOffset);
    }

    private bool TryGetCamera(out Camera targetCamera)
    {
        if (TargetCamera == null)
        {
            TargetCamera = Camera.main;
        }

        targetCamera = TargetCamera;
        return targetCamera != null;
    }

    private bool EnsureCanvas()
    {
        if (TargetCanvas != null)
        {
            return true;
        }

        var canvasObject = new GameObject(CanvasObjectName, typeof(RectTransform));
        var canvas = canvasObject.AddComponent<Canvas>();

        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        Object.DontDestroyOnLoad(canvasObject);

        TargetCanvas = canvas;
        ownsCanvas = true;

        return true;
    }

    private void PrewarmPool()
    {
        if (TargetCanvas == null || InitialPoolCapacity <= 0)
        {
            return;
        }

        for (var index = 0; index < InitialPoolCapacity; index++)
        {
            var view = CreateBarView();

            TrySetBarActive(view, false);
            barPool.Push(view);
        }
    }

    private HealthBarView GetOrCreateBar(Entity entity)
    {
        if (activeBars.TryGetValue(entity, out var activeView))
        {
            if (TrySetBarActive(activeView, true))
            {
                return activeView;
            }

            activeBars.Remove(entity);
        }

        var view = GetPooledOrCreateBarView();

        TrySetBarActive(view, true);
        activeBars.Add(entity, view);

        return view;
    }

    private HealthBarView GetPooledOrCreateBarView()
    {
        while (barPool.Count > 0)
        {
            var view = barPool.Pop();

            if (IsBarViewAlive(view))
            {
                return view;
            }
        }

        return CreateBarView();
    }

    private HealthBarView CreateBarView()
    {
        var prefab = GetHealthBarPrefab();
        var gameObject = prefab != null
            ? Instantiate(prefab, TargetCanvas.transform)
            : CreateDefaultHealthBar(TargetCanvas.transform);

        gameObject.name = "HP_Bar";
        gameObject.transform.SetParent(TargetCanvas.transform, false);

        var rectTransform = gameObject.GetComponent<RectTransform>();

        if (rectTransform == null)
        {
            Destroy(gameObject);
            gameObject = CreateDefaultHealthBar(TargetCanvas.transform);
            rectTransform = gameObject.GetComponent<RectTransform>();
        }

        var frontImage = FindFrontImage(gameObject);

        if (frontImage == null)
        {
            Destroy(gameObject);
            gameObject = CreateDefaultHealthBar(TargetCanvas.transform);
            rectTransform = gameObject.GetComponent<RectTransform>();
            frontImage = FindFrontImage(gameObject);
        }

        ConfigureFrontImage(frontImage);

        return new HealthBarView(rectTransform, frontImage);
    }

    private GameObject GetHealthBarPrefab()
    {
        if (HealthBarPrefab != null)
        {
            return HealthBarPrefab;
        }

        if (searchedResourcePrefab)
        {
            return null;
        }

        searchedResourcePrefab = true;
        HealthBarPrefab = Resources.Load<GameObject>(HealthBarResourceName);

        return HealthBarPrefab;
    }

    private bool TryCalculateCanvasPosition(
        Vector3 screenPoint,
        Camera targetCamera,
        out Vector2 canvasPosition)
    {
        var canvasTransform = TargetCanvas.transform as RectTransform;

        if (canvasTransform == null)
        {
            canvasPosition = Vector2.zero;
            return false;
        }

        var eventCamera = TargetCanvas.renderMode == RenderMode.ScreenSpaceOverlay
            ? null
            : TargetCanvas.worldCamera != null ? TargetCanvas.worldCamera : targetCamera;

        return RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvasTransform,
            screenPoint,
            eventCamera,
            out canvasPosition);
    }

    private bool IsInsideVisibleDistance(Vector3 anchorPosition, Vector3 cameraPosition)
    {
        if (MaxVisibleDistance <= 0f)
        {
            return true;
        }

        var maxDistanceSquared = MaxVisibleDistance * MaxVisibleDistance;

        return (anchorPosition - cameraPosition).sqrMagnitude <= maxDistanceSquared;
    }

    private void ReleaseBarsNotUpdatedThisFrame()
    {
        releaseEntities.Clear();

        foreach (var pair in activeBars)
        {
            if (!updatedEntities.Contains(pair.Key))
            {
                releaseEntities.Add(pair.Key);
            }
        }

        for (var index = 0; index < releaseEntities.Count; index++)
        {
            ReleaseBar(releaseEntities[index]);
        }
    }

    private void ReleaseBar(Entity entity)
    {
        if (!activeBars.TryGetValue(entity, out var view))
        {
            return;
        }

        activeBars.Remove(entity);

        if (TrySetBarActive(view, false))
        {
            barPool.Push(view);
        }
    }

    private void ReleaseAllBars()
    {
        releaseEntities.Clear();

        foreach (var pair in activeBars)
        {
            releaseEntities.Add(pair.Key);
        }

        for (var index = 0; index < releaseEntities.Count; index++)
        {
            ReleaseBar(releaseEntities[index]);
        }
    }

    private void DestroyPooledBars()
    {
        while (barPool.Count > 0)
        {
            var view = barPool.Pop();

            if (IsBarViewAlive(view))
            {
                Destroy(view.RectTransform.gameObject);
            }
        }
    }

    private static GameObject CreateDefaultHealthBar(Transform parent)
    {
        var root = new GameObject("HP_Bar", typeof(RectTransform));

        root.transform.SetParent(parent, false);

        var rootRect = root.GetComponent<RectTransform>();

        rootRect.anchorMin = new Vector2(0.5f, 0.5f);
        rootRect.anchorMax = new Vector2(0.5f, 0.5f);
        rootRect.pivot = new Vector2(0.5f, 0.5f);
        rootRect.sizeDelta = DefaultBarSize;

        var background = CreateImage("background-image", root.transform, BackgroundColor);
        var front = CreateImage(FrontImageName, background.transform, FrontColor);

        ConfigureFrontImage(front.GetComponent<Image>());

        return root;
    }

    private static GameObject CreateImage(string name, Transform parent, Color color)
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

        return gameObject;
    }

    private static Image FindFrontImage(GameObject root)
    {
        var frontTransform = root.transform.Find($"background-image/{FrontImageName}") ??
                             root.transform.Find(FrontImageName);

        if (frontTransform != null && frontTransform.TryGetComponent<Image>(out var namedImage))
        {
            return namedImage;
        }

        var images = root.GetComponentsInChildren<Image>(true);

        for (var index = 0; index < images.Length; index++)
        {
            if (images[index].type == Image.Type.Filled)
            {
                return images[index];
            }
        }

        return null;
    }

    private static void ConfigureFrontImage(Image image)
    {
        image.type = Image.Type.Filled;
        image.fillMethod = Image.FillMethod.Horizontal;
        image.fillOrigin = 0;
        image.raycastTarget = false;
    }

    private static bool TrySetBarActive(HealthBarView view, bool active)
    {
        if (!IsBarViewAlive(view))
        {
            return false;
        }

        view.RectTransform.gameObject.SetActive(active);
        return true;
    }

    private static bool IsBarViewAlive(HealthBarView view)
    {
        return view != null &&
               view.RectTransform != null &&
               view.FrontImage != null;
    }

    private sealed class HealthBarView
    {
        public readonly RectTransform RectTransform;
        public readonly Image FrontImage;

        public HealthBarView(RectTransform rectTransform, Image frontImage)
        {
            RectTransform = rectTransform;
            FrontImage = frontImage;
        }
    }
}

/// <summary>
/// HealthBarOverlay の Unity object に依存しない計算。
/// </summary>
public static class HealthBarOverlayMath
{
    /// <summary>
    /// HP の比率を UI Image.fillAmount 用に 0..1 へ正規化する。
    /// </summary>
    /// <param name="health">表示対象の HP。借用のみで変更しない。</param>
    /// <returns>MaxHp が正の場合は HP 比率。それ以外は 0。</returns>
    public static float CalculateFillAmount(HealthComponent health)
    {
        if (health.MaxHp <= 0)
        {
            return 0f;
        }

        return Mathf.Clamp01((float)health.CurrentHp / health.MaxHp);
    }

    /// <summary>
    /// Camera.WorldToScreenPoint の結果が画面内かを判定する。
    /// </summary>
    /// <param name="screenPoint">Camera.WorldToScreenPoint の戻り値。</param>
    /// <param name="screenSize">現在の screen pixel size。</param>
    /// <returns>camera 前方かつ screen 矩形内なら true。</returns>
    public static bool IsScreenPointVisible(Vector3 screenPoint, Vector2 screenSize)
    {
        return screenPoint.z > 0f &&
               screenPoint.x >= 0f &&
               screenPoint.x <= screenSize.x &&
               screenPoint.y >= 0f &&
               screenPoint.y <= screenSize.y;
    }
}
