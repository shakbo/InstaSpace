using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using System.Collections;
using System.Text;
using TMPro;
using System.Collections.Generic;
using System;
using UnityEngine.EventSystems;
using System.Threading.Tasks;

// 導入 glTFast 核心功能
using GLTFast;
// (不再需要 using GLTFast.Materials;)

public class MeshyGenerator : MonoBehaviour
{
    [Header("API Configuration")]
    [SerializeField] private string apiKey = "YOUR_MESHY_API_KEY";
    [SerializeField] private string meshyApiUrl = "https://api.meshy.ai/openapi/v2/text-to-3d";
    [SerializeField] private string meshyTaskStatusUrlBase = "https://api.meshy.ai/openapi/v2/text-to-3d/";
    [SerializeField] private float pollingIntervalSeconds = 5.0f;
    [SerializeField] private float maxPollingTimeSeconds = 600f; // 10 分鐘逾時

    [Header("Required References")]
    [Tooltip("用於讀取生成提示的 InputField")]
    [SerializeField] private InputField promptInput;
    [Tooltip("用於顯示預覽模型的 RawImage")]
    [SerializeField] private RawImage previewImage;
    [Tooltip("用於顯示狀態訊息的 TextMeshProUGUI")]
    [SerializeField] private TextMeshProUGUI statusText;
    [Tooltip("用於渲染預覽模型的攝影機")]
    [SerializeField] private Camera previewCamera;
    [Tooltip("用於放置和旋轉預覽模型的空 GameObject")]
    [SerializeField] private GameObject previewModelContainer;
    [Tooltip("預覽模型所在的 Layer (確保 previewCamera 看得到, 主相機看不到)")]
    [SerializeField] private LayerMask previewModelLayer;
    [Tooltip("請拖入您場景中帶有 PlaceModelInvoker.cs 的 GameObject")]
    [SerializeField] private PlaceModelInvoker placementInvoker;

    [Header("Preview Settings")]
    [SerializeField] private float previewPadding = 1.2f;
    [SerializeField] private float previewRotationSpeed = 0.4f;

    // 內部狀態
    private GameObject currentPreviewModelInstance;
    private RenderTexture previewRenderTexture;
    private bool isDraggingPreview = false;
    private bool isBusy = false; // 防止重複觸發 API 流程

    // JSON 輔助類別 (與之前相同)
    [System.Serializable] private class TextTo3DRequestPreview { public string mode = "preview"; public string prompt; public string art_style = "realistic"; }
    [System.Serializable] private class TextTo3DRequestRefine { public string mode = "refine"; public string preview_task_id; public bool enable_pbr = true; }
    [System.Serializable] private class TaskCreateResponse { public string result = string.Empty; }
    [System.Serializable] private class TaskStatusResponse { public string id = string.Empty; public ModelUrls model_urls = new ModelUrls(); public int progress = 0; public string status = string.Empty; public TaskError task_error = new TaskError(); }
    [System.Serializable] private class ModelUrls { public string glb = string.Empty; }
    [System.Serializable] private class TaskError { public string message = string.Empty; }

    void Start()
    {
        AutoAssignStatusTextIfMissing();
        if (!ValidateConfiguration())
        {
            this.enabled = false;
            return;
        }
        SetupPreviewRendering();
        SetupEventTriggerListeners();
        SetStatus("Ready");
    }

    // Try to auto-assign a TextMeshProUGUI status text if the inspector field was not set.
    void AutoAssignStatusTextIfMissing()
    {
        if (statusText != null) return;
#if UNITY_2023_2_OR_NEWER
        TextMeshProUGUI found = UnityEngine.Object.FindFirstObjectByType<TextMeshProUGUI>();
#else
        // Fall back to the older API if FindFirstObjectByType is not available.
        TextMeshProUGUI found = FindObjectOfType<TextMeshProUGUI>();
#endif
        if (found != null)
        {
            statusText = found;
            Debug.Log($"MeshyGenerator: Auto-assigned statusText to '{found.gameObject.name}'. Consider assigning explicitly in the inspector.");
        }
        else
        {
            Debug.LogWarning("MeshyGenerator: statusText not assigned and no TextMeshProUGUI found in scene. Status messages will not be shown in UI.");
        }
    }

    bool ValidateConfiguration()
    {
        bool isValid = true;
        if (string.IsNullOrEmpty(apiKey) || apiKey == "YOUR_MESHY_API_KEY") { SetStatus("Error: API Key not set!", true); isValid = false; }
        if (promptInput == null) { Debug.LogError("Prompt Input (TMP_InputField) not assigned!"); isValid = false; }
        if (previewImage == null) { Debug.LogError("Preview Image (RawImage) not assigned!"); isValid = false; }
        if (statusText == null) { Debug.LogError("Status Text (TMP) not assigned!"); isValid = false; }
        if (previewCamera == null) { Debug.LogError("Preview Camera not assigned!"); isValid = false; }
        if (previewModelContainer == null) { Debug.LogError("Preview Model Container not assigned!"); isValid = false; }
        if (placementInvoker == null) { Debug.LogWarning("PlaceModelInvoker not assigned. Model placement will be disabled."); }
        int layerIndex = LayerMaskUtility.GetLayerIndexFromMask(previewModelLayer);
        if (layerIndex == -1 || string.IsNullOrEmpty(LayerMask.LayerToName(layerIndex)))
        {
            Debug.LogError("Preview Model Layer not set or invalid!");
            isValid = false;
        }
        return isValid;
    }

    void SetupPreviewRendering()
    {
        RectTransform rawImageRect = previewImage.GetComponent<RectTransform>();
        int width = Mathf.Max(1, (int)rawImageRect.rect.width);
        int height = Mathf.Max(1, (int)rawImageRect.rect.height);
        previewRenderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.DefaultHDR);
        if (!previewRenderTexture.Create()) { Debug.LogError("Failed to create RenderTexture"); return; }
        previewCamera.targetTexture = previewRenderTexture;
        previewImage.texture = previewRenderTexture;
        previewImage.color = Color.white;
        int layerIndex = LayerMaskUtility.GetLayerIndexFromMask(previewModelLayer);
        if (layerIndex != -1)
        {
            SetLayerRecursively(previewModelContainer, layerIndex);
            previewCamera.cullingMask = 1 << layerIndex;
        }
    }

    void AddEventTriggerListener(EventTrigger trigger, EventTriggerType eventType, UnityEngine.Events.UnityAction<BaseEventData> action)
    {
        EventTrigger.Entry entry = new EventTrigger.Entry { eventID = eventType };
        entry.callback.AddListener(action);
        trigger.triggers.Add(entry);
    }

    void SetStatus(string message, bool isError = false)
    {
        if (statusText != null)
        {
            // Ensure status text object is active (may be disabled in scene)
            if (!statusText.gameObject.activeInHierarchy) statusText.gameObject.SetActive(true);
            // Bring status text to front so it is not occluded by other UI
            statusText.transform.SetAsLastSibling();
            statusText.text = message;
            // Use white for non-error so it is visible on dark UI backgrounds
            statusText.color = isError ? Color.red : Color.white;
        }
        if (isError) Debug.LogError(message); else Debug.Log(message);
    }

    void CleanupPreviousModels()
    {
        if (currentPreviewModelInstance != null) { Destroy(currentPreviewModelInstance); currentPreviewModelInstance = null; }
        foreach (Transform child in previewModelContainer.transform) { Destroy(child.gameObject); }
        if (placementInvoker != null && placementInvoker.prefabToPlace != null && placementInvoker.prefabToPlace.transform.IsChildOf(transform))
        {
            Destroy(placementInvoker.prefabToPlace);
            placementInvoker.SetPrefab(null);
        }
    }

    void SetupEventTriggerListeners()
    {
        if (previewImage == null) return;
        EventTrigger trigger;
        if (!previewImage.gameObject.TryGetComponent<EventTrigger>(out trigger))
        {
            trigger = previewImage.gameObject.AddComponent<EventTrigger>();
        }
        trigger.triggers.Clear();
        AddEventTriggerListener(trigger, EventTriggerType.PointerDown, (data) => { OnPreviewPointerDown((PointerEventData)data); });
        AddEventTriggerListener(trigger, EventTriggerType.Drag, (data) => { OnPreviewDrag((PointerEventData)data); });
        AddEventTriggerListener(trigger, EventTriggerType.PointerUp, (data) => { OnPreviewPointerUp((PointerEventData)data); });
    }

    // --- 1. 將「生成」按鈕的 When Select() 綁定到此函式 ---
    /// <summary>
    /// 公開函式，用於從外部 (例如按鈕的 When Select 事件) 觸發模型生成流程。
    /// </summary>
    public void StartGenerationFromPrompt()
    {
        if (isBusy) { Debug.LogWarning("MeshyGenerator is already busy."); return; }

        string prompt = promptInput.text;
        if (string.IsNullOrWhiteSpace(prompt)) { SetStatus("Prompt field is empty", true); return; }

        isBusy = true;
        CleanupPreviousModels();
        // 通知外部（例如 UI）進入忙碌狀態，可以透過 UnityEvent
        // OnGenerationStart?.Invoke();
        SetStatus("Starting generation..."); // 更新狀態

        StartCoroutine(GenerateAndRefineWorkflow(prompt));
    }

    // (移除了 UpdatePromptFromMetaDictation，由外部處理 InputField 的 text)

    // --- 2. 自動化工作流程 (Preview -> Refine -> Load) ---
    IEnumerator GenerateAndRefineWorkflow(string prompt)
    {
        // 階段 1 & 2: Preview
        SetStatus("Step 1/4: Creating preview...");
        string previewTaskId = null;
        yield return StartCoroutine(CreateTaskCoroutine(new TextTo3DRequestPreview { prompt = prompt }, result => previewTaskId = result));
        if (string.IsNullOrEmpty(previewTaskId)) { OnWorkflowEnd(false, false, "Failed to create preview task"); yield break; }

        SetStatus($"Step 2/4: Waiting for preview model...");
        TaskStatusResponse previewStatus = null;
        yield return StartCoroutine(PollTaskStatusCoroutine(previewTaskId, result => previewStatus = result));
        if (previewStatus == null || previewStatus.status != "SUCCEEDED") { OnWorkflowEnd(false, false, $"Preview task failed: {previewStatus?.task_error?.message ?? "N/A"}"); yield break; }

        // 階段 3 & 4: Refine
        SetStatus($"Step 3/4: Refining model...");
        string refineTaskId = null;
        yield return StartCoroutine(CreateTaskCoroutine(new TextTo3DRequestRefine { preview_task_id = previewTaskId, enable_pbr = true }, result => refineTaskId = result));
        if (string.IsNullOrEmpty(refineTaskId)) { OnWorkflowEnd(false, false, "Failed to create refine task"); yield break; }

        SetStatus($"Step 4/4: Waiting for refined model...");
        TaskStatusResponse finalStatus = null;
        yield return StartCoroutine(PollTaskStatusCoroutine(refineTaskId, result => finalStatus = result));
        if (finalStatus == null || finalStatus.status != "SUCCEEDED") { OnWorkflowEnd(false, false, $"Refine task failed: {finalStatus?.task_error?.message ?? "N/A"}"); yield break; }

        // 階段 5: 載入模型
        SetStatus("Loading final model...");
        string modelUrl = finalStatus.model_urls?.glb;
        if (string.IsNullOrEmpty(modelUrl)) { OnWorkflowEnd(false, false, "Refinement succeeded but GLB URL not found"); yield break; }

        yield return StartCoroutine(LoadModelIntoPreview(modelUrl));

        // 階段 6: 協作 - 傳遞模型給 PlaceModelInvoker
        bool success = currentPreviewModelInstance != null;
        bool placementReady = false;
        string finalMessage = "";
        if (success && placementInvoker != null)
        {
            GameObject placementPrefab = Instantiate(currentPreviewModelInstance);
            placementPrefab.name = currentPreviewModelInstance.name + "_PlacementPrefab";
            SetLayerRecursively(placementPrefab, LayerMask.NameToLayer("Default")); // 移到 Default 層供 MRUK 使用
            placementPrefab.SetActive(false);
            placementPrefab.transform.SetParent(transform);
            placementInvoker.SetPrefab(placementPrefab); // 設定給 Invoker
            finalMessage = "Model ready for placement";
            placementReady = true;
        }
        else if (success) { finalMessage = "Model loaded (PlaceModelInvoker not set)"; }
        else { finalMessage = "Failed to load GLB model"; success = false; }

        OnWorkflowEnd(success, placementReady, finalMessage);
    }

    /// <summary>
    /// 工作流程結束時呼叫。
    /// </summary>
    private void OnWorkflowEnd(bool success, bool placementReady, string message)
    {
        isBusy = false; // 允許再次觸發
        SetStatus(message, !success);
        // 通知外部（例如 UI）結束忙碌狀態，可以透過 UnityEvent
        // if(success) OnGenerationSuccess?.Invoke();
        // else OnGenerationFail?.Invoke();
        // if (placementReady) OnPlacementReady?.Invoke();
    }

    // --- 3. 核心 API 協程 (與之前相同) ---
    IEnumerator CreateTaskCoroutine(object requestData, System.Action<string> callback)
    {
        string jsonPayload = "";
        try { jsonPayload = JsonUtility.ToJson(requestData); } catch (Exception e) { SetStatus($"Failed to serialize request: {e.Message}", true); callback?.Invoke(null); yield break; }
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
        using (UnityWebRequest request = new UnityWebRequest(meshyApiUrl, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    TaskCreateResponse response = JsonUtility.FromJson<TaskCreateResponse>(request.downloadHandler.text);
                    if (response != null && !string.IsNullOrEmpty(response.result)) { callback?.Invoke(response.result); }
                    else { SetStatus($"API response format error: {request.downloadHandler.text}", true); callback?.Invoke(null); }
                }
                catch (Exception e) { SetStatus($"JSON parse failed: {e.Message}", true); callback?.Invoke(null); }
            }
            else { SetStatus($"API request failed: {request.responseCode} {request.error}", true); callback?.Invoke(null); }
        }
    }

    IEnumerator PollTaskStatusCoroutine(string taskId, System.Action<TaskStatusResponse> callback)
    {
        string pollUrl = meshyTaskStatusUrlBase + taskId;
        float timeWaited = 0f;
        while (timeWaited < maxPollingTimeSeconds)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(pollUrl))
            {
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        TaskStatusResponse statusResponse = JsonUtility.FromJson<TaskStatusResponse>(request.downloadHandler.text);
                        if (statusResponse == null) throw new Exception("Failed to parse status response");
                        if (statusResponse.status == "SUCCEEDED" || statusResponse.status == "FAILED") { callback?.Invoke(statusResponse); yield break; }
                        else { SetStatus($"In progress: {statusResponse.progress}% ({statusResponse.status})"); }
                    }
                    catch (Exception e) { SetStatus($"Polling JSON parse failed: {e.Message}", true); callback?.Invoke(null); yield break; }
                }
                else { SetStatus($"Polling API request failed: {request.responseCode}", true); callback?.Invoke(null); yield break; }
            }
            yield return new WaitForSeconds(pollingIntervalSeconds);
            timeWaited += pollingIntervalSeconds;
        }
        SetStatus("Polling timeout", true);
        callback?.Invoke(null);
    }

    // --- 4. 修正 URP 的 glTFast 載入函式 ---
    IEnumerator LoadModelIntoPreview(string modelUrl)
    {
        // Make substring safe to avoid exceptions for short URLs
        string preview = modelUrl;
        if (!string.IsNullOrEmpty(modelUrl) && modelUrl.Length > 30) preview = modelUrl.Substring(0, 30);
        SetStatus($"Loading GLB: {preview}...");
        CleanupPreviousModels();
        var gltf = new GltfImport(); // 自動偵測 URP
        Task<bool> loadTask = gltf.Load(modelUrl);
        yield return new WaitUntil(() => loadTask.IsCompleted);
        if (loadTask.IsCompletedSuccessfully && loadTask.Result)
        {
            Task<bool> instantiateTask = gltf.InstantiateMainSceneAsync(previewModelContainer.transform);
            yield return new WaitUntil(() => instantiateTask.IsCompleted);
            if (instantiateTask.IsCompletedSuccessfully && instantiateTask.Result && previewModelContainer.transform.childCount > 0)
            {
                currentPreviewModelInstance = previewModelContainer.transform.GetChild(0).gameObject;
                int targetLayer = LayerMaskUtility.GetLayerIndexFromMask(previewModelLayer);
                if (targetLayer != -1) SetLayerRecursively(currentPreviewModelInstance, targetLayer);
                yield return null;
                PositionPreviewCamera(currentPreviewModelInstance);
            }
            else { SetStatus("glTF instantiation failed", true); currentPreviewModelInstance = null; }
        }
        else { SetStatus("Failed to load GLB data", true); currentPreviewModelInstance = null; }
    }

    // --- 5. 預覽視窗輔助函式 (完整) ---
    void PositionPreviewCamera(GameObject targetModel)
    {
        if (targetModel == null || previewCamera == null) return;
        Bounds bounds = CalculateBounds(targetModel);
        if (bounds.size == Vector3.zero) { bounds = new Bounds(targetModel.transform.position, Vector3.one * 0.5f); }
        float objectSize = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z, 0.1f);
        float cameraDistance = Mathf.Max(objectSize * previewPadding * 1.5f, 0.5f);
        Vector3 initialDirection = new Vector3(0, 0.5f, -1);
        Vector3 rotatedDirection = previewModelContainer.transform.rotation * initialDirection.normalized;
        Vector3 cameraPositionOffset = rotatedDirection * cameraDistance;
        Vector3 targetCenter = bounds.center;
        previewCamera.transform.position = targetCenter + cameraPositionOffset;
        previewCamera.transform.LookAt(targetCenter);
        if (previewCamera.orthographic) { previewCamera.orthographicSize = objectSize * previewPadding * 0.6f; previewCamera.nearClipPlane = 0.01f; previewCamera.farClipPlane = cameraDistance * 3.0f; }
        else { previewCamera.nearClipPlane = Mathf.Max(0.01f, cameraDistance * 0.1f); previewCamera.farClipPlane = cameraDistance * 3.0f; }
    }

    Bounds CalculateBounds(GameObject obj)
    {
        Renderer[] renderers = obj.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return new Bounds(obj.transform.position, Vector3.zero);
        Bounds bounds = new Bounds(); bool boundsInitialized = false;
        foreach (Renderer rend in renderers)
        {
            if (rend is ParticleSystemRenderer || rend is LineRenderer || rend is TrailRenderer) continue;
            if (rend is SkinnedMeshRenderer smr && smr.sharedMesh == null) continue;
            if (rend is MeshRenderer mr && mr.GetComponent<MeshFilter>()?.sharedMesh == null) continue;
            if (!boundsInitialized) { bounds = rend.bounds; if (bounds.size != Vector3.zero || bounds.center != Vector3.zero) { boundsInitialized = true; } }
            else { if (rend.bounds.size != Vector3.zero || rend.bounds.center != Vector3.zero) { bounds.Encapsulate(rend.bounds); } }
        }
        if (!boundsInitialized) { return new Bounds(obj.transform.position, Vector3.one * 0.1f); }
        return bounds;
    }

    void SetLayerRecursively(GameObject obj, int newLayer)
    {
        if (obj == null) return; try { obj.layer = newLayer; } catch (Exception ex) { Debug.LogError($"Failed to set Layer {newLayer} on {obj.name}: {ex.Message}"); return; }
        foreach (Transform child in obj.transform) { if (child != null) SetLayerRecursively(child.gameObject, newLayer); }
    }

    // --- 預覽視窗拖曳旋轉 ---
    public void OnPreviewPointerDown(PointerEventData eventData) { isDraggingPreview = true; }
    public void OnPreviewDrag(PointerEventData eventData)
    {
        if (isDraggingPreview && currentPreviewModelInstance != null && previewCamera != null)
        {
            float rotX = eventData.delta.y * previewRotationSpeed * -1;
            float rotY = eventData.delta.x * previewRotationSpeed;
            previewModelContainer.transform.Rotate(Vector3.up, rotY, Space.World);
            previewModelContainer.transform.Rotate(previewCamera.transform.right, rotX, Space.World);
        }
    }
    public void OnPreviewPointerUp(PointerEventData eventData) { isDraggingPreview = false; }
}

// 輔助工具 (來自您的原始碼)
public static class LayerMaskUtility
{
    public static int GetLayerIndexFromMask(LayerMask layerMask)
    {
        int layerMaskValue = layerMask.value;
        if (layerMaskValue == 0) return -1;
        for (int i = 0; i < 32; i++) { if ((layerMaskValue & (1 << i)) != 0) return i; }
        return -1;
    }
}