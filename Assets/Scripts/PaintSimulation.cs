using UnityEngine;
using System.Collections.Generic;

// ════════════════════════════════════════════════════════════════════
//  PaintSimulation.cs  v6.2  — مع إصلاح حساب زاوية الإمالة
//  الإصلاحات عن v6.1:
//  [FIX-7] CheckSpillCondition — tiltAngle تُحسب من pendulum.theta مباشرةً
//          بدل Vector3.Angle(BucketUp, Vector3.up)
//          السبب: BucketUp مشتق من bucket.rotation اللي يعتمد على ropeDir،
//          وعند theta صغير (الدلو قريب من أسفل نقطة التعليق) يصبح ropeDir
//          غير مستقر ويعطي tiltAngle مضخَّمة → كل الطلاء ينسكب لحظة التوقف
//          الحل: theta بالراديان مباشرة من SphericalPendulum هو زاوية الإمالة
//          الحقيقية عن الشاقول، موثوق في كل الحالات
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
    public float R = 0.18f;
    public float G = 9.81f;
    [Range(0.95f, 1f)]
    public float DAMPING = 0.99f;
    public float MAX_VEL = 3f;

    // ══════════════════════════════════════════════
    [Header("Surface Tension — التوتر السطحي")]
    public bool enableSurfaceTension = true;
    [Range(0f, 2f)] public float surfaceTension = 0.5f;

    // ══════════════════════════════════════════════
    [Header("Temperature & Humidity — الحرارة والرطوبة")]
    [Range(0f, 100f)] public float temperature = 25f;
    [Range(0f, 1f)] public float fluidHumidity = 0.5f;

    float effectiveSigma;

    [HideInInspector] public float K;
    [HideInInspector] public float K_NEAR;
    [HideInInspector] public float REST_DENSITY;
    [HideInInspector] public float SIGMA;
    [HideInInspector] public float gravityScale;
    [HideInInspector] public float bucketInfluence;

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
    public float visualNoiseStrength = 0.02f;
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
        effectiveSigma = SIGMA;
        InitCanvas();
        InitVisualPS();
        Debug.Log($"[PaintV6.2] Type={paintType} | SIGMA={SIGMA:F3} | K={K} | REST={REST_DENSITY}");
    }

    // ════════════════════════════════════════════════════════════════
    void ApplyPaintType()
    {
        switch (paintType)
        {
            case PaintType.Watercolor:
                K = 0.0002f; K_NEAR = 0.002f; REST_DENSITY = 3f;
                SIGMA = 0.05f; gravityScale = 0.8f; bucketInfluence = 0.5f; MAX_VEL = 5f;
                break;
            case PaintType.Normal:
                K = 0.0001f; K_NEAR = 0.001f; REST_DENSITY = 4f;
                SIGMA = 0.2f; gravityScale = 0.6f; bucketInfluence = 0.35f; MAX_VEL = 4f;
                break;
            case PaintType.Acrylic:
                K = 0.00008f; K_NEAR = 0.0008f; REST_DENSITY = 5f;
                SIGMA = 0.5f; gravityScale = 0.5f; bucketInfluence = 0.25f; MAX_VEL = 3f;
                break;
            case PaintType.Oil:
                K = 0.00005f; K_NEAR = 0.0005f; REST_DENSITY = 7f;
                SIGMA = 0.8f; gravityScale = 0.4f; bucketInfluence = 0.15f;
                MAX_VEL = 1.5f; DAMPING = 0.995f;
                break;
            case PaintType.Honey:
                K = 0.00003f; K_NEAR = 0.0003f; REST_DENSITY = 9f;
                SIGMA = 1.5f; gravityScale = 0.3f; bucketInfluence = 0.08f;
                MAX_VEL = 0.8f; DAMPING = 0.998f;
                break;
        }
    }

    // ════════════════════════════════════════════════════════════════
    void FixedUpdate()
    {
        if (!bucketTransform) return;
        float dt = Time.fixedDeltaTime;

        if (!spawnDone) { SpawnParticles(); spawnDone = true; return; }

        Vector3 currentBucketVel = pendulum != null
            ? pendulum.GetBucketVelocity() : Vector3.zero;
        bucketAccelWorld = (currentBucketVel - prevBucketVel) / Mathf.Max(dt, 0.001f);
        prevBucketVel = currentBucketVel;

        BuildGrid();
        ResetSPH();
        UpdateEffectiveViscosity();
        CalcDensity();
        CalcPressure();
        ApplyPressure();
        ApplyViscosity();
        ApplySurfaceTension();
        ApplyExternal(dt);
        Integrate(dt);
        EnforceBoundary();
        CheckSpillCondition();
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
                                float q2 = q * q, q3 = q2 * q;
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
                    Vector3 vf = (1f - relDist) * vDiff * effectiveSigma * norm;
                    pts[i].vel -= vf * 0.5f;
                    pts[j].vel += vf * 0.5f;
                }
            }
        }
    }

    void UpdateEffectiveViscosity()
    {
        float tempFactor = Mathf.Clamp(1f - (temperature - 25f) * 0.01f, 0.3f, 1.5f);
        float humidityFactor = 1f + fluidHumidity * 0.2f;
        effectiveSigma = SIGMA * tempFactor * humidityFactor;
    }

    void ApplySurfaceTension()
    {
        if (!enableSurfaceTension || surfaceTension <= 0f) return;
        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].state != PState.Inside) continue;
            if (pts[i].neighbors.Count == 0) continue;
            Vector3 weightedCenter = Vector3.zero;
            float totalWeight = 0f;
            foreach (int j in pts[i].neighbors)
            {
                Vector3 dir = pts[j].pos - pts[i].pos;
                float dist = dir.magnitude;
                if (dist < 0.0001f) continue;
                float q = 1f - dist / R;
                float w = q * q;
                weightedCenter += pts[j].pos * w;
                totalWeight += w;
            }
            if (totalWeight < 0.0001f) continue;
            weightedCenter /= totalWeight;
            pts[i].force += (weightedCenter - pts[i].pos) * surfaceTension;
        }
    }

    void ApplyExternal(float dt)
    {
        Vector3 fluidForceFromBucket = -bucketAccelWorld * bucketInfluence;
        if (fluidForceFromBucket.magnitude > 15f)
            fluidForceFromBucket = fluidForceFromBucket.normalized * 15f;

        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].state == PState.OnCanvas) continue;
            if (pts[i].state == PState.Inside)
            {
                pts[i].force += Vector3.down * G * gravityScale;
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

            if (vert < -H2 + mg)
            {
                vert = -H2 + mg;
                pts[i].pos = center + horizVec + up * vert;
                float vy = Vector3.Dot(pts[i].vel, up);
                if (vy < 0f) pts[i].vel -= up * vy * 1.5f;
            }
            if (vert > H2 * 0.55f)
            {
                vert = H2 * 0.55f;
                pts[i].pos = center + horizVec + up * vert;
                float vy = Vector3.Dot(pts[i].vel, up);
                if (vy > 0f) pts[i].vel -= up * vy * 0.6f;
            }
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
    //  [FIX-7] CheckSpillCondition — حساب tiltAngle من theta مباشرةً
    //
    //  المشكلة القديمة:
    //  كانت tiltAngle = Vector3.Angle(BucketUp, Vector3.up)
    //  BucketUp مشتق من bucket.rotation الذي يُحسب من ropeDir في UpdatePosition()
    //  عند theta صغير (الدلو قريب من أسفل نقطة التعليق)، ropeDir يصبح شبه
    //  عمودي ويعطي rotation غير مستقر → BucketUp يُظهر إمالة كبيرة خاطئة
    //  → كل الطلاء ينسكب لحظة توقف البندول
    //
    //  الحل:
    //  theta (من SphericalPendulum) هو بالتعريف الزاوية بين الحبل والمحور
    //  الرأسي — مباشرة وموثوق في كل الحالات
    //  نحوّله لدرجات ونستخدمه بدلاً من BucketUp
    // ════════════════════════════════════════════════════════════════
    void CheckSpillCondition()
    {
        if (pts.Count == 0) return;

        // [FIX-7] نقرأ theta مباشرة من SphericalPendulum بدل BucketUp
        float tiltAngle;
        if (pendulum != null)
            tiltAngle = pendulum.theta * Mathf.Rad2Deg;
        else
            tiltAngle = Vector3.Angle(BucketUp, Vector3.up); // fallback لو ما في pendulum

        float spillRate = Mathf.InverseLerp(25f, 90f, tiltAngle);

        float motionFactor = 1f;

        if (pendulum != null)
        {
            motionFactor =
                Mathf.Clamp01(
                    (Mathf.Abs(pendulum.thetaVel)
                    + Mathf.Abs(pendulum.phiVel)) * 0.8f);
        }

        spillRate *= motionFactor;

        if (pendulum != null && pendulum.fadeOutStarted)
        {
            float fade = 1f - Mathf.Clamp01(
                pendulum.fadeOutTimer / pendulum.fadeOutDuration);

            spillRate *= fade * fade;
        }

        if (spillRate <= 0f) return;

        Vector3 center = BucketCenter;
        Vector3 up = BucketUp;
        float H2 = bucketWorldHeight * 0.5f;
        float rimY = H2 * 0.52f;
        float R2 = bucketWorldRadius;

        float maxSpillFraction = spillRate * 0.04f / Mathf.Max(SIGMA, 0.05f);
        maxSpillFraction = Mathf.Clamp(maxSpillFraction, 0f, 0.15f);

        int spillCount = 0;
        int maxThisFrame = Mathf.Max(1, Mathf.RoundToInt(pts.Count * maxSpillFraction));

        Vector3 tiltDir = Vector3.Cross(up, Vector3.up);
        if (tiltDir.sqrMagnitude < 0.001f) return;
        tiltDir.Normalize();

        for (int i = 0; i < pts.Count && spillCount < maxThisFrame; i++)
        {
            if (pts[i].state != PState.Inside) continue;

            Vector3 offset = pts[i].pos - center;
            float vert = Vector3.Dot(offset, up);
            Vector3 horizVec = offset - up * vert;
            float horizDist = horizVec.magnitude;

            bool nearRim = vert > rimY * 0.85f;
            bool nearWall = horizDist > R2 * 0.75f;

            float sideComponent = horizDist > 0.001f
                ? Vector3.Dot(horizVec.normalized, tiltDir) : 0f;
            bool onSpillSide = sideComponent > 0.3f;

            if ((nearRim || (nearWall && onSpillSide)) && onSpillSide)
            {
                pts[i].state = PState.Air;
                pts[i].lifetime = 0f;

                Vector3 exitVel = tiltDir * (1.5f + spillRate * 2f)
                                + Vector3.down * 0.5f
                                + Random.insideUnitSphere * 0.3f;
                exitVel /= Mathf.Max(SIGMA * 0.5f, 0.3f);
                exitVel = Vector3.ClampMagnitude(exitVel, MAX_VEL);

                pts[i].vel = exitVel;
                spillCount++;
            }
        }

        paintLeft = Mathf.Clamp01((float)InsideCount() / maxParticles);
    }

    // ════════════════════════════════════════════════════════════════
    Vector3 ClampToBucket(Vector3 pos)
    {
        Vector3 center = BucketCenter;
        Vector3 up = BucketUp;
        float R2 = bucketWorldRadius * 0.95f;
        float H2 = bucketWorldHeight * 0.5f;

        Vector3 offset = pos - center;
        float vert = Vector3.Dot(offset, up);
        Vector3 horizVec = offset - up * vert;
        float horizDist = horizVec.magnitude;

        if (horizDist > R2 && horizDist > 0.001f)
            horizVec = horizVec * (R2 / horizDist);
        vert = Mathf.Clamp(vert, -H2 * 0.95f, H2 * 0.5f);

        return center + horizVec + up * vert;
    }

    // ════════════════════════════════════════════════════════════════
    void InitVisualPS()
    {
        var go = new GameObject("PaintVisualPS");
        go.transform.SetParent(transform);
        visualPS = go.AddComponent<ParticleSystem>();

        var main = visualPS.main;
        main.loop = false;
        main.playOnAwake = false;
        main.maxParticles = maxParticles + 50;
        main.startLifetime = float.MaxValue;
        main.startSize = visualParticleSize;
        main.startColor = paintColor;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var emission = visualPS.emission; emission.enabled = false;
        var psShape = visualPS.shape; psShape.enabled = false;

        var ren = visualPS.GetComponent<ParticleSystemRenderer>();
        ren.renderMode = ParticleSystemRenderMode.Billboard;
        var mat = new Material(
            Shader.Find("Particles/Standard Unlit") ??
            Shader.Find("Sprites/Default") ??
            Shader.Find("Unlit/Color"));
        ren.material = mat;
        ren.material.color = paintColor;

        visualParticles = new ParticleSystem.Particle[maxParticles + 50];
        visualPS.Play();
    }

    void UpdateVisualPS()
    {
        if (visualPS == null || pts.Count == 0) return;

        Vector3 center = BucketCenter;
        Vector3 up = BucketUp;
        float particleDisplaySize = R * 1.6f;

        int vIdx = 0;

        for (int i = 0; i < pts.Count && vIdx < visualParticles.Length; i++)
        {
            var p = pts[i];

            if (p.state == PState.Inside)
            {
                Vector3 pos = p.pos + Random.insideUnitSphere * (R * 0.15f);
                pos = ClampToBucket(pos);

                float depth = Vector3.Dot(pos - center, up);
                float t = Mathf.InverseLerp(-bucketWorldHeight * 0.5f, 0f, depth);
                Color c = Color.Lerp(paintColorDark, paintColor, t);

                visualParticles[vIdx].position = pos;
                visualParticles[vIdx].startColor = c;
                visualParticles[vIdx].startSize = particleDisplaySize * Random.Range(0.9f, 1.1f);
                visualParticles[vIdx].remainingLifetime = float.MaxValue;
                visualParticles[vIdx].startLifetime = float.MaxValue;
                visualParticles[vIdx].velocity = Vector3.zero;
                vIdx++;
            }
            else if (p.state == PState.Air)
            {
                visualParticles[vIdx].position = p.pos;
                visualParticles[vIdx].startColor = paintColor * 1.2f;
                visualParticles[vIdx].startSize = particleDisplaySize * 0.7f;
                visualParticles[vIdx].remainingLifetime = float.MaxValue;
                visualParticles[vIdx].startLifetime = float.MaxValue;
                visualParticles[vIdx].velocity = p.vel;
                vIdx++;
            }
        }

        for (int v = vIdx; v < visualParticles.Length; v++)
        {
            visualParticles[v].remainingLifetime = 0f;
            visualParticles[v].startLifetime = 0f;
        }

        visualPS.SetParticles(visualParticles, visualParticles.Length);
    }

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
                : Vector3.Dot(pts[i].pos - canvasTransform.position,
                              canvasTransform.up) <= 0.02f;
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

        float spreadMult = paintType == PaintType.Watercolor ? 2f :
                           paintType == PaintType.Oil ? 0.6f : 1f;
        int r = Mathf.RoundToInt(brushSize * Mathf.Clamp(speed * 0.2f, 0.5f, 3f) * spreadMult);
        FillCircle(cx, cy, r, paintColor);

        int splats = paintType == PaintType.Watercolor ? 6 :
                     paintType == PaintType.Oil ? 1 : 3;
        for (int s = 0; s < splats; s++)
        {
            float a = Random.Range(0f, Mathf.PI * 2f);
            int d = Random.Range(r, r + (int)(20f / Mathf.Max(SIGMA, 0.1f)));
            FillCircle(cx + (int)(Mathf.Cos(a) * d),
                       cy + (int)(Mathf.Sin(a) * d),
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
                pos = center + right * (Mathf.Cos(a) * r)
                               + fwd * (Mathf.Sin(a) * r) + up * h,
                state = PState.Inside
            });
        }
        Debug.Log($"[PaintV6.2] Spawned {pts.Count} at {center}");
    }

    // ════════════════════════════════════════════════════════════════
    // API
    // ════════════════════════════════════════════════════════════════
    public void RefillBucket() { pts.Clear(); paintLeft = 1f; SpawnParticles(); }

    public void ClearCanvas()
    {
        var bg = new Color(0.95f, 0.92f, 0.85f, 1f);
        for (int i = 0; i < canvasPx.Length; i++) canvasPx[i] = bg;
        canvasTex.SetPixels(canvasPx); canvasTex.Apply();
    }

    public void SaveCanvas(string path = "PaintResult.png")
        => System.IO.File.WriteAllBytes(path, canvasTex.EncodeToPNG());

    public float GetPaintLeft() => paintLeft;
    public int InsideCount() { int c = 0; foreach (var p in pts) if (p.state == PState.Inside) c++; return c; }
    public int AirborneCount() { int c = 0; foreach (var p in pts) if (p.state == PState.Air) c++; return c; }

    // ════════════════════════════════════════════════════════════════
    GUIStyle paintHudStyle;
    Texture2D paintHudBg;

    void InitPaintHudStyle()
    {
        paintHudBg = new Texture2D(1, 1);
        paintHudBg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.6f));
        paintHudBg.Apply();
        paintHudStyle = new GUIStyle();
        paintHudStyle.normal.textColor = Color.white;
        paintHudStyle.fontSize = 11;
        paintHudStyle.fontStyle = FontStyle.Normal;
        paintHudStyle.padding = new RectOffset(4, 4, 2, 2);
    }

    void OnGUI()
    {
        if (paintHudStyle == null) InitPaintHudStyle();
        int x = 360, y = 10, lh = 18, lines = 5;
        GUI.DrawTexture(new Rect(x - 4, y - 3, 220, lh * lines + 6), paintHudBg);
        GUI.Label(new Rect(x, y, 230, lh), $"Type     : {paintType}", paintHudStyle);
        GUI.Label(new Rect(x, y + lh, 230, lh), $"Left     : {paintLeft * 100f:F1}%", paintHudStyle);
        GUI.Label(new Rect(x, y + lh * 2, 230, lh), $"Inside   : {InsideCount()} / {maxParticles}", paintHudStyle);
        GUI.Label(new Rect(x, y + lh * 3, 230, lh), $"Airborne : {AirborneCount()}", paintHudStyle);
        GUI.Label(new Rect(x, y + lh * 4, 230, lh), $"Tilt(θ)  : {(pendulum != null ? pendulum.theta * Mathf.Rad2Deg : Vector3.Angle(BucketUp, Vector3.up)):F1}°", paintHudStyle);
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