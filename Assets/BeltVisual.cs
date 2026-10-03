using UnityEngine;

// Moves the carriers (boxes) with the PLC's rPosition and colours the belt by PLC state.
// The belt is extended by hiddenExtension at both ends, beyond the visible section
// (beltLength), and the carriers wrap around over the whole extended length. So boxes are
// created and removed off-screen: they enter the picture on one side and leave on the other.
public class BeltVisual : MonoBehaviour
{
    public BeltTwinClient client;
    public Renderer beltRenderer;
    public float beltLength = 10f;        // visible section, framed by the camera
    public float hiddenExtension = 8f;    // extra belt beyond each end of the visible section
    public int carrierCount = 6;          // carriers on the visible section
    public float posScale = 0.01f;        // world units per rPosition unit

    Transform[] carriers;
    float unwrapped, displayed, lastPos, totalLength;
    bool hasLast;

    public float VisibleLength => beltLength * Mathf.Abs(transform.lossyScale.x);

    void Awake()
    {
        totalLength = beltLength + 2f * hiddenExtension;
        // Stretch the belt mesh symmetrically so it continues off-screen. Done in Awake so that
        // FactoryEnvironment (Start) builds the frame around the full length.
        if (beltRenderer != null && hiddenExtension > 0f)
        {
            var tr = beltRenderer.transform;
            var s = tr.localScale;
            tr.localScale = new Vector3(s.x * totalLength / beltLength, s.y, s.z);
        }
        else if (beltRenderer == null)
        {
            Debug.LogWarning("BeltVisual: beltRenderer not set, belt not extended; boxes will wrap beyond its ends.");
        }
    }

    void Start()
    {
        int n = Mathf.Max(1, Mathf.RoundToInt(carrierCount * totalLength / beltLength));
        carriers = new Transform[n];
        for (int i = 0; i < n; i++)
        {
            var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
            c.name = "Carrier" + i;
            c.transform.SetParent(transform);
            c.transform.localScale = new Vector3(0.6f, 0.6f, 0.6f);
            carriers[i] = c.transform;
        }
    }

    void Update()
    {
        var t = client != null ? client.latest : null;

        if (t != null)
        {
            // rPosition wraps at 1000: unwrap it so motion stays continuous
            if (!hasLast) { lastPos = t.rPosition; hasLast = true; }
            float d = t.rPosition - lastPos;
            if (d < -500f) d += 1000f;
            else if (d > 500f) d -= 1000f;
            unwrapped += d;
            lastPos = t.rPosition;
        }

        // Smooth between 4 Hz updates so motion isn't jerky
        displayed = Mathf.Lerp(displayed, unwrapped, 1f - Mathf.Exp(-8f * Time.deltaTime));

        float spacing = totalLength / carriers.Length;
        float offset = displayed * posScale;
        for (int i = 0; i < carriers.Length; i++)
        {
            float x = Mathf.Repeat(offset + i * spacing, totalLength) - totalLength / 2f;
            carriers[i].localPosition = new Vector3(x, 0.9f, 0f);
        }

        if (beltRenderer != null)
        {
            bool live = client != null && client.linkOk && t != null;
            beltRenderer.material.color = live ? StateColour(t.eState) : new Color(0.3f, 0.3f, 0.3f);
        }
    }

    Color StateColour(int s)
    {
        switch (s)
        {
            case 1: return new Color(0.90f, 0.80f, 0.20f); // Starting: yellow
            case 2: return new Color(0.20f, 0.70f, 0.30f); // Running: green
            case 3: return new Color(0.85f, 0.20f, 0.20f); // Faulted: red
            default: return new Color(0.50f, 0.50f, 0.50f); // Stopped: grey
        }
    }
}
