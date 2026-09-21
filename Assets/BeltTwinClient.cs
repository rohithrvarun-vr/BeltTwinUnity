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

    [Header("Live data (read-only)")]
    public int msgCount;
    public float msgRate;
    public Telemetry latest;

    IMqttClient client;
    readonly object lk = new object();
    string pending;
    int received;
    float rateTimer;
    int rateStart;

    async void Start()
    {
        var factory = new MqttFactory();
        client = factory.CreateMqttClient();

        // Runs on a background thread: store the raw string only, no Unity API here
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
        // Main thread: parse the newest message
        string s;
        lock (lk) { s = pending; pending = null; }
        if (s != null)
        {
            try { latest = JsonUtility.FromJson<Telemetry>(s); }
            catch (Exception ex) { Debug.LogWarning("Parse failed: " + ex.Message); }
        }

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
        GUI.Box(new Rect(5, 5, 560, 70), "");
        GUI.Label(new Rect(10, 10, 600, 20), $"msgs {msgCount}   rate {msgRate:F1} Hz");
        if (latest == null) return;
        GUI.Label(new Rect(10, 30, 600, 20),
            $"state {latest.eState}   fault {latest.eFaultCode}   speed {latest.rSpeed:F1}   pos {latest.rPosition:F1}");
        GUI.Label(new Rect(10, 50, 600, 20),
            $"I {latest.rMotorCurrent:F2} A   Tmotor {latest.rMotorTemp:F1}   Tbearing {latest.rBearingTemp:F1}   vib {latest.rVibration:F2}");
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