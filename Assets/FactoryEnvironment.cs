using UnityEngine;

// Builds a simple factory hall around the existing belt at runtime: concrete floor with safety
// markings, cladded walls, roof beams and high-bay lights, a conveyor frame (rails, legs, end
// drums, drive motor), a control cabinet with a stack light that follows the PLC state, a pallet
// and bollards. It also frames the camera so the belt sits in the free area between the HMI panels.
//
// No scene editing needed: it creates itself when Play starts. To remove it, delete this file.
// The camera frames BeltVisual's visible section; the extended belt ends stay off-screen.
// Materials are copies of the belt's own material, so it works in the built-in pipeline and URP.
public class FactoryEnvironment : MonoBehaviour
{
    [Header("Camera framing")]
    public bool autoFrameCamera = true;
    [Range(0.2f, 1.0f)] public float beltWidthOnScreen = 0.85f;  // share of the free screen width
    public float elevationDeg = 22f;

    Bounds belt;
    Vector3 axis, side, fwd;      // belt long axis, belt cross axis, horizontal view direction
    float L, Lv, Wd, H, floorY;   // L = full belt length, Lv = visible section
    Material template;
    Transform root;
    Camera cam;
    BeltTwinClient hmi;
    Renderer lampGreen, lampAmber, lampRed;
    Material mGreenOn, mGreenOff, mAmberOn, mAmberOff, mRedOn, mRedOff;
    int lastState = -99;
    bool ready;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindObjectOfType<FactoryEnvironment>() == null)
            new GameObject("FactoryEnvironment").AddComponent<FactoryEnvironment>();
    }

    void Start()
    {
        var r = FindBeltRenderer();
        if (r == null) { Debug.LogWarning("FactoryEnvironment: no renderer under 'BeltVisual' or 'Belt' found, nothing built."); return; }
        belt = r.bounds;
        template = r.sharedMaterial;
        cam = Camera.main;
        hmi = FindObjectOfType<BeltTwinClient>();

        bool alongX = belt.size.x >= belt.size.z;
        axis = alongX ? Vector3.right : Vector3.forward;
        side = alongX ? Vector3.forward : Vector3.right;
        L = alongX ? belt.size.x : belt.size.z;
        Wd = alongX ? belt.size.z : belt.size.x;
        H = Mathf.Max(Wd * 2.2f, 0.3f);                 // belt height above the floor
        var bv = FindObjectOfType<BeltVisual>();
        Lv = (bv != null && bv.VisibleLength > 0f && bv.VisibleLength < L) ? bv.VisibleLength : L;
        floorY = belt.min.y - H;

        fwd = cam != null ? Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up) : Vector3.forward;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        if (Vector3.Dot(side, fwd) < 0) side = -side;    // "side" points away from the camera

        root = new GameObject("Factory").transform;
        HideOldGround(r);
        BuildHall();
        BuildConveyorFrame();
        BuildProps();
        SetupLighting();
        ready = true;
    }

    // ------------------------------------------------------------------ helpers
    Renderer FindBeltRenderer()
    {
        Renderer best = null; float bestArea = 0f;
        foreach (var name in new[] { "BeltVisual", "Belt" })
        {
            var go = GameObject.Find(name);
            if (go == null) continue;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                float a = r.bounds.size.x * r.bounds.size.z;
                if (a > bestArea) { bestArea = a; best = r; }
            }
        }
        return best;
    }

    void HideOldGround(Renderer beltR)
    {
        // a large existing ground plane under the belt would hide the new floor
        var start = belt.center - Vector3.up * (belt.extents.y + 0.01f);
        foreach (var hit in Physics.RaycastAll(start, Vector3.down, 1000f))
        {
            var gr = hit.collider.GetComponent<Renderer>();
            if (gr == null || gr == beltR) continue;
            if (gr.bounds.size.x * gr.bounds.size.z > 4f * belt.size.x * belt.size.z) gr.enabled = false;
        }
    }

    Material Mat(Color c, float metallic = 0f, float smooth = 0.25f)
    {
        var m = new Material(template);
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", null);
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", null);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        return m;
    }

    Material Glow(Color c)
    {
        var m = Mat(c, 0f, 0.6f);
        if (m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c * 1.5f);
        }
        return m;
    }

    GameObject Box(string name, Vector3 center, Vector3 size, Material m, PrimitiveType type = PrimitiveType.Cube)
    {
        var g = GameObject.CreatePrimitive(type);
        g.name = name;
        var col = g.GetComponent<Collider>();
        if (col != null) Destroy(col);                  // decoration only, no physics
        g.transform.SetParent(root, false);
        g.transform.position = center;
        g.transform.localScale = size;
        g.GetComponent<Renderer>().sharedMaterial = m;
        return g;
    }

    // box aligned with the belt: a = along the belt, s = across, y = up
    GameObject BeltBox(string name, float a, float s, float y, float la, float ls, float ly, Material m)
    {
        var c = new Vector3(belt.center.x, 0, belt.center.z) + axis * a + side * s + Vector3.up * y;
        var size = axis * la + side * ls + Vector3.up * ly;
        return Box(name, c, new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)), m);
    }

    GameObject Cylinder(string name, Vector3 center, float diameter, float length, Vector3 dir, Material m)
    {
        var g = Box(name, center, new Vector3(diameter, length / 2f, diameter), m, PrimitiveType.Cylinder);
        g.transform.rotation = Quaternion.FromToRotation(Vector3.up, dir);
        return g;
    }

    // ------------------------------------------------------------------ building
    void BuildHall()
    {
        float span = Mathf.Max(Mathf.Max(Lv * 6f, L * 1.3f), 20f * Wd);
        float wallH = Mathf.Max(H * 5f, 8f * Wd);
        var concrete = Mat(new Color(0.44f, 0.44f, 0.42f), 0f, 0.15f);
        var cladding = Mat(new Color(0.60f, 0.64f, 0.68f), 0.1f, 0.3f);
        var dado = Mat(new Color(0.28f, 0.31f, 0.34f), 0.1f, 0.3f);
        var steel = Mat(new Color(0.20f, 0.22f, 0.25f), 0.6f, 0.4f);
        var yellow = Mat(new Color(0.95f, 0.75f, 0.05f), 0f, 0.3f);
        var green = Mat(new Color(0.20f, 0.45f, 0.28f), 0f, 0.2f);

        // floor and markings
        BeltBox("Floor", 0, 0, floorY - 0.05f * Wd, span, span, 0.1f * Wd, concrete);
        int hallStart = root.childCount;
        float mx = L / 2f + 1.6f * Wd, ms = Wd / 2f + 1.6f * Wd, t = 0.12f * Wd, ty = floorY + 0.004f;
        BeltBox("Marking", 0, -ms, ty, 2 * mx, t, 0.01f, yellow);
        BeltBox("Marking", 0, ms, ty, 2 * mx, t, 0.01f, yellow);
        BeltBox("Marking", -mx, 0, ty, t, 2 * ms, 0.01f, yellow);
        BeltBox("Marking", mx, 0, ty, t, 2 * ms, 0.01f, yellow);
        BeltBox("Walkway", 0, -ms - 2.2f * Wd, ty, span, 1.6f * Wd, 0.008f, green);

        // walls: behind the belt and both ends
        float back = span * 0.3f;
        BeltBox("WallBack", 0, back, floorY + wallH / 2f, span, 0.2f * Wd, wallH, cladding);
        BeltBox("DadoBack", 0, back - 0.11f * Wd, floorY + H * 0.75f, span, 0.02f * Wd, H * 1.5f, dado);
        BeltBox("WallLeft", -span / 2f, back / 2f, floorY + wallH / 2f, 0.2f * Wd, span, wallH, cladding);
        BeltBox("WallRight", span / 2f, back / 2f, floorY + wallH / 2f, 0.2f * Wd, span, wallH, cladding);

        // roof beams and high-bay lights
        for (int i = -3; i <= 3; i++)
        {
            float a = i * span / 7f;
            BeltBox("RoofBeam", a, back / 2f - span / 4f, floorY + wallH, 0.25f * Wd, span, 0.4f * Wd, steel);
            if (Mathf.Abs(i) <= 1)
            {
                BeltBox("Lamp", a, 0, floorY + wallH - 0.6f * Wd, 1.2f * Wd, 0.5f * Wd, 0.08f * Wd, Glow(new Color(1f, 0.96f, 0.88f)));
                var lg = new GameObject("LampLight").AddComponent<Light>();
                lg.transform.SetParent(root, false);
                lg.transform.position = new Vector3(belt.center.x, 0, belt.center.z) + axis * a + Vector3.up * (floorY + wallH - 0.8f * Wd);
                lg.type = LightType.Point;
                lg.range = wallH * 1.8f;
                lg.intensity = 1.1f;
                lg.color = new Color(1f, 0.95f, 0.86f);
                lg.shadows = LightShadows.None;
            }
        }
        // walls, roof beams and lamps must not cast shadows: the beams drew diagonal stripes across the floor
        for (int i = hallStart; i < root.childCount; i++)
        {
            var hr = root.GetChild(i).GetComponent<Renderer>();
            if (hr != null) hr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }

    void BuildConveyorFrame()
    {
        var alu = Mat(new Color(0.72f, 0.74f, 0.76f), 0.7f, 0.55f);
        var steel = Mat(new Color(0.24f, 0.27f, 0.30f), 0.5f, 0.35f);
        var drum = Mat(new Color(0.35f, 0.37f, 0.40f), 0.8f, 0.6f);
        var motorBlue = Mat(new Color(0.10f, 0.28f, 0.52f), 0.3f, 0.45f);
        var guardYellow = Mat(new Color(0.95f, 0.75f, 0.05f), 0.1f, 0.35f);

        float top = belt.max.y, bottom = belt.min.y;
        float railS = Wd / 2f + 0.05f * Wd, railH = 0.22f * Wd;
        // side rails (aluminium profiles), slightly above the belt surface
        BeltBox("RailFront", 0, -railS, top - railH / 2f + 0.06f * Wd, L * 1.02f, 0.08f * Wd, railH, alu);
        BeltBox("RailBack", 0, railS, top - railH / 2f + 0.06f * Wd, L * 1.02f, 0.08f * Wd, railH, alu);

        // legs and cross braces
        int n = Mathf.Max(2, Mathf.CeilToInt(L / (4.5f * Wd)) + 1);
        for (int i = 0; i < n; i++)
        {
            float a = -L / 2f + 0.06f * L + i * (0.88f * L) / (n - 1);
            float legH = bottom - floorY;
            BeltBox("Leg", a, -railS, floorY + legH / 2f, 0.1f * Wd, 0.1f * Wd, legH, steel);
            BeltBox("Leg", a, railS, floorY + legH / 2f, 0.1f * Wd, 0.1f * Wd, legH, steel);
            BeltBox("Brace", a, 0, floorY + legH * 0.35f, 0.06f * Wd, 2 * railS, 0.06f * Wd, steel);
            BeltBox("Foot", a, -railS, floorY + 0.02f * Wd, 0.22f * Wd, 0.22f * Wd, 0.04f * Wd, steel);
            BeltBox("Foot", a, railS, floorY + 0.02f * Wd, 0.22f * Wd, 0.22f * Wd, 0.04f * Wd, steel);
        }
        BeltBox("Stringer", 0, -railS, floorY + (bottom - floorY) * 0.35f, 0.88f * L, 0.06f * Wd, 0.06f * Wd, steel);
        BeltBox("Stringer", 0, railS, floorY + (bottom - floorY) * 0.35f, 0.88f * L, 0.06f * Wd, 0.06f * Wd, steel);

        // end drums
        float dDrum = Mathf.Max(belt.size.y * 1.6f, 0.25f * Wd);
        var c0 = new Vector3(belt.center.x, belt.center.y, belt.center.z);
        Cylinder("DrumHead", c0 + axis * (L / 2f), dDrum, Wd * 1.04f, side, drum);
        Cylinder("DrumTail", c0 - axis * (L / 2f), dDrum, Wd * 1.04f, side, drum);

        // centre drive inside the visible section, on the far side (away from the camera), with a yellow guard
        var head = c0 + axis * (Lv * 0.32f);
        Box("Gearbox", head + side * (railS + 0.3f * Wd) + Vector3.down * 0.05f * Wd,
            Abs(axis * 0.45f * Wd + side * 0.4f * Wd + Vector3.up * 0.45f * Wd), steel);
        Cylinder("Motor", head + side * (railS + 0.95f * Wd) + Vector3.down * 0.05f * Wd, 0.42f * Wd, 0.9f * Wd, side, motorBlue);
        Box("DriveGuard", head + axis * 0.05f * Wd + side * (railS + 0.02f * Wd),
            Abs(axis * 0.5f * Wd + side * 0.04f * Wd + Vector3.up * dDrum * 1.6f), guardYellow);
    }

    static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    void BuildProps()
    {
        var cabinet = Mat(new Color(0.78f, 0.79f, 0.77f), 0.2f, 0.4f);
        var doorLine = Mat(new Color(0.35f, 0.36f, 0.36f), 0.2f, 0.4f);
        var dark = Mat(new Color(0.12f, 0.12f, 0.13f), 0.2f, 0.4f);
        var wood = Mat(new Color(0.55f, 0.40f, 0.24f), 0f, 0.1f);
        var carton = Mat(new Color(0.66f, 0.51f, 0.32f), 0f, 0.1f);
        var yellow = Mat(new Color(0.95f, 0.75f, 0.05f), 0f, 0.35f);

        // control cabinet with stack light, behind the tail end
        float ca = -Lv * 0.38f, cs = Wd / 2f + 1.3f * Wd, cH = H * 1.9f;
        BeltBox("Cabinet", ca, cs, floorY + cH / 2f, 1.1f * Wd, 0.6f * Wd, cH, cabinet);
        BeltBox("CabinetDoorLine", ca, cs - 0.31f * Wd, floorY + cH / 2f, 0.02f * Wd, 0.01f * Wd, cH * 0.9f, doorLine);
        BeltBox("CabinetBase", ca, cs, floorY + 0.05f * Wd, 1.12f * Wd, 0.62f * Wd, 0.1f * Wd, dark);

        mGreenOn = Glow(new Color(0.1f, 0.9f, 0.2f)); mGreenOff = Mat(new Color(0.08f, 0.22f, 0.10f), 0f, 0.6f);
        mAmberOn = Glow(new Color(1f, 0.65f, 0.05f)); mAmberOff = Mat(new Color(0.28f, 0.20f, 0.05f), 0f, 0.6f);
        mRedOn = Glow(new Color(1f, 0.12f, 0.08f)); mRedOff = Mat(new Color(0.28f, 0.06f, 0.05f), 0f, 0.6f);
        float d = 0.22f * Wd, seg = 0.2f * Wd, y0 = floorY + cH + 0.05f * Wd;
        var basePos = new Vector3(belt.center.x, 0, belt.center.z) + axis * (ca + 0.35f * Wd) + side * cs;
        Cylinder("StackPole", basePos + Vector3.up * (y0 + 0.05f * Wd), d * 0.4f, 0.1f * Wd, Vector3.up, dark);
        lampGreen = Cylinder("StackGreen", basePos + Vector3.up * (y0 + 0.1f * Wd + seg * 0.5f), d, seg, Vector3.up, mGreenOff).GetComponent<Renderer>();
        lampAmber = Cylinder("StackAmber", basePos + Vector3.up * (y0 + 0.1f * Wd + seg * 1.5f), d, seg, Vector3.up, mAmberOff).GetComponent<Renderer>();
        lampRed = Cylinder("StackRed", basePos + Vector3.up * (y0 + 0.1f * Wd + seg * 2.5f), d, seg, Vector3.up, mRedOff).GetComponent<Renderer>();

        // pallet with cartons behind the belt
        float pa = Lv * 0.12f, ps = Wd / 2f + 1.6f * Wd;
        BeltBox("Pallet", pa, ps, floorY + 0.08f * Wd, 1.2f * Wd, 1.0f * Wd, 0.16f * Wd, wood);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                BeltBox("Carton", pa + (i - 0.5f) * 0.55f * Wd, ps + (j - 0.5f) * 0.47f * Wd, floorY + 0.16f * Wd + 0.22f * Wd,
                        0.52f * Wd, 0.44f * Wd, 0.44f * Wd, carton);
        BeltBox("Carton", pa, ps, floorY + 0.16f * Wd + 0.66f * Wd, 0.52f * Wd, 0.44f * Wd, 0.44f * Wd, carton);

        // bollards along the front of the marked zone, inside the visible section
        float mx = Lv * 0.45f, ms = Wd / 2f + 1.6f * Wd;
        foreach (var sa in new[] { -1f, 1f })
            foreach (var ss in new[] { -1f })
                Cylinder("Bollard", new Vector3(belt.center.x, 0, belt.center.z) + axis * (sa * mx) + side * (ss * ms) + Vector3.up * (floorY + 0.45f * H),
                         0.16f * Wd, 0.9f * H, Vector3.up, yellow);
    }

    void SetupLighting()
    {
        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.10f, 0.11f, 0.12f);
        }
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.36f, 0.37f, 0.39f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.12f, 0.13f, 0.14f);
        RenderSettings.fogStartDistance = Lv * 2.5f;
        RenderSettings.fogEndDistance = Lv * 9f;
        foreach (var l in FindObjectsOfType<Light>())
            if (l.type == LightType.Directional) l.intensity = Mathf.Min(l.intensity, 0.7f);
    }

    // ------------------------------------------------------------------ per frame
    void LateUpdate()
    {
        if (!ready) return;

        if (hmi != null && hmi.latest != null)
        {
            int st = hmi.linkOk ? hmi.latest.eState : -1;
            if (st != lastState)
            {
                lampGreen.sharedMaterial = st == 2 ? mGreenOn : mGreenOff;
                lampAmber.sharedMaterial = (st == 0 || st == 1 || st == -1) ? mAmberOn : mAmberOff;
                lampRed.sharedMaterial = st == 3 ? mRedOn : mRedOff;
                lastState = st;
            }
        }

        if (autoFrameCamera && cam != null && Screen.width > 0)
        {
            // free area between the HMI side panels, as a share of the full screen width
            float free = Mathf.Clamp01((Screen.width - BeltTwinClient.LeftPanelW - BeltTwinClient.RightPanelW - 32f) / Screen.width);
            float frac = Mathf.Max(0.15f, free * beltWidthOnScreen);
            float vfov = cam.fieldOfView * Mathf.Deg2Rad;
            float hfov = 2f * Mathf.Atan(Mathf.Tan(vfov / 2f) * cam.aspect);
            float dist = (Lv * 0.5f) / (Mathf.Tan(hfov / 2f) * frac);   // frame the visible section only
            float el = elevationDeg * Mathf.Deg2Rad;
            // keep the panels' horizontal offset in mind: shift the view so the belt sits mid-gap
            float shift = (BeltTwinClient.RightPanelW - BeltTwinClient.LeftPanelW) / Screen.width;
            Vector3 target = belt.center + Vector3.down * belt.size.y * 0.5f;
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            Vector3 pos = target - fwd * dist * Mathf.Cos(el) + Vector3.up * dist * Mathf.Sin(el)
                          + right * (shift * dist * Mathf.Tan(hfov / 2f));
            cam.transform.position = pos;
            cam.transform.rotation = Quaternion.LookRotation(target + right * (shift * dist * Mathf.Tan(hfov / 2f)) - pos, Vector3.up);
        }
    }
}
