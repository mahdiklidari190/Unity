using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditorInternal;
#endif

[DisallowMultipleComponent]
[HelpURL("https://docs.unity3d.com/ScriptReference/CullingGroup.html")]
public partial class AdvancedVisibilityManager : MonoBehaviour
{
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
    private NativeList<ObjectInfo> objects;
    private NativeList<int> dynamicObjectIndices;
    private NativeArray<BoundingSphere> boundingSpheres;
    private Dictionary<GameObject, int> objectIndexMap = new();
    private int cleanupIndex = 0;
    private int frameCounter = 0;

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

    void OnEnable()
    {
        objects = new NativeList<ObjectInfo>(Allocator.Persistent);
        dynamicObjectIndices = new NativeList<int>(Allocator.Persistent);
        InitializeSystem();
        Camera.onPreCull += OnCameraPreCull;
    }

    void Update()
    {
        UpdateDynamicObjects();
        CleanUpDestroyedObjects();
    }

    void OnDisable()
    {
        Camera.onPreCull -= OnCameraPreCull;
    }

    void OnDestroy()
    {
        DisposeGroup();
    }

    #endregion

    #region Public Methods

    [ContextMenu("Refresh Objects")]
    public void RefreshObjects() => InitializeSystem();

    public void AddObject(GameObject target)
    {
        if (target == null || ((1 << target.layer) & targetLayer) == 0) return;

        var rend = target.GetComponent<Renderer>();
        var lod = target.GetComponent<LODGroup>();
        if (rend == null && lod == null) return;

        var objInfo = new ObjectInfo
        {
            renderer = rend,
            lodGroup = lod,
            transform = rend != null ? rend.transform : lod.transform,
            lastPosition = rend != null ? rend.transform.position : lod.transform.position,
            lastRotation = rend != null ? rend.transform.rotation : lod.transform.rotation,
            lastScale = rend != null ? rend.transform.localScale : lod.transform.localScale,
            isVisible = false
        };

        int index = objects.Length;
        objects.Add(objInfo);
        objectIndexMap[target] = index;

        if (!areObjectsStatic && !target.isStatic)
            dynamicObjectIndices.Add(index);

        UpdateBoundingSphere(index);
        UpdateCullingGroup();
    }

    public void RemoveObject(GameObject target)
    {
        if (target == null || !objectIndexMap.TryGetValue(target, out int index)) return;

        objects.RemoveAt(index);
        objectIndexMap.Remove(target);
        dynamicObjectIndices.RemoveAtSwapBack(dynamicObjectIndices.IndexOf(index));

        // Update indices in objectIndexMap
        for (int i = index; i < objects.Length; i++)
        {
            if (objects[i].transform != null)
                objectIndexMap[objects[i].transform.gameObject] = i;
        }

        UpdateBoundingSpheres();
        UpdateCullingGroup();
    }

    #endregion

    #region Core Functionality

    private void InitializeSystem()
    {
        if (!ValidateCamera()) return;

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
        objects.Clear();
        dynamicObjectIndices.Clear();
        objectIndexMap.Clear();
        if (boundingSpheres.IsCreated)
            boundingSpheres.Dispose();
    }

    private void CacheTransforms()
    {
        GameObject[] targets = GetTargetObjects();

        if (targets.Length == 0 && debugLevel >= LogLevel.Warnings)
        {
            Debug.LogWarning($"[VisibilityManager] No valid objects found");
            return;
        }

        foreach (var target in targets)
        {
            if (target == null || ((1 << target.layer) & targetLayer) == 0) continue;

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
                    isVisible = false
                };

                int index = objects.Length;
                objects.Add(objInfo);
                objectIndexMap[target] = index;

                if (!areObjectsStatic && !target.isStatic)
                    dynamicObjectIndices.Add(index);
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
        if (boundingSpheres.IsCreated)
            boundingSpheres.Dispose();

        boundingSpheres = new NativeArray<BoundingSphere>(objects.Length, Allocator.Persistent);

        var job = new UpdateBoundingSpheresJob
        {
            Objects = objects,
            BoundingSpheres = boundingSpheres
        };

        JobHandle handle = job.Schedule(objects.Length, 64);
        handle.Complete();
    }

    private void UpdateBoundingSphere(int index)
    {
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

        boundingSpheres[index] = sphere;
    }

    private void SetupCullingGroup()
    {
        DisposeGroup();

        if (objects.Length == 0)
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
    }

    private void UpdateCullingGroup()
    {
        if (cullingGroup == null || objects.Length == 0)
        {
            SetupCullingGroup();
            return;
        }

        cullingGroup.SetBoundingSpheres(boundingSpheres);
        cullingGroup.SetBoundingSphereCount(boundingSpheres.Length);
    }

    private void UpdateDynamicObjects()
    {
        if (areObjectsStatic || !boundingSpheres.IsCreated) return;

        var job = new UpdateDynamicObjectsJob
        {
            Objects = objects,
            DynamicIndices = dynamicObjectIndices,
            BoundingSpheres = boundingSpheres,
            ChangeThreshold = changeThreshold
        };

        JobHandle handle = job.Schedule(dynamicObjectIndices.Length, 64);
        handle.Complete();
    }

    private void OnStateChanged(CullingGroupEvent evt)
    {
        try
        {
            if (evt.index < 0 || evt.index >= objects.Length)
                return;

            bool visible = evt.isVisible;
            if (distanceBands != null && distanceBands.Length > 0)
                visible &= evt.currentDistance < distanceBands.Length;

            var obj = objects[evt.index];
            obj.isVisible = visible;
            objects[evt.index] = obj;

            if (visible)
                visibleObjectCount++;
            else
                visibleObjectCount = Mathf.Max(0, visibleObjectCount - 1);

            OnVisibilityChanged?.Invoke(evt.index, visible, evt.currentDistance);

            if (obj.lodGroup != null)
            {
                HandleLODVisibility(evt.index, visible, evt.currentDistance);
            }
            else if (obj.renderer != null)
            {
                obj.renderer.enabled = visible;
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
        lodGroup.enabled = isGroupVisible;

        if (!isGroupVisible) return;

        var lods = lodGroup.GetLODs();
        for (int j = 0; j < lods.Length; j++)
        {
            bool lodVisible = j < distanceBands.Length ? distance <= distanceBands[j] : j == lods.Length - 1;
            foreach (var renderer in lods[j].renderers)
            {
                if (renderer != null)
                    renderer.enabled = lodVisible;
            }
        }
    }

    private void CleanUpDestroyedObjects()
    {
        if (objects.Length == 0 || frameCounter++ % cleanupInterval != 0) return;

        int itemsToCheck = Mathf.Min(cleanupBatchSize, objects.Length);
        bool needsUpdate = false;

        for (int i = 0; i < itemsToCheck; i++)
        {
            int index = (cleanupIndex + i) % objects.Length;
            if (objects[index].transform == null)
            {
                objectIndexMap.Remove(objects[index].transform?.gameObject);
                objects.RemoveAt(index);
                dynamicObjectIndices.RemoveAtSwapBack(dynamicObjectIndices.IndexOf(index));

                needsUpdate = true;
                i--;
                itemsToCheck = Mathf.Min(cleanupBatchSize, objects.Length);

                // Update indices in objectIndexMap
                for (int j = index; j < objects.Length; j++)
                {
                    if (objects[j].transform != null)
                        objectIndexMap[objects[j].transform.gameObject] = j;
                }
            }
        }

        cleanupIndex = (cleanupIndex + itemsToCheck) % objects.Length;

        if (needsUpdate)
        {
            var newSpheres = new NativeArray<BoundingSphere>(objects.Length, Allocator.Persistent);
            for (int i = 0; i < objects.Length; i++)
            {
                if (i < boundingSpheres.Length && objects[i].transform != null)
                    newSpheres[i] = boundingSpheres[i];
                else
                    UpdateBoundingSphere(i);
            }
            if (boundingSpheres.IsCreated)
                boundingSpheres.Dispose();
            boundingSpheres = newSpheres;

            UpdateCullingGroup();
        }
    }

    private void DisposeGroup()
    {
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
            UpdateCullingGroup();
        }
    }

    #endregion

    #region Jobs

    [Unity.Burst.BurstCompile]
    private struct UpdateBoundingSpheresJob : IJobParallelFor
    {
        [ReadOnly] public NativeList<ObjectInfo> Objects;
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

    [Unity.Burst.BurstCompile]
    private struct UpdateDynamicObjectsJob : IJobParallelFor
    {
        public NativeList<ObjectInfo> Objects;
        [ReadOnly] public NativeList<int> DynamicIndices;
        public NativeArray<BoundingSphere> BoundingSpheres;
        public float ChangeThreshold;

        public void Execute(int index)
        {
            int objIndex = DynamicIndices[index];
            var obj = Objects[objIndex];
            if (obj.transform == null || !obj.transform.hasChanged) return;

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

// ReadOnly attribute for Inspector
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
