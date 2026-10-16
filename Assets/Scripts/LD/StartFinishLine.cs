using System.Collections;
using UnityEngine;

//Start/finish gantry of a circuit: its lamps are red while a lap is running and turn green for a moment
//when the car is set on the start line and when it completes the round trip
public class StartFinishLine : MonoBehaviour
{
    [SerializeField] private Circuit m_circuit;
    [SerializeField] private Renderer[] m_lamps;
    [SerializeField] private Material m_lampWaiting;
    [SerializeField] private Material m_lampGo;
    [SerializeField] private float m_goDuration = 2.0f;

    private Coroutine m_goRoutine;

    private void OnEnable()
    {
        SetLamps(m_lampWaiting);

        if (m_circuit == null)
            return;

        m_circuit.onAgentSpawned += ShowGo;
        m_circuit.onCircuitFinished += OnCircuitFinished;
    }

    private void OnDisable()
    {
        if (m_circuit != null)
        {
            m_circuit.onAgentSpawned -= ShowGo;
            m_circuit.onCircuitFinished -= OnCircuitFinished;
        }

        m_goRoutine = null;
    }

    private void OnCircuitFinished(AgentML agent, Circuit circuit)
    {
        ShowGo();
    }

    private void ShowGo()
    {
        if (m_goRoutine != null)
            StopCoroutine(m_goRoutine);

        m_goRoutine = StartCoroutine(GoRoutine());
    }

    private IEnumerator GoRoutine()
    {
        SetLamps(m_lampGo);
        yield return new WaitForSeconds(m_goDuration);
        SetLamps(m_lampWaiting);
        m_goRoutine = null;
    }

    private void SetLamps(Material material)
    {
        if (material == null)
            return;

        foreach (Renderer lamp in m_lamps)
        {
            if (lamp != null)
                lamp.sharedMaterial = material;
        }
    }
}
