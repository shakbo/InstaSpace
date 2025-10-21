using Meta.XR.MRUtilityKit;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using LibCSG;

// VirtualWallTool
// - Raycasts from the right controller to place two points on MRUK floor anchors (pointA, pointB);
// - Creates a vertical wall between pointA and pointB;
// - Press 'B' (OVR Button Two) to toggle "cutout mode".
// - In cutout mode: press the right index trigger to add polygon vertices projected onto the selected wall.
// - Press 'A' (OVR Button One) to apply the polygon cutout to the currently selected wall.
// Notes:
// - Uses LibCSG-Runtime (Assets/LibCSG-Runtime) if available for accurate boolean subtraction.
public class VirtualWallTool : MonoBehaviour
{
    [Header("Settings")]
    [Tooltip("Height of generated walls (meters)")]
    public float wallHeight = 2.5f;
    [Tooltip("Thickness of generated walls (meters)")]
    public float wallThickness = 0.05f;
    [Tooltip("Material used for walls")]
    public Material wallMaterial;
    [Tooltip("Material used for preview / polygon")]
    public Material previewMaterial;
    [Tooltip("Material used for hole rim (exposed thickness) - defaults to wallMaterial if null")]
    public Material rimMaterial;
    [Tooltip("Maximum raycast distance")]
    public float maxRayDistance = 10f;
    [Tooltip("Enable debug logging for virtual wall tool")]
    public bool enableDebugLog = false;

    [Header("Placement Integration")]
    [Tooltip("Button to request placing a selected prefab using MRUKPlacementManager. If None, manual call RequestPlacementOf(prefab) must be used.")]
    public OVRInput.Button enterPlacementButton = OVRInput.Button.None;
    [Tooltip("Button to clear placement selection and return to this tool. If None, manual call ReturnFromPlacement() must be used.")]
    public OVRInput.Button clearSelectionButton = OVRInput.Button.None;

    private Transform _rightHandAnchor;
    private GameObject _previewLineGO;
    private LineRenderer _previewLine;
    private Vector3? pointA;
    private Vector3? pointB;
    private readonly List<GameObject> createdWalls = new();
    private bool cutoutMode = false;

    // polygon drawing state
    private List<Vector3> currentPolyWorld = new();
    private LineRenderer polyLineRenderer;
    private GameObject polyPreviewGO;
    private GameObject selectedWall; // the wall currently being edited

    // preview spheres
    private GameObject _previewSphere;
    private GameObject _pointASphere;
    private GameObject _pointBSphere;

    // Integration with MRUKPlacementManager
    private MRUKPlacementManager _placementManager;
    private bool _wasEnabledBeforePlacement = false;

    private void Awake()
    {
        // attempt to find OVRCameraRig in scene and get its rightHandOnControllerAnchor
        var rig = FindObjectOfType<OVRCameraRig>();
        if (rig)
        {
            _rightHandAnchor = rig.rightHandOnControllerAnchor;
        }

        // preview line
        _previewLineGO = new GameObject("VWT_PreviewLine");
        _previewLine = _previewLineGO.AddComponent<LineRenderer>();
        _previewLine.positionCount = 2;
        _previewLine.startWidth = 0.01f;
        _previewLine.endWidth = 0.01f;
        _previewLine.loop = false;
        _previewLine.material = previewMaterial;
        // start hidden by default; enable via EnableTool()
        _previewLineGO.SetActive(false);

        // polygon preview
        polyPreviewGO = new GameObject("VWT_PolyPreview");
        polyLineRenderer = polyPreviewGO.AddComponent<LineRenderer>();
        polyLineRenderer.positionCount = 0;
        polyLineRenderer.startWidth = 0.01f;
        polyLineRenderer.endWidth = 0.01f;
        polyLineRenderer.loop = true;
        polyLineRenderer.material = previewMaterial;
        polyPreviewGO.SetActive(false);

        // preview spheres
        _previewSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _previewSphere.name = "VWT_PreviewSphere";
        _previewSphere.transform.localScale = Vector3.one * 0.05f;
        _previewSphere.GetComponent<Collider>().enabled = false;
        if (previewMaterial) _previewSphere.GetComponent<Renderer>().material = previewMaterial;
        _previewSphere.SetActive(false);

        _pointASphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _pointASphere.name = "VWT_PointA";
        _pointASphere.transform.localScale = Vector3.one * 0.06f;
        _pointASphere.GetComponent<Collider>().enabled = false;
        // ensure pointA sphere is visible and uses preview material (or a default)
        if (previewMaterial)
            _pointASphere.GetComponent<Renderer>().material = previewMaterial;
        else
            _pointASphere.GetComponent<Renderer>().material = new Material(Shader.Find("Standard")) { color = Color.yellow };
        _pointASphere.SetActive(false);

        _pointBSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _pointBSphere.name = "VWT_PointB";
        _pointBSphere.transform.localScale = Vector3.one * 0.06f;
        _pointBSphere.GetComponent<Collider>().enabled = false;
        if (previewMaterial)
            _pointBSphere.GetComponent<Renderer>().material = previewMaterial;
        else
            _pointBSphere.GetComponent<Renderer>().material = new Material(Shader.Find("Standard")) { color = Color.cyan };
        _pointBSphere.SetActive(false);

        // try to find the MRUKPlacementManager in scene for integration
        _placementManager = FindObjectOfType<MRUKPlacementManager>();

        // Start disabled by default; tool must be enabled explicitly
        this.enabled = false;
    }

    private void Update()
    {
        // ensure right hand anchor present
        if (_rightHandAnchor == null)
        {
            var rig = FindObjectOfType<OVRCameraRig>();
            if (rig) _rightHandAnchor = rig.rightHandOnControllerAnchor;
        }

        // handle 'B' press to toggle cutout mode
        if (OVRInput.GetDown(OVRInput.Button.Two))
        {
            cutoutMode = !cutoutMode;
            currentPolyWorld.Clear();
            polyLineRenderer.positionCount = 0;
            polyPreviewGO.SetActive(cutoutMode);
            Debug.Log($"VirtualWallTool: cutoutMode = {cutoutMode}");
        }

        // allow quick integration controls if configured
        if (enterPlacementButton != OVRInput.Button.None && OVRInput.GetDown(enterPlacementButton))
        {
            // If user configured a button, we'll attempt to request placement of currently selectedWall as a prefab (if available)
            if (selectedWall != null)
            {
                // Use the selectedWall's GameObject as a template; user may prefer to reference an actual prefab instead.
                RequestPlacementOf(selectedWall);
            }
            else
            {
                Debug.Log("VirtualWallTool: enterPlacementButton pressed but no selected wall to send to placement manager");
            }
        }

        if (clearSelectionButton != OVRInput.Button.None && OVRInput.GetDown(clearSelectionButton))
        {
            // clear placement selection / return control
            ReturnFromPlacement();
        }

        // Raycast from right controller
        if (_rightHandAnchor == null) return;
        var ray = new Ray(_rightHandAnchor.position, _rightHandAnchor.forward);
        MRUKAnchor hitAnchor = null;
        RaycastHit hitInfo = default;
        bool hit = MRUK.Instance != null && MRUK.Instance.GetCurrentRoom() != null &&
                   MRUK.Instance.GetCurrentRoom().Raycast(ray, maxRayDistance, out hitInfo, out hitAnchor);

        // update preview line (only if preview visible)
        if (_previewLineGO != null && _previewLineGO.activeSelf && _previewLine != null)
        {
            _previewLine.SetPosition(0, ray.origin);
            _previewLine.SetPosition(1, hit ? hitInfo.point : ray.origin + ray.direction * maxRayDistance);
        }

        // show preview sphere when pointing at floor in placement mode
        if (!cutoutMode && hit && hitAnchor != null && (hitAnchor.Label & MRUKAnchor.SceneLabels.FLOOR) != 0)
        {
            _previewSphere.SetActive(true);
            _previewSphere.transform.position = hitInfo.point;
        }
        else
        {
            _previewSphere.SetActive(false);
        }

        // place points or add polygon vertices on trigger press
        float triggerVal = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
        if (triggerVal < 0.01f)
        {
            triggerVal = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger);
        }

        // detect rising edge
        bool triggerDown = triggerVal > 0.8f;

        HandleTrigger(hit, hitInfo, hitAnchor, triggerDown);

        // If in cutout mode, update polygon preview position from controller pointing ray
        if (cutoutMode && selectedWall != null)
        {
            UpdatePolyPreview(ray);
        }

        // apply polygon cutout when pressing 'A' (OVR Button One)
        if (OVRInput.GetDown(OVRInput.Button.One) && cutoutMode && currentPolyWorld.Count >= 3 && selectedWall != null)
        {
            ApplyCutoutToSelectedWall();
            currentPolyWorld.Clear();
            polyLineRenderer.positionCount = 0;
            Debug.Log("VirtualWallTool: applied cutout");
        }

        // allow selecting an existing wall by pointing and pressing secondary trigger (grip) - optional
        if (OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger))
        {
            RaycastHit physHit;
            if (Physics.Raycast(ray, out physHit, maxRayDistance))
            {
                var go = physHit.collider != null ? physHit.collider.gameObject : null;
                if (go != null && createdWalls.Contains(go))
                {
                    selectedWall = go;
                    Debug.Log($"VirtualWallTool: selected wall {selectedWall.name}");
                }
            }
        }
    }

    private bool _prevTriggerState = false;
    private void HandleTrigger(bool hit, RaycastHit hitInfo, MRUKAnchor hitAnchor, bool triggerDown)
    {
        if (triggerDown && !_prevTriggerState)
        {
            if (cutoutMode)
            {
                // add polygon vertex projected onto the selected wall face (use physics raycast for precision)
                if (selectedWall != null)
                {
                    var ray = new Ray(_rightHandAnchor.position, _rightHandAnchor.forward);
                    RaycastHit physHit;
                    if (Physics.Raycast(ray, out physHit, maxRayDistance) && physHit.collider != null && physHit.collider.gameObject == selectedWall)
                    {
                        currentPolyWorld.Add(physHit.point);
                        UpdatePolyLineRenderer();
                        Debug.Log($"VirtualWallTool: added polygon vertex (phys hit) at {physHit.point}");
                    }
                    else
                    {
                        // fallback: intersect with the nearest wall face plane
                        var data = selectedWall.GetComponent<VirtualWallData>();
                        if (data != null)
                        {
                            // compute the two face centers in world space
                            var t = selectedWall.transform;
                            var halfL = data.halfLength;
                            var forward = t.forward;
                            var candidate1 = t.position + forward * halfL;
                            var candidate2 = t.position - forward * halfL;

                            Plane plane1 = new Plane(forward, candidate1);
                            Plane plane2 = new Plane(-forward, candidate2);
                            var ray2 = new Ray(_rightHandAnchor.position, _rightHandAnchor.forward);
                            if (plane1.Raycast(ray2, out var enter1))
                            {
                                currentPolyWorld.Add(ray2.GetPoint(enter1));
                                UpdatePolyLineRenderer();
                                Debug.Log($"VirtualWallTool: added polygon vertex (plane1) at {ray2.GetPoint(enter1)}");
                            }
                            else if (plane2.Raycast(ray2, out var enter2))
                            {
                                currentPolyWorld.Add(ray2.GetPoint(enter2));
                                UpdatePolyLineRenderer();
                                Debug.Log($"VirtualWallTool: added polygon vertex (plane2) at {ray2.GetPoint(enter2)}");
                            }
                            else
                            {
                                Debug.Log("VirtualWallTool: could not project polygon vertex on wall");
                            }
                        }
                    }
                }
                else if (hit)
                {
                    // if no wall selected, add vertex on hit surface position
                    currentPolyWorld.Add(hitInfo.point);
                    UpdatePolyLineRenderer();
                    Debug.Log($"VirtualWallTool: added polygon vertex at {hitInfo.point}");
                }
                else
                {
                    Debug.Log("VirtualWallTool: no valid target to add polygon vertex");
                }
            }
            else
            {
                // regular placement mode: only place on MRUK floor anchors
                if (hit && hitAnchor != null && (hitAnchor.Label & MRUKAnchor.SceneLabels.FLOOR) != 0)
                {
                    if (!pointA.HasValue)
                    {
                        pointA = hitInfo.point;
                        _pointASphere.SetActive(true);
                        _pointASphere.transform.position = pointA.Value;
                        Debug.Log($"VirtualWallTool: set pointA = {pointA.Value}");
                    }
                    else if (!pointB.HasValue)
                    {
                        pointB = hitInfo.point;
                        _pointBSphere.SetActive(true);
                        _pointBSphere.transform.position = pointB.Value;
                        Debug.Log($"VirtualWallTool: set pointB = {pointB.Value}");
                        CreateWallBetweenPoints(pointA.Value, pointB.Value);
                        // reset for additional walls
                        pointA = null;
                        pointB = null;
                        _pointASphere.SetActive(false);
                        _pointBSphere.SetActive(false);
                    }
                }
            }
        }

        _prevTriggerState = triggerDown;
    }

    private void UpdatePolyLineRenderer()
    {
        polyLineRenderer.positionCount = currentPolyWorld.Count;
        for (int i = 0; i < currentPolyWorld.Count; ++i)
            polyLineRenderer.SetPosition(i, currentPolyWorld[i]);
    }

    private void UpdatePolyPreview(Ray ray)
    {
        // show where next vertex would be (snap to selected wall face if available)
        Vector3 nextPos = ray.origin + ray.direction * 2f;
        if (selectedWall != null)
        {
            // try to raycast against the selectedWall first
            RaycastHit physHit;
            if (Physics.Raycast(ray, out physHit, maxRayDistance) && physHit.collider != null && physHit.collider.gameObject == selectedWall)
            {
                nextPos = physHit.point;
            }
            else
            {
                var data = selectedWall.GetComponent<VirtualWallData>();
                if (data != null)
                {
                    var t = selectedWall.transform;
                    var halfL = data.halfLength;
                    var forward = t.forward;
                    var candidate = t.position + forward * halfL;
                    Plane plane = new Plane(forward, candidate);
                    if (plane.Raycast(ray, out var enter))
                    {
                        nextPos = ray.GetPoint(enter);
                    }
                    else
                    {
                        // try the back face
                        candidate = t.position - forward * halfL;
                        plane = new Plane(-forward, candidate);
                        if (plane.Raycast(ray, out enter))
                            nextPos = ray.GetPoint(enter);
                    }
                }
            }
        }

        // optionally show a small marker (reuse poly preview by adding temporary last point)
        if (currentPolyWorld.Count > 0)
        {
            polyLineRenderer.positionCount = currentPolyWorld.Count + 1;
            for (int i = 0; i < currentPolyWorld.Count; ++i)
                polyLineRenderer.SetPosition(i, currentPolyWorld[i]);
            polyLineRenderer.SetPosition(currentPolyWorld.Count, nextPos);
        }
    }

    private void CreateWallBetweenPoints(Vector3 a, Vector3 b)
    {
        var dir = b - a;
        var length = dir.magnitude;
        if (length <= 0.001f) return;
        var mid = (a + b) * 0.5f;
        var rot = Quaternion.LookRotation(dir.normalized, Vector3.up);

        // create a GameObject with generated mesh (thin box)
        var go = new GameObject($"VirtualWall_{createdWalls.Count}");
        var meshFilter = go.AddComponent<MeshFilter>();
        var meshRenderer = go.AddComponent<MeshRenderer>();
        meshRenderer.material = wallMaterial != null ? wallMaterial : new Material(Shader.Find("Standard"));
        var mesh = BuildWallMesh(length, wallHeight, wallThickness);
        meshFilter.mesh = mesh;

        var collider = go.AddComponent<MeshCollider>();
        collider.sharedMesh = mesh;

        go.transform.position = mid + Vector3.up * (wallHeight * 0.5f); // make floor-aligned (y=0 -> bottom at y)
        go.transform.rotation = rot;

        // attach data for later projection/calculations
        var data = go.AddComponent<VirtualWallData>();
        data.halfLength = length * 0.5f;
        data.halfHeight = wallHeight * 0.5f;
        data.halfThickness = wallThickness * 0.5f;

        createdWalls.Add(go);
        selectedWall = go; // select the new wall
        Debug.Log($"VirtualWallTool: created wall {go.name} length={length}");
    }

    private Mesh BuildWallMesh(float length, float height, float thickness)
    {
        // Build a simple box centered at origin, length along +Z, width thickness on X, height on Y.
        float halfW = thickness * 0.5f;
        float halfL = length * 0.5f;
        float halfH = height * 0.5f;

        var verts = new Vector3[]
        {
            // front (z +)
            new Vector3(-halfW, -halfH, halfL),
            new Vector3( halfW, -halfH, halfL),
            new Vector3( halfW,  halfH, halfL),
            new Vector3(-halfW,  halfH, halfL),

            // back (z -)
            new Vector3( halfW, -halfH, -halfL),
            new Vector3(-halfW, -halfH, -halfL),
            new Vector3(-halfW,  halfH, -halfL),
            new Vector3( halfW,  halfH, -halfL),
        };

        var tris = new int[]
        {
            // front
            0,2,1, 0,3,2,
            // right
            1,2,7, 1,7,4,
            // back
            4,7,6, 4,6,5,
            // left
            5,6,3, 5,3,0,
            // top
            3,6,7, 3,7,2,
            // bottom
            5,0,1, 5,1,4
        };

        // Ensure correct outward-facing normals by flipping triangle winding if needed.
        // Many times the mesh can appear "inside-out" depending on winding, so flip every triangle (a,b,c) -> (a,c,b)
        for (int i = 0; i < tris.Length; i += 3)
        {
            int tmp = tris[i + 1];
            tris[i + 1] = tris[i + 2];
            tris[i + 2] = tmp;
        }

        var mesh = new Mesh();
        mesh.name = "VirtualWall_Mesh";
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private void ApplyCutoutToSelectedWall()
    {
        if (selectedWall == null) return;
        var mf = selectedWall.GetComponent<MeshFilter>();
        if (mf == null) return;
        var mesh = mf.mesh;

        var data = selectedWall.GetComponent<VirtualWallData>();
        if (data == null)
        {
            Debug.LogWarning("VirtualWallTool: selected wall has no VirtualWallData");
            return;
        }

        // Convert polygon vertices into wall-local points for triangulation and mesh building
        var localPts = currentPolyWorld.Select(w => selectedWall.transform.InverseTransformPoint(w)).ToList();

        // Build cutter mesh as prism extruded along local X (thickness) covering wall height
        float extrude = Math.Max(0.1f, data.halfThickness * 2f + 0.05f);
        int n = localPts.Count;
        if (n < 3) return;

        // Build left and right cap points in local space
        var leftPts = new List<Vector3>(n);
        var rightPts = new List<Vector3>(n);
        for (int i = 0; i < n; ++i)
        {
            var lp = localPts[i];
            // clamp Y to wall vertical extents to ensure full coverage
            float y = Mathf.Clamp(lp.y, -data.halfHeight, data.halfHeight);
            leftPts.Add(new Vector3(lp.x - extrude, y, lp.z));
            rightPts.Add(new Vector3(lp.x + extrude, y, lp.z));
        }

        // Triangulate caps in (z,y) plane (2D)
        var poly2D = localPts.Select(p => new Vector2(p.z, p.y)).ToList();
        var capTris = Triangulate(poly2D);

        // Assemble cutter mesh vertices (in world space to simplify transform handling)
        var vertices = new List<Vector3>();
        // left cap
        foreach (var p in leftPts)
            vertices.Add(selectedWall.transform.TransformPoint(p));
        // right cap
        foreach (var p in rightPts)
            vertices.Add(selectedWall.transform.TransformPoint(p));

        var cutterTris = new List<int>();
        // left cap triangles (use capTris). Use the same winding as triangulation.
        for (int i = 0; i < capTris.Count; i += 3)
        {
            cutterTris.Add(capTris[i]);
            cutterTris.Add(capTris[i + 1]);
            cutterTris.Add(capTris[i + 2]);
        }
        // right cap triangles (indices offset by n). Reverse winding so normals point outward from the other side
        for (int i = 0; i < capTris.Count; i += 3)
        {
            cutterTris.Add(n + capTris[i + 2]);
            cutterTris.Add(n + capTris[i + 1]);
            cutterTris.Add(n + capTris[i]);
        }

        // sides between left and right
        for (int i = 0; i < n; ++i)
        {
            int ni = (i + 1) % n;
            int a = i;
            int b = ni;
            int c = n + ni;
            int d = n + i;
            // quad a,b,c,d -> two triangles (a,b,c) and (a,c,d)
            cutterTris.Add(a);
            cutterTris.Add(b);
            cutterTris.Add(c);

            cutterTris.Add(a);
            cutterTris.Add(c);
            cutterTris.Add(d);
        }

        var cutterMesh = new Mesh();
        cutterMesh.name = "VirtualWall_CutterMesh";
        cutterMesh.SetVertices(vertices);
        cutterMesh.SetTriangles(cutterTris, 0);
        cutterMesh.RecalculateNormals();
        cutterMesh.RecalculateBounds();

        // Build brushes for CSG
        var brushA = new CSGBrush(selectedWall);
        brushA.build_from_mesh(mesh);

        // Create temporary cutter GameObject with world-space vertices (obj at identity so TransformPoint = vertices)
        var cutterGO = new GameObject("VWT_CutterGO");
        var mfC = cutterGO.AddComponent<MeshFilter>();
        var mrC = cutterGO.AddComponent<MeshRenderer>();
        mfC.mesh = cutterMesh;

        var brushB = new CSGBrush(cutterGO);
        brushB.build_from_mesh(cutterMesh);

        var mergedBrush = new CSGBrush("merged");
        var op = new CSGBrushOperation();
        bool csgSuccess = false;
        try
        {
            op.merge_brushes(Operation.OPERATION_SUBTRACTION, brushA, brushB, ref mergedBrush, 0.00001f);

            var finalMesh = mergedBrush.getMesh();
            if (finalMesh != null)
            {
                mf.mesh = finalMesh;
                var meshCollider = selectedWall.GetComponent<MeshCollider>();
                if (meshCollider) meshCollider.sharedMesh = finalMesh;
                csgSuccess = true;
            }
            else
            {
                Debug.LogWarning("VirtualWallTool: mergedBrush produced null mesh");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"VirtualWallTool: CSG subtraction failed: {e.Message}\nFalling back to centroid-triangle removal.");
            // fallback: remove triangles whose centroids project inside polygon (previous method)
            FallbackCutout(selectedWall, currentPolyWorld);
        }
        finally
        {
            // cleanup temporary cutter
            DestroyImmediate(cutterGO);
        }

        // Create a visible rim along the hole edges to display the wall thickness properly.
        // This runs whether we used CSG or the fallback.
        CreateHoleRim(selectedWall, currentPolyWorld);
    }

    private void FallbackCutout(GameObject wall, List<Vector3> polyWorld)
    {
        var mf = wall.GetComponent<MeshFilter>();
        var mesh = mf.mesh;
        var worldToLocal = wall.transform.worldToLocalMatrix;
        var polyLocal = polyWorld.Select(w => worldToLocal.MultiplyPoint3x4(w)).ToList();
        var poly2D = polyLocal.Select(p => new Vector2(p.z, p.y)).ToList();

        var verts = mesh.vertices;
        var tris = mesh.triangles;
        var normals = mesh.normals;
        var uv = mesh.uv;

        var keptTriangles = new List<int>();
        for (int t = 0; t < tris.Length; t += 3)
        {
            var ia = tris[t];
            var ib = tris[t + 1];
            var ic = tris[t + 2];
            var ca = verts[ia];
            var cb = verts[ib];
            var cc = verts[ic];
            var centroid = (ca + cb + cc) / 3f;
            var cen2 = new Vector2(centroid.z, centroid.y);
            if (!PointInPolygon(cen2, poly2D))
            {
                keptTriangles.Add(ia);
                keptTriangles.Add(ib);
                keptTriangles.Add(ic);
            }
        }

        var newMesh = new Mesh();
        newMesh.name = mesh.name + "_cutout";
        newMesh.vertices = verts;
        newMesh.normals = normals;
        newMesh.uv = uv;
        newMesh.triangles = keptTriangles.ToArray();
        newMesh.RecalculateNormals();
        newMesh.RecalculateBounds();

        mf.mesh = newMesh;
        var meshCollider = wall.GetComponent<MeshCollider>();
        if (meshCollider) meshCollider.sharedMesh = newMesh;
    }

    // Create a rim mesh (child object) to render the exposed thickness around the polygon hole.
    private void CreateHoleRim(GameObject wall, List<Vector3> polyWorld)
    {
        if (wall == null || polyWorld == null || polyWorld.Count < 3) return;
        var data = wall.GetComponent<VirtualWallData>();
        if (data == null) return;

        // remove existing rim if present
        var existing = wall.transform.Find("VWT_HoleRim");
        if (existing != null)
        {
            DestroyImmediate(existing.gameObject);
        }

        // Convert polygon points to wall-local coordinates
        var polyLocal = polyWorld.Select(w => wall.transform.InverseTransformPoint(w)).ToList();
        int n = polyLocal.Count;
        if (n < 3) return;

        // Build two loops across the wall thickness: +halfThickness and -halfThickness
        var verts = new List<Vector3>(n * 2);
        for (int i = 0; i < n; ++i)
        {
            var p = polyLocal[i];
            float y = Mathf.Clamp(p.y, -data.halfHeight, data.halfHeight);
            float z = p.z;
            verts.Add(new Vector3(data.halfThickness, y, z)); // outer (+X)
        }
        for (int i = 0; i < n; ++i)
        {
            var p = polyLocal[i];
            float y = Mathf.Clamp(p.y, -data.halfHeight, data.halfHeight);
            float z = p.z;
            verts.Add(new Vector3(-data.halfThickness, y, z)); // inner (-X)
        }

        var tris = new List<int>();
        // build side quads between outer and inner loops (connect in same winding as polygon)
        for (int i = 0; i < n; ++i)
        {
            int ni = (i + 1) % n;
            int a = i;
            int b = ni;
            int c = n + ni;
            int d = n + i;
            // two triangles: a,b,c and a,c,d
            tris.Add(a); tris.Add(b); tris.Add(c);
            tris.Add(a); tris.Add(c); tris.Add(d);
            // Also add the opposite face so both sides of the rim are visible if needed:
            // (optional — depending on preferred look you can remove these)
            tris.Add(c); tris.Add(b); tris.Add(a);
            tris.Add(d); tris.Add(c); tris.Add(a);
        }

        var rimMesh = new Mesh();
        rimMesh.name = "VirtualWall_HoleRimMesh";
        rimMesh.SetVertices(verts);
        rimMesh.SetTriangles(tris, 0);
        rimMesh.RecalculateNormals();
        rimMesh.RecalculateBounds();

        var rimGO = new GameObject("VWT_HoleRim");
        rimGO.transform.SetParent(wall.transform, false);
        var rimMf = rimGO.AddComponent<MeshFilter>();
        var rimMr = rimGO.AddComponent<MeshRenderer>();
        rimMf.mesh = rimMesh;
        rimMr.material = rimMaterial != null ? rimMaterial : (wallMaterial != null ? wallMaterial : new Material(Shader.Find("Standard")));

        // Optionally disable shadow casting/receiving for rim to match wall visuals
        rimMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        rimMr.receiveShadows = true;
    }

    // Simple winding rule point-in-polygon for 2D points
    private bool PointInPolygon(Vector2 point, List<Vector2> polygon)
    {
        bool inside = false;
        int j = polygon.Count - 1;
        for (int i = 0; i < polygon.Count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            if (((pi.y > point.y) != (pj.y > point.y)) &&
                (point.x < (pj.x - pi.x) * (point.y - pi.y) / (pj.y - pi.y + Mathf.Epsilon) + pi.x))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    // Ear clipping triangulation for a 2D polygon (list of Vector2)
    private List<int> Triangulate(List<Vector2> poly)
    {
        var result = new List<int>();
        int n = poly.Count;
        if (n < 3) return result;

        // Work with an index list so we can reverse winding without mutating the input list
        var indices = Enumerable.Range(0, n).ToList();

        // Ensure polygon is CCW. If not, reverse the index order so orientation tests work correctly
        if (SignedArea(poly) < 0f)
        {
            indices.Reverse();
        }

        int guard = 0;
        while (indices.Count > 3 && guard++ < 1000)
        {
            bool earFound = false;
            for (int i = 0; i < indices.Count; ++i)
            {
                int i0 = indices[(i - 1 + indices.Count) % indices.Count];
                int i1 = indices[i];
                int i2 = indices[(i + 1) % indices.Count];

                var a = poly[i0];
                var b = poly[i1];
                var c = poly[i2];

                if (IsConvex(a, b, c))
                {
                    bool anyInside = false;
                    for (int j = 0; j < indices.Count; ++j)
                    {
                        int idx = indices[j];
                        if (idx == i0 || idx == i1 || idx == i2) continue;
                        if (PointInTriangle(poly[idx], a, b, c))
                        {
                            anyInside = true;
                            break;
                        }
                    }

                    if (!anyInside)
                    {
                        result.Add(i0);
                        result.Add(i1);
                        result.Add(i2);
                        indices.RemoveAt(i);
                        earFound = true;
                        break;
                    }
                }
            }

            if (!earFound)
            {
                // fallback: break to avoid infinite loop
                break;
            }
        }

        if (indices.Count == 3)
        {
            result.Add(indices[0]);
            result.Add(indices[1]);
            result.Add(indices[2]);
        }

        return result;
    }

    private float SignedArea(List<Vector2> poly)
    {
        float a = 0f;
        for (int i = 0; i < poly.Count; ++i)
        {
            var p1 = poly[i];
            var p2 = poly[(i + 1) % poly.Count];
            a += (p1.x * p2.y - p2.x * p1.y);
        }
        return a * 0.5f;
    }

    private bool IsConvex(Vector2 a, Vector2 b, Vector2 c)
    {
        return ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) > 0;
    }

    private bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        var v0 = c - a;
        var v1 = b - a;
        var v2 = p - a;

        float dot00 = Vector2.Dot(v0, v0);
        float dot01 = Vector2.Dot(v0, v1);
        float dot02 = Vector2.Dot(v0, v2);
        float dot11 = Vector2.Dot(v1, v1);
        float dot12 = Vector2.Dot(v1, v2);

        float invDenom = 1f / (dot00 * dot11 - dot01 * dot01 + Mathf.Epsilon);
        float u = (dot11 * dot02 - dot01 * dot12) * invDenom;
        float v = (dot00 * dot12 - dot01 * dot02) * invDenom;

        return (u >= 0) && (v >= 0) && (u + v < 1);
    }

    /// <summary>
    /// Enable the tool so Update runs and preview helpers are visible.
    /// Call from inspector events.
    /// </summary>
    public void EnableTool()
    {
        this.enabled = true;
        if (_previewLineGO != null) _previewLineGO.SetActive(true);
        if (polyPreviewGO != null) polyPreviewGO.SetActive(cutoutMode);
        
        if (enableDebugLog)
            Debug.Log("VirtualWallTool: Tool ENABLED - Update loop now running");
    }

    /// <summary>
    /// Disable the tool and hide preview helpers.
    /// </summary>
    public void DisableTool()
    {
        if (_previewLineGO != null) _previewLineGO.SetActive(false);
        if (polyPreviewGO != null) polyPreviewGO.SetActive(false);
        if (_previewSphere != null) _previewSphere.SetActive(false);
        if (_pointASphere != null) _pointASphere.SetActive(false);
        if (_pointBSphere != null) _pointBSphere.SetActive(false);
        this.enabled = false;
        
        if (enableDebugLog)
            Debug.Log("VirtualWallTool: Tool DISABLED - Update loop stopped");
    }

    /// <summary>
    /// Allow external code to bind a Transform to be used as the right-hand ray origin.
    /// This lets a central switcher share the same transform between tools to avoid duplicate FindObjectOfType calls.
    /// Pass null to unbind and let the tool fall back to its own lookup.
    /// </summary>
    public void BindRightHandTransform(Transform t)
    {
        _rightHandAnchor = t;
    }

    // ---------- Integration APIs ----------

    /// <summary>
    /// Enable extra functionalities for integration scenario. This will attempt to find MRUKPlacementManager in scene.
    /// </summary>
    public void EnableFunctionalities()
    {
        _placementManager = _placementManager ?? FindObjectOfType<MRUKPlacementManager>();
        if (_placementManager == null)
        {
            Debug.LogWarning("VirtualWallTool: MRUKPlacementManager not found in scene. Integration features will be unavailable.");
        }
        else if (enableDebugLog)
        {
            Debug.Log("VirtualWallTool: Found MRUKPlacementManager for integration.");
        }
    }

    /// <summary>
    /// Request switching to placement manager and set the provided prefab as the selected model to place.
    /// If placement manager is not found, this will log a warning.
    /// This disables the virtual wall tool while placement manager runs.
    /// </summary>
    public void RequestPlacementOf(GameObject prefab)
    {
        if (prefab == null)
        {
            Debug.LogWarning("VirtualWallTool: RequestPlacementOf called with null prefab");
            return;
        }

        _placementManager = _placementManager ?? FindObjectOfType<MRUKPlacementManager>();
        if (_placementManager == null)
        {
            Debug.LogWarning("VirtualWallTool: MRUKPlacementManager not found in scene. Cannot request placement.");
            return;
        }

        // store our enabled state so we can restore later
        _wasEnabledBeforePlacement = this.enabled;

        // disable this tool and hand off to placement manager
        DisableTool();

        // If the provided object is not a prefab asset but a runtime GameObject (e.g., a created wall),
        // we will try to instantiate a simple clone prefab to use for placement. If the user wants to place a specific
        // prefab asset, pass that instead.
        GameObject prefabToUse = prefab;
#if UNITY_EDITOR
        // In editor builds we could create a temporary prefab asset, but to keep runtime safe we just use the object itself as a template.
#endif
        _placementManager.EnterPlacementWithModel(prefabToUse);

        if (enableDebugLog)
            Debug.Log($"VirtualWallTool: Requested placement of {prefabToUse.name} via MRUKPlacementManager");
    }

    /// <summary>
    /// Return from placement mode: clear placement selection and re-enable this tool if it was enabled before.
    /// </summary>
    public void ReturnFromPlacement()
    {
        _placementManager = _placementManager ?? FindObjectOfType<MRUKPlacementManager>();
        if (_placementManager != null)
        {
            _placementManager.ClearSelection();
        }

        if (_wasEnabledBeforePlacement)
        {
            EnableTool();
        }

        if (enableDebugLog)
            Debug.Log("VirtualWallTool: Returned from placement and restored tool state");
    }
}

// small component to store wall geometry
public class VirtualWallData : MonoBehaviour
{
    public float halfLength;
    public float halfHeight;
    public float halfThickness;
}