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
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using GLTFast;

public class MeshyGenerator : MonoBehaviour
{
    [Header("API Configuration")]
    [SerializeField] private string apiKey = "YOUR_MESHY_API_KEY";
    [SerializeField] private string meshyApiUrl = "https://api.meshy.ai/openapi/v2/text-to-3d";
    [SerializeField] private string meshyTaskStatusUrlBase = "https://api.meshy.ai/openapi/v2/text-to-3d/";
    [SerializeField] private float pollingIntervalSeconds = 5.0f;
    [SerializeField] private float maxPollingTimeSeconds = 600f;

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

    // New: optional prefab override to use for preview instead of AI-generated GLB.
    [Header("Debug / Override")]
    [Tooltip("Optional: prefab to use as the preview model instead of loading the AI-generated GLB (useful for testing)")]
    [SerializeField] private GameObject previewPrefabOverride;

    [Header("Preview Settings")]
    [SerializeField] private float previewPadding = 1.2f;
    [SerializeField] private float previewRotationSpeed = 0.4f;

    private GameObject currentPreviewModelInstance;
    private RenderTexture previewRenderTexture;
    private bool isDraggingPreview = false;
    private bool isBusy = false;

    [System.Serializable] private class TextTo3DRequestPreview { public string mode = "preview"; public string prompt; public string art_style = "realistic"; }
    [System.Serializable] private class TextTo3DRequestRefine { public string mode = "refine"; public string preview_task_id; public bool enable_pbr = true; }
    [System.Serializable] private class TaskCreateResponse { public string result = string.Empty; public string id = string.Empty; }
    [System.Serializable] private class TextureUrl { public string base_color = string.Empty; public string metallic = string.Empty; public string normal = string.Empty; public string roughness = string.Empty; }
    [System.Serializable] private class TaskStatusResponse { public string id = string.Empty; public ModelUrls model_urls = new ModelUrls(); public int progress = 0; public string status = string.Empty; public TaskError task_error = new TaskError(); public TextureUrl[] texture_urls = null; }
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

    void AutoAssignStatusTextIfMissing()
    {
        if (statusText != null) return;
#if UNITY_2023_2_OR_NEWER
        TextMeshProUGUI found = UnityEngine.Object.FindFirstObjectByType<TextMeshProUGUI>();
#else
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

        // [FIX]: 將 RenderTextureFormat 從 DefaultHDR 改為 Default，以確保在 URP 中具有正確的深度緩衝區，解決粉紅色 Shader 錯誤。
        previewRenderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.Default);

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
            if (!statusText.gameObject.activeInHierarchy) statusText.gameObject.SetActive(true);
            statusText.transform.SetAsLastSibling();
            statusText.text = message;
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

    public void StartGenerationFromPrompt()
    {
        if (isBusy) { Debug.LogWarning("MeshyGenerator is already busy."); return; }
        string prompt = promptInput.text;
        if (string.IsNullOrWhiteSpace(prompt)) { SetStatus("Prompt field is empty", true); return; }
        isBusy = true;
        CleanupPreviousModels();
        SetStatus("Starting generation...");
        StartCoroutine(GenerateAndRefineWorkflow(prompt));
    }

    IEnumerator GenerateAndRefineWorkflow(string prompt)
    {
        SetStatus("Step 1/4: Creating preview...");
        string previewTaskId = null;
        yield return StartCoroutine(CreateTaskCoroutine(new TextTo3DRequestPreview { prompt = prompt }, result => previewTaskId = result, "preview"));
        if (string.IsNullOrEmpty(previewTaskId)) { OnWorkflowEnd(false, false, "Failed to create preview task"); yield break; }

        SetStatus($"Step 2/4: Waiting for preview model...");
        TaskStatusResponse previewStatus = null;
        yield return StartCoroutine(PollTaskStatusCoroutine(previewTaskId, result => previewStatus = result));
        if (previewStatus == null || previewStatus.status != "SUCCEEDED") { OnWorkflowEnd(false, false, $"Preview task failed: {previewStatus?.task_error?.message ?? "N/A"}"); yield break; }

        SetStatus($"Step 3/4: Refining model...");
        string refineTaskId = null;
        yield return StartCoroutine(CreateTaskCoroutine(new TextTo3DRequestRefine { preview_task_id = previewTaskId, enable_pbr = true }, result => refineTaskId = result, "refine"));
        if (string.IsNullOrEmpty(refineTaskId)) { OnWorkflowEnd(false, false, "Failed to create refine task"); yield break; }

        SetStatus($"Step 4/4: Waiting for refined model...");
        TaskStatusResponse finalStatus = null;
        yield return StartCoroutine(PollTaskStatusCoroutine(refineTaskId, result => finalStatus = result));
        if (finalStatus == null || finalStatus.status != "SUCCEEDED") { OnWorkflowEnd(false, false, $"Refine task failed: {finalStatus?.task_error?.message ?? "N/A"}"); yield break; }

        SetStatus("Loading final model...");
        string modelUrl = finalStatus.model_urls?.glb;
        if (string.IsNullOrEmpty(modelUrl)) { OnWorkflowEnd(false, false, "Refinement succeeded but GLB URL not found"); yield break; }

        yield return StartCoroutine(LoadModelIntoPreview(modelUrl, finalStatus));

        bool success = currentPreviewModelInstance != null;
        bool placementReady = false;
        string finalMessage = "";
        if (success && placementInvoker != null)
        {
            // Do not modify the inspector-assigned prefabToPlace on placementInvoker anymore.
            // Simply indicate that a model is ready for placement; Placement invoker can use its configured prefab.
            finalMessage = "Model ready for placement";
            placementReady = true;
        }
        else if (success) { finalMessage = "Model loaded (PlaceModelInvoker not set)"; }
        else { finalMessage = "Failed to load GLB model"; success = false; }

        OnWorkflowEnd(success, placementReady, finalMessage);
    }

    private void OnWorkflowEnd(bool success, bool placementReady, string message)
    {
        isBusy = false;
        SetStatus(message, !success);
    }

    IEnumerator CreateTaskCoroutine(object requestData, System.Action<string> callback, string requestName = null)
    {
        string jsonPayload = JsonUtility.ToJson(requestData);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
        Debug.Log($"CreateTask ({requestName ?? "create"}) request payload: {jsonPayload}");
        using (UnityWebRequest request = new UnityWebRequest(meshyApiUrl, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                string raw = request.downloadHandler.text;
                Debug.Log($"CreateTask ({requestName ?? "create"}) response: {raw}");
                try
                {
                    string returnedId = null;
                    try
                    {
                        var jobj = JObject.Parse(raw);
                        var rtoken = jobj["result"];
                        if (rtoken != null)
                        {
                            if (rtoken.Type == JTokenType.String) returnedId = rtoken.Value<string>();
                            else if (rtoken.Type == JTokenType.Object && rtoken["id"] != null) returnedId = rtoken["id"].Value<string>();
                        }
                        if (string.IsNullOrEmpty(returnedId) && jobj["id"] != null) returnedId = jobj["id"].Value<string>();
                    }
                    catch (Exception) { }

                    if (string.IsNullOrEmpty(returnedId))
                    {
                        try
                        {
                            TaskCreateResponse resp = JsonUtility.FromJson<TaskCreateResponse>(raw);
                            if (resp != null)
                            {
                                if (!string.IsNullOrEmpty(resp.result)) returnedId = resp.result;
                                else if (!string.IsNullOrEmpty(resp.id)) returnedId = resp.id;
                            }
                        }
                        catch { }
                    }

                    if (string.IsNullOrEmpty(returnedId))
                    {
                        var m = Regex.Match(raw, "\"id\"\\s*:\\s*\"(?<id>[^\"]+)\"");
                        if (m.Success) returnedId = m.Groups["id"].Value;
                        else
                        {
                            m = Regex.Match(raw, "\"result\"\\s*:\\s*\"(?<res>[^\"]+)\"");
                            if (m.Success) returnedId = m.Groups["res"].Value;
                        }
                    }

                    Debug.Log($"CreateTask ({requestName ?? "create"}) parsed id: {returnedId}");
                    if (!string.IsNullOrEmpty(returnedId)) callback?.Invoke(returnedId);
                    else { SetStatus($"API response format error: {raw}", true); callback?.Invoke(null); }
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

    IEnumerator LoadModelIntoPreview(string modelUrl, TaskStatusResponse taskStatus = null)
    {
        string preview = modelUrl;
        if (!string.IsNullOrEmpty(modelUrl) && modelUrl.Length > 30) preview = modelUrl.Substring(0, 30);
        SetStatus($"Loading GLB: {preview}...");
        CleanupPreviousModels();

        // If a prefab override is provided, use it instead of loading GLB from URL.
        if (previewPrefabOverride != null)
        {
            SetStatus("Using preview prefab override for preview model...");
            GameObject instance = Instantiate(previewPrefabOverride, previewModelContainer.transform);
            instance.name = previewPrefabOverride.name;
            instance.transform.localPosition = Vector3.zero;
            // Rotate preview by 180 degrees on Y
            instance.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            instance.transform.localScale = Vector3.one;
            currentPreviewModelInstance = instance;
            int targetLayer = LayerMaskUtility.GetLayerIndexFromMask(previewModelLayer);
            if (targetLayer != -1) SetLayerRecursively(currentPreviewModelInstance, targetLayer);
            // Position camera around the instantiated prefab
            yield return null;
            PositionPreviewCamera(currentPreviewModelInstance);
            yield break;
        }

        var gltf = new GltfImport();

        var importSettings = new ImportSettings
        {
            GenerateMipMaps = true,
            AnisotropicFilterLevel = 1,
            NodeNameMethod = NameImportMethod.OriginalUnique
        };

        Task<bool> loadTask = gltf.Load(modelUrl, importSettings);
        yield return new WaitUntil(() => loadTask.IsCompleted);

        if (loadTask.IsCompletedSuccessfully && loadTask.Result)
        {
            Task<bool> instantiateTask = gltf.InstantiateMainSceneAsync(previewModelContainer.transform);
            yield return new WaitUntil(() => instantiateTask.IsCompleted);

            if (instantiateTask.IsCompletedSuccessfully && instantiateTask.Result && previewModelContainer.transform.childCount > 0)
            {
                currentPreviewModelInstance = previewModelContainer.transform.GetChild(0).gameObject;
                // Rotate preview model 180 degrees around local Y
                currentPreviewModelInstance.transform.localRotation = Quaternion.Euler(
                    currentPreviewModelInstance.transform.localEulerAngles.x,
                    currentPreviewModelInstance.transform.localEulerAngles.y + 180f,
                    currentPreviewModelInstance.transform.localEulerAngles.z);
                int targetLayer = LayerMaskUtility.GetLayerIndexFromMask(previewModelLayer);
                if (targetLayer != -1) SetLayerRecursively(currentPreviewModelInstance, targetLayer);

                yield return null;
                PositionPreviewCamera(currentPreviewModelInstance);
            }
            else { SetStatus("glTF instantiation failed", true); currentPreviewModelInstance = null; }
        }
        else { SetStatus("Failed to load GLB data", true); currentPreviewModelInstance = null; }
    }

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
        if (obj == null) return;
        try { obj.layer = newLayer; } catch (Exception ex) { Debug.LogError($"Failed to set Layer {newLayer} on {obj.name}: {ex.Message}"); return; }
        foreach (Transform child in obj.transform) { if (child != null) SetLayerRecursively(child.gameObject, newLayer); }
    }

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