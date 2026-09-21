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

    [Header("Link")]
    public float staleAfterSeconds = 1.0f;

    [Header("Live data (read-only)")]
    public int msgCount;
    public float msgRate;
    public bool linkOk;
    public Telemetry latest;

    IMqttClient client;
    readonly object lk = new object();
    string pending;
    int received;
    float rateTimer;
    int rateStart;
    float lastMsgTime = -999f;

    GUIStyle bannerStyle, textStyle;

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

    void OnGUI()
    {
        if (bannerStyle == null)
        {
            bannerStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 28,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            bannerStyle.normal.textColor = Color.white;
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

        // Banners, centred at the top
        float w = 460f, h = 56f;
        var bannerRect = new Rect((Screen.width - w) / 2f, 110, w, h);
        var old = GUI.backgroundColor;

        if (!linkOk)
        {
            GUI.backgroundColor = new Color(1f, 0.6f, 0f);
            GUI.Box(bannerRect, "NO DATA — CHECK PIPELINE", bannerStyle);
        }
        else if (latest != null && latest.eState == 3)
        {
            GUI.backgroundColor = Color.red;
            GUI.Box(bannerRect, "FAULT: " + FaultName(latest.eFaultCode), bannerStyle);
        }

        GUI.backgroundColor = old;
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