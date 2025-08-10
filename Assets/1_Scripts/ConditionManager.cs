﻿using Microsoft.MixedReality.Toolkit.UI;
using Microsoft.MixedReality.Toolkit.Utilities;
using Photon.Pun;
using Photon.Realtime;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;


[RequireComponent(typeof(PhotonView))]
public class ConditionManager : MonoBehaviourPunCallbacks
{
    [Header("Network Settings")]
    [SerializeField] private NetworkManager networkManager;

    [Header("Component References")]
    [SerializeField] private Transform phoneObject;
    [SerializeField] private PhoneLabelHandler phoneLabelHandler;
    //[SerializeField] private HoloLensRelativeReceiver phoneReceiver; 

    [Header("Interaction Settings")]
    [SerializeField] private float rayLength = 10f;
    [SerializeField] private float proximityThreshold = 0.25f;
    [SerializeField] private LayerMask objectLayer;
    [SerializeField] private LayerMask buttonLayer;

    [Header("Visuals")]
    [SerializeField] private Material connectionLineMaterial;
    [SerializeField] private Material baseOutlineMaterial;
    [SerializeField] private Color highlightColor = Color.white;

    // State Management
    private TaskManager taskManager;
    private PhotonView pv;
    private Player smartphone;

    private StudySettings.Condition currentCondition;
    private readonly List<GameObject> sceneObjects = new List<GameObject>();
    private readonly Dictionary<GameObject, Color> objectColors = new Dictionary<GameObject, Color>();

    // Selection State
    private GameObject selectedObject;
    private GameObject lastHoveredButton;

    //Interaction State
    private bool isConnectedToPhone = false;
    private bool isPointing = false;
    private bool selectActionTriggered = false;

    //Visual Components
    private LineRenderer connectionLine;
    private LineRenderer pointingRayLine;


    private void Start()
    {
        InitialiseComponents();
        InitialisePointingRay();
    }

    private void Update()
    {
        HandleRaySelection();
        UpdateVisualCues();
    }

    private void InitialiseComponents()
    {
        taskManager = GetComponent<TaskManager>();
        pv = PhotonView.Get(this);
    }


    public void Initialise(StudySettings.Condition condition, StudySettings.Task task, int block, List<GameObject> allSceneObjects)
    {
        currentCondition = condition;
        sceneObjects.Clear();
        sceneObjects.AddRange(allSceneObjects);

        ConfigureCondition();
        phoneLabelHandler.InitializePhoneUI(sceneObjects, objectColors, currentCondition);
        pv.RPC("InitialiseUIOnPhone", smartphone);
    }


    private void ConfigureCondition()
    {
        switch (currentCondition)
        {
            case StudySettings.Condition.Color:
                AssignUniqueColorsToObjects();
                break;
            case StudySettings.Condition.Line:
                InitialiseLineCue();
                break;
        }
    }

    #region Connection Management

    public override void OnJoinedRoom()
    {
        CheckForExistingPhoneConnection();
    }
    private void CheckForExistingPhoneConnection()
    {
        Player existingPhone = PhotonNetwork.PlayerListOthers.FirstOrDefault(p => p.NickName == NetworkManager.SMARTPHONE_NICKNAME);
        if (existingPhone != null)
            EstablishPhoneConnection(existingPhone);
    }

    public override void OnPlayerEnteredRoom(Player newPlayer)
    {
        if (newPlayer.NickName == NetworkManager.SMARTPHONE_NICKNAME && !isConnectedToPhone)
            EstablishPhoneConnection(newPlayer);
    }

    public override void OnPlayerLeftRoom(Player otherPlayer)
    {
        if (otherPlayer == smartphone && isConnectedToPhone)
            HandlePhoneDisconnection();
    }

    private void EstablishPhoneConnection(Player phonePlayer)
    {
        isConnectedToPhone = true;
        smartphone = phonePlayer;
        taskManager.ReportPhoneConnected();

        if (taskManager.IsExperimentRunning)
        {
            SyncInitialDataWithPhone();
            Debug.Log("Phone reconnected to an ongoing experiment. Syncing UI state.");
            if (selectedObject != null)
            {
                int id = sceneObjects.IndexOf(selectedObject);
                if (id != -1)
                {
                    Color tagColor = Color.white;
                    if (currentCondition == StudySettings.Condition.Color)
                        objectColors.TryGetValue(selectedObject, out tagColor);

                    float[] colorArray = { tagColor.r, tagColor.g, tagColor.b, tagColor.a };
                    pv.RPC("ControlLabelOnPhone", smartphone, true, id, colorArray, "");
                }
            }
            else
                pv.RPC("InitialiseUIOnPhone", smartphone);
        }
    }

    private void HandlePhoneDisconnection()
    {
        isConnectedToPhone = false;
        smartphone = null;
        Debug.Log("Phone disconnected");
    }

    public void SyncInitialDataWithPhone()
    {
        if (smartphone != null)
        {
            Debug.Log("Syncing initial data.");
            pv.RPC("ReceiveInitialDataOnPhone", smartphone, (object)phoneLabelHandler.LabelContents.ToArray());
        }
    }
    #endregion

    #region Interaction Handlers

    private void HandleRaySelection()
    {
        //Transform logicalPhone = phoneReceiver.GetLogicalTransform();
        if (phoneObject == null) return;

        pointingRayLine.enabled = isPointing;

        if (!isPointing)
        {
            ResetLastHoveredButton();
            return;
        }

        Vector3 rayOrigin = PhoneRayOrigin;
        Vector3 rayDirection = phoneObject.forward;
        Ray ray = new(rayOrigin, rayDirection);
        //Vector3 rayOrigin = PhoneRayOrigin;
        //Vector3 rayDirection = phoneObject.transform.forward; // Keep phone's forward direction
        //Ray ray = new(rayOrigin, rayDirection);
        bool didHit = Physics.Raycast(ray, out RaycastHit hit, rayLength, objectLayer | buttonLayer);

        UpdateRayVisuals(didHit, hit.point, ray.origin, ray.direction);
        ProcessRaycastHit(didHit, hit);

        if (selectActionTriggered) selectActionTriggered = false;
    }

    private void UpdateVisualCues()
    {
        if (currentCondition == StudySettings.Condition.Proximity) HandleProximitySelection();
        if (currentCondition == StudySettings.Condition.Line && selectedObject != null) UpdateLineCue();
    }

    private void UpdateRayVisuals(bool didHit, Vector3 hitPoint, Vector3 origin, Vector3 direction)
    {
        pointingRayLine.material.color = didHit ? Color.red : Color.cyan;
        Vector3 endPoint = didHit ? hitPoint : origin + direction * rayLength;
        pointingRayLine.SetPosition(0, origin);
        pointingRayLine.SetPosition(1, endPoint);
    }

    private void ProcessRaycastHit(bool didHit, RaycastHit hit)
    {
        if (!didHit)
        {
            ResetLastHoveredButton();
            return;
        }

        if (((1 << hit.collider.gameObject.layer) & buttonLayer) != 0)
            ProcessButtonHit(hit.collider.gameObject);
        else
        {
            ResetLastHoveredButton();
            if (selectActionTriggered)
                ToggleSelection(GetRootObject(hit.collider.gameObject));
        }
    }

    private void ProcessButtonHit(GameObject buttonObject)
    {
        var interactable = buttonObject.GetComponent<Interactable>();
        if (interactable == null) return;
        if (!interactable.IsEnabled) return;

        if (lastHoveredButton != buttonObject)
        {
            ResetLastHoveredButton();
            lastHoveredButton = buttonObject;
            interactable.HasFocus = true;
        }

        if (selectActionTriggered)
        {
            if (buttonObject.name == "StartButton")
            {
                taskManager.StartExperiment();
                return;
            }

            interactable.OnClick.Invoke();
            interactable.HasPress = true;
        }
        else
            interactable.HasPress = false;
    }

    private void ResetLastHoveredButton()
    {
        if (lastHoveredButton != null)
        {
            var interactable = lastHoveredButton.GetComponent<Interactable>();
            if (interactable != null)
            {
                interactable.HasFocus = false;
                interactable.HasPress = false;
            }
            lastHoveredButton = null;
        }
    }

    private GameObject GetClosestObjectInProximity()
    {
        //Transform logicalPhone = phoneReceiver.GetLogicalTransform();
        Collider[] nearbyColliders = Physics.OverlapSphere(phoneObject.position, proximityThreshold, objectLayer);
        //Collider[] nearbyColliders = Physics.OverlapSphere(logicalPhone.position, proximityThreshold, objectLayer);
        GameObject closestObject = null;
        float minSqrDistance = float.MaxValue;

        foreach (var col in nearbyColliders)
        {
            GameObject root = GetRootObject(col.gameObject);
            if (root != null)
            {
                float sqrDistance = (phoneObject.position - col.ClosestPoint(phoneObject.position)).sqrMagnitude;
                //float sqrDistance = (logicalPhone.position - col.ClosestPoint(logicalPhone.position)).sqrMagnitude;
                if (sqrDistance < minSqrDistance)
                {
                    minSqrDistance = sqrDistance;
                    closestObject = root;
                }
            }
        }

        return closestObject;
    }

    public void HandleProximitySelection()
    {
        GameObject closestObject = GetClosestObjectInProximity();

        if (closestObject != null && selectedObject != closestObject) SelectObject(closestObject);
        else if (closestObject == null && selectedObject != null) DeselectObject();
    }


    #endregion

    #region Object Selection

    private void ToggleSelection(GameObject obj)
    {
        if (obj == null) return;
        int id = sceneObjects.IndexOf(obj);

        if (currentCondition == StudySettings.Condition.Proximity)
        {
            StudyLogger.Instance.LogInteraction("object", id.ToString(), "ApproachPrompt");
            StartCoroutine(ShowApproachThenEvaluate());
            return;
        }

        if (selectedObject == obj)
        {
            StudyLogger.Instance.LogInteraction("object", null, "Deselect");
            DeselectObject();
        }
        else
        {
            StudyLogger.Instance.LogInteraction("object", id.ToString(), "Select");
            SelectObject(obj);
        }
    }

    public IEnumerator ShowApproachThenEvaluate()
    {
        SendApproachMessageToPhone();
        phoneLabelHandler.ShowApproachMessage();

        yield return new WaitForSeconds(3f);

        GameObject closestObject = GetClosestObjectInProximity();

        if (closestObject != null)
            SelectObject(closestObject);
        else
        {
            phoneLabelHandler.HideLabel();
            SendHideMessageToPhone();
        }
    }

    private void SelectObject(GameObject obj)
    {
        if (obj == null) return;

        if (selectedObject != null) DeselectObject();
        selectedObject = obj;

        if (currentCondition == StudySettings.Condition.Line)
        {
            connectionLine.enabled = true;
            UpdateLineCue();
        }

        Color tagColor = Color.white;
        if (currentCondition == StudySettings.Condition.Color)
            objectColors.TryGetValue(obj, out tagColor);

        if (currentCondition == StudySettings.Condition.Highlight)
        {
            var outlineManager = selectedObject.GetComponent<MeshOutlineHierarchy>() ?? selectedObject.AddComponent<MeshOutlineHierarchy>();
            if (outlineManager.OutlineMaterial == null)
            {
                outlineManager.OutlineMaterial = new Material(baseOutlineMaterial);
                outlineManager.OutlineWidth = 0.01f;
                outlineManager.OutlineMaterial.color = highlightColor;
            }
            else
                ChangeHighlight(selectedObject, true);  
        }

        int id = sceneObjects.IndexOf(selectedObject);
        if (id == -1) return;

        phoneLabelHandler.ShowLabelFullscreen(id);
        if (smartphone != null)
        {
            float[] colorArray = { tagColor.r, tagColor.g, tagColor.b, tagColor.a };
            pv.RPC("ControlLabelOnPhone", smartphone, true, id, colorArray, "");
        }
    }

    private void DeselectObject()
    {
        if (selectedObject == null) return;
        if (currentCondition == StudySettings.Condition.Line && connectionLine != null)
            connectionLine.enabled = false;

        if (currentCondition == StudySettings.Condition.Highlight)
            ChangeHighlight(selectedObject, false);

        selectedObject = null;
        phoneLabelHandler.HideLabel();

        if (smartphone != null)
            pv.RPC("ControlLabelOnPhone", smartphone, false, -1, null, "");
    }

    private void ChangeHighlight(GameObject obj, bool enabled)
    {
        var outlines = obj.GetComponentsInChildren<MeshOutline>();
        foreach (var outline in outlines)
            outline.enabled = enabled;
    }

    public void SendApproachMessageToPhone()
    {
        if (smartphone != null)
            pv.RPC("ControlLabelOnPhone", smartphone, true, -1, null, "설명을 보려면 가까이 다가가주세요.");
    }

    public void SendHideMessageToPhone()
    {
        if (smartphone != null)
            pv.RPC("ControlLabelOnPhone", smartphone, false, -1, null, "");
    }

    public void SelectObjectExternally(GameObject obj) => SelectObject(obj);
    public void DeselectObjectExternally() => DeselectObject();

    #endregion

    #region Initialisation
    private void AssignUniqueColorsToObjects()
    {
        Color[] palette = { Color.red, Color.green, Color.blue, Color.yellow, Color.cyan, Color.magenta };

        for (int i = 0; i < sceneObjects.Count && i < palette.Length; i++)
        {
            GameObject obj = sceneObjects[i];
            Color color = palette[i];
            objectColors[obj] = color;

            Material matInstance = new Material(baseOutlineMaterial) { color = color };
            var outline = obj.GetComponent<MeshOutlineHierarchy>() ?? obj.AddComponent<MeshOutlineHierarchy>();
            outline.OutlineMaterial = matInstance;
            outline.OutlineWidth = 0.01f;
        }
    }

    private void InitialisePointingRay()
    {
        pointingRayLine = new GameObject("PhonePointingRay").AddComponent<LineRenderer>();
        pointingRayLine.material = new Material(Shader.Find("Mixed Reality Toolkit/Standard")) { color = Color.cyan };
        pointingRayLine.startWidth = 0.002f;
        pointingRayLine.endWidth = 0.002f;
        pointingRayLine.positionCount = 2;
        pointingRayLine.enabled = false;
    }

    private void InitialiseLineCue()
    {
        connectionLine = new GameObject("SelectionLineCue").AddComponent<LineRenderer>();
        connectionLine.material = connectionLineMaterial;
        connectionLine.startWidth = 0.01f;
        connectionLine.endWidth = 0.01f;

        connectionLine.positionCount = 2;

        connectionLine.textureMode = LineTextureMode.Tile;
        connectionLine.enabled = false;
    }


    //private Vector3[] CalculateQuadraticBezierCurve(Vector3 startPoint, Vector3 controlPoint, Vector3 endPoint, int segmentCount)
    //{
    //    Vector3[] points = new Vector3[segmentCount];
    //    for (int i = 0; i < segmentCount; i++)
    //    {
    //        float t = i / (float)(segmentCount - 1);
    //        // The quadratic Bezier formula
    //        points[i] = (1 - t) * (1 - t) * startPoint + 2 * (1 - t) * t * controlPoint + t * t * endPoint;
    //    }
    //    return points;
    //}

    #endregion

    #region Utility Methods

    private void UpdateLineCue()
    {

        if (connectionLine != null && selectedObject != null)
        {
            //Transform logicalPhone = phoneReceiver.GetLogicalTransform();
            connectionLine.SetPosition(0, PhoneRayOrigin);
            //connectionLine.SetPosition(0, GetPhoneRayOrigin(logicalPhone));
            connectionLine.SetPosition(1, selectedObject.transform.position);
        }
    }

    //private void UpdateLineCue()
    //{
    //    if (connectionLine == null || selectedObject == null) return;

    //    Vector3 startPoint = phoneObject.position;
    //    Vector3 endPoint = selectedObject.position;

    //    // Find the midpoint and add an upward offset. You can adjust the offset value.
    //    Vector3 controlPoint = (startPoint + endPoint) / 2f + Vector3.up * 0.5f;

    //    // Calculate the points for the curve
    //    Vector3[] curvePoints = CalculateQuadraticBezierCurve(startPoint, controlPoint, endPoint, connectionLine.positionCount);

    //    // Update the LineRenderer with the new curve points
    //    connectionLine.SetPositions(curvePoints);
    //}

    private GameObject GetRootObject(GameObject hitObject)
    {
        Transform current = hitObject.transform;
        while (current != null)
        {
            if (sceneObjects.Contains(current.gameObject))
                return current.gameObject;
            current = current.parent;
        }
        return null;
    }
    private Vector3 PhoneRayOrigin
    {
        get
        {
            float offset = 0.05f;
            Vector3 localHeadPosition = new Vector3(0, 0, 0.1633f / 2.0f + offset);
            return phoneObject.transform.TransformPoint(localHeadPosition);
        }
    }

    //private Vector3 GetPhoneRayOrigin(Transform phoneTransform)
    //{
    //    Vector3 localHeadPosition = new Vector3(0, 0, 0.1633f / 2.0f);
    //    return phoneTransform.TransformPoint(localHeadPosition);
    //}

    #endregion

    #region PUN RPCs

    [PunRPC]
    public void RequestSelectObjectFromPhone(int index)
    {
        if (currentCondition == StudySettings.Condition.Proximity)
        {
            StudyLogger.Instance.LogInteraction("object", index.ToString(), "ApproachPrompt");
            StartCoroutine(ShowApproachThenEvaluate());
            return;
        }
        if (index >= 0 && index < sceneObjects.Count)
            SelectObject(sceneObjects[index]);
        StudyLogger.Instance.LogInteraction("phone", index.ToString(), "Select");
    }

    [PunRPC]
    public void RequestDeselectFromPhone()
    {
        DeselectObject();
        StudyLogger.Instance.LogInteraction("phone", null, "Deselect");
    }

    [PunRPC]
    public void SetRayHold(bool isHeld)
    {
        isPointing = isHeld;
    }

    [PunRPC]
    public void SelectWithRay()
    {
        if (!isPointing) return;
        selectActionTriggered = true;
    }
    #endregion
}