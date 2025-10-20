using UnityEngine;

// Small helper component to simplify wiring UnityEvents in the inspector.
// Attach this to the same GameObject you use for your Interactable "When Select()" event.
// Set `placementManager` (optional, will auto-find), set `prefabToPlace` in inspector,
// then select this GameObject in the UnityEvent and choose `PlaceModelInvoker.PlaceModel`.
// That will call the MRUKPlacementManager to enter placement mode with the configured prefab.
public class PlaceModelInvoker : MonoBehaviour
{
    [Tooltip("Optional: reference to MRUKPlacementManager. If null the manager will be found automatically at runtime.")]
    public MRUKPlacementManager placementManager;

    [Tooltip("Prefab to place. This is exposed in the inspector so you can drag a prefab here and then call PlaceModel() from a UnityEvent.")]
    public GameObject prefabToPlace;

    [Tooltip("If true, the component will try to auto-find a MRUKPlacementManager on Awake/OnValidate when none is assigned.")]
    public bool autoFindManager = true;

    private void Awake()
    {
        if (placementManager == null && autoFindManager)
            placementManager = FindObjectOfType<MRUKPlacementManager>();
    }

    private void OnValidate()
    {
        // make it easy to wire in the editor
        if (placementManager == null && autoFindManager)
            placementManager = FindObjectOfType<MRUKPlacementManager>();
    }

    /// <summary>
    /// Call this from a UnityEvent (e.g. When Select) to start placement mode for the configured prefab.
    /// </summary>
    public void PlaceModel()
    {
        if (prefabToPlace == null)
        {
            Debug.LogWarning("PlaceModelInvoker: prefabToPlace is null. Please assign a prefab in the inspector.");
            return;
        }

        if (placementManager == null)
        {
            placementManager = FindObjectOfType<MRUKPlacementManager>();
            if (placementManager == null)
            {
                Debug.LogWarning("PlaceModelInvoker: MRUKPlacementManager not found in scene.");
                return;
            }
        }

        placementManager.EnterPlacementWithModel(prefabToPlace);
        Debug.Log($"PlaceModelInvoker: requested placement of {prefabToPlace.name}");
    }

    /// <summary>
    /// Convenience method to clear selection and disable placement manager.
    /// Can be used as a UnityEvent target as well.
    /// </summary>
    public void ClearPlacement()
    {
        if (placementManager == null)
            placementManager = FindObjectOfType<MRUKPlacementManager>();
        if (placementManager == null)
        {
            Debug.LogWarning("PlaceModelInvoker: MRUKPlacementManager not found in scene.");
            return;
        }

        placementManager.ClearSelection();
    }

    /// <summary>
    /// Programmatic helper to set the prefab that will be placed.
    /// </summary>
    public void SetPrefab(GameObject prefab)
    {
        prefabToPlace = prefab;
    }

    /// <summary>
    /// Programmatic helper to set the placement manager target.
    /// </summary>
    public void SetPlacementManager(MRUKPlacementManager manager)
    {
        placementManager = manager;
    }
}
