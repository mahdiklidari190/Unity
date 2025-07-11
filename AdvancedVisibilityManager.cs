using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditorInternal;
#endif

[DisallowMultipleComponent]
[HelpURL("https://docs.unity3d.com/ScriptReference/CullingGroup.html")]
public sealed class AdvancedVisibilityManager : MonoBehaviour
{
    #region Singleton

    private static AdvancedVisibilityManager _instance;
    private static readonly object _lock = new object();

    public static AdvancedVisibilityManager Instance
    {
        get
        {
            lock (_lock)
            {
                if (_instance == null)
                {
                    _instance = FindObjectOfType<AdvancedVisibilityManager>();
                    if (_instance == null)
                    {
                        GameObject singleton = new GameObject(nameof(AdvancedVisibilityManager));
                        _instance = singleton.AddComponent<AdvancedVisibilityManager>();
                        Debug.Log("[VisibilityManager] Created new instance");
                    }
                }
                return _instance;
            }
        }
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Debug.Log("[VisibilityManager] Duplicate instance destroyed");
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);
        
        activeJobHandles = new NativeList<JobHandle>(Allocator.Persistent);
        objectsToRemove = new NativeList<int>(Allocator.Persistent);
        dynamicObjectIndices = new NativeList<int>(Allocator.Persistent);
        
        if (debugLevel >= LogLevel.All)
            Debug.Log("[VisibilityManager] System initialized");
    }

    #endregion

    #region Settings

    [Header("Object Selection")]
    [Tooltip("Objects with this tag will be managed")]
    public string targetTag = "BK";

    [Tooltip("Optional: Provide custom objects instead of using tag")]
    public List<GameObject> customObjects;

    [Tooltip("Filter objects by layer")]
    public LayerMask targetLayer = -1;

    [Header("Camera Settings")]
    [Tooltip("Main camera for visibility checks")]
    public Camera targetCamera;

    [Tooltip("Additional cameras for visibility checks")]
    public List<Camera> additionalCameras;

    [Tooltip("Automatically find main camera if none is assigned")]
    public bool autoFindCamera = true;

    [Header("Optimization Settings")]
    [Tooltip("Optimization for static objects")]
    public bool areObjectsStatic = false;

    [Tooltip("Minimum change required to update bounds")]
    public float changeThreshold = 0.01f;

    [Tooltip("Number of objects to check for cleanup per frame")]
    public int cleanupBatchSize = 10;

    [Tooltip("Frames between cleanup checks")]
    public int cleanupInterval = 3;

    [Header("Distance Culling")]
    [Tooltip("Distance bands in meters (sorted)")]
    public float[] distanceBands = new float[] { 50f, 100f };

    [Header("Occlusion Culling")]
    [Tooltip("Enable occlusion culling")]
    public bool useOcclusionCulling = true;

    [Tooltip("Occlusion update interval")]
    public int occlusionUpdateInterval = 2;

    [Header("Frustum Culling")]
    [Tooltip("Enable custom frustum culling")]
    public bool useFrustumCulling = true;

    [Header("Debug")]
    [Tooltip("Draw bounding spheres in Scene view")]
    public bool drawGizmos = true;

    [Tooltip("Maximum number of gizmos to draw")]
    public int maxGizmos = 100;

    [Tooltip("Debug logging level")]
    public LogLevel debugLevel = LogLevel.Warnings;

    [Header("Statistics")]
    [ReadOnly, SerializeField] 
    private int visibleObjectCount;
    
    [ReadOnly, SerializeField]
    private int totalManagedObjects;

    #endregion

    #region Private Variables

    private CullingGroup cullingGroup;
    private NativeArray<ObjectData> objectData;
    private NativeArray<BoundingSphere> boundingSpheres;
    private NativeList<int> dynamicObjectIndices;
    private NativeList<JobHandle> activeJobHandles;
    private NativeList<int> objectsToRemove;
    
    private Dictionary<GameObject, int> objectIndexMap = new Dictionary<GameObject, int>(1024);
    private List<Camera> activeCameras = new List<Camera>();
    
    private int cleanupIndex = 0;
    private int frameCounter = 0;
    private int occlusionFrameCounter = 0;
    private bool requiresBoundsUpdate = false;
    private bool isInitialized = false;

    #endregion

    #region Structs

    [BurstCompile]
    private struct ObjectData
    {
        public Renderer renderer;
        public LODGroup lodGroup;
        public Transform transform;
        public Vector3 lastPosition;
        public Quaternion lastRotation;
        public Vector3 lastScale;
        public bool isVisible;
        public bool isMarkedForRemoval;
        public float currentDistance;
    }

    #endregion

    #region Enums

    public enum LogLevel
    {
        None,
        Errors,
        Warnings,
        All
    }

    #endregion

    #region Events

    public delegate void VisibilityChangedHandler(GameObject obj, bool isVisible, float distance);
    public event VisibilityChangedHandler OnVisibilityChanged;

    public delegate void LODChangedHandler(GameObject obj, int lodLevel);
    public event LODChangedHandler OnLODChanged;

    #endregion

    #region Unity Lifecycle

    private void OnEnable()
    {
        InitializeSystem();
        Camera.onPreCull += OnCameraPreCull;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        StartCoroutine(UpdateDynamicObjectsCoroutine());
    }

    private void Update()
    {
        if (!isInitialized) return;

        CompleteJobs();
        HandleOcclusionUpdates();
        
        if (requiresBoundsUpdate)
        {
            UpdateBoundingSpheres();
            requiresBoundsUpdate = false;
        }

        if (frameCounter++ % cleanupInterval == 0)
        {
            CleanUpDestroyedObjects();
        }
    }

    private void LateUpdate()
    {
        CompleteJobs();
    }

    private void OnDisable()
    {
        Camera.onPreCull -= OnCameraPreCull;
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        CompleteJobs();
    }

    private void OnDestroy()
    {
        DisposeSystem();
        
        if (debugLevel >= LogLevel.All)
            Debug.Log("[VisibilityManager] System destroyed");
    }

    #endregion

    #region Public API

    [ContextMenu("Refresh Objects")]
    public void RefreshObjects()
    {
        CompleteJobs();
        InitializeSystem();
    }

    public void RegisterObject(GameObject target)
    {
        if (target == null)
        {
            LogWarning("Attempted to register null object");
            return;
        }

        if (((1 << target.layer) & targetLayer) == 0)
        {
            LogWarning($"Object {target.name} is not in target layer");
            return;
        }

        var rend = target.GetComponent<Renderer>();
        var lod = target.GetComponent<LODGroup>();
        
        if (rend == null && lod == null)
        {
            LogWarning($"Object {target.name} has no Renderer or LODGroup");
            return;
        }

        if (objectIndexMap.ContainsKey(target))
        {
            LogWarning($"Object {target.name} is already registered");
            return;
        }

        CompleteJobs();

        int newIndex = objectData.Length;
        ResizeNativeArrays(newIndex + 1);

        var data = new ObjectData
        {
            renderer = rend,
            lodGroup = lod,
            transform = target.transform,
            lastPosition = target.transform.position,
            lastRotation = target.transform.rotation,
            lastScale = target.transform.localScale,
            isVisible = false,
            isMarkedForRemoval = false,
            currentDistance = float.MaxValue
        };

        objectData[newIndex] = data;
        objectIndexMap[target] = newIndex;

        if (!areObjectsStatic && !target.isStatic)
        {
            dynamicObjectIndices.Add(newIndex);
        }

        UpdateBoundingSphere(newIndex);
        requiresBoundsUpdate = true;
        totalManagedObjects = objectData.Length;
        
        Log($"Registered object: {target.name}");
    }

    public void UnregisterObject(GameObject target)
    {
        if (target == null || !objectIndexMap.TryGetValue(target, out int index))
        {
            LogWarning("Attempted to unregister null or unregistered object");
            return;
        }

        CompleteJobs();

        var data = objectData[index];
        data.isMarkedForRemoval = true;
        objectData[index] = data;

        objectsToRemove.Add(index);
        objectIndexMap.Remove(target);
        requiresBoundsUpdate = true;
        
        Log($"Unregistered object: {target.name}");
    }

    public bool IsObjectVisible(GameObject target)
    {
        if (target == null || !objectIndexMap.TryGetValue(target, out int index))
            return false;

        return objectData[index].isVisible;
    }

    #endregion

    #region Core System

    private void InitializeSystem()
    {
        if (isInitialized)
        {
            DisposeSystem();
        }

        if (!ValidateCameras())
        {
            LogError("Camera validation failed");
            return;
        }

        InitializeNativeCollections();
        CacheObjects();
        SetupCullingGroup();

        isInitialized = true;
        totalManagedObjects = objectData.Length;
        
        Log("System initialized successfully");
    }

    private void DisposeSystem()
    {
        CompleteJobs();
        DisposeGroup();
        
        if (objectData.IsCreated) objectData.Dispose();
        if (boundingSpheres.IsCreated) boundingSpheres.Dispose();
        if (dynamicObjectIndices.IsCreated) dynamicObjectIndices.Dispose();
        if (activeJobHandles.IsCreated) activeJobHandles.Dispose();
        if (objectsToRemove.IsCreated) objectsToRemove.Dispose();
        
        objectIndexMap.Clear();
        isInitialized = false;
    }

    private bool ValidateCameras()
    {
        activeCameras.Clear();

        if (targetCamera == null && autoFindCamera)
        {
            targetCamera = Camera.main;
        }

        if (targetCamera != null)
        {
            activeCameras.Add(targetCamera);
        }

        if (additionalCameras != null)
        {
            activeCameras.AddRange(additionalCameras.Where(cam => cam != null));
        }

        if (activeCameras.Count == 0)
        {
            LogError("No valid cameras found");
            return false;
        }

        return true;
    }

    private void InitializeNativeCollections()
    {
        objectData = new NativeArray<ObjectData>(0, Allocator.Persistent);
        boundingSpheres = new NativeArray<BoundingSphere>(0, Allocator.Persistent);
        dynamicObjectIndices = new NativeList<int>(Allocator.Persistent);
        activeJobHandles = new NativeList<JobHandle>(Allocator.Persistent);
        objectsToRemove = new NativeList<int>(Allocator.Persistent);
    }

    private void CacheObjects()
    {
        GameObject[] targets = GetTargetObjects();

        if (targets.Length == 0)
        {
            LogWarning("No valid objects found");
            return;
        }

        ResizeNativeArrays(targets.Length);

        for (int i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            if (target == null) continue;

            var rend = target.GetComponent<Renderer>();
            var lod = target.GetComponent<LODGroup>();

            if (rend != null || lod != null)
            {
                objectData[i] = new ObjectData
                {
                    renderer = rend,
                    lodGroup = lod,
                    transform = target.transform,
                    lastPosition = target.transform.position,
                    lastRotation = target.transform.rotation,
                    lastScale = target.transform.localScale,
                    isVisible = false,
                    isMarkedForRemoval = false,
                    currentDistance = float.MaxValue
                };

                objectIndexMap[target] = i;

                if (!areObjectsStatic && !target.isStatic)
                {
                    dynamicObjectIndices.Add(i);
                }
            }
        }
    }

    private void ResizeNativeArrays(int newSize)
    {
        if (objectData.IsCreated && objectData.Length == newSize)
            return;

        var newObjectData = new NativeArray<ObjectData>(newSize, Allocator.Persistent);
        var newBoundingSpheres = new NativeArray<BoundingSphere>(newSize, Allocator.Persistent);

        if (objectData.IsCreated && objectData.Length > 0)
        {
            NativeArray<ObjectData>.Copy(objectData, newObjectData, Mathf.Min(objectData.Length, newSize));
            objectData.Dispose();
        }

        if (boundingSpheres.IsCreated && boundingSpheres.Length > 0)
        {
            NativeArray<BoundingSphere>.Copy(boundingSpheres, newBoundingSpheres, Mathf.Min(boundingSpheres.Length, newSize));
            boundingSpheres.Dispose();
        }

        objectData = newObjectData;
        boundingSpheres = newBoundingSpheres;
    }

    private GameObject[] GetTargetObjects()
    {
        if (customObjects != null && customObjects.Count > 0)
        {
            return customObjects
                .Where(obj => obj != null && ((1 << obj.layer) & targetLayer) != 0)
                .Distinct()
                .ToArray();
        }

        var allObjects = GameObject.FindGameObjectsWithTag(targetTag);
        return allObjects
            .Where(obj => ((1 << obj.layer) & targetLayer) != 0)
            .ToArray();
    }

    private void SetupCullingGroup()
    {
        DisposeGroup();

        if (!objectData.IsCreated || objectData.Length == 0)
        {
            LogWarning("No objects to cull");
            return;
        }

        cullingGroup = new CullingGroup
        {
            targetCamera = targetCamera,
            onStateChanged = OnStateChanged,
            enableOcclusionCulling = useOcclusionCulling
        };

        UpdateCullingGroupReferences();

        if (distanceBands != null && distanceBands.Length > 0)
        {
            cullingGroup.SetDistanceBands(distanceBands);
        }

        Log($"CullingGroup initialized with {objectData.Length} objects");
    }

    private void UpdateCullingGroupReferences()
    {
        if (cullingGroup == null || !objectData.IsCreated)
            return;

        cullingGroup.SetBoundingSpheres(boundingSpheres);
        cullingGroup.SetBoundingSphereCount(boundingSpheres.Length);
    }

    private void DisposeGroup()
    {
        if (cullingGroup != null)
        {
            cullingGroup.onStateChanged -= OnStateChanged;
            cullingGroup.Dispose();
            cullingGroup = null;
        }
    }

    #endregion

    #region Visibility Management

    private void OnStateChanged(CullingGroupEvent evt)
    {
        if (evt.index < 0 || evt.index >= objectData.Length)
            return;

        var data = objectData[evt.index];
        if (data.isMarkedForRemoval || data.transform == null)
            return;

        bool visible = evt.isVisible;
        float distance = evt.currentDistance;

        if (distanceBands != null && distanceBands.Length > 0)
        {
            visible &= distance < distanceBands.Length;
        }

        if (data.isVisible != visible || Mathf.Abs(data.currentDistance - distance) > 0.1f)
        {
            data.isVisible = visible;
            data.currentDistance = distance;
            objectData[evt.index] = data;

            visibleObjectCount = Mathf.Max(0, visible ? visibleObjectCount + 1 : visibleObjectCount - 1);

            var targetGameObject = data.transform.gameObject;
            OnVisibilityChanged?.Invoke(targetGameObject, visible, distance);

            if (data.lodGroup != null)
            {
                HandleLODVisibility(evt.index, visible, distance);
            }
            else if (data.renderer != null)
            {
                data.renderer.enabled = visible;
            }
        }
    }

    private void HandleLODVisibility(int index, bool isGroupVisible, float distance)
    {
        var data = objectData[index];
        if (data.lodGroup == null) return;

        data.lodGroup.enabled = isGroupVisible;

        if (!isGroupVisible) return;

        var lods = data.lodGroup.GetLODs();
        int lodLevel = CalculateLODLevel(distance, lods.Length);

        for (int i = 0; i < lods.Length; i++)
        {
            bool lodVisible = i == lodLevel;
            foreach (var renderer in lods[i].renderers)
            {
                if (renderer != null)
                {
                    renderer.enabled = lodVisible;
                }
            }
        }

        OnLODChanged?.Invoke(data.transform.gameObject, lodLevel);
    }

    private int CalculateLODLevel(float distance, int lodCount)
    {
        if (distanceBands == null || distanceBands.Length == 0)
            return 0;

        for (int i = 0; i < distanceBands.Length; i++)
        {
            if (distance <= distanceBands[i])
                return Mathf.Min(i, lodCount - 1);
        }

        return lodCount - 1;
    }

    private void HandleOcclusionUpdates()
    {
        if (!useOcclusionCulling || cullingGroup == null)
            return;

        if (occlusionFrameCounter++ % occlusionUpdateInterval == 0)
        {
            cullingGroup.Update();
        }
    }

    #endregion

    #region Dynamic Objects & Bounds

    private System.Collections.IEnumerator UpdateDynamicObjectsCoroutine()
    {
        while (true)
        {
            if (!areObjectsStatic && boundingSpheres.IsCreated && dynamicObjectIndices.Length > 0)
            {
                var job = new UpdateDynamicObjectsJob
                {
                    Objects = objectData,
                    DynamicIndices = dynamicObjectIndices.AsArray(),
                    BoundingSpheres = boundingSpheres,
                    ChangeThreshold = changeThreshold
                };

                JobHandle handle = job.Schedule(dynamicObjectIndices.Length, 64);
                activeJobHandles.Add(handle);
                
                yield return new WaitUntil(() => handle.IsCompleted);
            }
            yield return new WaitForSeconds(0.1f);
        }
    }

    private void UpdateBoundingSpheres()
    {
        if (!objectData.IsCreated || objectData.Length == 0)
        {
            if (boundingSpheres.IsCreated)
                boundingSpheres.Dispose();
            return;
        }

        if (boundingSpheres.IsCreated && boundingSpheres.Length != objectData.Length)
        {
            boundingSpheres.Dispose();
            boundingSpheres = new NativeArray<BoundingSphere>(objectData.Length, Allocator.Persistent);
        }

        if (!boundingSpheres.IsCreated)
        {
            boundingSpheres = new NativeArray<BoundingSphere>(objectData.Length, Allocator.Persistent);
        }

        var job = new UpdateBoundingSpheresJob
        {
            Objects = objectData,
            BoundingSpheres = boundingSpheres
        };

        JobHandle handle = job.Schedule(objectData.Length, 64);
        activeJobHandles.Add(handle);
    }

    private void UpdateBoundingSphere(int index)
    {
        if (!objectData.IsCreated || index < 0 || index >= objectData.Length)
            return;

        var data = objectData[index];
        BoundingSphere sphere;

        if (data.renderer != null)
        {
            var bounds = data.renderer.bounds;
            sphere = new BoundingSphere(bounds.center, bounds.extents.magnitude);
        }
        else if (data.lodGroup != null)
        {
            var lods = data.lodGroup.GetLODs();
            if (lods.Length > 0 && lods[0].renderers.Length > 0)
            {
                var lodBounds = lods[0].renderers[0].bounds;
                sphere = new BoundingSphere(lodBounds.center, lodBounds.extents.magnitude);
            }
            else
            {
                sphere = new BoundingSphere(data.transform.position, 0f);
                LogWarning($"Invalid LODGroup on object: {data.transform.name}");
            }
        }
        else
        {
            sphere = new BoundingSphere(data.transform.position, 0f);
        }

        if (boundingSpheres.IsCreated && index < boundingSpheres.Length)
            boundingSpheres[index] = sphere;
    }

    #endregion

    #region Cleanup

    private void CleanUpDestroyedObjects()
    {
        if (!objectData.IsCreated || objectData.Length == 0)
            return;

        CompleteJobs();

        // Process marked objects first
        if (objectsToRemove.Length > 0)
        {
            RemoveMarkedObjects();
            return;
        }

        // Check for null transforms
        CheckForNullTransforms();
    }

    private void RemoveMarkedObjects()
    {
        int newSize = objectData.Length - objectsToRemove.Length;
        if (newSize <= 0)
        {
            DisposeSystem();
            InitializeSystem();
            return;
        }

        var newObjectData = new NativeArray<ObjectData>(newSize, Allocator.Persistent);
        var newBoundingSpheres = new NativeArray<BoundingSphere>(newSize, Allocator.Persistent);

        int newIndex = 0;
        for (int i = 0; i < objectData.Length; i++)
        {
            if (!objectsToRemove.Contains(i))
            {
                newObjectData[newIndex] = objectData[i];
                newBoundingSpheres[newIndex] = boundingSpheres[i];
                newIndex++;
            }
        }

        objectData.Dispose();
        boundingSpheres.Dispose();

        objectData = newObjectData;
        boundingSpheres = newBoundingSpheres;

        // Update dynamic indices
        for (int i = dynamicObjectIndices.Length - 1; i >= 0; i--)
        {
            if (objectsToRemove.Contains(dynamicObjectIndices[i]))
            {
                dynamicObjectIndices.RemoveAtSwapBack(i);
            }
        }

        objectsToRemove.Clear();
        requiresBoundsUpdate = true;
        totalManagedObjects = objectData.Length;
    }

    private void CheckForNullTransforms()
    {
        bool needsUpdate = false;
        int itemsToCheck = Mathf.Min(cleanupBatchSize, objectData.Length);

        for (int i = 0; i < itemsToCheck; i++)
        {
            int index = (cleanupIndex + i) % objectData.Length;
            if (objectData[index].transform == null)
            {
                objectsToRemove.Add(index);
                objectIndexMap.Remove(objectData[index].transform?.gameObject);
                needsUpdate = true;
            }
        }

        cleanupIndex = (cleanupIndex + itemsToCheck) % objectData.Length;

        if (needsUpdate)
        {
            requiresBoundsUpdate = true;
        }
    }

    #endregion

    #region Job Management

    private void CompleteJobs()
    {
        if (!activeJobHandles.IsCreated || activeJobHandles.Length == 0)
            return;

        JobHandle.CompleteAll(activeJobHandles);
        activeJobHandles.Clear();
    }

    #endregion

    #region Camera Handling

    private void OnCameraPreCull(Camera cam)
    {
        if (activeCameras.Contains(cam) && cullingGroup != null)
        {
            CompleteJobs();
            UpdateCullingGroupReferences();
        }
    }

    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (activeCameras.Contains(camera) && cullingGroup != null)
        {
            CompleteJobs();
            UpdateCullingGroupReferences();
        }
    }

    #endregion

    #region Jobs

    [BurstCompile]
    private struct UpdateBoundingSpheresJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ObjectData> Objects;
        [WriteOnly] public NativeArray<BoundingSphere> BoundingSpheres;

        public void Execute(int index)
        {
            var obj = Objects[index];
            BoundingSphere sphere;

            if (obj.renderer != null)
            {
                var bounds = obj.renderer.bounds;
                sphere = new BoundingSphere(bounds.center, bounds.extents.magnitude);
            }
            else if (obj.lodGroup != null)
            {
                var lods = obj.lodGroup.GetLODs();
                if (lods.Length > 0 && lods[0].renderers.Length > 0)
                {
                    var lodBounds = lods[0].renderers[0].bounds;
                    sphere = new BoundingSphere(lodBounds.center, lodBounds.extents.magnitude);
                }
                else
                {
                    sphere = new BoundingSphere(obj.transform.position, 0f);
                }
            }
            else
            {
                sphere = new BoundingSphere(obj.transform.position, 0f);
            }

            BoundingSpheres[index] = sphere;
        }
    }

    [BurstCompile]
    private struct UpdateDynamicObjectsJob : IJobParallelFor
    {
        public NativeArray<ObjectData> Objects;
        [ReadOnly] public NativeArray<int> DynamicIndices;
        public NativeArray<BoundingSphere> BoundingSpheres;
        public float ChangeThreshold;

        public void Execute(int index)
        {
            int objIndex = DynamicIndices[index];
            var obj = Objects[objIndex];
            
            if (obj.transform == null || !obj.transform.hasChanged)
                return;

            bool positionChanged = Vector3.Distance(obj.transform.position, obj.lastPosition) > ChangeThreshold;
            bool rotationChanged = Quaternion.Angle(obj.transform.rotation, obj.lastRotation) > ChangeThreshold;
            bool scaleChanged = Vector3.Distance(obj.transform.localScale, obj.lastScale) > ChangeThreshold;

            if (positionChanged || rotationChanged || scaleChanged)
            {
                BoundingSphere sphere;
                if (obj.renderer != null)
                {
                    var bounds = obj.renderer.bounds;
                    sphere = new BoundingSphere(bounds.center, bounds.extents.magnitude);
                }
                else if (obj.lodGroup != null)
                {
                    var lods = obj.lodGroup.GetLODs();
                    if (lods.Length > 0 && lods[0].renderers.Length > 0)
                    {
                        var lodBounds = lods[0].renderers[0].bounds;
                        sphere = new BoundingSphere(lodBounds.center, lodBounds.extents.magnitude);
                    }
                    else
                    {
                        sphere = new BoundingSphere(obj.transform.position, 0f);
                    }
                }
                else
                {
                    sphere = new BoundingSphere(obj.transform.position, 0f);
                }

                BoundingSpheres[objIndex] = sphere;

                obj.lastPosition = obj.transform.position;
                obj.lastRotation = obj.transform.rotation;
                obj.lastScale = obj.transform.localScale;
                Objects[objIndex] = obj;
            }

            obj.transform.hasChanged = false;
        }
    }

    #endregion

    #region Utility Methods

    private void Log(string message)
    {
        if (debugLevel >= LogLevel.All)
            Debug.Log($"[VisibilityManager] {message}");
    }

    private void LogWarning(string message)
    {
        if (debugLevel >= LogLevel.Warnings)
            Debug.LogWarning($"[VisibilityManager] {message}");
    }

    private void LogError(string message)
    {
        if (debugLevel >= LogLevel.Errors)
            Debug.LogError($"[VisibilityManager] {message}");
    }

    #endregion

    #region Editor & Debug

#if UNITY_EDITOR
    private void OnValidate()
    {
        ValidateDistanceBands();
        ValidateCustomObjects();
    }

    private void ValidateDistanceBands()
    {
        if (distanceBands == null) return;

        for (int i = 0; i < distanceBands.Length; i++)
        {
            if (distanceBands[i] < 0)
            {
                distanceBands[i] = 0;
                LogWarning("Distance bands must be positive");
            }

            if (i > 0 && distanceBands[i] <= distanceBands[i - 1])
            {
                distanceBands[i] = distanceBands[i - 1] + 1f;
                LogWarning("Distance bands should be in increasing order");
            }
        }
    }

    private void ValidateCustomObjects()
    {
        if (customObjects == null) return;

        for (int i = customObjects.Count - 1; i >= 0; i--)
        {
            if (customObjects[i] == null)
            {
                customObjects.RemoveAt(i);
                LogWarning($"Null object found in customObjects at index {i}");
            }
        }

        customObjects = customObjects.Distinct().ToList();
    }

    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying || !boundingSpheres.IsCreated || !drawGizmos)
            return;

        Gizmos.color = Color.yellow;
        int count = Mathf.Min(maxGizmos, boundingSpheres.Length);
        
        for (int i = 0; i < count; i++)
        {
            if (boundingSpheres[i].radius > 0)
            {
                Gizmos.DrawWireSphere(boundingSpheres[i].position, boundingSpheres[i].radius);
            }
        }
    }
#endif

    #endregion
}

#if UNITY_EDITOR
[CustomEditor(typeof(AdvancedVisibilityManager))]
public class AdvancedVisibilityManagerEditor : Editor
{
    private SerializedProperty customObjectsProp;
    private ReorderableList customObjectsList;

    private void OnEnable()
    {
        customObjectsProp = serializedObject.FindProperty("customObjects");
        customObjectsList = new ReorderableList(serializedObject, customObjectsProp, true, true, true, true)
        {
            drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Custom Objects"),
            drawElementCallback = (rect, index, isActive, isFocused) =>
            {
                var element = customObjectsProp.GetArrayElementAtIndex(index);
                EditorGUI.PropertyField(new Rect(rect.x, rect.y, rect.width, EditorGUIUtility.singleLineHeight), element);
            }
        };
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        var manager = (AdvancedVisibilityManager)target;
        
        EditorGUILayout.LabelField("System Status", manager.isInitialized ? "Active" : "Inactive");
        EditorGUILayout.LabelField("Managed Objects", manager.totalManagedObjects.ToString());
        EditorGUILayout.LabelField("Visible Objects", manager.visibleObjectCount.ToString());

        if (GUILayout.Button("Refresh System"))
        {
            manager.RefreshObjects();
        }

        EditorGUILayout.Space();
        DrawDefaultInspector();
        customObjectsList.DoLayoutList();

        serializedObject.ApplyModifiedProperties();
    }
}
#endif
