using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using MQTTnet;
using MQTTnet.Client;

[Serializable]
public class Telemetry
{
    public long ts;
    public int eState;
    public float rSpeed;
    public float rSpeedRequest;
    public float rPosition;
    public long nStartCount;
    public long nStopCount;
    public long nResetCount;
    public long nFaultCount;
    public long nPlcTime;
    public int eFaultCode;
    public float rMotorCurrent;
    public float rMotorTemp;
    public float rBearingTemp;
    public float rVibration;
    public float rAmbientTemp;
}

public class BeltTwinClient : MonoBehaviour
{
    [Header("Broker")]
    public string host = "localhost";
    public int port = 1883;
    public string topic = "conveyor2/telemetry";
    public string cmdTopic = "conveyor2/cmd";

    [Header("Link")]
    public float staleAfterSeconds = 1.0f;

    [Header("Live data (read-only)")]
    public int msgCount;
    public float msgRate;
    public bool linkOk;
    public Telemetry latest;
    public string lastCommand = "";

    IMqttClient client;
    readonly object lk = new object();
    string pending;
    int received;
    float rateTimer;
    int rateStart;
    float lastMsgTime = -999f;

    GUIStyle faultStyle, linkStyle, textStyle;

    static readonly string[] StateNames = { "STOPPED", "STARTING", "RUNNING", "FAULTED" };
    static readonly string[] FaultNames = { "NONE", "JAM", "BELT SLIP", "BEARING WEAR", "MOTOR OVERLOAD", "MANUAL FAULT" };

    public static string StateName(int s) => (s >= 0 && s < StateNames.Length) ? StateNames[s] : "UNKNOWN " + s;
    public static string FaultName(int f) => (f >= 0 && f < FaultNames.Length) ? FaultNames[f] : "UNKNOWN " + f;

    async void Start()
    {
        var factory = new MqttFactory();
        client = factory.CreateMqttClient();

        // Background thread: store the raw string only, no Unity API here
        client.ApplicationMessageReceivedAsync += e =>
        {
            var seg = e.ApplicationMessage.PayloadSegment;
            string s = Encoding.UTF8.GetString(seg.Array, seg.Offset, seg.Count);
            lock (lk) { pending = s; }
            Interlocked.Increment(ref received);
            return Task.CompletedTask;
        };

        var opts = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId("BeltTwinUnity-" + Guid.NewGuid().ToString("N").Substring(0, 8))
            .Build();

        try
        {
            await client.ConnectAsync(opts, CancellationToken.None);
            var sub = factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(topic))
                .Build();
            await client.SubscribeAsync(sub, CancellationToken.None);
            Debug.Log("MQTT connected, subscribed to " + topic);
        }
        catch (Exception ex)
        {
            Debug.LogError("MQTT connect failed: " + ex.Message);
        }
    }

    public async void SendCommand(string cmd)
    {
        if (client == null || !client.IsConnected)
        {
            Debug.LogWarning("Not connected, command dropped: " + cmd);
            return;
        }
        try
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(cmdTopic)
                .WithPayload("{\"cmd\":\"" + cmd + "\"}")
                .Build();
            await client.PublishAsync(msg, CancellationToken.None);
            lastCommand = cmd + "  @ " + DateTime.Now.ToString("HH:mm:ss");
            Debug.Log("Sent command: " + cmd);
        }
        catch (Exception ex)
        {
            Debug.LogError("Command publish failed: " + ex.Message);
        }
    }

    void Update()
    {
        string s;
        lock (lk) { s = pending; pending = null; }
        if (s != null)
        {
            try
            {
                latest = JsonUtility.FromJson<Telemetry>(s);
                lastMsgTime = Time.time;
            }
            catch (Exception ex) { Debug.LogWarning("Parse failed: " + ex.Message); }
        }

        linkOk = (Time.time - lastMsgTime) < staleAfterSeconds;

        msgCount = received;
        rateTimer += Time.deltaTime;
        if (rateTimer >= 1f)
        {
            msgRate = (msgCount - rateStart) / rateTimer;
            rateStart = msgCount;
            rateTimer = 0f;
        }
    }

    static Texture2D MakeTex(Color c)
    {
        var t = new Texture2D(1, 1);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }

    static GUIStyle MakeBanner(Color bg)
    {
        var s = new GUIStyle(GUI.skin.box)
        {
            fontSize = 28,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        s.normal.background = MakeTex(bg);
        s.normal.textColor = Color.white;
        return s;
    }

    void OnGUI()
    {
        if (faultStyle == null)
        {
            faultStyle = MakeBanner(new Color(0.80f, 0.10f, 0.10f, 0.92f));
            linkStyle = MakeBanner(new Color(0.95f, 0.55f, 0.00f, 0.92f));
            textStyle = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            textStyle.normal.textColor = Color.white;
        }

        // Info panel
        GUI.Box(new Rect(5, 5, 600, 95), "");
        GUI.Label(new Rect(12, 10, 600, 20),
            $"Link {(linkOk ? "OK" : "NO DATA")}   msgs {msgCount}   rate {msgRate:F1} Hz", textStyle);

        if (latest != null)
        {
            GUI.Label(new Rect(12, 32, 600, 20),
                $"{StateName(latest.eState)}   fault: {FaultName(latest.eFaultCode)}   speed {latest.rSpeed:F1}   pos {latest.rPosition:F1}",
                textStyle);
            GUI.Label(new Rect(12, 54, 600, 20),
                $"Current {latest.rMotorCurrent:F2} A   Motor {latest.rMotorTemp:F1} °C   Bearing {latest.rBearingTemp:F1} °C   Vib {latest.rVibration:F2}",
                textStyle);
        }

        // Banners: link loss takes priority over faults
        float w = 460f, h = 56f;
        var r = new Rect((Screen.width - w) / 2f, 110, w, h);
        if (!linkOk)
            GUI.Box(r, "NO DATA — CHECK PIPELINE", linkStyle);
        else if (latest != null && latest.eState == 3)
            GUI.Box(r, "FAULT: " + FaultName(latest.eFaultCode), faultStyle);

        // Control panel, bottom of screen
        float py = Screen.height - 110;
        GUI.Box(new Rect(5, py, 620, 105), "");
        GUI.Label(new Rect(12, py + 4, 600, 20),
            "Control" + (lastCommand != "" ? "   last: " + lastCommand : ""), textStyle);

        bool canSend = client != null && client.IsConnected;
        GUI.enabled = canSend;

        float bw = 95, bh = 30, x = 12, y1 = py + 28, y2 = py + 66;
        if (GUI.Button(new Rect(x, y1, bw, bh), "START")) SendCommand("start");
        if (GUI.Button(new Rect(x + (bw + 5), y1, bw, bh), "STOP")) SendCommand("stop");
        if (GUI.Button(new Rect(x + (bw + 5) * 2, y1, bw, bh), "RESET")) SendCommand("reset");

        GUI.Label(new Rect(x, y2 - 18, 300, 18), "Inject fault:", textStyle);
        if (GUI.Button(new Rect(x, y2, bw, bh), "JAM")) SendCommand("jam");
        if (GUI.Button(new Rect(x + (bw + 5), y2, bw, bh), "SLIP")) SendCommand("slip");
        if (GUI.Button(new Rect(x + (bw + 5) * 2, y2, bw, bh), "OVERLOAD")) SendCommand("overload");
        if (GUI.Button(new Rect(x + (bw + 5) * 3, y2, bw, bh), "WEAR")) SendCommand("wear");
        if (GUI.Button(new Rect(x + (bw + 5) * 4, y2, bw, bh), "MANUAL")) SendCommand("fault");

        GUI.enabled = true;
    }

    async void OnDestroy()
    {
        if (client == null) return;
        try
        {
            if (client.IsConnected)
                await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
        }
        catch { }
        client.Dispose();
    }
}