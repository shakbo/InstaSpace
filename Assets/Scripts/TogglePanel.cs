using UnityEngine;

public class TogglePanel : MonoBehaviour
{
    // assign the Background panel GameObject in the Inspector
    public GameObject background;

    // if true, Background will be hidden on Start
    public bool startHidden = true;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        if (background != null && startHidden)
        {
            background.SetActive(false);
        }
    }

    // Call this from your poke/press event to toggle visibility
    public void Toggle()
    {
        if (background == null) return;
        background.SetActive(!background.activeSelf);
    }

    // Optional helpers
    public void Show()
    {
        if (background == null) return;
        background.SetActive(true);
    }

    public void Hide()
    {
        if (background == null) return;
        background.SetActive(false);
    }

    // Update is not used but kept if needed later
    void Update()
    {
    }
}
