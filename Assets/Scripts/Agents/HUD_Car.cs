using UnityEngine;

//On-screen indicator of the agent's throttle / brake action, like the Arcade project's HUD: a pedal icon where the red pedal is the one
//pressed, plus the analog value of each pedal (the actions are continuous here). Added at runtime by AgentML (see its m_showHud) and drawn
//with IMGUI, so it needs no canvas, scene or prefab setup. Only throttle and brake are shown: the other actions are not used for now.
public class HUD_Car : MonoBehaviour
{
    //Icons shared with the Arcade project (Assets/Resources/HUD): acc = accelerator pedal red, dec = brake pedal red
    private const string m_acceleratingIcon = "HUD/acc";
    private const string m_brakingIcon = "HUD/dec";

    //Below this window height (training players are small) the HUD would hide the scene, so it is not drawn
    private const float m_minScreenHeight = 200.0f;
    //Reference height of the layout, everything scales with the window
    private const float m_referenceHeight = 1080.0f;
    //Pedal action under this value is shown as coasting (faded icon)
    private const float m_coastingThreshold = 0.05f;

    private static readonly Color m_brakeColor = new Color(0.85f, 0.15f, 0.10f, 1.0f);
    private static readonly Color m_throttleColor = new Color(0.20f, 0.80f, 0.25f, 1.0f);
    private static readonly Color m_barBackgroundColor = new Color(0.0f, 0.0f, 0.0f, 0.55f);
    private static readonly Color m_tagActiveColor = new Color(0.95f, 0.60f, 0.10f, 1.0f);

    private AgentML m_agent;
    private Texture2D m_acceleratingTexture;
    private Texture2D m_brakingTexture;
    private GUIStyle m_labelStyle;

    public void Initialize(AgentML agent)
    {
        m_agent = agent;
    }

    private void Awake()
    {
        m_acceleratingTexture = Resources.Load<Texture2D>(m_acceleratingIcon);
        m_brakingTexture = Resources.Load<Texture2D>(m_brakingIcon);
    }

    private void OnGUI()
    {
        if (m_agent == null || Screen.height < m_minScreenHeight)
            return;

        if (m_labelStyle == null)
        {
            m_labelStyle = new GUIStyle(GUI.skin.label);
            m_labelStyle.alignment = TextAnchor.UpperCenter;
            m_labelStyle.fontStyle = FontStyle.Bold;
            m_labelStyle.wordWrap = false;
        }

        float throttle = m_agent.ThrottleAction;
        float scale = Mathf.Clamp(Screen.height / m_referenceHeight, 0.5f, 1.5f);
        float iconSize = 170.0f * scale;
        float margin = 24.0f * scale;
        float barHeight = 12.0f * scale;
        float labelHeight = 40.0f * scale;
        m_labelStyle.fontSize = Mathf.RoundToInt(15.0f * scale);

        float x = Screen.width - iconSize - margin;
        float y = Screen.height - margin - labelHeight - barHeight - iconSize;

        //Red pedal = the pressed one (accelerator when the action is positive, brake when negative), faded while coasting
        Texture2D icon = throttle < 0.0f ? m_brakingTexture : m_acceleratingTexture;
        Color previousColor = GUI.color;
        if (icon != null)
        {
            GUI.color = Mathf.Abs(throttle) < m_coastingThreshold ? new Color(1.0f, 1.0f, 1.0f, 0.4f) : Color.white;
            GUI.DrawTexture(new Rect(x, y, iconSize, iconSize), icon, ScaleMode.ScaleToFit, true);
        }

        //One bar under each pedal of the icon: brake on the left, accelerator on the right
        float barWidth = iconSize * 0.34f;
        float barY = y + iconSize;
        DrawPedal(new Rect(x + iconSize * 0.31f - barWidth * 0.5f, barY, barWidth, barHeight), labelHeight, "BRAKE", Mathf.Max(0.0f, -throttle), m_brakeColor);
        DrawPedal(new Rect(x + iconSize * 0.71f - barWidth * 0.5f, barY, barWidth, barHeight), labelHeight, "ACC", Mathf.Max(0.0f, throttle), m_throttleColor);

        //Tags of the optional actions, only shown while their option is on (like the Arcade HUD hides what is disabled), lit while used
        float tagHeight = 22.0f * scale;
        float tagWidth = 104.0f * scale;
        float tagX = x + iconSize - tagWidth;
        float tagY = y - tagHeight - 4.0f * scale;
        if (m_agent.IsBoostEnabled)
        {
            DrawTag(new Rect(tagX, tagY, tagWidth, tagHeight), "BOOST", m_agent.BoostAction);
            tagY -= tagHeight + 4.0f * scale;
        }
        if (m_agent.IsHandbrakeEnabled)
            DrawTag(new Rect(tagX, tagY, tagWidth, tagHeight), "HANDBRAKE", m_agent.HandbrakeAction);
        GUI.color = previousColor;
    }

    private void DrawTag(Rect rect, string name, bool active)
    {
        GUI.color = active ? m_tagActiveColor : m_barBackgroundColor;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = Color.white;
        TextAnchor previousAlignment = m_labelStyle.alignment;
        m_labelStyle.alignment = TextAnchor.MiddleCenter;
        GUI.Label(rect, name, m_labelStyle);
        m_labelStyle.alignment = previousAlignment;
    }

    private void DrawPedal(Rect bar, float labelHeight, string name, float value, Color color)
    {
        GUI.color = m_barBackgroundColor;
        GUI.DrawTexture(bar, Texture2D.whiteTexture);
        GUI.color = color;
        GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(value), bar.height), Texture2D.whiteTexture);

        //Name and value, with a dark shadow to stay readable on any background
        Rect label = new Rect(bar.x - bar.width * 0.25f, bar.yMax + 2.0f, bar.width * 1.5f, labelHeight);
        string text = name + "\n" + value.ToString("0.00");
        GUI.color = Color.black;
        GUI.Label(new Rect(label.x + 1.0f, label.y + 1.0f, label.width, label.height), text, m_labelStyle);
        GUI.color = Color.white;
        GUI.Label(label, text, m_labelStyle);
    }
}
