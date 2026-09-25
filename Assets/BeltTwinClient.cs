using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using MQTTnet;
using MQTTnet.Client;

[Serializable]
public class Telemetry
{
    public long ts;        // Node-RED publish time (ms since epoch)
    public long readTs;    // last OPC UA read time in Node-RED (ms since epoch)
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
    public long nMaintenanceCount;
}

[Serializable]
public class Detection
{
    public string @event;      // "prediction", "not_running", "detector_online", "detector_offline"
    public string pred;        // healthy / jam / slip / overload / wear
    public float confidence;
    public float speed;
    public long plc_ts;        // Node-RED ts of the sample that triggered the event
    public long ts;
}

public class BeltTwinClient : MonoBehaviour
{
    [Header("Broker")]
    public string host = "localhost";
    public int port = 1883;
    public string topic = "conveyor2/telemetry";
    public string cmdTopic = "conveyor2/cmd";
    public string detTopic = "conveyor2/detection";

    [Header("Link")]
    public float staleAfterSeconds = 1.0f;
    public float reconnectDelaySeconds = 2.0f;

    [Header("Live data (read-only)")]
    public int msgCount;
    public float msgRate;
    public bool linkOk;
    public int reconnects;
    public Telemetry latest;
    public string lastCommand = "";

    [Header("RF detector (read-only)")]
    public Detection det;
    public string rfPredFault = "";   // fault the RF is currently calling while Running
    public string rfVerdict = "";     // set when the PLC trips
    long rfPredTs;
    bool rfVerdictGood;
    int prevState = -1;
    readonly ConcurrentQueue<string> detQ = new ConcurrentQueue<string>();

    [Header("Latency (read-only, ms)")]
    public float mqttMedian;
    public float mqttP95;
    public float ageMedian;
    public float ageP95;

    IMqttClient client;
    MqttClientOptions opts;
    MqttClientSubscribeOptions subOpts;
    volatile bool quitting;
    int reconnecting;   // 0 = idle, 1 = a reconnect loop is running

    readonly object lk = new object();
    string pending;
    int received;
    float rateTimer;
    int rateStart;
    float lastMsgTime = -999f;

    // Rolling latency window: 240 samples = ~60 s at 4 Hz
    readonly float[] latMqtt = new float[240];
    readonly float[] latAge = new float[240];
    int latIdx, latN;

    GUIStyle faultStyle, linkStyle, textStyle, rfWarn, rfOk, rfIdle, rfGood, rfBad;

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
            if (seg.Array == null || seg.Count == 0) return Task.CompletedTask;
            string s = Encoding.UTF8.GetString(seg.Array, seg.Offset, seg.Count);
            if (e.ApplicationMessage.Topic == detTopic)
            {
                detQ.Enqueue(s);                       // detection events: keep every one, in order
            }
            else
            {
                lock (lk) { pending = s; }             // telemetry: only the newest matters
                Interlocked.Increment(ref received);
            }
            return Task.CompletedTask;
        };

        // Auto-reconnect whenever the connection drops
        client.DisconnectedAsync += async e =>
        {
            if (quitting) return;
            Debug.LogWarning("MQTT disconnected: " + e.Reason + ". Reconnecting...");
            await ReconnectLoop();
        };

        opts = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId("BeltTwinUnity-" + Guid.NewGuid().ToString("N").Substring(0, 8))
            .Build();

        subOpts = factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(topic))
            .WithTopicFilter(f => f.WithTopic(detTopic))
            .Build();

        try
        {
            await client.ConnectAsync(opts, CancellationToken.None);
            await client.SubscribeAsync(subOpts, CancellationToken.None);
            Debug.Log("MQTT connected, subscribed to " + topic + " and " + detTopic);
        }
        catch (Exception ex)
        {
            Debug.LogError("MQTT connect failed: " + ex.Message + ". Retrying...");
            await ReconnectLoop();
        }
    }

    async Task ReconnectLoop()
    {
        // Only one reconnect loop at a time
        if (Interlocked.Exchange(ref reconnecting, 1) == 1) return;
        try
        {
            while (!quitting && !client.IsConnected)
            {
                await Task.Delay((int)(reconnectDelaySeconds * 1000));
                if (quitting) break;
                try
                {
                    await client.ConnectAsync(opts, CancellationToken.None);
                    await client.SubscribeAsync(subOpts, CancellationToken.None);
                    Interlocked.Increment(ref reconnects);
                    Debug.Log("MQTT reconnected");
                }
                catch { /* broker still down, keep trying */ }
            }
        }
        finally
        {
            Interlocked.Exchange(ref reconnecting, 0);
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
                TrackTrip(latest);

                // Latency: same machine, same clock, so subtraction is valid
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (latest.ts > 0 && latest.readTs > 0)
                {
                    latMqtt[latIdx] = now - latest.ts;      // Node-RED publish -> Unity
                    latAge[latIdx] = now - latest.readTs;  // OPC UA read -> Unity
                    latIdx = (latIdx + 1) % latMqtt.Length;
                    if (latN < latMqtt.Length) latN++;
                    if (latN < 8 || latIdx % 8 == 0) ComputeLatency();
                }
            }
            catch (Exception ex) { Debug.LogWarning("Parse failed: " + ex.Message); }
        }

        while (detQ.TryDequeue(out var ds))
        {
            try { det = JsonUtility.FromJson<Detection>(ds); OnDetection(det); }
            catch (Exception ex) { Debug.LogWarning("Detection parse failed: " + ex.Message); }
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

    static int FaultCodeOf(string pred)
    {
        switch (pred)
        {
            case "jam": return 1;
            case "slip": return 2;
            case "wear": return 3;
            case "overload": return 4;
            default: return 0;
        }
    }

    void OnDetection(Detection d)
    {
        // Only predictions made while Running count; "not_running" also arrives right
        // after a trip, so it must NOT clear the pre-trip prediction.
        if (d.@event != "prediction") return;
        if (latest != null && latest.eState != 2) return;
        if (d.pred == "healthy") { rfPredFault = ""; return; }
        if (d.pred != rfPredFault) { rfPredFault = d.pred; rfPredTs = d.plc_ts; }
    }

    void TrackTrip(Telemetry t)
    {
        if (t.eState == 1 && prevState != 1) { rfPredFault = ""; rfVerdict = ""; }   // new run
        if (t.eState == 3 && prevState == 2)
        {
            if (t.eFaultCode == 5)
            {
                rfVerdict = "Manual fault: no sensor signature, RF not expected to see it";
                rfVerdictGood = true;
            }
            else if (rfPredFault != "")
            {
                // both timestamps are Node-RED ts, so the difference is consistent
                float lead = (t.ts - rfPredTs) / 1000f;
                rfVerdictGood = FaultCodeOf(rfPredFault) == t.eFaultCode;
                rfVerdict = rfVerdictGood
                    ? $"RF called {rfPredFault.ToUpper()} {lead:F1} s before the PLC tripped"
                    : $"RF said {rfPredFault.ToUpper()}, PLC tripped on {FaultName(t.eFaultCode)}";
            }
            else
            {
                rfVerdict = "RF did not detect this fault before the PLC tripped";
                rfVerdictGood = false;
            }
        }
        prevState = t.eState;
    }

    void ComputeLatency()
    {
        if (latN == 0) return;
        var a = new float[latN]; Array.Copy(latMqtt, a, latN); Array.Sort(a);
        var b = new float[latN]; Array.Copy(latAge, b, latN); Array.Sort(b);
        int p95 = Mathf.Min(latN - 1, (int)(latN * 0.95f));
        mqttMedian = a[latN / 2]; mqttP95 = a[p95];
        ageMedian = b[latN / 2]; ageP95 = b[p95];
    }

    static Texture2D MakeTex(Color c)
    {
        var t = new Texture2D(1, 1);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }

    static GUIStyle MakeBanner(Color bg, int size = 28)
    {
        var s = new GUIStyle(GUI.skin.box)
        {
            fontSize = size,
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
            rfWarn = MakeBanner(new Color(0.45f, 0.20f, 0.75f, 0.92f), 20);   // purple: RF calls a fault
            rfOk   = MakeBanner(new Color(0.10f, 0.10f, 0.10f, 0.60f), 16);
            rfIdle = MakeBanner(new Color(0.30f, 0.30f, 0.30f, 0.60f), 16);
            rfGood = MakeBanner(new Color(0.10f, 0.55f, 0.20f, 0.92f), 20);
            rfBad  = MakeBanner(new Color(0.50f, 0.10f, 0.10f, 0.92f), 20);
        }

        bool connected = client != null && client.IsConnected;

        // Info panel
        GUI.Box(new Rect(5, 5, 660, 117), "");
        GUI.Label(new Rect(12, 10, 660, 20),
            $"Link {(linkOk ? "OK" : "NO DATA")}   MQTT {(connected ? "connected" : "reconnecting")}   msgs {msgCount}   rate {msgRate:F1} Hz   reconnects {reconnects}",
            textStyle);

        if (latest != null)
        {
            GUI.Label(new Rect(12, 32, 660, 20),
                $"{StateName(latest.eState)}   fault: {FaultName(latest.eFaultCode)}   speed {latest.rSpeed:F1}   pos {latest.rPosition:F1}",
                textStyle);
            GUI.Label(new Rect(12, 54, 660, 20),
                $"Current {latest.rMotorCurrent:F2} A   Motor {latest.rMotorTemp:F1} °C   Bearing {latest.rBearingTemp:F1} °C   Vib {latest.rVibration:F2}",
                textStyle);
            GUI.Label(new Rect(12, 76, 660, 20),
                latN > 0
                    ? $"Latency ({latN} samples):  MQTT {mqttMedian:F0} / p95 {mqttP95:F0} ms   data age {ageMedian:F0} / p95 {ageP95:F0} ms"
                    : "Latency: waiting for readTs (check build json in Node-RED)",
                textStyle);
        }

        // Banners: link loss takes priority over faults
        float w = 460f, h = 56f;
        var r = new Rect((Screen.width - w) / 2f, 130, w, h);
        if (!linkOk)
            GUI.Box(r, "NO DATA - CHECK PIPELINE", linkStyle);
        else if (latest != null && latest.eState == 3)
            GUI.Box(r, "FAULT: " + FaultName(latest.eFaultCode), faultStyle);

        // RF detector banner, under the PLC banner
        float rw = 640f, rh = 40f;
        var rr = new Rect((Screen.width - rw) / 2f, 192, rw, rh);
        string rfText; GUIStyle rfSt;
        int st = latest != null ? latest.eState : -1;
        if (!linkOk)                                  { rfText = "RF: no telemetry"; rfSt = rfIdle; }
        else if (det == null)                         { rfText = "RF: waiting for detector"; rfSt = rfIdle; }
        else if (det.@event == "detector_offline")    { rfText = "RF: detector offline"; rfSt = rfIdle; }
        else if ((st == 3 || st == 0) && rfVerdict != "") { rfText = rfVerdict; rfSt = rfVerdictGood ? rfGood : rfBad; }
        else if (st == 2 && rfPredFault != "")
        {
            float since = (latest.ts - rfPredTs) / 1000f;
            rfText = $"RF: {rfPredFault.ToUpper()} predicted ({det.confidence:F2}), PLC not tripped yet, {since:F0} s";
            rfSt = rfWarn;
        }
        else if (st == 2 && det.@event == "prediction") { rfText = $"RF: {det.pred} ({det.confidence:F2})"; rfSt = rfOk; }
        else if (st == 2)                             { rfText = "RF: warming up (needs 30 s of running data)"; rfSt = rfIdle; }
        else                                          { rfText = "RF: idle (conveyor not running)"; rfSt = rfIdle; }
        GUI.Box(rr, rfText, rfSt);

        // Control panel, bottom of screen
        float py = Screen.height - 115;
        GUI.Box(new Rect(5, py, 620, 110), "");
        GUI.Label(new Rect(12, py + 4, 600, 20),
            "Control" + (lastCommand != "" ? "   last: " + lastCommand : ""), textStyle);

        GUI.enabled = connected;

        float bw = 95, bh = 30, x = 12, y1 = py + 26, y2 = py + 76;
        if (GUI.Button(new Rect(x, y1, bw, bh), "START")) SendCommand("start");
        if (GUI.Button(new Rect(x + (bw + 5), y1, bw, bh), "STOP")) SendCommand("stop");
        if (GUI.Button(new Rect(x + (bw + 5) * 2, y1, bw, bh), "RESET")) SendCommand("reset");
        if (GUI.Button(new Rect(x + (bw + 5) * 3, y1, bw, bh), "MAINT")) SendCommand("maintenance");

        GUI.Label(new Rect(x, y2 - 19, 300, 18), "Inject fault:", textStyle);
        if (GUI.Button(new Rect(x, y2, bw, bh), "JAM")) SendCommand("jam");
        if (GUI.Button(new Rect(x + (bw + 5), y2, bw, bh), "SLIP")) SendCommand("slip");
        if (GUI.Button(new Rect(x + (bw + 5) * 2, y2, bw, bh), "OVERLOAD")) SendCommand("overload");
        if (GUI.Button(new Rect(x + (bw + 5) * 3, y2, bw, bh), "WEAR")) SendCommand("wear");
        if (GUI.Button(new Rect(x + (bw + 5) * 4, y2, bw, bh), "MANUAL")) SendCommand("fault");

        GUI.enabled = true;
    }

    async void OnDestroy()
    {
        quitting = true;   // stop any reconnect loop
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