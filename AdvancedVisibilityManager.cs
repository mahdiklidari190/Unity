using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditorInternal;
#endif

[DisallowMultipleComponent]
public sealed class AdvancedVisibilityManager : MonoBehaviour
{
    #region Settings

    [Header("Core Settings")]
    public string targetTag = "BK";
    public List<GameObject> customObjects;
    public LayerMask targetLayer = -1;

    [Header("Camera Settings")] 
    public Camera targetCamera;
    public List<Camera> additionalCameras;
    public bool autoFindCamera = true;
    public float predictionDistance = 5f; // New: Camera prediction

    [Header("Optimization")]
    public bool areObjectsStatic = false;
    public float changeThreshold = 0.01f;
    public int cleanupBatchSize = 10;
    public int cleanupInterval = 3;

    [Header("Culling")] 
    public float[] distanceBands = { 50f, 100f };
    public bool useOcclusionCulling = true;
    public int occlusionUpdateInterval = 2;
    public bool useFrustumCulling = true;
    public bool useGPUCulling = false; // New: GPU culling toggle

    [Header("LOD")] 
    public bool autoLODSelection = true; // New: Auto LOD control

    [Header("Debug")] 
    public bool drawGizmos = true;
    public int maxGizmos = 100;
    public LogLevel debugLevel = LogLevel.Warnings;
    public bool logToFile = false; // New: File logging

    [Header("Statistics")]
    [ReadOnly] public int visibleObjectCount;
    [ReadOnly] public int totalManagedObjects;
    [ReadOnly] public float visiblePercentage; // New: Percentage display

    #endregion

    #region Private Variables

    // Core data structures
    private CullingGroup cullingGroup;
    private NativeArray<Vector3> positions;
    private NativeArray<Quaternion> rotations;
    private NativeArray<Vector3> scales;
    private NativeArray<Bounds> bounds;
    private NativeArray<bool> visibilityStates;
    private NativeArray<float> distances;
    private NativeList<int> dynamicIndices;
    private NativeList<JobHandle> activeJobHandles;
    
    // GPU Culling
    private ComputeBuffer gpuObjectsBuffer;
    private ComputeBuffer gpuResultBuffer;
    private ComputeShader cullingShader;
    
    // Object tracking
    private Dictionary<GameObject, int> objectIndexMap = new(1024);
    private List<Renderer> managedRenderers = new();
    private List<LODGroup> managedLODGroups = new();
    
    // Camera prediction
    private Vector3 lastCameraPosition;
    private Quaternion lastCameraRotation;
    private Vector3 cameraVelocity;
    
    // Debug & logging
    private StreamWriter logFileWriter;
    private string logFilePath;
    private float lastLogTime;

    #endregion

    #region Initialization

    private void Awake()
    {
        InitializeSystem();
        
        // Setup file logging if enabled
        if (logToFile)
        {
            logFilePath = Path.Combine(Application.persistentDataPath, "VisibilityLog.txt");
            logFileWriter = new StreamWriter(logFilePath, true);
            LogToFile("System initialized at " + System.DateTime.Now);
        }
    }

    private void InitializeSystem()
    {
        DisposeSystem();
        
        // Initialize native collections with proper allocators
        positions = new NativeArray<Vector3>(0, Allocator.Persistent);
        rotations = new NativeArray<Quaternion>(0, Allocator.Persistent);
        scales = new NativeArray<Vector3>(0, Allocator.Persistent);
        bounds = new NativeArray<Bounds>(0, Allocator.Persistent);
        visibilityStates = new NativeArray<bool>(0, Allocator.Persistent);
        distances = new NativeArray<float>(0, Allocator.Persistent);
        dynamicIndices = new NativeList<int>(Allocator.Persistent);
        activeJobHandles = new NativeList<JobHandle>(Allocator.Persistent);
        
        // Initialize GPU culling if enabled
        if (useGPUCulling)
        {
            InitializeGPUCulling();
        }
        
        CacheObjects();
        SetupCullingGroup();
        
        // Initialize camera prediction
        if (targetCamera != null)
        {
            lastCameraPosition = targetCamera.transform.position;
            lastCameraRotation = targetCamera.transform.rotation;
        }
    }

    private void InitializeGPUCulling()
    {
        cullingShader = Resources.Load<ComputeShader>("VisibilityCulling");
        
        // Create buffers with proper size
        int stride = System.Runtime.InteropServices.Marshal.SizeOf(typeof(GPUObjectData));
        gpuObjectsBuffer = new ComputeBuffer(1024, stride);
        gpuResultBuffer = new ComputeBuffer(1024, sizeof(uint), ComputeBufferType.Append);
        
        Log("GPU Culling initialized");
    }

    #endregion

    #region Main Update Loop

    private void Update()
    {
        UpdateCameraPrediction();
        
        // Only complete jobs if we need the data this frame
        if (RequiresImmediateData())
        {
            CompleteJobs();
        }
        
        UpdateVisibility();
        UpdateStatistics();
        
        // Log periodic stats
        if (Time.time - lastLogTime > 5f && logToFile)
        {
            LogStatsToFile();
            lastLogTime = Time.time;
        }
    }

    private void LateUpdate()
    {
        // Always complete jobs before rendering
        CompleteJobs();
        
        if (requiresBoundsUpdate)
        {
            UpdateBoundingSpheres();
            requiresBoundsUpdate = false;
        }
        
        // Update GPU culling if enabled
        if (useGPUCulling)
        {
            RunGPUCulling();
        }
    }

    private void UpdateCameraPrediction()
    {
        if (targetCamera == null) return;
        
        // Calculate camera movement for prediction
        cameraVelocity = (targetCamera.transform.position - lastCameraPosition) / Time.deltaTime;
        lastCameraPosition = targetCamera.transform.position;
        lastCameraRotation = targetCamera.transform.rotation;
    }

    #endregion

    #region GPU Culling

    private void RunGPUCulling()
    {
        if (cullingShader == null || !gpuObjectsBuffer.IsValid()) return;
        
        // Update GPU object data
        var gpuData = new GPUObjectData[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            gpuData[i] = new GPUObjectData
            {
                position = positions[i],
                bounds = bounds[i],
                isVisible = visibilityStates[i]
            };
        }
        
        gpuObjectsBuffer.SetData(gpuData);
        gpuResultBuffer.SetCounterValue(0);
        
        // Dispatch compute shader
        int kernel = cullingShader.FindKernel("CSMain");
        cullingShader.SetBuffer(kernel, "_Objects", gpuObjectsBuffer);
        cullingShader.SetBuffer(kernel, "_VisibleObjects", gpuResultBuffer);
        cullingShader.SetMatrix("_CameraViewProj", targetCamera.projectionMatrix * targetCamera.worldToCameraMatrix);
        cullingShader.SetFloat("_PredictionDistance", predictionDistance);
        
        cullingShader.Dispatch(kernel, Mathf.CeilToInt(positions.Length / 64f), 1, 1);
        
        // Read back results
        ComputeBuffer.CopyCount(gpuResultBuffer, gpuResultBuffer, 0);
        uint[] visibleIndices = new uint[positions.Length];
        gpuResultBuffer.GetData(visibleIndices);
        
        // Update visibility states
        for (int i = 0; i < visibleIndices.Length; i++)
        {
            int index = (int)visibleIndices[i];
            if (index >= 0 && index < visibilityStates.Length)
            {
                visibilityStates[index] = true;
            }
        }
    }

    private struct GPUObjectData
    {
        public Vector3 position;
        public Bounds bounds;
        public bool isVisible;
    }

    #endregion

    #region Object Management

    public void RegisterObject(GameObject target)
    {
        // ... (existing registration code)
        
        // Add to appropriate lists
        var renderer = target.GetComponent<Renderer>();
        if (renderer != null)
        {
            managedRenderers.Add(renderer);
        }
        
        var lodGroup = target.GetComponent<LODGroup>();
        if (lodGroup != null)
        {
            managedLODGroups.Add(lodGroup);
        }
        
        Log($"Registered object: {target.name}");
        if (logToFile) LogToFile($"Registered: {target.name}");
    }

    public void UnregisterObject(GameObject target)
    {
        // ... (existing unregistration code)
        
        // Remove from lists
        var renderer = target.GetComponent<Renderer>();
        if (renderer != null)
        {
            managedRenderers.Remove(renderer);
        }
        
        var lodGroup = target.GetComponent<LODGroup>();
        if (lodGroup != null)
        {
            managedLODGroups.Remove(lodGroup);
        }
        
        Log($"Unregistered object: {target.name}");
        if (logToFile) LogToFile($"Unregistered: {target.name}");
    }

    #endregion

    #region Debug & Logging

    private void LogStatsToFile()
    {
        if (!logToFile || logFileWriter == null) return;
        
        string stats = $"Frame: {Time.frameCount}\n" +
                      $"Visible: {visibleObjectCount}/{totalManagedObjects} ({visiblePercentage:F1}%)\n" +
                      $"Camera Pos: {targetCamera.transform.position}\n" +
                      $"Camera Vel: {cameraVelocity.magnitude:F2} m/s\n";
        
        LogToFile(stats);
    }

    private void LogToFile(string message)
    {
        try
        {
            logFileWriter?.WriteLine($"[{System.DateTime.Now:HH:mm:ss}] {message}");
            logFileWriter?.Flush();
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Log file error: {e.Message}");
            logToFile = false;
        }
    }

    #endregion

    #region Editor & Export

#if UNITY_EDITOR
    [ContextMenu("Export Current State")]
    public void ExportCurrentState()
    {
        var state = new VisibilityState
        {
            objectPositions = positions.ToArray(),
            objectVisibility = visibilityStates.ToArray(),
            cameraPosition = targetCamera.transform.position,
            cameraRotation = targetCamera.transform.rotation,
            timestamp = System.DateTime.Now.ToString()
        };
        
        string json = JsonUtility.ToJson(state, true);
        string path = EditorUtility.SaveFilePanel("Save Visibility State", "", "visibility_state", "json");
        
        if (!string.IsNullOrEmpty(path))
        {
            File.WriteAllText(path, json);
            Debug.Log($"Visibility state saved to {path}");
        }
    }

    [Serializable]
    private class VisibilityState
    {
        public Vector3[] objectPositions;
        public bool[] objectVisibility;
        public Vector3 cameraPosition;
        public Quaternion cameraRotation;
        public string timestamp;
    }
#endif

    #endregion

    #region Cleanup

    private void OnDestroy()
    {
        DisposeSystem();
        
        // Cleanup GPU resources
        gpuObjectsBuffer?.Release();
        gpuResultBuffer?.Release();
        
        // Close log file
        logFileWriter?.Close();
        
        Log("System destroyed");
    }

    private void DisposeSystem()
    {
        CompleteJobs();
        
        // Dispose native collections safely
        if (positions.IsCreated) positions.Dispose();
        if (rotations.IsCreated) rotations.Dispose();
        if (scales.IsCreated) scales.Dispose();
        if (bounds.IsCreated) bounds.Dispose();
        if (visibilityStates.IsCreated) visibilityStates.Dispose();
        if (distances.IsCreated) distances.Dispose();
        if (dynamicIndices.IsCreated) dynamicIndices.Dispose();
        if (activeJobHandles.IsCreated) activeJobHandles.Dispose();
        
        // Clear managed lists
        managedRenderers.Clear();
        managedLODGroups.Clear();
        objectIndexMap.Clear();
        
        DisposeGroup();
    }

    #endregion

    #region Jobs

    [BurstCompile]
    private struct UpdateVisibilityJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Vector3> positions;
        [ReadOnly] public NativeArray<Bounds> bounds;
        [WriteOnly] public NativeArray<bool> visibilityStates;
        [WriteOnly] public NativeArray<float> distances;
        
        public Matrix4x4 cameraMatrix;
        public Plane[] frustumPlanes;
        public Vector3 cameraVelocity;
        public float predictionDistance;
        public bool useFrustumCulling;

        public void Execute(int index)
        {
            // Frustum culling
            bool visible = !useFrustumCulling || GeometryUtility.TestPlanesAABB(frustumPlanes, bounds[index]);
            
            // Distance culling
            float distance = Vector3.Distance(positions[index], cameraMatrix.GetPosition());
            visible &= distance < predictionDistance;
            
            // Predictive visibility
            if (cameraVelocity.sqrMagnitude > 0.1f)
            {
                Vector3 predictedPosition = positions[index] + cameraVelocity * 0.1f;
                visible |= Vector3.Distance(predictedPosition, cameraMatrix.GetPosition()) < predictionDistance;
            }
            
            visibilityStates[index] = visible;
            distances[index] = distance;
        }
    }

    #endregion
}
