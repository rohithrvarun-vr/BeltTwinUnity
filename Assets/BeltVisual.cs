using UnityEngine;

public class BeltVisual : MonoBehaviour
{
    public BeltTwinClient client;
    public Renderer beltRenderer;
    public float beltLength = 10f;
    public int carrierCount = 6;
    public float posScale = 0.01f;   // world units per rPosition unit

    Transform[] carriers;
    float unwrapped, displayed, lastPos;
    bool hasLast;

    void Start()
    {
        carriers = new Transform[carrierCount];
        for (int i = 0; i < carrierCount; i++)
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

        float spacing = beltLength / carrierCount;
        float offset = displayed * posScale;
        for (int i = 0; i < carrierCount; i++)
        {
            float x = Mathf.Repeat(offset + i * spacing, beltLength) - beltLength / 2f;
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