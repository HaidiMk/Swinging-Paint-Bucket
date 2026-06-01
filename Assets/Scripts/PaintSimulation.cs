using UnityEngine;
using System.Collections.Generic;

// ════════════════════════════════════════════════════════════════════
//  PaintSimulation.cs  v6.0  — FINAL REALISTIC
//  أنواع طلاء مختلفة + تأثير حركة الدلو الحقيقي
// ════════════════════════════════════════════════════════════════════

public class PaintSimulation : MonoBehaviour
{
    // ══════════════════════════════════════════════
    [Header("References — المراجع")]
    public SphericalPendulum pendulum;
    public Transform bucketTransform;
    public Transform canvasTransform;
    public Renderer canvasRenderer;

    // ══════════════════════════════════════════════
    [Header("Paint Type — نوع الطلاء")]
    public PaintType paintType = PaintType.Normal;

    public enum PaintType
    {
        Watercolor,   // مائي — رقيق، يتدفق بسرعة
        Normal,       // عادي — متوازن
        Acrylic,      // أكريليك — لزج متوسط
        Oil,          // زيتي — ثقيل جداً، بطيء
        Honey         // عسل — لزوجة قصوى
    }

    // ══════════════════════════════════════════════
    [Header("SPH Physics — فيزياء السائل")]
    [Range(50, 250)]
    public int maxParticles = 150;
    [Range(0.08f, 0.25f)]
    public float R = 0.18f;   // نصف قطر التأثير
    public float G = 9.81f;
    [Range(0.95f, 1f)]
    public float DAMPING = 0.99f;
    public float MAX_VEL = 3f;

    // هذي بتتغير تلقائياً حسب نوع الطلاء
    [HideInInspector] public float K;
    [HideInInspector] public float K_NEAR;
    [HideInInspector] public float REST_DENSITY;
    [HideInInspector] public float SIGMA;
    [HideInInspector] public float gravityScale;  // تأثير الجاذبية على السائل
    [HideInInspector] public float bucketInfluence; // تأثير حركة الدلو

    // ══════════════════════════════════════════════
    [Header("Bucket Size — حجم الدلو")]
    public float bucketWorldRadius = 0.25f;
    public float bucketWorldHeight = 0.4f;
    public Vector3 bucketCenterOffset = new Vector3(0f, -0.15f, 0f);


    // ══════════════════════════════════════════════
    [Header("Visual — التصور البصري")]
    [Range(1000, 15000)]
    public int visualParticleCount = 8000;
    public float visualParticleSize = 0.015f;
    public float visualNoiseStrength = 0.04f;
    public Color paintColor = new Color(0.9f, 0.1f, 0.1f, 1f);
    public Color paintColorDark = new Color(0.5f, 0.05f, 0.05f, 1f);

    // ══════════════════════════════════════════════
    [Header("Canvas — اللوحة")]
    public int canvasWidth = 512;
    public int canvasHeight = 512;
    [Range(1, 40)]
    public int brushSize = 12;
    public bool canvasIsHorizontal = true;

    [Header("Debug")]
    public bool showDebugGizmos = true;

    // ══════════════════════════════════════════════


    // ══════════════════════════════════════════════
    class PhysicsParticle
    {
        public Vector3 pos, prevPos, vel, force;
        public float rho, rhoNear, press, pressNear;
        public List<int> neighbors = new List<int>(32);
        public int gx, gy, gz;
        public PState state;
        public float lifetime;
    }
    enum PState { Inside, Air, OnCanvas }

    List<PhysicsParticle> pts = new List<PhysicsParticle>();
    Dictionary<Vector3Int, List<int>> grid = new Dictionary<Vector3Int, List<int>>();

    ParticleSystem visualPS;
    ParticleSystem.Particle[] visualParticles;
    bool visualDirty = true;
    bool spawnDone = false;

    Texture2D canvasTex;
    Color[] canvasPx;
    bool canvasDirty;
    float paintLeft = 1f;

    // سرعة الدلو السابقة لحساب التسارع
    Vector3 prevBucketVel = Vector3.zero;
    Vector3 bucketAccelWorld = Vector3.zero;

    public Vector3 BucketCenter => bucketTransform.position
                           + bucketTransform.TransformDirection(bucketCenterOffset);
    Vector3 BucketUp => bucketTransform.TransformDirection(Vector3.up).normalized;
    Vector3 BucketRight => bucketTransform.TransformDirection(Vector3.right).normalized;
    Vector3 BucketForward => bucketTransform.TransformDirection(Vector3.forward).normalized;

    // ════════════════════════════════════════════════════════════════
    void Start()
    {
        ApplyPaintType();
        InitCanvas();
        InitVisualPS();
        // Spawn مؤجل لأول FixedUpdate — حتى يكون البندول في موقعه الصح
        Debug.Log($"[PaintV6] Type={paintType} | SIGMA={SIGMA:F3} | K={K} | REST={REST_DENSITY}");
    }

    // ════════════════════════════════════════════════════════════════
    // إعدادات كل نوع طلاء
    // ════════════════════════════════════════════════════════════════
    void ApplyPaintType()
    {
        switch (paintType)
        {
            case PaintType.Watercolor:
                // مائي — يتدفق بحرية كالماء
                K = 0.0002f;
                K_NEAR = 0.002f;
                REST_DENSITY = 3f;
                SIGMA = 0.05f;   // لزوجة منخفضة جداً
                gravityScale = 0.8f;    // يستجيب للجاذبية بسرعة
                bucketInfluence = 0.5f;    // يتأثر بحركة الدلو بقوة
                MAX_VEL = 5f;
                break;

            case PaintType.Normal:
                // عادي — متوازن
                K = 0.0001f;
                K_NEAR = 0.001f;
                REST_DENSITY = 4f;
                SIGMA = 0.2f;
                gravityScale = 0.6f;
                bucketInfluence = 0.35f;
                MAX_VEL = 4f;
                break;

            case PaintType.Acrylic:
                // أكريليك — لزج متوسط
                K = 0.00008f;
                K_NEAR = 0.0008f;
                REST_DENSITY = 5f;
                SIGMA = 0.5f;    // لزوجة متوسطة
                gravityScale = 0.5f;
                bucketInfluence = 0.25f;
                MAX_VEL = 3f;
                break;

            case PaintType.Oil:
                // زيتي — ثقيل وبطيء
                K = 0.00005f;
                K_NEAR = 0.0005f;
                REST_DENSITY = 7f;
                SIGMA = 0.8f;    // لزوجة عالية
                gravityScale = 0.4f;
                bucketInfluence = 0.15f;   // يتأثر ببطء بحركة الدلو
                MAX_VEL = 1.5f;
                DAMPING = 0.995f;
                break;

            case PaintType.Honey:
                // عسل — لزوجة قصوى
                K = 0.00003f;
                K_NEAR = 0.0003f;
                REST_DENSITY = 9f;
                SIGMA = 1.5f;    // لزوجة قصوى
                gravityScale = 0.3f;
                bucketInfluence = 0.08f;   // بالكاد يتحرك مع الدلو
                MAX_VEL = 0.8f;
                DAMPING = 0.998f;
                break;
        }
    }

    // ════════════════════════════════════════════════════════════════
    void FixedUpdate()
    {
        if (!bucketTransform) return;
        float dt = Time.fixedDeltaTime;

        // Spawn مؤجل — ننتظر أول frame بعد ما البندول يحدد موقعه
        if (!spawnDone)
        {
            SpawnParticles();
            spawnDone = true;
            return;
        }

        // حساب تسارع الدلو الحقيقي
        Vector3 currentBucketVel = pendulum != null
            ? pendulum.GetBucketVelocity()
            : Vector3.zero;
        bucketAccelWorld = (currentBucketVel - prevBucketVel) / Mathf.Max(dt, 0.001f);
        prevBucketVel = currentBucketVel;

        BuildGrid();
        ResetSPH();
        CalcDensity();
        CalcPressure();
        ApplyPressure();
        ApplyViscosity();
        ApplyExternal(dt);
        Integrate(dt);
        EnforceBoundary();
        HandleCanvas();

        visualDirty = true;
    }

    void Update()
    {
        if (visualDirty) { UpdateVisualPS(); visualDirty = false; }
        if (canvasDirty)
        {
            canvasTex.SetPixels(canvasPx);
            canvasTex.Apply();
            canvasDirty = false;
        }
    }


    // ════════════════════════════════════════════════════════════════
    // SPH
    // ════════════════════════════════════════════════════════════════
    void BuildGrid()
    {
        grid.Clear();
        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].state != PState.Inside) continue;
            var c = ToCell(pts[i].pos);
            pts[i].gx = c.x; pts[i].gy = c.y; pts[i].gz = c.z;
            if (!grid.ContainsKey(c)) grid[c] = new List<int>(8);
            grid[c].Add(i);
        }
    }

    Vector3Int ToCell(Vector3 p) => new Vector3Int(
        Mathf.FloorToInt(p.x / R),
        Mathf.FloorToInt(p.y / R),
        Mathf.FloorToInt(p.z / R));

    void ResetSPH()
    {
        foreach (var p in pts)
        {
            p.force = Vector3.zero;
            p.rho = p.rhoNear = p.press = p.pressNear = 0f;
            p.neighbors.Clear();
        }
    }

    void CalcDensity()
    {
        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].state != PState.Inside) continue;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        var cell = new Vector3Int(pts[i].gx + dx, pts[i].gy + dy, pts[i].gz + dz);
                        if (!grid.TryGetValue(cell, out var list)) continue;
                        foreach (int j in list)
                        {
                            if (j == i) continue;
                            float dist = Vector3.Distance(pts[i].pos, pts[j].pos);
                            if (dist < R && dist > 0.0001f)
                            {
                                float q = 1f - dist / R;
                                float q2 = q * q;
                                float q3 = q2 * q;
                                pts[i].rho += q2; pts[j].rho += q2;
                                pts[i].rhoNear += q3; pts[j].rhoNear += q3;
                                pts[i].neighbors.Add(j);
                            }
                        }
                    }
        }
    }

    void CalcPressure()
    {
        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].state != PState.Inside) continue;
            pts[i].press = K * (pts[i].rho - REST_DENSITY);
            pts[i].pressNear = K_NEAR * pts[i].rhoNear;
        }
    }

    void ApplyPressure()
    {
        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].state != PState.Inside) continue;
            Vector3 pf = Vector3.zero;
            foreach (int j in pts[i].neighbors)
            {
                Vector3 dir = pts[j].pos - pts[i].pos;
                float dist = dir.magnitude;
                if (dist < 0.0001f) continue;
                float q = 1f - dist / R;
                float totalP = (pts[i].press + pts[j].press) * q * q
                             + (pts[i].pressNear + pts[j].pressNear) * q * q * q;
                Vector3 pv = totalP * dir.normalized;
                pts[j].force += pv;
                pf += pv;
            }
            pts[i].force -= pf;
        }
    }

    void ApplyViscosity()
    {
        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].state != PState.Inside) continue;
            foreach (int j in pts[i].neighbors)
            {
                Vector3 dir = pts[j].pos - pts[i].pos;
                float dist = dir.magnitude;
                if (dist < 0.0001f) continue;
                Vector3 norm = dir.normalized;
                float relDist = dist / R;
                float vDiff = Vector3.Dot(pts[i].vel - pts[j].vel, norm);
                if (vDiff > 0f)
                {
                    Vector3 vf = (1f - relDist) * vDiff * SIGMA * norm;
                    pts[i].vel -= vf * 0.5f;
                    pts[j].vel += vf * 0.5f;
                }
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    // القوى الخارجية — الجاذبية + تأثير الدلو الحقيقي
    // ════════════════════════════════════════════════════════════════
    void ApplyExternal(float dt)
    {
        // تسارع الدلو الحقيقي — يؤثر على السائل كأنه في إناء متحرك
        // القانون: F = -m * a_bucket (قانون نيوتن في الإطار المتحرك)
        Vector3 fluidForceFromBucket = -bucketAccelWorld * bucketInfluence;

        // تحديد حد معقول للقوة
        if (fluidForceFromBucket.magnitude > 15f)
            fluidForceFromBucket = fluidForceFromBucket.normalized * 15f;

        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].state == PState.OnCanvas) continue;

            if (pts[i].state == PState.Inside)
            {
                // جاذبية حسب نوع الطلاء
                pts[i].force += Vector3.down * G * gravityScale;

                // تأثير حركة الدلو — هاد اللي بيخلي السائل يميل
                pts[i].force += fluidForceFromBucket;
            }
            else // Air
            {
                pts[i].force += Vector3.down * G;
                pts[i].force -= pts[i].vel * 0.03f;
                pts[i].lifetime += dt;
            }
        }
    }

    void Integrate(float dt)
    {
        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].state == PState.OnCanvas) continue;
            pts[i].prevPos = pts[i].pos;
            pts[i].vel += pts[i].force * dt;
            pts[i].vel *= DAMPING;
            if (pts[i].vel.magnitude > MAX_VEL)
                pts[i].vel = pts[i].vel.normalized * MAX_VEL;
            pts[i].pos += pts[i].vel * dt;
            if (pts[i].state == PState.Air && pts[i].lifetime > 5f)
                pts.RemoveAt(i--);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // حدود الدلو — جدران صلبة
    // ════════════════════════════════════════════════════════════════
    void EnforceBoundary()
    {
        Vector3 center = BucketCenter;
        Vector3 up = BucketUp;
        float R2 = bucketWorldRadius * 0.88f;
        float H2 = bucketWorldHeight * 0.5f;
        float mg = R * 0.4f;

        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].state != PState.Inside) continue;

            Vector3 offset = pts[i].pos - center;
            float vert = Vector3.Dot(offset, up);
            Vector3 horizVec = offset - up * vert;
            float horizDist = horizVec.magnitude;

            // القاع
            if (vert < -H2 + mg)
            {
                vert = -H2 + mg;
                pts[i].pos = center + horizVec + up * vert;
                float vy = Vector3.Dot(pts[i].vel, up);
                if (vy < 0f) pts[i].vel -= up * vy * 1.5f;
            }

            // السقف
            if (vert > H2 * 0.55f)
            {
                vert = H2 * 0.55f;
                pts[i].pos = center + horizVec + up * vert;
                float vy = Vector3.Dot(pts[i].vel, up);
                if (vy > 0f) pts[i].vel -= up * vy * 0.6f;
            }

            // الجدار الجانبي
            if (horizDist > R2 - mg && horizDist > 0.001f)
            {
                Vector3 inward = -horizVec.normalized;
                pts[i].pos = center + (-inward) * (R2 - mg) + up * vert;
                float vOut = Vector3.Dot(pts[i].vel, -inward);
                if (vOut > 0f) pts[i].vel += inward * vOut * 1.5f;
            }
        }
    }


    // ════════════════════════════════════════════════════════════════
    // Particle System البصري
    // ════════════════════════════════════════════════════════════════
    void InitVisualPS()
    {
        var go = new GameObject("PaintVisualPS");
        go.transform.SetParent(transform);
        visualPS = go.AddComponent<ParticleSystem>();

        var main = visualPS.main;
        main.loop = false;
        main.playOnAwake = false;
        main.maxParticles = visualParticleCount + 100;
        main.startLifetime = float.MaxValue;
        main.startSize = visualParticleSize;
        main.startColor = paintColor;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var emission = visualPS.emission;
        emission.enabled = false;
        var psShape = visualPS.shape;
        psShape.enabled = false;

        var ren = visualPS.GetComponent<ParticleSystemRenderer>();
        ren.renderMode = ParticleSystemRenderMode.Billboard;

        // نستخدم Shader بسيط موجود دايماً
        var mat = new Material(Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color"));
        // shader fallback handled above

        ren.material = mat;
        ren.material.color = paintColor;

        visualParticles = new ParticleSystem.Particle[visualParticleCount];
        visualPS.Play();
    }

    void UpdateVisualPS()
    {
        if (visualPS == null || pts.Count == 0) return;

        Vector3 center = BucketCenter;
        Vector3 up = BucketUp;
        Vector3 right = BucketRight;
        Vector3 fwd = BucketForward;

        var insidePts = new List<PhysicsParticle>(pts.Count);
        var airPts = new List<PhysicsParticle>();
        foreach (var p in pts)
        {
            if (p.state == PState.Inside) insidePts.Add(p);
            else if (p.state == PState.Air) airPts.Add(p);
        }

        int insideVisual = Mathf.RoundToInt(visualParticleCount * paintLeft * 0.95f);
        int vIdx = 0;

        // جسيمات الداخل
        for (int v = 0; v < insideVisual && vIdx < visualParticleCount; v++, vIdx++)
        {
            Vector3 basePos;
            if (insidePts.Count > 0)
            {
                var rp = insidePts[v % insidePts.Count];
                basePos = rp.pos + Random.insideUnitSphere * visualNoiseStrength;
            }
            else
            {
                float a = Random.Range(0f, Mathf.PI * 2f);
                float r = Random.Range(0f, bucketWorldRadius * 0.8f);
                float h = Random.Range(-bucketWorldHeight * 0.45f, 0f);
                basePos = center + right * (Mathf.Cos(a) * r) + fwd * (Mathf.Sin(a) * r) + up * h;
            }

            float depth = Vector3.Dot(basePos - center, up);
            float t = Mathf.InverseLerp(-bucketWorldHeight * 0.5f, 0f, depth);
            Color c = Color.Lerp(paintColorDark, paintColor, t);

            visualParticles[vIdx].position = basePos;
            visualParticles[vIdx].startColor = c;
            visualParticles[vIdx].startSize = visualParticleSize * Random.Range(0.8f, 1.2f);
            visualParticles[vIdx].remainingLifetime = float.MaxValue;
            visualParticles[vIdx].startLifetime = float.MaxValue;
            visualParticles[vIdx].velocity = Vector3.zero;
        }

        // جسيمات الهواء
        foreach (var ap in airPts)
        {
            int spread = Mathf.Min(4, visualParticleCount - vIdx);
            for (int s = 0; s < spread && vIdx < visualParticleCount; s++, vIdx++)
            {
                visualParticles[vIdx].position = ap.pos + Random.insideUnitSphere * 0.02f;
                visualParticles[vIdx].startColor = paintColor * 1.3f;
                visualParticles[vIdx].startSize = visualParticleSize * 2f;
                visualParticles[vIdx].remainingLifetime = float.MaxValue;
                visualParticles[vIdx].startLifetime = float.MaxValue;
                visualParticles[vIdx].velocity = ap.vel;
            }
        }

        for (int v = vIdx; v < visualParticleCount; v++)
        {
            visualParticles[v].remainingLifetime = 0f;
            visualParticles[v].startLifetime = 0f;
        }

        visualPS.SetParticles(visualParticles, visualParticleCount);
    }

    // ════════════════════════════════════════════════════════════════
    // اللوحة
    // ════════════════════════════════════════════════════════════════
    void InitCanvas()
    {
        canvasTex = new Texture2D(canvasWidth, canvasHeight, TextureFormat.RGBA32, false);
        canvasPx = new Color[canvasWidth * canvasHeight];
        var bg = new Color(0.95f, 0.92f, 0.85f, 1f);
        for (int i = 0; i < canvasPx.Length; i++) canvasPx[i] = bg;
        canvasTex.SetPixels(canvasPx); canvasTex.Apply();
        if (canvasRenderer) canvasRenderer.material.mainTexture = canvasTex;
    }

    void HandleCanvas()
    {
        if (!canvasTransform) return;
        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].state != PState.Air) continue;
            bool hit = canvasIsHorizontal
                ? pts[i].pos.y <= canvasTransform.position.y + 0.02f
                : Vector3.Dot(pts[i].pos - canvasTransform.position, canvasTransform.up) <= 0.02f;
            if (!hit) continue;
            DrawOnCanvas(pts[i].pos, pts[i].vel.magnitude);
            pts[i].state = PState.OnCanvas;
        }
    }

    void DrawOnCanvas(Vector3 wp, float speed)
    {
        Vector3 lp = canvasTransform.InverseTransformPoint(wp);
        int cx = Mathf.RoundToInt(Mathf.Clamp01(lp.x + 0.5f) * canvasWidth);
        int cy = Mathf.RoundToInt(Mathf.Clamp01(lp.z + 0.5f) * canvasHeight);

        // حجم البقعة يعتمد على السرعة + نوع الطلاء
        float spreadMult = paintType == PaintType.Watercolor ? 2f :
                           paintType == PaintType.Oil ? 0.6f : 1f;
        int r = Mathf.RoundToInt(brushSize * Mathf.Clamp(speed * 0.2f, 0.5f, 3f) * spreadMult);

        FillCircle(cx, cy, r, paintColor);

        // تناثر — الطلاء المائي يتناثر أكثر
        int splats = paintType == PaintType.Watercolor ? 6 :
                     paintType == PaintType.Oil ? 1 : 3;
        for (int s = 0; s < splats; s++)
        {
            float a = Random.Range(0f, Mathf.PI * 2f);
            int d = Random.Range(r, r + (int)(20f / Mathf.Max(SIGMA, 0.1f)));
            FillCircle(cx + (int)(Mathf.Cos(a) * d), cy + (int)(Mathf.Sin(a) * d),
                       Mathf.Max(r / 3, 1), paintColor * 0.7f);
        }
        canvasDirty = true;
    }

    void FillCircle(int cx, int cy, int r, Color c)
    {
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                if (dx * dx + dy * dy > r * r) continue;
                int px = Mathf.Clamp(cx + dx, 0, canvasWidth - 1);
                int py = Mathf.Clamp(cy + dy, 0, canvasHeight - 1);
                int idx = py * canvasWidth + px;
                canvasPx[idx] = Color.Lerp(canvasPx[idx], c, c.a * 0.85f);
            }
    }

    void SpawnParticles()
    {
        Vector3 center = BucketCenter;
        Vector3 up = BucketUp, right = BucketRight, fwd = BucketForward;
        for (int i = 0; i < maxParticles; i++)
        {
            float a = Random.Range(0f, Mathf.PI * 2f);
            float r = Random.Range(0f, bucketWorldRadius * 0.9f);
            float h = Random.Range(-bucketWorldHeight * 0.45f, bucketWorldHeight * 0.15f);
            pts.Add(new PhysicsParticle
            {
                pos = center + right * (Mathf.Cos(a) * r) + fwd * (Mathf.Sin(a) * r) + up * h,
                state = PState.Inside
            });
        }
        Debug.Log($"[PaintV6] Spawned {pts.Count} at {center}");
    }

    // ════════════════════════════════════════════════════════════════
    // API
    // ════════════════════════════════════════════════════════════════
    public void RefillBucket()
    { pts.Clear(); paintLeft = 1f; SpawnParticles(); }

    public void ClearCanvas()
    {
        var bg = new Color(0.95f, 0.92f, 0.85f, 1f);
        for (int i = 0; i < canvasPx.Length; i++) canvasPx[i] = bg;
        canvasTex.SetPixels(canvasPx); canvasTex.Apply();
    }

    public void SaveCanvas(string path = "PaintResult.png")
    { System.IO.File.WriteAllBytes(path, canvasTex.EncodeToPNG()); }

    public float GetPaintLeft() => paintLeft;
    public int InsideCount() { int c = 0; foreach (var p in pts) if (p.state == PState.Inside) c++; return c; }
    public int AirborneCount() { int c = 0; foreach (var p in pts) if (p.state == PState.Air) c++; return c; }

    // ════════════════════════════════════════════════════════════════
    void OnGUI()
    {

        int x = 10, y = 220, lh = 22;
        GUI.Label(new Rect(x, y, 350, lh), $"Paint Type  : {paintType}");
        GUI.Label(new Rect(x, y + lh, 350, lh), $"Paint Left  : {paintLeft * 100f:F1}%");
        GUI.Label(new Rect(x, y + lh * 2, 350, lh), $"Inside      : {InsideCount()} / {maxParticles}");
        GUI.Label(new Rect(x, y + lh * 3, 350, lh), $"Airborne    : {AirborneCount()}");
        GUI.Label(new Rect(x, y + lh * 6, 350, lh), $"Viscosity   : {SIGMA:F2}  (SIGMA)");
        GUI.Label(new Rect(x, y + lh * 7, 350, lh), $"Bucket Accel: {bucketAccelWorld.magnitude:F2} m/s²");
        GUI.Label(new Rect(x, y + lh * 8, 350, lh), $"Visual PS   : {visualParticleCount}");
    }

    void OnDrawGizmos()
    {
        if (!showDebugGizmos || !bucketTransform) return;
        Vector3 c = BucketCenter, up = BucketUp, r = BucketRight, f = BucketForward;
        float H2 = bucketWorldHeight * 0.5f, R2 = bucketWorldRadius;
        Gizmos.color = new Color(0f, 1f, 0f, 0.25f);
        for (int s = 0; s < 16; s++)
        {
            float a1 = s / 16f * Mathf.PI * 2f, a2 = (s + 1) / 16f * Mathf.PI * 2f;
            Vector3 p1 = c + (r * Mathf.Cos(a1) + f * Mathf.Sin(a1)) * R2;
            Vector3 p2 = c + (r * Mathf.Cos(a2) + f * Mathf.Sin(a2)) * R2;
            Gizmos.DrawLine(p1 - up * H2, p2 - up * H2);
            Gizmos.DrawLine(p1 + up * H2 * 0.55f, p2 + up * H2 * 0.55f);
            Gizmos.DrawLine(p1 - up * H2, p1 + up * H2 * 0.55f);
        }
        Gizmos.color = paintColor;
        foreach (var p in pts)
            Gizmos.DrawSphere(p.pos, p.state == PState.Inside ? 0.012f : 0.018f);
    }
}