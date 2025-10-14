using Meta.XR.MRUtilityKit;
using System.Collections.Generic;
using UnityEngine;

// Requires: MRUK package in project and an OVRCameraRig in the scene.
// Attach this to an empty GameObject in the scene (or the MRUK manager GameObject).
/// Set up optional prefabs in the inspector. If none provided, 5 cube placeholders will be created.


public class MRUKPlacementManager : MonoBehaviour
{
    [Tooltip("Prefabs to place. If empty, 5 cube placeholders will be generated.")]
    public List<GameObject> modelPrefabs = new List<GameObject>();

    [Tooltip("Maximum raycast distance (meters)")]
    public float maxRayDistance = 10f;

    [Tooltip("Minimum axis value to register left-stick change")]
    public float leftStickThreshold = 0.7f;

    [Tooltip("Cooldown time (s) between left-stick changes")]
    public float stickCooldown = 0.25f;

    // If you want to manually bind a Transform to be used as the ray origin (right controller),
    // set this via inspector or at runtime using BindRightControllerTransform.
    [Tooltip("Optional: provide a Transform to use as the right-controller ray origin. If null the manager will try to find OVRCameraRig anchors.")]
    public Transform rightControllerTransform;

    [Tooltip("When true the manager will prefer using the externally bound rightControllerTransform.")]
    public bool preferManualBinding = true;

    private int _selectedIndex = 0;
    private float _lastStickTime = 0f;

    private GameObject _preview;
    private Material[] _previewMaterials;

    private LineRenderer _lineRenderer;
    private bool _placedThisPress = false;

    private OVRCameraRig _cameraRig;

    /// <summary>
    /// Current selected index. Can be set externally to change selection.
    /// </summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (modelPrefabs == null || modelPrefabs.Count == 0) return;
            _selectedIndex = Mathf.Clamp(value, 0, modelPrefabs.Count - 1);
            CreatePreview();
        }
    }

    void Awake()
    {
        // If no prefabs provided, create 5 cube placeholders
        if (modelPrefabs == null || modelPrefabs.Count == 0)
        {
            modelPrefabs = new List<GameObject>();
            for (int i = 0; i < 5; ++i)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"Placeholder_{i}";
                // small default size
                go.transform.localScale = Vector3.one * 0.25f;
                // remove collider for use as prefab template
                DestroyImmediate(go.GetComponent<Collider>());
                // mark as inactive template
                go.SetActive(false);
                modelPrefabs.Add(go);
            }
        }

        // create a line renderer for the ray
        _lineRenderer = gameObject.AddComponent<LineRenderer>();
        _lineRenderer.positionCount = 2;
        _lineRenderer.widthMultiplier = 0.005f;
        // Use a safe fallback shader name; users may replace this material in editor if using URP/HDRP
        var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
        _lineRenderer.material = new Material(shader);
        _lineRenderer.material.color = Color.green;

        // find OVRCameraRig if present
#if UNITY_2023_2_OR_NEWER
        _cameraRig = Object.FindFirstObjectByType<OVRCameraRig>();
#else
        _cameraRig = Object.FindObjectOfType<OVRCameraRig>();
#endif
    }

    void Start()
    {
        CreatePreview();
        UpdatePreviewVisibility(false);
    }

    void CreatePreview()
    {
        if (_preview != null)
            Destroy(_preview);

        if (modelPrefabs == null || modelPrefabs.Count == 0) return;

        var template = modelPrefabs[_selectedIndex];
        _preview = Instantiate(template, Vector3.zero, Quaternion.identity, transform);
        _preview.name = template.name + "_preview";
        _preview.SetActive(false);

        // remove any colliders and make materials translucent
        var renderers = _preview.GetComponentsInChildren<Renderer>();
        _previewMaterials = new Material[renderers.Length];
        for (int i = 0; i < renderers.Length; ++i)
        {
            var r = renderers[i];
            if (r.GetComponent<Collider>()) Destroy(r.GetComponent<Collider>());
            var shared = r.sharedMaterial;
            Material mat;
            if (shared != null)
                mat = new Material(shared);
            else
                mat = new Material(Shader.Find("Standard"));

            Color c = mat.HasProperty("_Color") ? mat.color : Color.white;
            c.a = 0.5f;
            if (mat.HasProperty("_Color")) mat.color = c;

            // attempt to make material transparent where supported
            if (mat.HasProperty("_Mode"))
            {
                mat.SetFloat("_Mode", 3);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.renderQueue = 3000;
            }

            r.material = mat;
            _previewMaterials[i] = mat;
        }
    }

    void Update()
    {
        HandleLeftStickSelection();
        UpdateRayAndPreview();
    }

    private void HandleLeftStickSelection()
    {
        // read left stick on left controller with fallback
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
        // if zero, try reading generic axis (some setups map differently)
        if (leftStick.sqrMagnitude < 0.0001f)
        {
            leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);
        }

        if (Time.time - _lastStickTime > stickCooldown)
        {
            if (leftStick.x > leftStickThreshold)
            {
                SelectedIndex = (SelectedIndex + 1) % modelPrefabs.Count;
                _lastStickTime = Time.time;
            }
            else if (leftStick.x < -leftStickThreshold)
            {
                SelectedIndex = (SelectedIndex - 1 + modelPrefabs.Count) % modelPrefabs.Count;
                _lastStickTime = Time.time;
            }
        }
    }

    private void UpdateRayAndPreview()
    {
        // get controller ray origin and direction from either the externally bound transform or right controller anchors
        Vector3 rayOrigin;
        Vector3 rayDirection;

        // default to center eye if camera rig missing
        Transform centerEye = (_cameraRig != null && _cameraRig.centerEyeAnchor != null) ? _cameraRig.centerEyeAnchor : Camera.main?.transform;

        // If a manual binding is provided and preferred, use it
        if (preferManualBinding && rightControllerTransform != null)
        {
            rayOrigin = rightControllerTransform.position;
            rayDirection = rightControllerTransform.forward;
        }
        else
        {
            // Prefer right controller on-controller anchor, then controller anchor, then right hand anchor pointer pose
            if (_cameraRig != null)
            {
                if (_cameraRig.rightHandOnControllerAnchor != null)
                {
                    rayOrigin = _cameraRig.rightHandOnControllerAnchor.position;
                    rayDirection = _cameraRig.rightHandOnControllerAnchor.forward;
                }
                else if (_cameraRig.rightControllerAnchor != null)
                {
                    rayOrigin = _cameraRig.rightControllerAnchor.position;
                    rayDirection = _cameraRig.rightControllerAnchor.forward;
                }
                else if (_cameraRig.rightHandAnchor != null)
                {
                    var hand = _cameraRig.rightHandAnchor.GetComponentInChildren<OVRHand>();
                    if (hand != null)
                    {
                        rayOrigin = hand.PointerPose.position;
                        rayDirection = hand.PointerPose.forward;
                    }
                    else
                    {
                        rayOrigin = centerEye != null ? centerEye.position : Vector3.zero;
                        rayDirection = centerEye != null ? centerEye.forward : Vector3.forward;
                    }
                }
                else
                {
                    rayOrigin = centerEye != null ? centerEye.position : Vector3.zero;
                    rayDirection = centerEye != null ? centerEye.forward : Vector3.forward;
                }
            }
            else
            {
                rayOrigin = centerEye != null ? centerEye.position : Vector3.zero;
                rayDirection = centerEye != null ? centerEye.forward : Vector3.forward;
            }
        }

        Ray ray = new Ray(rayOrigin, rayDirection);

        // default to not showing preview
        bool showPreview = false;
        Vector3 previewPos = Vector3.zero;
        Quaternion previewRot = Quaternion.identity;

        var room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
        RaycastHit hitInfo;
        MRUKAnchor hitAnchor = null;

        if (room != null)
        {
            // use MRUK room raycast API
            bool hit = room.Raycast(ray, maxRayDistance, out hitInfo, out hitAnchor);
            if (hit && hitAnchor != null)
            {
                // only place on floor anchors
                if ((hitAnchor.Label & MRUKAnchor.SceneLabels.FLOOR) != 0)
                {
                    showPreview = true;
                    previewPos = hitInfo.point;
                    // orient preview to face world forward and align up to surface normal
                    previewRot = Quaternion.LookRotation(Vector3.ProjectOnPlane(centerEye != null ? centerEye.forward : Vector3.forward, hitInfo.normal), hitInfo.normal);
                }
            }
        }

        // update line renderer
        if (showPreview)
        {
            _lineRenderer.enabled = true;
            _lineRenderer.SetPosition(0, rayOrigin);
            _lineRenderer.SetPosition(1, previewPos);
        }
        else
        {
            _lineRenderer.enabled = true;
            _lineRenderer.SetPosition(0, rayOrigin);
            _lineRenderer.SetPosition(1, rayOrigin + rayDirection * maxRayDistance);
        }

        // show or hide preview
        UpdatePreviewVisibility(showPreview);
        if (showPreview && _preview != null)
        {
            _preview.transform.SetPositionAndRotation(previewPos, previewRot);
        }

        // placement logic: require holding right index trigger; check RTouch first, then fallback to generic trigger reading
        float triggerVal = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
        if (triggerVal < 0.01f)
        {
            triggerVal = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger);
        }

        if (triggerVal > 0.8f && showPreview)
        {
            if (!_placedThisPress)
            {
                PlaceSelectedAt(previewPos, previewRot);
                _placedThisPress = true;
            }
        }
        else if (triggerVal < 0.2f)
        {
            _placedThisPress = false;
        }
    }

    private void UpdatePreviewVisibility(bool visible)
    {
        if (_preview == null) return;
        if (_preview.activeSelf != visible)
            _preview.SetActive(visible);
    }

    private void PlaceSelectedAt(Vector3 pos, Quaternion rot)
    {
        var template = modelPrefabs[_selectedIndex];
        var placed = Instantiate(template, pos, rot);
        placed.name = template.name + "_placed";
        // ensure placed objects have colliders enabled if needed (templates may have had collider removed)
        if (placed.GetComponent<Collider>() == null)
        {
            var col = placed.AddComponent<BoxCollider>();
            // try to size roughly
            var r = placed.GetComponentInChildren<Renderer>();
            if (r) col.size = r.bounds.size;
        }
    }

    // Public binding API --------------------------------------------------

    /// <summary>
    /// Bind an external transform to be used as the right-controller ray origin.
    /// Pass null to unbind.
    /// </summary>
    public void BindRightControllerTransform(Transform t)
    {
        rightControllerTransform = t;
    }

    /// <summary>
    /// Force placing the currently selected prefab at the given world pose.
    /// </summary>
    public void ForcePlaceAt(Vector3 worldPosition, Quaternion worldRotation)
    {
        PlaceSelectedAt(worldPosition, worldRotation);
    }

    /// <summary>
    /// Manually set the preview active state (useful if you handle visibility externally).
    /// </summary>
    public void SetPreviewActive(bool active)
    {
        UpdatePreviewVisibility(active);
    }

    /// <summary>
    /// Select next model in the list.
    /// </summary>
    public void SelectNext()
    {
        SelectedIndex = (SelectedIndex + 1) % modelPrefabs.Count;
    }

    /// <summary>
    /// Select previous model in the list.
    /// </summary>
    public void SelectPrevious()
    {
        SelectedIndex = (SelectedIndex - 1 + modelPrefabs.Count) % modelPrefabs.Count;
    }

    /// <summary>
    /// Call this to force the ray/preview update immediately (for external control).
    /// </summary>
    public void UpdateRayAndPreviewNow()
    {
        UpdateRayAndPreview();
    }
}
