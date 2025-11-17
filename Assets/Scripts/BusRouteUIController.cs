using TMPro;
using UnityEngine;

public class BusRouteUIController : MonoBehaviour
{
    [Header("참조")]
    public MultiBusSimulationManager manager;
    public TMP_InputField busNoInput;

    public void ApplyBusNoFromInput()
    {
        if (manager == null)
        {
            Debug.LogWarning("[BusRouteUI] MultiBusSimulationManager가 연결되지 않았습니다.");
            return;
        }

        if (busNoInput == null)
        {
            Debug.LogWarning("[BusRouteUI] TMP_InputField가 연결되지 않았습니다.");
            return;
        }

        string newBusNo = busNoInput.text.Trim();
        manager.ChangeBusNo(newBusNo);
    }
}
