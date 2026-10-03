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
    public long nInjectCount;
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

    static readonly string[] StateNames = { "STOPPED", "STARTING", "RUNNING", "FAULTED" };
    static readonly string[] FaultNames = { "NONE", "JAM", "BELT SLIP", "BEARING WEAR", "MOTOR OVERLOAD", "MANUAL FAULT" };

    public static string StateName(int s) => (s >= 0 && s < StateNames.Length) ? StateNames[s] : "UNKNOWN " + s;
    public static string FaultName(int f) => (f >= 0 && f < FaultNames.Length) ? FaultNames[f] : "UNKNOWN " + f;

    // ------------------------------------------------------------------ HMI
    // Display limits for the operator screen only. They are NOT the detector thresholds.
    class Gauge
    {
        public string label, unit, fmt;
        public float min, max, warn, alarm;
        public Gauge(string label, string unit, float min, float max, float warn, float alarm, string fmt)
        { this.label = label; this.unit = unit; this.min = min; this.max = max; this.warn = warn; this.alarm = alarm; this.fmt = fmt; }
    }

    readonly Gauge gSpeed   = new Gauge("BELT SPEED",    "",   0f, 80f, float.MaxValue, float.MaxValue, "F1");
    readonly Gauge gCurrent = new Gauge("MOTOR CURRENT", "A",  0f, 16f, 10.5f, 12f, "F2");
    readonly Gauge gMotor   = new Gauge("MOTOR TEMP",    "°C", 0f, 100f, 66f, 72f, "F1");
    readonly Gauge gBearing = new Gauge("BEARING TEMP",  "°C", 0f, 80f, 40f, 45f, "F1");
    readonly Gauge gVib     = new Gauge("VIBRATION",     "",   0f, 8f, 3.0f, 4.0f, "F2");

    // Trends: last 120 s at 4 Hz
    const int TrendN = 480, TrendW = 300, TrendH = 56;
    class Trend
    {
        public readonly float[] buf = new float[TrendN];
        public int head, count;
        public Texture2D tex;
        public Color32[] px;
        public float lo, hi;
        public void Push(float v) { buf[head] = v; head = (head + 1) % TrendN; if (count < TrendN) count++; }
        public float Get(int i) => buf[(head - count + i + TrendN * 2) % TrendN];   // i = 0 oldest
    }
    Trend tMotor, tBearing, tCurrent;
    bool trendsDirty;

    // Palette in the spirit of ISA-101 high-performance HMIs: grey by default, colour only for abnormal states
    static readonly Color cBg      = new Color(0.16f, 0.17f, 0.18f, 0.94f);
    static readonly Color cHeader  = new Color(0.10f, 0.11f, 0.12f, 0.97f);
    static readonly Color cTrack   = new Color(0.09f, 0.09f, 0.10f, 1f);
    static readonly Color cFill    = new Color(0.55f, 0.60f, 0.66f, 1f);
    static readonly Color cWarn    = new Color(0.95f, 0.70f, 0.10f, 1f);
    static readonly Color cAlarm   = new Color(0.85f, 0.15f, 0.12f, 1f);
    static readonly Color cOk      = new Color(0.25f, 0.65f, 0.30f, 1f);
    static readonly Color cText    = new Color(0.86f, 0.88f, 0.90f, 1f);
    static readonly Color cDim     = new Color(0.58f, 0.61f, 0.64f, 1f);

    Texture2D texBg, texHeader, texTrack, texFill, texWarn, texAlarm, texOk, texWhite;
    GUIStyle sPanel, sHeader, sTitle, sLabel, sValue, sSmall, sDim, sDimRight, sBadge, sBanner, sBannerNoData;
    GUIStyle sAppTitle, sStateText, sLinkText, sClock, sTrendValue;
    GUIStyle sBtnStart, sBtnStop, sBtnNeutral, sBtnInject;
    GUIStyle rfWarn, rfOk, rfIdle, rfGood, rfBad;

    async void Start()
    {
        tMotor = NewTrend(15f, 85f);
        tBearing = NewTrend(15f, 55f);
        tCurrent = NewTrend(0f, 16f);

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

                tMotor.Push(latest.rMotorTemp);
                tBearing.Push(latest.rBearingTemp);
                tCurrent.Push(latest.rMotorCurrent);
                trendsDirty = true;

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

        if (trendsDirty)
        {
            DrawTrend(tMotor, gMotor.warn, gMotor.alarm);
            DrawTrend(tBearing, gBearing.warn, gBearing.alarm);
            DrawTrend(tCurrent, gCurrent.warn, gCurrent.alarm);
            trendsDirty = false;
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

    // ------------------------------------------------------------------ trend drawing
    static Trend NewTrend(float lo, float hi)
    {
        var t = new Trend { lo = lo, hi = hi };
        t.tex = new Texture2D(TrendW, TrendH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        t.px = new Color32[TrendW * TrendH];
        return t;
    }

    static int YOf(Trend t, float v)
    {
        float f = (v - t.lo) / (t.hi - t.lo);
        return Mathf.Clamp(Mathf.RoundToInt(f * (TrendH - 1)), 0, TrendH - 1);
    }

    static void Plot(Color32[] px, int x, int y, Color32 c)
    {
        if (x < 0 || x >= TrendW || y < 0 || y >= TrendH) return;
        px[y * TrendW + x] = c;
    }

    static void Line(Color32[] px, int x0, int y0, int x1, int y1, Color32 c)
    {
        int dx = Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            Plot(px, x0, y0, c);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    static void DrawTrend(Trend t, float warn, float alarm)
    {
        Color32 bg = new Color32(22, 23, 25, 255), grid = new Color32(48, 50, 54, 255);
        Color32 w = new Color32(242, 178, 26, 255), a = new Color32(217, 38, 31, 255);
        Color32 ln = new Color32(215, 220, 225, 255);
        var px = t.px;
        for (int i = 0; i < px.Length; i++) px[i] = bg;
        for (int g = 1; g < 4; g++)
        {
            int y = g * (TrendH - 1) / 4;
            for (int x = 0; x < TrendW; x += 2) Plot(px, x, y, grid);
        }
        if (warn > t.lo && warn < t.hi) { int y = YOf(t, warn); for (int x = 0; x < TrendW; x += 4) { Plot(px, x, y, w); Plot(px, x + 1, y, w); } }
        if (alarm > t.lo && alarm < t.hi) { int y = YOf(t, alarm); for (int x = 0; x < TrendW; x += 4) { Plot(px, x, y, a); Plot(px, x + 1, y, a); } }
        if (t.count > 1)
        {
            int px0 = -1, py0 = -1;
            for (int i = 0; i < t.count; i++)
            {
                int x = (TrendN - t.count + i) * (TrendW - 1) / (TrendN - 1);
                int y = YOf(t, t.Get(i));
                if (px0 >= 0) { Line(px, px0, py0, x, y, ln); Line(px, px0, py0 + 1, x, y + 1, ln); }
                px0 = x; py0 = y;
            }
        }
        t.tex.SetPixels32(px);
        t.tex.Apply(false);
    }

    // ------------------------------------------------------------------ GUI helpers
    static Texture2D MakeTex(Color c)
    {
        var t = new Texture2D(1, 1);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }

    static GUIStyle MakeBanner(Color bg, int size)
    {
        var s = new GUIStyle(GUI.skin.box)
        {
            fontSize = size,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true
        };
        s.normal.background = MakeTex(bg);
        s.normal.textColor = Color.white;
        return s;
    }

    static GUIStyle MakeButton(Color c)
    {
        var s = new GUIStyle(GUI.skin.button) { fontSize = 13, fontStyle = FontStyle.Bold };
        s.normal.background = MakeTex(c);
        s.hover.background = MakeTex(Color.Lerp(c, Color.white, 0.15f));
        s.active.background = MakeTex(Color.Lerp(c, Color.black, 0.25f));
        s.normal.textColor = s.hover.textColor = s.active.textColor = Color.white;
        s.border = new RectOffset(0, 0, 0, 0);
        return s;
    }

    GUIStyle Label(int size, Color c, FontStyle f = FontStyle.Normal, TextAnchor a = TextAnchor.MiddleLeft)
    {
        var s = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = f, alignment = a };
        s.normal.textColor = c;
        return s;
    }

    void InitStyles()
    {
        texBg = MakeTex(cBg); texHeader = MakeTex(cHeader); texTrack = MakeTex(cTrack); texFill = MakeTex(cFill);
        texWarn = MakeTex(cWarn); texAlarm = MakeTex(cAlarm); texOk = MakeTex(cOk); texWhite = MakeTex(cText);

        sPanel = new GUIStyle(GUI.skin.box); sPanel.normal.background = texBg;
        sHeader = new GUIStyle(GUI.skin.box); sHeader.normal.background = texHeader;
        sTitle = Label(12, cDim, FontStyle.Bold);
        sLabel = Label(11, cDim, FontStyle.Bold);
        sValue = Label(15, cText, FontStyle.Bold, TextAnchor.MiddleRight);
        sSmall = Label(11, cText);
        sDim = Label(10, cDim);
        sDimRight = Label(10, cDim, FontStyle.Normal, TextAnchor.MiddleRight);
        sAppTitle = Label(15, cText, FontStyle.Bold);
        sStateText = Label(13, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
        sLinkText = Label(12, cText, FontStyle.Bold);
        sClock = Label(13, cText, FontStyle.Bold, TextAnchor.MiddleRight);
        sTrendValue = Label(12, cText, FontStyle.Bold, TextAnchor.MiddleRight);
        sBadge = MakeBanner(new Color(0.30f, 0.32f, 0.35f, 1f), 13);

        sBanner = MakeBanner(new Color(0.80f, 0.10f, 0.10f, 0.95f), 24);
        sBannerNoData = MakeBanner(new Color(0.95f, 0.55f, 0.00f, 0.95f), 22);

        sBtnStart = MakeButton(new Color(0.16f, 0.45f, 0.22f, 1f));
        sBtnStop = MakeButton(new Color(0.62f, 0.13f, 0.12f, 1f));
        sBtnNeutral = MakeButton(new Color(0.33f, 0.35f, 0.38f, 1f));
        sBtnInject = MakeButton(new Color(0.40f, 0.33f, 0.14f, 1f));
        sBtnInject.fontSize = 11;

        rfWarn = MakeBanner(new Color(0.45f, 0.20f, 0.75f, 0.95f), 13);   // purple: RF calls a fault
        rfOk   = MakeBanner(new Color(0.12f, 0.13f, 0.14f, 0.95f), 13);
        rfIdle = MakeBanner(new Color(0.28f, 0.29f, 0.31f, 0.95f), 13);
        rfGood = MakeBanner(new Color(0.10f, 0.50f, 0.20f, 0.95f), 13);
        rfBad  = MakeBanner(new Color(0.55f, 0.10f, 0.10f, 0.95f), 13);
    }

    void Panel(Rect r, string title)
    {
        GUI.Box(r, "", sPanel);
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, 22), texHeader);
        GUI.Label(new Rect(r.x + 8, r.y + 1, r.width - 16, 20), title, sTitle);
    }

    void BarIndicator(Rect r, Gauge g, float v, bool live, float setpoint = float.NaN)
    {
        GUI.Label(new Rect(r.x, r.y, r.width * 0.6f, 18), g.label, sLabel);
        string val = live ? v.ToString(g.fmt) + (g.unit != "" ? " " + g.unit : "") : "----";
        GUI.Label(new Rect(r.x + r.width * 0.4f, r.y - 1, r.width * 0.6f, 20), val, sValue);

        var track = new Rect(r.x, r.y + 20, r.width, 10);
        GUI.DrawTexture(track, texTrack);
        if (live)
        {
            float f = Mathf.Clamp01((v - g.min) / (g.max - g.min));
            Texture2D fill = v >= g.alarm ? texAlarm : v >= g.warn ? texWarn : texFill;
            GUI.DrawTexture(new Rect(track.x, track.y + 1, track.width * f, track.height - 2), fill);
        }
        Tick(track, g, g.warn, texWarn);
        Tick(track, g, g.alarm, texAlarm);
        if (!float.IsNaN(setpoint)) Tick(track, g, setpoint, texWhite, 3, 16);
        GUI.Label(new Rect(r.x, r.y + 30, 60, 14), g.min.ToString("0"), sDim);
        GUI.Label(new Rect(r.x + r.width - 60, r.y + 30, 60, 14), g.max.ToString("0"), sDimRight);
    }

    void Tick(Rect track, Gauge g, float v, Texture2D tex, float w = 2, float h = 14)
    {
        if (v <= g.min || v >= g.max) return;
        float x = track.x + track.width * (v - g.min) / (g.max - g.min);
        GUI.DrawTexture(new Rect(x - w / 2f, track.y - (h - track.height) / 2f, w, h), tex);
    }

    void TrendBox(Rect r, string name, Trend t, float v, string unit, bool live)
    {
        GUI.Label(new Rect(r.x, r.y, r.width * 0.6f, 16), name, sLabel);
        GUI.Label(new Rect(r.x + r.width * 0.4f, r.y - 1, r.width * 0.6f, 18), live ? $"{v:F1} {unit}" : "----", sTrendValue);
        var img = new Rect(r.x, r.y + 17, r.width - 26, TrendH);
        GUI.DrawTexture(img, t.tex);
        GUI.Label(new Rect(img.xMax + 2, img.y - 4, 26, 14), t.hi.ToString("0"), sDim);
        GUI.Label(new Rect(img.xMax + 2, img.yMax - 10, 26, 14), t.lo.ToString("0"), sDim);
    }

    // ------------------------------------------------------------------ GUI
    // Compact layout: the belt stays visible in the middle. Trends and link diagnostics open
    // on demand (TRENDS button in the header), as on a typical operator panel.
    bool showTrends;
    public const float LeftPanelW = 252f, RightPanelW = 262f, BottomBarH = 66f, HeaderH = 34f;

    void OnGUI()
    {
        if (sPanel == null) InitStyles();

        bool connected = client != null && client.IsConnected;
        bool live = linkOk && latest != null;
        int st = latest != null ? latest.eState : -1;
        float W = Screen.width, H = Screen.height;

        // ---- header bar
        GUI.Box(new Rect(0, 0, W, HeaderH), "", sHeader);
        GUI.Label(new Rect(12, 7, 300, 20), "CONVEYOR C2  ·  BELT TWIN", sAppTitle);
        string stateText = live ? StateName(st) : "NO DATA";
        Color stateCol = !live ? cWarn : st == 3 ? cAlarm : st == 2 ? cOk : new Color(0.40f, 0.42f, 0.45f, 1f);
        GUI.DrawTexture(new Rect(W / 2f - 70, 6, 140, 22), MakeCached(stateCol));
        GUI.Label(new Rect(W / 2f - 70, 6, 140, 22), stateText, sStateText);
        if (GUI.Button(new Rect(W - 330, 6, 86, 22), showTrends ? "TRENDS  ▴" : "TRENDS  ▾", sBtnNeutral)) showTrends = !showTrends;
        GUI.DrawTexture(new Rect(W - 230, 12, 10, 10), linkOk ? texOk : texWarn);
        GUI.Label(new Rect(W - 214, 7, 110, 20), linkOk ? "LINK OK" : "LINK LOST", sLinkText);
        GUI.Label(new Rect(W - 110, 7, 100, 20), DateTime.Now.ToString("HH:mm:ss"), sClock);

        // ---- alarm banner (PLC), top centre
        float bw = Mathf.Min(420f, W - LeftPanelW - RightPanelW - 40f), bh = 40f;
        var br = new Rect((W - bw) / 2f, HeaderH + 8, bw, bh);
        if (!linkOk) GUI.Box(br, "NO DATA - CHECK PIPELINE", sBannerNoData);
        else if (st == 3) GUI.Box(br, "FAULT: " + FaultName(latest.eFaultCode), sBanner);

        // ---- left: process values
        var lp = new Rect(8, HeaderH + 8, LeftPanelW, 268);
        Panel(lp, "PROCESS VALUES");
        float gx = lp.x + 10, gw = LeftPanelW - 20, gy = lp.y + 28, step = 46f;
        BarIndicator(new Rect(gx, gy, gw, 44), gSpeed, live ? latest.rSpeed : 0f, live, live ? latest.rSpeedRequest : float.NaN); gy += step;
        BarIndicator(new Rect(gx, gy, gw, 44), gCurrent, live ? latest.rMotorCurrent : 0f, live); gy += step;
        BarIndicator(new Rect(gx, gy, gw, 44), gMotor, live ? latest.rMotorTemp : 0f, live); gy += step;
        BarIndicator(new Rect(gx, gy, gw, 44), gBearing, live ? latest.rBearingTemp : 0f, live); gy += step;
        BarIndicator(new Rect(gx, gy, gw, 44), gVib, live ? latest.rVibration : 0f, live);
        GUI.Label(new Rect(gx, lp.yMax - 18, gw, 16),
            live ? $"Ambient {latest.rAmbientTemp:F1} °C   Faults {latest.nFaultCount}" : "", sDim);

        // ---- right top: RF detector panel
        var rp = new Rect(W - RightPanelW - 8, HeaderH + 8, RightPanelW, 72);
        Panel(rp, "FAULT PREDICTION · RF (PHASE 1)");
        string rfText; GUIStyle rfSt;
        if (!linkOk)                                  { rfText = "No telemetry"; rfSt = rfIdle; }
        else if (det == null)                         { rfText = "Waiting for detector"; rfSt = rfIdle; }
        else if (det.@event == "detector_offline")    { rfText = "Detector offline"; rfSt = rfIdle; }
        else if ((st == 3 || st == 0) && rfVerdict != "") { rfText = rfVerdict; rfSt = rfVerdictGood ? rfGood : rfBad; }
        else if (st == 2 && rfPredFault != "")
        {
            float since = (latest.ts - rfPredTs) / 1000f;
            rfText = $"{rfPredFault.ToUpper()} predicted ({det.confidence:F2})\nPLC not tripped yet, {since:F0} s";
            rfSt = rfWarn;
        }
        else if (st == 2 && det.@event == "prediction") { rfText = $"{det.pred} ({det.confidence:F2})"; rfSt = rfOk; }
        else if (st == 2)                             { rfText = "Warming up (needs 30 s of data)"; rfSt = rfIdle; }
        else                                          { rfText = "Idle (conveyor not running)"; rfSt = rfIdle; }
        GUI.Box(new Rect(rp.x + 8, rp.y + 27, rp.width - 16, rp.height - 34), rfText, rfSt);

        // ---- bottom bar: operator controls and simulation panel
        float cy = H - BottomBarH - 6;
        var cp = new Rect(8, cy, 382, BottomBarH);
        Panel(cp, "OPERATOR CONTROL" + (lastCommand != "" ? "  ·  last: " + lastCommand : ""));
        GUI.enabled = connected;
        float bx = cp.x + 8, by = cp.y + 27, bW = 88, bH = 32;
        if (GUI.Button(new Rect(bx, by, bW, bH), "START", sBtnStart)) SendCommand("start");
        if (GUI.Button(new Rect(bx + (bW + 4), by, bW, bH), "STOP", sBtnStop)) SendCommand("stop");
        if (GUI.Button(new Rect(bx + (bW + 4) * 2, by, bW, bH), "RESET", sBtnNeutral)) SendCommand("reset");
        if (GUI.Button(new Rect(bx + (bW + 4) * 3, by, bW, bH), "MAINT", sBtnNeutral)) SendCommand("maintenance");

        var sp = new Rect(W - 8 - 432, cy, 432, BottomBarH);
        Panel(sp, "SIMULATION · FAULT INJECTION (TEST ONLY)");
        float sx = sp.x + 8, sW = 80, sH = 32;
        if (GUI.Button(new Rect(sx, by, sW, sH), "JAM", sBtnInject)) SendCommand("jam");
        if (GUI.Button(new Rect(sx + (sW + 4), by, sW, sH), "SLIP", sBtnInject)) SendCommand("slip");
        if (GUI.Button(new Rect(sx + (sW + 4) * 2, by, sW, sH), "OVERLOAD", sBtnInject)) SendCommand("overload");
        if (GUI.Button(new Rect(sx + (sW + 4) * 3, by, sW, sH), "WEAR", sBtnInject)) SendCommand("wear");
        if (GUI.Button(new Rect(sx + (sW + 4) * 4, by, sW, sH), "MANUAL", sBtnInject)) SendCommand("fault");
        GUI.enabled = true;

        // ---- trends and diagnostics, on demand (overlay)
        if (showTrends)
        {
            float tw = 340f, th = 3 * 78 + 30 + 34;
            var tp = new Rect(W - tw - 8, rp.yMax + 8, tw, th);
            Panel(tp, "TRENDS · LAST 120 s");
            if (GUI.Button(new Rect(tp.xMax - 26, tp.y + 2, 20, 18), "x", sBtnNeutral)) showTrends = false;
            float tx = tp.x + 10, tW = tw - 20, ty = tp.y + 28;
            TrendBox(new Rect(tx, ty, tW, 76), "MOTOR TEMP", tMotor, live ? latest.rMotorTemp : 0f, "°C", live); ty += 78;
            TrendBox(new Rect(tx, ty, tW, 76), "BEARING TEMP", tBearing, live ? latest.rBearingTemp : 0f, "°C", live); ty += 78;
            TrendBox(new Rect(tx, ty, tW, 76), "MOTOR CURRENT", tCurrent, live ? latest.rMotorCurrent : 0f, "A", live); ty += 80;
            GUI.Label(new Rect(tx, ty, tW, 14),
                $"MQTT {(connected ? "connected" : "reconnecting")}   {msgRate:F1} Hz   msgs {msgCount}   reconnects {reconnects}", sDim);
            GUI.Label(new Rect(tx, ty + 14, tW, 14),
                latN > 0 ? $"latency ({latN})  MQTT {mqttMedian:F0} / p95 {mqttP95:F0} ms   data age {ageMedian:F0} / p95 {ageP95:F0} ms" : "latency: waiting for readTs",
                sDim);
        }
    }

    // one 1x1 texture per colour, created once
    readonly System.Collections.Generic.Dictionary<Color, Texture2D> texCache = new System.Collections.Generic.Dictionary<Color, Texture2D>();
    Texture2D MakeCached(Color c)
    {
        if (!texCache.TryGetValue(c, out var t)) { t = MakeTex(c); texCache[c] = t; }
        return t;
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
