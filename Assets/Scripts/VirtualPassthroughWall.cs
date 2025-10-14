using Meta.XR.MRUtilityKit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// Rewritten VirtualPassthroughWall
// Features:
// - Right-controller raycasting to place wall endpoints on MRUK-scanned floor
// - Create wall between two placed points
// - Toggle to hole-cutting mode (B), draw polygon on wall using right-controller trigger
// - Finalize polygon with A to create passthrough polygon. If LibCSG is available at runtime,
//   this will attempt to subtract the polygon from the wall mesh via reflection. Otherwise a
//   visual passthrough polygon child is created.

public class VirtualPassthroughWall : MonoBehaviour
{
    [Tooltip("Optional: set a GameObject prefab for the wall. If null a simple quad will be created.")]
    public GameObject wallPrefab;

    [Tooltip("When true, created window meshes will be hidden (no renderer). Useful when you only want the passthrough to show through the hole.")]
    public bool hideWindowRenderer = true;

    [Header("Input")]
    [Tooltip("Assign a Transform that represents the right controller pose. If null, OVRCameraRig anchors will be used.")]
    public Transform rightControllerTransform;

    [Tooltip("If true, and a manual rightControllerTransform is provided, it will be preferred over auto-detection.")]
    public bool preferManualBinding = true;

    [Tooltip("Line renderer to visualize the controller ray. If null, one will be created at runtime.")]
    public LineRenderer debugLineRenderer;

    [Tooltip("Maximum ray distance in meters.")]
    public float maxRayDistance = 10f;

    [Tooltip("Wall height (meters) when created on the floor.")]
    public float wallHeight = 2.5f;

    [Tooltip("Material for created wall if no prefab or material present on prefab.")]
    public Material wallMaterial;

    private enum Mode { WallCreation, HoleCutting }
    private Mode _mode = Mode.WallCreation;

    // working state
    private OVRCameraRig _cameraRig;
    private object _envRaycastManagerInstance;
    private MethodInfo[] _envRaycastMethods;

    private readonly List<Vector2> _currentPolygon = new List<Vector2>();

    private bool _hasWallStart = false;
    private Vector3 _wallStartPoint;

    private GameObject _currentWall;
    private readonly List<GameObject> _holes = new List<GameObject>();

    // runtime debug line
    private LineRenderer _runtimeLine;
    private Color _rayColor = Color.cyan;
    private Color _hitColor = Color.green;

    void Awake()
    {
#if UNITY_2023_2_OR_NEWER
        _cameraRig = UnityEngine.Object.FindFirstObjectByType<OVRCameraRig>();
#else
        _cameraRig = UnityEngine.Object.FindObjectOfType<OVRCameraRig>();
#endif
        CacheEnvironmentRaycastManager();
        EnsureLineRenderer();

        // If preferManualBinding is false and no manual binding provided, attempt to auto-bind right controller transform
        if (rightControllerTransform == null && _cameraRig != null)
        {
            TryAutoBindControllerTransform();
        }
    }

    void Update()
    {
        HandleVRInput();
    }

    private void EnsureLineRenderer()
    {
        if (debugLineRenderer != null)
        {
            _runtimeLine = debugLineRenderer;
            return;
        }

        if (_runtimeLine == null)
        {
            var go = new GameObject("VPW_DebugLine");
            go.transform.SetParent(transform, false);
            _runtimeLine = go.AddComponent<LineRenderer>();
            _runtimeLine.positionCount = 2;
            _runtimeLine.startWidth = 0.01f;
            _runtimeLine.endWidth = 0.01f;
            _runtimeLine.material = new Material(Shader.Find("Unlit/Color"));
            _runtimeLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _runtimeLine.receiveShadows = false;
        }
    }

    private void CacheEnvironmentRaycastManager()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var asm in assemblies)
        {
            try
            {
                var type = asm.GetTypes().FirstOrDefault(t => string.Equals(t.Name, "EnvironmentRaycastManager", StringComparison.OrdinalIgnoreCase));
                if (type == null) continue;
                var all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                foreach (var mb in all)
                {
                    if (mb == null) continue;
                    if (mb.GetType() == type || type.IsAssignableFrom(mb.GetType()))
                    {
                        _envRaycastManagerInstance = mb;
                        _envRaycastMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => m.Name.IndexOf("Raycast", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                        return;
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Attempt to find common right-controller anchor transforms under OVRCameraRig when explicit properties may be null.
    /// This is a best-effort to support variations in rig setups.
    /// </summary>
    private void TryAutoBindControllerTransform()
    {
        if (_cameraRig == null) return;

        // Try common property anchors first
        var candidateProps = new Transform[] {
            _cameraRig.rightControllerInHandAnchor,
            _cameraRig.rightHandOnControllerAnchor,
            _cameraRig.rightControllerAnchor,
            _cameraRig.rightHandAnchorDetached,
            _cameraRig.rightHandAnchor
        };

        foreach (var t in candidateProps)
        {
            if (t != null)
            {
                rightControllerTransform = t;
                Debug.Log($"VirtualPassthroughWall: Auto-bound rightControllerTransform to {_cameraRig.name}/{t.name}");
                return;
            }
        }

        // If none found, try matching by name in children (case-insensitive)
        var namesToFind = new string[] { "RightHandOnControllerAnchor", "RightControllerInHandAnchor", "RightControllerAnchor", "RightHandAnchorDetached", "RightHandAnchor", "RightController" };
        var children = _cameraRig.GetComponentsInChildren<Transform>(true);
        foreach (var n in namesToFind)
        {
            var found = children.FirstOrDefault(c => c.name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
            if (found != null)
            {
                rightControllerTransform = found;
                Debug.Log($"VirtualPassthroughWall: Auto-bound rightControllerTransform by name to {_cameraRig.name}/{found.name}");
                return;
            }
        }
    }

    // Returns true if environment raycast (MRUK or EnvironmentRaycastManager) hit something
    private bool TryEnvironmentRaycast(Ray ray, out Vector3 hitPoint, out object hitAnchor, out Vector3 hitNormal)
    {
        hitPoint = Vector3.zero; hitAnchor = null; hitNormal = Vector3.up;

        // Try EnvironmentRaycastManager first (via reflection)
        if (_envRaycastManagerInstance != null && _envRaycastMethods != null)
        {
            foreach (var m in _envRaycastMethods)
            {
                var parms = m.GetParameters();
                object[] args = new object[parms.Length];
                bool canCall = false;
                if (parms.Length >= 2 && parms[0].ParameterType == typeof(Ray) && parms[1].ParameterType == typeof(float))
                {
                    args[0] = ray; args[1] = maxRayDistance;
                    for (int i = 2; i < parms.Length; ++i) args[i] = parms[i].ParameterType.IsValueType ? Activator.CreateInstance(parms[i].ParameterType) : null;
                    canCall = true;
                }
                else if (parms.Length >= 3 && parms[0].ParameterType == typeof(Vector3) && parms[1].ParameterType == typeof(Vector3) && parms[2].ParameterType == typeof(float))
                {
                    args[0] = ray.origin; args[1] = ray.direction; args[2] = maxRayDistance;
                    for (int i = 3; i < parms.Length; ++i) args[i] = parms[i].ParameterType.IsValueType ? Activator.CreateInstance(parms[i].ParameterType) : null;
                    canCall = true;
                }

                if (!canCall) continue;
                try
                {
                    var ret = m.Invoke(_envRaycastManagerInstance, args);
                    if (m.ReturnType == typeof(bool) && ret is bool b && b)
                    {
                        for (int i = 0; i < args.Length; ++i)
                        {
                            if (args[i] == null) continue;
                            var at = args[i].GetType();
                            if (at == typeof(RaycastHit))
                            {
                                var rh = (RaycastHit)args[i]; hitPoint = rh.point; hitNormal = rh.normal; return true;
                            }
                            var propPoint = at.GetProperty("point") ?? at.GetProperty("Point");
                            var propNormal = at.GetProperty("normal") ?? at.GetProperty("Normal");
                            if (propPoint != null)
                            {
                                var pVal = propPoint.GetValue(args[i]);
                                if (pVal is Vector3 v)
                                {
                                    hitPoint = v;
                                    if (propNormal != null)
                                    {
                                        var nVal = propNormal.GetValue(args[i]);
                                        if (nVal is Vector3 nv) hitNormal = nv;
                                    }
                                    // try to capture anchor-like object
                                    for (int j = 0; j < args.Length; ++j)
                                    {
                                        if (args[j] == null) continue;
                                        var jt = args[j].GetType();
                                        if (jt.Name.IndexOf("Anchor", StringComparison.OrdinalIgnoreCase) >= 0) { hitAnchor = args[j]; break; }
                                    }
                                    return true;
                                }
                            }
                        }
                    }
                    else if (m.ReturnType != typeof(void) && ret != null)
                    {
                        var rt = ret.GetType();
                        var propP = rt.GetProperty("point") ?? rt.GetProperty("Point") ?? rt.GetProperty("hitPoint");
                        if (propP != null)
                        {
                            var pVal = propP.GetValue(ret);
                            if (pVal is Vector3 v) { hitPoint = v; var propN = rt.GetProperty("normal") ?? rt.GetProperty("Normal"); if (propN != null) { var nVal = propN.GetValue(ret); if (nVal is Vector3 nv) hitNormal = nv; } return true; }
                        }
                    }
                }
                catch { }
            }
        }

        // Try MRUK room if available
        var room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
        if (room != null)
        {
            if (room.Raycast(ray, maxRayDistance, out var hitInfo, out var hitAnchor2))
            {
                hitPoint = hitInfo.point; hitNormal = hitInfo.normal; hitAnchor = hitAnchor2; return true;
            }
        }

        return false;
    }

    private void HandleVRInput()
    {
        // Determine controller pose
        Vector3 origin = Vector3.zero;
        Vector3 forward = Vector3.forward;
        bool haveRightPose = false;

        // If manual binding exists and is preferred, use it
        if (preferManualBinding && rightControllerTransform != null)
        {
            origin = rightControllerTransform.position;
            forward = rightControllerTransform.forward;
            haveRightPose = true;
        }
        else
        {
            // attempt to auto bind if none provided
            if (rightControllerTransform == null && _cameraRig != null)
            {
                TryAutoBindControllerTransform();
            }

            if (rightControllerTransform != null)
            {
                origin = rightControllerTransform.position;
                forward = rightControllerTransform.forward;
                haveRightPose = true;
            }
            else if (_cameraRig != null)
            {
                // prefer anchors that represent the controller (check more options and order)
                var anchors = new Transform[] {
                    _cameraRig.rightControllerInHandAnchor,
                    _cameraRig.rightHandOnControllerAnchor,
                    _cameraRig.rightControllerAnchor,
                    _cameraRig.rightHandAnchorDetached,
                    _cameraRig.rightHandAnchor
                };
                var chosen = anchors.FirstOrDefault(a => a != null);
                if (chosen != null)
                {
                    origin = chosen.position; forward = chosen.forward; haveRightPose = true;
                }
            }
        }

        // fallback to camera origin but mark as not the real controller (so input won't place points)
        if (!haveRightPose && Camera.main != null)
        {
            origin = Camera.main.transform.position; forward = Camera.main.transform.forward;
        }

        // normalize forward defensively
        if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
        forward.Normalize();

        var ray = new Ray(origin, forward);

        // Update line renderer visuals
        if (_runtimeLine != null)
        {
            _runtimeLine.SetPosition(0, origin);

            // Default end
            var endPos = origin + forward * maxRayDistance;

            if (Physics.Raycast(ray, out var rh, maxRayDistance))
            {
                _runtimeLine.startColor = _hitColor; _runtimeLine.endColor = _hitColor;
                endPos = rh.point;
            }
            else
            {
                // check environment hit
                if (TryEnvironmentRaycast(ray, out var ePt, out var eAnchor, out var eNormal))
                {
                    _runtimeLine.startColor = _hitColor; _runtimeLine.endColor = _hitColor;
                    endPos = ePt;
                }
                else
                {
                    _runtimeLine.startColor = _rayColor; _runtimeLine.endColor = _rayColor;
                }
            }

            _runtimeLine.SetPosition(1, endPos);
        }

        // Read controller buttons (OVRInput if available, else keyboard)
        bool aDown = Input.GetKeyDown(KeyCode.A);
        bool bDown = Input.GetKeyDown(KeyCode.B);
        bool triggerDown = Input.GetMouseButtonDown(0);

        // Try OVRInput via reflection if available to get controller inputs on device
        try
        {
            var ovrType = Type.GetType("OVRInput");
            if (ovrType != null)
            {
                var getDown = ovrType.GetMethod("GetDown", new Type[] { typeof(object), typeof(object) }) ?? ovrType.GetMethod("GetDown", new Type[] { typeof(Enum) });
                if (getDown != null)
                {
                    // PrimaryIndexTrigger on RTouch -> if present
                    // Best-effort: call static GetDown with enum args via reflection
                    var btnType = Type.GetType("OVRInput+Button");
                    var ctrlType = Type.GetType("OVRInput+Controller");
                    if (btnType != null && ctrlType != null)
                    {
                        object btnOne = Enum.Parse(btnType, "One");
                        object btnTwo = Enum.Parse(btnType, "Two");
                        object btnTrigger = Enum.Parse(btnType, "PrimaryIndexTrigger");
                        object rtouch = Enum.Parse(ctrlType, "RTouch");

                        // call for A (Button One)
                        var valA = getDown.Invoke(null, new object[] { btnOne, rtouch });
                        if (valA is bool && (bool)valA) aDown = true;

                        var valB = getDown.Invoke(null, new object[] { btnTwo, rtouch });
                        if (valB is bool && (bool)valB) bDown = true;

                        var valT = getDown.Invoke(null, new object[] { btnTrigger, rtouch });
                        if (valT is bool && (bool)valT) triggerDown = true;
                    }
                }
            }
        }
        catch { /* ignore OVR reflection errors */ }

        // Toggle mode
        if (bDown)
        {
            _mode = _mode == Mode.WallCreation ? Mode.HoleCutting : Mode.WallCreation;
            _currentPolygon.Clear();
            _hasWallStart = false;
            Debug.Log($"VirtualPassthroughWall: Mode={_mode}");
            return; // consume toggle input this frame
        }

        // Handle A in hole cutting: finalize polygon
        if (aDown && _mode == Mode.HoleCutting)
        {
            if (_currentPolygon.Count >= 3 && _currentWall != null)
            {
                CutHolePolygon(_currentWall, new List<Vector2>(_currentPolygon));
                _currentPolygon.Clear();
            }
            else
            {
                Debug.Log("Need at least 3 points and a wall selected to cut a hole.");
            }
            return;
        }

        // Handle trigger: context sensitive
        if (triggerDown)
        {
            if (!haveRightPose)
            {
                Debug.Log("Right controller not available; trigger ignored.");
                return;
            }

            if (_mode == Mode.WallCreation)
            {
                // attempt environment raycast to floor
                if (TryEnvironmentRaycast(ray, out var envPt, out var envAnchor, out var envNormal))
                {
                    // accept if normal is roughly up or anchor indicates floor
                    bool isFloor = Vector3.Dot(envNormal, Vector3.up) > 0.5f;
                    if (!isFloor && envAnchor != null)
                    {
                        try
                        {
                            var prop = envAnchor.GetType().GetProperty("Label");
                            if (prop != null)
                            {
                                var val = prop.GetValue(envAnchor);
                                if (val != null && val.ToString().IndexOf("FLOOR", StringComparison.OrdinalIgnoreCase) >= 0) isFloor = true;
                            }
                        }
                        catch { }
                    }

                    if (isFloor)
                    {
                        if (!_hasWallStart)
                        {
                            _wallStartPoint = envPt; _hasWallStart = true; Debug.Log($"Wall start set at {_wallStartPoint}");
                        }
                        else
                        {
                            CreateWallBetween(_wallStartPoint, envPt);
                            _hasWallStart = false;
                        }
                        return;
                    }
                }

                // fallback to MRUK room ray (TryEnvironmentRaycast already tries MRUK) and physics
                if (Physics.Raycast(ray, out var physHit, maxRayDistance))
                {
                    if (Vector3.Dot(physHit.normal, Vector3.up) > 0.5f)
                    {
                        if (!_hasWallStart)
                        {
                            _wallStartPoint = physHit.point; _hasWallStart = true; Debug.Log($"Wall start set at {_wallStartPoint}");
                        }
                        else
                        {
                            CreateWallBetween(_wallStartPoint, physHit.point);
                            _hasWallStart = false;
                        }
                        return;
                    }
                }

                Debug.Log("Aim at scanned floor to place wall points.");
            }
            else // HoleCutting mode: add polygon point on wall
            {
                // cast against current wall collider/mesh
                if (Physics.Raycast(ray, out var hit, maxRayDistance))
                {
                    var go = hit.collider.gameObject;
                    // check if hit is on current wall (or one of created walls)
                    if (go == _currentWall || (_currentWall != null && hit.collider.transform.IsChildOf(_currentWall.transform)))
                    {
                        // convert hit point into wall local XY (assuming wall plane is local Z=0)
                        var local = _currentWall.transform.InverseTransformPoint(hit.point);
                        _currentPolygon.Add(new Vector2(local.x, local.y));
                        Debug.Log($"Added polygon point {local.x},{local.y}");
                    }
                    else
                    {
                        // If hit another wall: switch current wall to that wall
                        if (IsWallGameObject(go))
                        {
                            _currentWall = GetWallRoot(go);
                            Debug.Log($"Selected wall {_currentWall.name} for hole cutting.");
                        }
                        else
                        {
                            Debug.Log("Aim at the wall surface to add polygon points.");
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Bind an external transform to be used as the right-controller ray origin. Pass null to unbind.
    /// </summary>
    public void BindRightControllerTransform(Transform t)
    {
        rightControllerTransform = t;
        Debug.Log($"VirtualPassthroughWall: Bound rightControllerTransform to {(t!=null?t.name:"null")} (preferManualBinding={preferManualBinding})");
    }

    private bool IsWallGameObject(GameObject g)
    {
        if (g == null) return false;
        return g.name.StartsWith("VirtualWall") || g.GetComponent<MeshFilter>() != null;
    }

    private GameObject GetWallRoot(GameObject g)
    {
        // climb to top-level created wall by name
        var t = g.transform;
        while (t.parent != null)
        {
            if (t.name.StartsWith("VirtualWall")) return t.gameObject;
            t = t.parent;
        }
        return g;
    }

    private void CreateWallBetween(Vector3 start, Vector3 end)
    {
        var startXZ = new Vector3(start.x, 0f, start.z);
        var endXZ = new Vector3(end.x, 0f, end.z);
        float length = Vector3.Distance(startXZ, endXZ);
        if (length < 0.01f) { Debug.LogWarning("Wall length too small"); return; }

        var centerXZ = (startXZ + endXZ) * 0.5f;
        var go = wallPrefab != null ? Instantiate(wallPrefab) : new GameObject("VirtualWall");
        go.name = go.name.StartsWith("VirtualWall") ? go.name : "VirtualWall";
        go.transform.position = new Vector3(centerXZ.x, start.y + wallHeight * 0.5f, centerXZ.z);

        var dir = (endXZ - startXZ).normalized;
        var normal = Vector3.Cross(Vector3.up, dir).normalized;
        go.transform.rotation = Quaternion.LookRotation(normal, Vector3.up);

        // ensure mesh
        var mf = go.GetComponent<MeshFilter>();
        if (mf == null)
        {
            mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = CreateQuadMesh();
        }

        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) mr = go.AddComponent<MeshRenderer>();
        if (mr.sharedMaterial == null) mr.sharedMaterial = wallMaterial != null ? wallMaterial : new Material(Shader.Find("Standard"));

        go.transform.localScale = new Vector3(length, wallHeight, 1f);

        var mc = go.GetComponent<MeshCollider>();
        if (mc == null) mc = go.AddComponent<MeshCollider>();
        mc.sharedMesh = mf.sharedMesh;

        _currentWall = go;
        Debug.Log($"Created wall length={length} height={wallHeight}");
    }

    // Creates a child polygon mesh to visualize the hole; attempts CSG subtraction if LibCSG available
    private void CutHolePolygon(GameObject wall, List<Vector2> polygonLocal)
    {
        if (wall == null) { Debug.LogError("No wall selected for cutting."); return; }
        if (polygonLocal == null || polygonLocal.Count < 3) { Debug.LogError("Polygon must have at least 3 points."); return; }

        // Build polygon mesh in wall local space (Z=0 plane)
        var centroid = ComputeCentroid(polygonLocal);
        var verts2D = polygonLocal.Select(p => p - centroid).ToList();
        var verts3 = verts2D.Select(p => new Vector3(p.x, p.y, 0f)).ToArray();
        var tris = Triangulate(verts2D);

        var polyGO = new GameObject($"PassthroughPolygon_{_holes.Count}");
        polyGO.transform.SetParent(wall.transform, false);
        polyGO.transform.localPosition = new Vector3(centroid.x, centroid.y, 0f);
        polyGO.transform.localRotation = Quaternion.identity;

        var mesh = new Mesh(); mesh.name = "PassthroughPolygon";
        mesh.vertices = verts3; mesh.triangles = tris.ToArray(); mesh.RecalculateNormals(); mesh.RecalculateBounds();

        var mf = polyGO.AddComponent<MeshFilter>(); mf.sharedMesh = mesh;
        var mr = polyGO.AddComponent<MeshRenderer>(); mr.enabled = !hideWindowRenderer;
        if (!hideWindowRenderer) mr.sharedMaterial = new Material(Shader.Find("Standard")) { color = Color.black };

        _holes.Add(polyGO);

        // Try to perform CSG subtraction via reflection if a CSG runtime exists
        bool csgApplied = TryApplyCSGSubtraction(wall, polyGO);
        if (!csgApplied)
        {
            Debug.LogWarning("LibCSG (or compatible CSG runtime) not found. Created visual passthrough polygon only. Install LibCSG-Runtime and re-run to perform mesh subtraction.");
        }
    }

    private bool TryApplyCSGSubtraction(GameObject wall, GameObject polyGO)
    {
        // Best-effort: look for a type named CSGModel or CSG in loaded assemblies and try to call a Subtract method.
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var asm in assemblies)
        {
            try
            {
                var csgType = asm.GetTypes().FirstOrDefault(t => t.Name.IndexOf("CSGModel", StringComparison.OrdinalIgnoreCase) >= 0 || t.Name.IndexOf("CSG", StringComparison.OrdinalIgnoreCase) == 0);
                if (csgType == null) continue;

                // Look for a static helper with Subtract(GameObject subject, GameObject tool) or similar
                var methods = csgType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
                foreach (var m in methods)
                {
                    if (m.Name.IndexOf("Subtract", StringComparison.OrdinalIgnoreCase) >= 0 && m.GetParameters().Length >= 2)
                    {
                        try
                        {
                            var isStatic = m.IsStatic;
                            object instance = null;
                            if (!isStatic)
                            {
                                // try to find an existing component of that type on wall or scene
                                var comp = wall.GetComponent(csgType) ?? UnityEngine.Object.FindObjectOfType(csgType) as Component;
                                if (comp == null) continue;
                                instance = comp;
                            }

                            // call method: passing wall and polyGO. Signature may vary; best-effort
                            var parameters = m.GetParameters();
                            var args = new object[parameters.Length];
                            args[0] = wall; args[1] = polyGO;
                            m.Invoke(instance, args);
                            Debug.Log("Applied CSG subtraction via runtime API.");
                            return true;
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"CSG subtraction call failed: {ex.Message}");
                            // continue searching
                        }
                    }
                }
            }
            catch { }
        }
        return false;
    }

    // Helpers: triangulation and centroid (same as original)
    private static Vector2 ComputeCentroid(List<Vector2> pts)
    {
        float x = 0f, y = 0f; for (int i = 0; i < pts.Count; i++) { x += pts[i].x; y += pts[i].y; } return new Vector2(x / pts.Count, y / pts.Count);
    }

    private static List<int> Triangulate(List<Vector2> points)
    {
        var indices = new List<int>();
        int n = points.Count; if (n < 3) return indices;
        var V = new List<int>(n);
        if (SignedArea(points) > 0f) for (int v = 0; v < n; v++) V.Add(v); else for (int v = n - 1; v >= 0; v--) V.Add(v);
        int count = 0;
        while (V.Count > 3)
        {
            bool earFound = false;
            for (int i = 0; i < V.Count; i++)
            {
                int prev = V[(i - 1 + V.Count) % V.Count]; int curr = V[i]; int next = V[(i + 1) % V.Count];
                var a = points[prev]; var b = points[curr]; var c = points[next];
                if (IsTriangleConvex(a, b, c))
                {
                    bool hasPointInside = false;
                    for (int j = 0; j < V.Count; j++)
                    {
                        int vi = V[j]; if (vi == prev || vi == curr || vi == next) continue;
                        if (PointInTriangle(points[vi], a, b, c)) { hasPointInside = true; break; }
                    }
                    if (!hasPointInside) { indices.Add(prev); indices.Add(curr); indices.Add(next); V.RemoveAt(i); earFound = true; break; }
                }
            }
            if (!earFound) break; if (++count > 10000) break;
        }
        if (V.Count == 3) { indices.Add(V[0]); indices.Add(V[1]); indices.Add(V[2]); }
        return indices;
    }

    private static float SignedArea(List<Vector2> pts)
    {
        float area = 0f; for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++) area += (pts[j].x + pts[i].x) * (pts[j].y - pts[i].y); return area * 0.5f;
    }

    private static bool IsTriangleConvex(Vector2 a, Vector2 b, Vector2 c)
    {
        var ab = b - a; var bc = c - b; return (ab.x * bc.y - ab.y * bc.x) > 0f;
    }

    private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        var v0 = c - a; var v1 = b - a; var v2 = p - a; float dot00 = Vector2.Dot(v0, v0); float dot01 = Vector2.Dot(v0, v1); float dot02 = Vector2.Dot(v0, v2); float dot11 = Vector2.Dot(v1, v1); float dot12 = Vector2.Dot(v1, v2); float denom = (dot00 * dot11 - dot01 * dot01); if (Mathf.Abs(denom) < Mathf.Epsilon) return false; float invDenom = 1f / denom; float u = (dot11 * dot02 - dot01 * dot12) * invDenom; float v = (dot00 * dot12 - dot01 * dot02) * invDenom; return (u >= 0) && (v >= 0) && (u + v < 1);
    }

    private static Mesh CreateQuadMesh()
    {
        var mesh = new Mesh(); mesh.name = "PassthroughQuad";
        mesh.vertices = new Vector3[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) };
        mesh.uv = new Vector2[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        mesh.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
        mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
    }

    public IReadOnlyList<GameObject> Holes => _holes.AsReadOnly();
}