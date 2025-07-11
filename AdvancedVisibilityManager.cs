using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;

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
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);
        activeJobHandles = new NativeList<JobHandle>(Allocator.Persistent);
        objectsToRemove = new NativeList<int>(Allocator.Persistent);
    }

    #endregion

    #region Settings

    [Header("Object Selection")]
    [Tooltip("Objects with this tag will be managed")]
    public string targetTag = "BK";

    [Tooltip("Optional: Provide custom objects instead of using tag")]
    public List<GameObject> customObjects;

    [Tooltip("Filter objects by layer")]
    public LayerMask targetLayer = -1; // Default: Everything

    [Header("Camera Settings")]
    [Tooltip("Main camera for visibility checks (used if cameras list is empty)")]
    public Camera targetCamera;

    [Tooltip("Cameras for visibility checks (if empty, uses targetCamera)")]
    public List<Camera> cameras;

    [Tooltip("Automatically find main camera if none is assigned")]
    public bool autoFindCamera = true;

    [Header("Optimization Settings")]
    [Tooltip("Optimization for static objects")]
    public bool areObjectsStatic = false;

    [Tooltip("Minimum change required to update bounds (meters/degrees)")]
    public float changeThreshold = 0.01f;

    [Tooltip("Number of objects to check for cleanup per frame")]
    public int cleanupBatchSize = 10;

    [Tooltip("Frames between cleanup checks")]
    public int cleanupInterval = 1;

    [Header("Distance Culling")]
    [Tooltip("Distance bands in meters (must be sorted and positive)")]
    public float[] distanceBands = new float[] { 50f, 100f };

    [Header("Occlusion Culling")]
    [Tooltip("Enable occlusion culling")]
    public bool useOcclusionCulling = true;

    [Header("Debug")]
    [Tooltip("Draw bounding spheres in Scene view")]
    public bool drawGizmos = true;

    [Tooltip("Maximum number of gizmos to draw")]
    public int maxGizmos = 100;

    [Tooltip("Debug logging level")]
    public LogLevel debugLevel = LogLevel.Warnings;

    [Header("Statistics")]
    [Tooltip("Number of visible objects")]
    [SerializeField, ReadOnly]
    private int visibleObjectCount;

    #endregion

    #region Private Variables

    private CullingGroup cullingGroup;
    private NativeArray<ObjectInfo> objects;
    private NativeList<int> dynamicObjectIndices;
    private NativeArray<BoundingSphere> boundingSpheres;
    private Dictionary<GameObject, int> objectIndexMap = new();
    private int cleanupIndex = 0;
    private int frameCounter = 0;
    private NativeList<JobHandle> activeJobHandles;
    private NativeList<int> objectsToRemove;
    private bool requiresBoundsUpdate = false;

    #endregion

    #region Structs

    [System.Serializable]
    private struct ObjectInfo
    {
        public Renderer renderer;
        public LODGroup lodGroup;
        public Transform transform;
        public Vector3 lastPosition;
        public Quaternion lastRotation;
        public Vector3 lastScale;
        public bool isVisible;
        public bool isMarkedForRemoval;
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

    public delegate void VisibilityChangedEvent(int index, bool isVisible, float distance);
    public event VisibilityChangedEvent OnVisibilityChanged;

    #endregion

    #region Unity Lifecycle

    private void OnEnable()
    {
        InitializeSystem();
        Camera.onPreCull += OnCameraPreCull;
        StartCoroutine(UpdateDynamicObjectsCoroutine());
    }

    private void Update()
    {
        CompleteJobs();

        if (requiresBoundsUpdate)
        {
            UpdateBoundingSpheres();
            requiresBoundsUpdate = false;
        }

        CleanUpDestroyedObjects();
    }

    private void LateUpdate()
    {
        CompleteJobs();
    }

    private void OnDisable()
    {
        Camera.onPreCull -= OnCameraPreCull;
        CompleteJobs();
    }

    private void OnDestroy()
    {
        CompleteJobs();
        DisposeGroup();

        if (activeJobHandles.IsCreated)
            activeJobHandles.Dispose();

        if (objectsToRemove.IsCreated)
            objectsToRemove.Dispose();
    }

    #endregion

    #region Public Methods

    [ContextMenu("Refresh Objects")]
    public void RefreshObjects()
    {
        CompleteJobs();
        InitializeSystem();
    }

    public void AddObject(GameObject target)
    {
        if (target == null || ((1 << target.layer) & targetLayer) == 0)
        {
            if (debugLevel >= LogLevel.Warnings)
                Debug.LogWarning("[VisibilityManager] Invalid or null target object.");
            return;
        }

        var rend = target.GetComponent<Renderer>();
        var lod = target.GetComponent<LODGroup>();
        if (rend == null && lod == null)
        {
            if (debugLevel >= LogLevel.Warnings)
                Debug.LogWarning("[VisibilityManager] Target object has no Renderer or LODGroup.");
            return;
        }

        CompleteJobs();

        var newObjects = new NativeArray<ObjectInfo>(objects.Length + 1, Allocator.Temp);
        if (objects.IsCreated && objects.Length > 0)
        {
            objects.CopyTo(newObjects.GetSubArray(0, objects.Length));
            objects.Dispose();
        }

        var objInfo = new ObjectInfo
        {
            renderer = rend,
            lodGroup = lod,
            transform = rend != null ? rend.transform : lod.transform,
            lastPosition = rend != null ? rend.transform.position : lod.transform.position,
            lastRotation = rend != null ? rend.transform.rotation : lod.transform.rotation,
            lastScale = rend != null ? rend.transform.localScale : lod.transform.localScale,
            isVisible = false,
            isMarkedForRemoval = false
        };

        int index = newObjects.Length - 1;
        newObjects[index] = objInfo;
        objects = new NativeArray<ObjectInfo>(newObjects, Allocator.Persistent);
        newObjects.Dispose();

        objectIndexMap[target] = index;

        if (!areObjectsStatic && !target.isStatic)
        {
            dynamicObjectIndices.Add(index);
        }

        UpdateBoundingSphere(index);
        requiresBoundsUpdate = true;
    }

    public void RemoveObject(GameObject target)
    {
        if (target == null || !objectIndexMap.TryGetValue(target, out int index))
        {
            if (debugLevel >= LogLevel.Warnings)
                Debug.LogWarning("[VisibilityManager] Target object not found or null.");
            return;
        }

        CompleteJobs();

        var obj = objects[index];
        obj.isMarkedForRemoval = true;
        objects[index] = obj;

        objectsToRemove.Add(index);
        objectIndexMap.Remove(target);

        requiresBoundsUpdate = true;
    }

    #endregion

    #region Core Functionality

    private void InitializeSystem()
    {
        if (!ValidateCamera())
            return;

        InitializeObjects();
        SetupCullingGroup();

        if (debugLevel >= LogLevel.All)
            Debug.Log("[VisibilityManager] System initialized successfully");
    }

    private bool ValidateCamera()
    {
        if (cameras != null && cameras.Count > 0)
        {
            cameras.RemoveAll(cam => cam == null);
            if (cameras.Count > 0)
            {
                targetCamera = cameras[0];
                return true;
            }
        }

        if (targetCamera == null && autoFindCamera)
        {
            targetCamera = Camera.main;
            if (targetCamera == null && debugLevel >= LogLevel.Errors)
            {
                Debug.LogError("[VisibilityManager] No camera available and auto-find failed.");
                return false;
            }
        }

        if (targetCamera == null)
        {
            if (debugLevel >= LogLevel.Errors)
                Debug.LogError("[VisibilityManager] No camera assigned.");
            return false;
        }

        return true;
    }

    private void InitializeObjects()
    {
        ClearCollections();
        CacheTransforms();
        UpdateBoundingSpheres();
    }

    private void ClearCollections()
    {
        if (objects.IsCreated)
            objects.Dispose();

        if (dynamicObjectIndices.IsCreated)
            dynamicObjectIndices.Dispose();

        if (boundingSpheres.IsCreated)
            boundingSpheres.Dispose();

        dynamicObjectIndices = new NativeList<int>(Allocator.Persistent);
        objectIndexMap.Clear();
        objectsToRemove.Clear();
    }

    private void CacheTransforms()
    {
        GameObject[] targets = GetTargetObjects();

        if (targets.Length == 0 && debugLevel >= LogLevel.Warnings)
        {
            Debug.LogWarning($"[VisibilityManager] No valid objects found");
            return;
        }

        objects = new NativeArray<ObjectInfo>(targets.Length, Allocator.Persistent);

        for (int i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            if (target == null || ((1 << target.layer) & targetLayer) == 0)
                continue;

            var rend = target.GetComponent<Renderer>();
            var lod = target.GetComponent<LODGroup>();

            if (rend != null || lod != null)
            {
                var objInfo = new ObjectInfo
                {
                    renderer = rend,
                    lodGroup = lod,
                    transform = rend != null ? rend.transform : lod.transform,
                    lastPosition = rend != null ? rend.transform.position : lod.transform.position,
                    lastRotation = rend != null ? rend.transform.rotation : lod.transform.rotation,
                    lastScale = rend != null ? rend.transform.localScale : lod.transform.localScale,
                    isVisible = false,
                    isMarkedForRemoval = false
                };

                objects[i] = objInfo;
                objectIndexMap[target] = i;

                if (!areObjectsStatic && !target.isStatic)
                    dynamicObjectIndices.Add(i);
            }
        }
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

    private void UpdateBoundingSpheres()
    {
        if (!objects.IsCreated || objects.Length == 0)
        {
            if (boundingSpheres.IsCreated)
                boundingSpheres.Dispose();
            return;
        }

        if (boundingSpheres.IsCreated && boundingSpheres.Length != objects.Length)
            boundingSpheres.Dispose();

        if (!boundingSpheres.IsCreated)
            boundingSpheres = new NativeArray<BoundingSphere>(objects.Length, Allocator.Persistent);

        var job = new UpdateBoundingSpheresJob
        {
            Objects = objects,
            BoundingSpheres = boundingSpheres
        };

        JobHandle handle = job.Schedule(objects.Length, 64);
        activeJobHandles.Add(handle);
    }

    private void UpdateBoundingSphere(int index)
    {
        if (!objects.IsCreated || index < 0 || index >= objects.Length)
            return;

        var obj = objects[index];
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
                sphere = new BoundingSphere(obj.transform?.position ?? Vector3.zero, 0f);
                if (debugLevel >= LogLevel.Warnings)
                    Debug.LogWarning($"[VisibilityManager] Invalid LODGroup on object: {obj.transform?.name}");
            }
        }
        else
        {
            sphere = new BoundingSphere(obj.transform?.position ?? Vector3.zero, 0f);
        }

        if (boundingSpheres.IsCreated && index < boundingSpheres.Length)
            boundingSpheres[index] = sphere;
    }

    private void SetupCullingGroup()
    {
        DisposeGroup();

        if (!objects.IsCreated || objects.Length == 0)
        {
            if (debugLevel >= LogLevel.Warnings)
                Debug.LogWarning("[VisibilityManager] No objects to cull, CullingGroup not created.");
            return;
        }

        cullingGroup = new CullingGroup
        {
            targetCamera = targetCamera,
            onStateChanged = OnStateChanged,
            enableOcclusionCulling = useOcclusionCulling
        };

        cullingGroup.SetBoundingSpheres(boundingSpheres);
        cullingGroup.SetBoundingSphereCount(boundingSpheres.Length);

        if (distanceBands != null && distanceBands.Length > 0)
            cullingGroup.SetDistanceBands(distanceBands);

        if (debugLevel >= LogLevel.All)
            Debug.Log("[VisibilityManager] CullingGroup initialized with occlusion culling: " + useOcclusionCulling);
    }

    private void UpdateCullingGroup()
    {
        if (cullingGroup == null || !objects.IsCreated || objects.Length == 0)
        {
            SetupCullingGroup();
            return;
        }

        cullingGroup.SetBoundingSpheres(boundingSpheres);
        cullingGroup.SetBoundingSphereCount(boundingSpheres.Length);
    }

    private System.Collections.IEnumerator UpdateDynamicObjectsCoroutine()
    {
        while (true)
        {
            if (!areObjectsStatic && boundingSpheres.IsCreated && dynamicObjectIndices.Length > 0)
            {
                var job = new UpdateDynamicObjectsJob
                {
                    Objects = objects,
                    DynamicIndices = dynamicObjectIndices,
                    BoundingSpheres = boundingSpheres,
                    ChangeThreshold = changeThreshold
                };

                JobHandle handle = job.Schedule(dynamicObjectIndices.Length, 64);
                activeJobHandles.Add(handle);
                yield return new WaitUntil(() => handle.IsCompleted);
            }
            yield return new WaitForSeconds(0.1f); // تنظیم فاصله به‌روزرسانی
        }
    }

    private void CompleteJobs()
    {
        if (!activeJobHandles.IsCreated || activeJobHandles.Length == 0)
            return;

        JobHandle.CompleteAll(activeJobHandles);
        activeJobHandles.Clear();
    }

    private void OnStateChanged(CullingGroupEvent evt)
    {
        try
        {
            if (evt.index < 0 || evt.index >= objects.Length)
                return;

            var obj = objects[evt.index];
            if (obj.isMarkedForRemoval || obj.transform == null)
                return;

            bool visible = evt.isVisible;
            if (distanceBands != null && distanceBands.Length > 0)
                visible &= evt.currentDistance < distanceBands.Length;

            if (obj.isVisible != visible)
            {
                obj.isVisible = visible;
                objects[evt.index] = obj;

                visibleObjectCount = Mathf.Max(0, visible ? visibleObjectCount + 1 : visibleObjectCount - 1);

                OnVisibilityChanged?.Invoke(evt.index, visible, evt.currentDistance);

                if (obj.lodGroup != null)
                {
                    HandleLODVisibility(evt.index, visible, evt.currentDistance);
                }
                else if (obj.renderer != null)
                {
                    obj.renderer.enabled = visible;
                    obj.renderer.gameObject.SetActive(visible);
                }
            }
        }
        catch (System.Exception e)
        {
            if (debugLevel >= LogLevel.Errors)
                Debug.LogError($"[VisibilityManager] Error in OnStateChanged: {e.Message}");
        }
    }

    private void HandleLODVisibility(int index, bool isGroupVisible, float distance)
    {
        var lodGroup = objects[index].lodGroup;
        if (lodGroup == null) return;

        lodGroup.enabled = isGroupVisible;
        lodGroup.gameObject.SetActive(isGroupVisible); // اضافه کردن SetActive

        if (!isGroupVisible) return;

        var lods = lodGroup.GetLODs();
        for (int j = 0; j < lods.Length; j++)
        {
            bool lodVisible = j < distanceBands.Length ? distance <= distanceBands[j] : j == lods.Length - 1;
            foreach (var renderer in lods[j].renderers)
            {
                if (renderer != null)
                {
                    renderer.enabled = lodVisible;
                    renderer.gameObject.SetActive(lodVisible); // اضافه کردن SetActive
                }
            }
        }
    }

    private void CleanUpDestroyedObjects()
    {
        if (!objects.IsCreated || objects.Length == 0 || frameCounter++ % cleanupInterval != 0)
            return;

        CompleteJobs();

        // First process marked objects
        if (objectsToRemove.Length > 0)
        {
            var newObjects = new NativeArray<ObjectInfo>(objects.Length - objectsToRemove.Length, Allocator.Temp);
            int newIndex = 0;

            for (int i = 0; i < objects.Length; i++)
            {
                if (!objectsToRemove.Contains(i))
                {
                    newObjects[newIndex++] = objects[i];
                }
            }

            objects.Dispose();
            objects = new NativeArray<ObjectInfo>(newObjects, Allocator.Persistent);
            newObjects.Dispose();

            for (int i = dynamicObjectIndices.Length - 1; i >= 0; i--)
            {
                if (objectsToRemove.Contains(dynamicObjectIndices[i]))
                {
                    dynamicObjectIndices.RemoveAtSwapBack(i);
                }
            }

            objectsToRemove.Clear();
            requiresBoundsUpdate = true;
            return;
        }

        // Then check for null transforms
        int itemsToCheck = Mathf.Min(cleanupBatchSize, objects.Length);
        bool needsUpdate = false;

        for (int i = 0; i < itemsToCheck; i++)
        {
            int index = (cleanupIndex + i) % objects.Length;
            if (objects[index].transform == null)
            {
                objectsToRemove.Add(index);
                objectIndexMap.Remove(objects[index].transform?.gameObject);
                needsUpdate = true;
            }
        }

        cleanupIndex = (cleanupIndex + itemsToCheck) % objects.Length;

        if (needsUpdate)
        {
            requiresBoundsUpdate = true;
        }
    }

    private void DisposeGroup()
    {
        CompleteJobs();

        if (cullingGroup != null)
        {
            cullingGroup.onStateChanged -= OnStateChanged;
            cullingGroup.Dispose();
            cullingGroup = null;
        }

        if (boundingSpheres.IsCreated)
            boundingSpheres.Dispose();

        if (objects.IsCreated)
            objects.Dispose();

        if (dynamicObjectIndices.IsCreated)
            dynamicObjectIndices.Dispose();
    }

    private void OnCameraPreCull(Camera cam)
    {
        if (cam == targetCamera && cullingGroup != null)
        {
            CompleteJobs();
            UpdateCullingGroup();
        }
    }

    #endregion

    #region Jobs

    [BurstCompile]
    private struct UpdateBoundingSpheresJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ObjectInfo> Objects;
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
                    sphere = new BoundingSphere(obj.transform?.position ?? Vector3.zero, 0f);
                }
            }
            else
            {
                sphere = new BoundingSphere(obj.transform?.position ?? Vector3.zero, 0f);
            }

            BoundingSpheres[index] = sphere;
        }
    }

    [BurstCompile]
    private struct UpdateDynamicObjectsJob : IJobParallelFor
    {
        public NativeArray<ObjectInfo> Objects;
        [ReadOnly] public NativeList<int> DynamicIndices;
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
                        sphere = new BoundingSphere(obj.transform?.position ?? Vector3.zero, 0f);
                    }
                }
                else
                {
                    sphere = new BoundingSphere(obj.transform?.position ?? Vector3.zero, 0f);
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
                Debug.LogWarning("[VisibilityManager] Distance bands must be positive");
                distanceBands[i] = 0;
            }

            if (i > 0 && distanceBands[i] <= distanceBands[i - 1])
            {
                Debug.LogWarning("[VisibilityManager] Distance bands should be in increasing order");
                distanceBands[i] = distanceBands[i - 1] + 1f;
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
                Debug.LogWarning($"[VisibilityManager] Null object found in customObjects at index {i}");
                customObjects.RemoveAt(i);
            }
        }

        customObjects = customObjects.Distinct().ToList();
    }

    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying || !boundingSpheres.IsCreated || !drawGizmos)
            return;

        Gizmos.color = Color.yellow;
        int count = Mathf.Min(maxGizmos, boundingSpheres.Length);
        for (int i = 0; i < count; i++)
        {
            if (boundingSpheres[i].radius > 0)
                Gizmos.DrawWireSphere(boundingSpheres[i].position, boundingSpheres[i].radius);
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

        DrawDefaultInspector();

        var manager = (AdvancedVisibilityManager)target;
        EditorGUILayout.LabelField("Visible Objects", manager.visibleObjectCount.ToString());

        if (GUILayout.Button("Refresh Objects"))
        {
            manager.RefreshObjects();
        }

        customObjectsList.DoLayoutList();

        serializedObject.ApplyModifiedProperties();
    }
}

public class ReadOnlyAttribute : PropertyAttribute { }
[CustomPropertyDrawer(typeof(ReadOnlyAttribute))]
public class ReadOnlyDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        GUI.enabled = false;
        EditorGUI.PropertyField(position, property, label, true);
        GUI.enabled = true;
    }
}
#endif
