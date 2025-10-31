using LearnXR.Core.Utilities;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

public class MeasurementTool: MonoBehaviour
{
    [Range(0.005f, 0.05f)]
    [SerializeField] private float tapeWidth = 0.01f;
    [SerializeField] private OVRInput.Button tapeActionButton;
    [SerializeField] private Material tapeMaterial;
    [SerializeField] private GameObject measurementIntoPrefab;
    [SerializeField] private Vector3 measurementInfoControllerOffset = new (0, 0.045f, 0);
    [SerializeField] private string measurementInfoFormat = "<mark=#0000005A padding=\"20,20,10,10\"><color=white>{0}</color></mark>";
    [SerializeField] private Transform leftControllerTapePointer;
    [SerializeField] private Transform rightControllerTapePointer;

    [Tooltip("Enable debug logging for measurement tool")]
    [SerializeField] private bool enableDebugLog = false;

    private List<MeasuringTape> savedTapeLines = new();
    private TextMeshPro lastMeasurementInfo;
    private LineRenderer lastTapeLineRenderer;
    private OVRInput.Controller? currentController;
    private OVRCameraRig cameraRig;

    private void Awake()
    {
        cameraRig = FindFirstObjectByType<OVRCameraRig>();
        this.enabled = false;
    }

    private void Update()
    {
        HandleControllerAction(OVRInput.Controller.LTouch, leftControllerTapePointer);
        HandleControllerAction(OVRInput.Controller.RTouch, rightControllerTapePointer);
    }

    private void HandleControllerAction(OVRInput.Controller controller, Transform tapePointer)
    {
        if (currentController != controller && currentController != null) return;
        if (OVRInput.GetDown(tapeActionButton, controller))
        {
            currentController = controller;
            HandleDownAction(tapePointer);
        }
        if (OVRInput.Get(tapeActionButton, controller))
        {
            HandleHoldAction(tapePointer);
        }
        if (OVRInput.GetUp(tapeActionButton, controller))
        {
            currentController = null;
            HandleUpAction(tapePointer);
        }
    }

    private void HandleDownAction(Transform tapePointer)
    {
        CreateNewTapeLine(tapePointer.position);
        AttachAndDetachMeasurementInfo(tapePointer);
    }

    private void HandleHoldAction(Transform tapePointer)
    {
        lastTapeLineRenderer.SetPosition(1, tapePointer.position);
        CalculateMeasurements();
        AttachAndDetachMeasurementInfo(tapePointer);
    }

    private void HandleUpAction(Transform tapePointer)
    {
        AttachAndDetachMeasurementInfo(tapePointer, false);
    }

    private void CreateNewTapeLine(Vector3 startPosition)
    {
        var newTapeLine = new GameObject($"TapeLine_{savedTapeLines.Count}", typeof(LineRenderer));

        lastTapeLineRenderer = newTapeLine.GetComponent<LineRenderer>();
        lastTapeLineRenderer.positionCount = 2;
        lastTapeLineRenderer.startWidth = tapeWidth;
        lastTapeLineRenderer.endWidth = tapeWidth;
        lastTapeLineRenderer.material = tapeMaterial;
        lastTapeLineRenderer.SetPosition(0, startPosition);

        lastMeasurementInfo = Instantiate(measurementIntoPrefab, Vector3.zero, Quaternion.identity).GetComponent<TextMeshPro>();
        lastMeasurementInfo.GetComponent<BillboardAlignment>().AttachTo(cameraRig.centerEyeAnchor);
        lastMeasurementInfo.gameObject.SetActive(false);
        savedTapeLines.Add(new MeasuringTape
        {
            TapeLine = newTapeLine,
            TapeInfo = lastMeasurementInfo
        });
    }

    private void AttachAndDetachMeasurementInfo(Transform tapePointer, bool attachToController = true)
    {
        if (attachToController)
        {
            lastMeasurementInfo.gameObject.SetActive(true);
            lastMeasurementInfo.transform.SetParent(tapePointer.transform.parent, false);
            lastMeasurementInfo.transform.localPosition = measurementInfoControllerOffset;
            lastMeasurementInfo.transform.localRotation = Quaternion.identity;
        }
        else
        {
            lastMeasurementInfo.transform.SetParent(null);

            Vector3 lineMidPoint = (lastTapeLineRenderer.GetPosition(0) + lastTapeLineRenderer.GetPosition(1)) / 2.0f;

            lastMeasurementInfo.transform.position = lineMidPoint;
        }
    }

    private void CalculateMeasurements()
    {
        var distance = Vector3.Distance(lastTapeLineRenderer.GetPosition(0), lastTapeLineRenderer.GetPosition(1));
        var centimeters = MeasuringTape.MetersToCentimeters(distance);
        var lastLine = savedTapeLines.Last();
        lastLine.TapeInfo.text = string.Format(measurementInfoFormat, $"{centimeters:F2}cm");
    }

    /// <summary>
    /// Enable the tool so Update runs.
    /// Call from inspector events.
    /// </summary>
    public void EnableTool()
    {
        this.enabled = true;
        if (enableDebugLog)
            Debug.Log("MeasurementTool: Tool ENABLED - Update loop now running");
    }

    /// <summary>
    /// Disable the tool.
    /// </summary>
    public void DisableTool()
    {
        this.enabled = false;
        if (currentController.HasValue)
        {
            HandleUpAction(currentController.Value == OVRInput.Controller.LTouch
                ? leftControllerTapePointer
                : rightControllerTapePointer);
        }
        if (enableDebugLog)
            Debug.Log("MeasurementTool: Tool DISABLED - Update loop stopped");
    }
}