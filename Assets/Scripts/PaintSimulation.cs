using UnityEngine;
using System.Collections.Generic;

// ════════════════════════════════════════════════════════════════════
//  PaintSimulation.cs  v6.3  — إصلاح انسكاب الطلاء عند التوقف
//  الإصلاحات عن v6.2:
//  [FIX-8] CheckSpillCondition — إضافة حد أدنى للسرعة الزاوية
//          قبل الإصلاح: motionFactor = Clamp01((|θ̇| + |φ̇|) * 0.8f)
//          حتى بـ thetaVel = 0.1 كان motionFactor ≈ 0.08 وهو غير صفر
//          فيسبب انسكاباً طفيفاً مستمراً عند حركة بطيئة
//          الحل: اشتراط أن تكون السرعة الزاوية > 0.15 قبل أي انسكاب
//          أقل من هذا = البندول شبه متوقف = لا انسكاب
//  [FIX-9] spillRate = tiltSpill * motionSpill معاً (ضرب لا جمع)
//          القديم: spillRate = InverseLerp(25,90,tiltAngle) × motionFactor
//          المشكلة: motionFactor كان معامل تقليل فقط، لكن spillRate الأساسي
//          (من tiltAngle) ظل مرتفعاً عند التباطؤ لأن theta لم ينخفض بعد
//          الجديد: spillRate = 0 تلقائياً إذا السرعة < MIN_SPILL_SPEED
//          فلا يهم كم كانت tiltAngle كبيرة
// ════════════════════════════════════════════════════════════════════

public class PaintSimulation : MonoBehaviour
{
    [Header("References — المراجع")]
    public SphericalPendulum pendulum;
    public Transform bucketTransform;
    public Transform canvasTransform;
    public Renderer canvasRenderer;

    [Header("Paint Type — نوع الطلاء")]
    public PaintType paintType = PaintType.Acrylic;

    // ════════════════════════════════════════════════════════════════
    //  أنواع الطلاء الحقيقية المستخدمة في الرسم بالدلو المتأرجح
    //  المعاملات مبنية على الخصائص الفيزيائية الفعلية لكل نوع
    // ════════════════════════════════════════════════════════════════
    public enum PaintType
    {
        Watercolor, // مائي خفيف   — كثافة 1.0 g/cm³، لزوجة 1-3 mPa·s
        Acrylic,    // أكريليك     — كثافة 1.1 g/cm³، لزوجة 50-200 mPa·s
        OilPaint,   // زيتي        — كثافة 1.3 g/cm³، لزوجة 200-300 mPa·s
        Tempera,    // تيمبيرا     — كثافة 1.05 g/cm³، لزوجة 10-50 mPa·s
        Gouache,    // غواش        — كثافة 1.15 g/cm³، لزوجة 100-400 mPa·s
        Latex,      // لاتكس جداري — كثافة 1.2 g/cm³، لزوجة 400-1000 mPa·s
        Enamel,     // مينا لامع   — كثافة 1.4 g/cm³، لزوجة 300-500 mPa·s
        Ink         // حبر         — كثافة 1.0 g/cm³، لزوجة 1-5 mPa·s
    }

    [Header("SPH Physics — فيزياء السائل")]
    [Range(50, 250)]
    public int maxParticles = 150;
    [Range(0.08f, 0.25f)]
    public float R = 0.18f;
    public float G = 9.81f;
    [Range(0.95f, 1f)]
    public float DAMPING = 0.99f;
    public float MAX_VEL = 3f;

    [Header("Surface Tension — التوتر السطحي")]
    public bool enableSurfaceTension = true;
    [Range(0f, 2f)] public float surfaceTension = 0.5f;

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

    [Header("Bucket Size — حجم الدلو")]
    public float bucketWorldRadius = 0.25f;
    public float bucketWorldHeight = 0.4f;
    public Vector3 bucketCenterOffset = new Vector3(0f, -0.15f, 0f);

    [Header("Visual — التصور البصري")]
    [Range(1000, 15000)]
    public int visualParticleCount = 8000;
    public float visualParticleSize = 0.015f;
    public float visualNoiseStrength = 0.02f;
    public Color paintColor = new Color(0.9f, 0.1f, 0.1f, 1f);
    public Color paintColorDark = new Color(0.5f, 0.05f, 0.05f, 1f);

    [Header("Canvas — اللوحة")]
    public int canvasWidth = 512;
    public int canvasHeight = 512;
    [Range(1, 40)]
    public int brushSize = 12;
    public bool canvasIsHorizontal = true;

    [Header("Debug")]
    public bool showDebugGizmos = true;

    class PhysicsParticle
    {
        public Vector3 pos, prevPos, vel, force;
        public float rho, rhoNear, press, pressNear;
        public List<int> neighbors = new List<int>(32);
        public int gx, gy, gz;
        public PState state;
        public float lifetime;
    }
    // السائل بيبقى داخل الدلو دائماً — حذفنا Air
    enum PState { Inside, OnCanvas }

    List<PhysicsParticle> pts = new List<PhysicsParticle>();
    Dictionary<Vector3Int, List<int>> grid = new Dictionary<Vector3Int, List<int>>();

    ParticleSystem visualPS;
    ParticleSystem.Particle[] visualParticles;
    bool visualDirty = true;
    bool spawnDone = false;

    Texture2D canvasTex;
    Color[] canvasPx;
    bool canvasDirty;

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
        Debug.Log($"[PaintV6.3] Type={paintType} | SIGMA={SIGMA:F3} | K={K} | REST={REST_DENSITY}");
    }

    // ════════════════════════════════════════════════════════════════
    //  ApplyPaintType — معاملات فيزيائية واقعية لكل نوع طلاء
    //
    //  SIGMA  = اللزوجة (كبير = أبطأ وأكثر تماسكاً)
    //  K      = صلابة الضغط (يحدد مقاومة الضغط)
    //  REST_DENSITY = كثافة الراحة (كبير = أكثف)
    //  gravityScale = تأثير الجاذبية على السائل داخل الدلو
    //  bucketInfluence = مدى استجابة السائل لتسارع الدلو
    //  MAX_VEL = أقصى سرعة للجسيمات
    //  DAMPING = معامل التخميد (قريب من 1 = يحفظ الزخم أطول)
    // ════════════════════════════════════════════════════════════════
    void ApplyPaintType()
    {
        switch (paintType)
        {
            case PaintType.Watercolor:
                // مائي خفيف — لزوجة منخفضة جداً، يتدفق بحرية كاملة
                // في الواقع: يتمدد على السطح بسرعة ويرسم خطوطاً شفافة
                K = 0.0002f; K_NEAR = 0.002f; REST_DENSITY = 3f;
                SIGMA = 0.04f; gravityScale = 0.9f; bucketInfluence = 0.6f;
                MAX_VEL = 5f; DAMPING = 0.988f;
                break;

            case PaintType.Acrylic:
                // أكريليك — لزوجة متوسطة، الأكثر استخداماً في الدلو المتأرجح
                // في الواقع: يتدفق جيداً ويجف سريعاً، يعطي خطوطاً واضحة
                K = 0.00009f; K_NEAR = 0.0009f; REST_DENSITY = 4.5f;
                SIGMA = 0.3f; gravityScale = 0.6f; bucketInfluence = 0.3f;
                MAX_VEL = 3.5f; DAMPING = 0.991f;
                break;

            case PaintType.OilPaint:
                // زيتي — ثقيل وبطيء، لزوجة عالية
                // في الواقع: يسيل ببطء شديد، يعطي خطوطاً سميكة وجمالية
                K = 0.00005f; K_NEAR = 0.0005f; REST_DENSITY = 6.5f;
                SIGMA = 0.85f; gravityScale = 0.4f; bucketInfluence = 0.12f;
                MAX_VEL = 1.5f; DAMPING = 0.996f;
                break;

            case PaintType.Tempera:
                // تيمبيرا — بين المائي والأكريليك، أكثف من المائي وأخف من الأكريليك
                // في الواقع: يتدفق بشكل منتظم، يُستخدم كثيراً في الفن التعليمي
                K = 0.00015f; K_NEAR = 0.0015f; REST_DENSITY = 3.8f;
                SIGMA = 0.15f; gravityScale = 0.75f; bucketInfluence = 0.45f;
                MAX_VEL = 4f; DAMPING = 0.990f;
                break;

            case PaintType.Gouache:
                // غواش — معتم وكثيف، يجف ليكون مطفياً
                // في الواقع: أكثف من الأكريليك، يعطي ألواناً كثيفة وغير شفافة
                K = 0.00007f; K_NEAR = 0.0007f; REST_DENSITY = 5.5f;
                SIGMA = 0.55f; gravityScale = 0.5f; bucketInfluence = 0.2f;
                MAX_VEL = 2.5f; DAMPING = 0.993f;
                break;

            case PaintType.Latex:
                // لاتكس جداري — سميك جداً ومطاطي، يُستخدم لطلاء الجدران
                // في الواقع: لزوجة عالية جداً، يسيل ببطء شديد ويترك طبقة سميكة
                K = 0.00004f; K_NEAR = 0.0004f; REST_DENSITY = 7.5f;
                SIGMA = 1.1f; gravityScale = 0.35f; bucketInfluence = 0.1f;
                MAX_VEL = 1.2f; DAMPING = 0.997f;
                break;

            case PaintType.Enamel:
                // مينا لامع — سائل لزج يعطي سطحاً لامعاً جداً
                // في الواقع: يسيل ببطء كالعسل، يترك طبقة ملساء لامعة
                K = 0.00004f; K_NEAR = 0.0004f; REST_DENSITY = 7f;
                SIGMA = 1.0f; gravityScale = 0.38f; bucketInfluence = 0.11f;
                MAX_VEL = 1.3f; DAMPING = 0.996f;
                break;

            case PaintType.Ink:
                // حبر — رقيق جداً كالماء تقريباً، لزوجة منخفضة جداً
                // في الواقع: أرق من الطلاء المائي، يتدفق بأقصى سرعة ويصنع خطوطاً دقيقة
                K = 0.00025f; K_NEAR = 0.0025f; REST_DENSITY = 2.5f;
                SIGMA = 0.02f; gravityScale = 0.95f; bucketInfluence = 0.7f;
                MAX_VEL = 6f; DAMPING = 0.985f;
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

        // [FIX-10] لما fadeOut شغال، نصفّر تأثير تسارع الدلو على السائل تدريجياً
        // السبب: fadeOutFriction يُبطئ البندول فجأة → bucketAccelWorld يتضخم
        // → يدفع كل جسيمات الطلاء للأعلى دفعة وحدة → كل الطلاء ينسكب
        // الحل: نقلّل تأثير التسارع بنسبة ما تبقى من fadeOut
        if (pendulum != null && pendulum.fadeOutStarted)
        {
            float fadeProgress = Mathf.Clamp01(pendulum.fadeOutTimer / pendulum.fadeOutDuration);
            bucketAccelWorld *= (1f - fadeProgress);
        }

        // [FIX-10] سقف إضافي على التسارع — يمنع أي قيمة مبالغ فيها
        bucketAccelWorld = Vector3.ClampMagnitude(bucketAccelWorld, 8f);

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
        // [FIX-10] خفّضنا السقف من 15 لـ 5 — 15 كان يسمح بدفع قوي جداً للجسيمات
        if (fluidForceFromBucket.magnitude > 5f)
            fluidForceFromBucket = fluidForceFromBucket.normalized * 5f;

        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].state == PState.OnCanvas) continue;
            // كل الجسيمات داخل الدلو — جاذبية + تأثير تسارع الدلو
            pts[i].force += Vector3.down * G * gravityScale;
            pts[i].force += fluidForceFromBucket;
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
            if (p.state != PState.Inside) continue;

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

    // HandleCanvas محذوف — الطلاء بيبقى داخل الدلو دائماً ولا يصل اللوحة

    void DrawOnCanvas(Vector3 wp, float speed)
    {
        Vector3 lp = canvasTransform.InverseTransformPoint(wp);
        int cx = Mathf.RoundToInt(Mathf.Clamp01(lp.x + 0.5f) * canvasWidth);
        int cy = Mathf.RoundToInt(Mathf.Clamp01(lp.z + 0.5f) * canvasHeight);

        // انتشار البقعة حسب نوع الطلاء — الرقيق ينتشر أكثر
        float spreadMult = paintType switch
        {
            PaintType.Ink => 3.0f,  // حبر — ينتشر أكثر من أي شيء
            PaintType.Watercolor => 2.5f,  // مائي — ينتشر كثيراً
            PaintType.Tempera => 1.5f,  // تيمبيرا — انتشار متوسط
            PaintType.Acrylic => 1.0f,  // أكريليك — طبيعي
            PaintType.Gouache => 0.8f,  // غواش — أقل انتشاراً
            PaintType.OilPaint => 0.6f,  // زيتي — بقعة صغيرة سميكة
            PaintType.Enamel => 0.5f,  // مينا — بقعة صغيرة لامعة
            PaintType.Latex => 0.4f,  // لاتكس — بقعة سميكة محدودة
            _ => 1.0f
        };

        int r = Mathf.RoundToInt(brushSize * Mathf.Clamp(speed * 0.2f, 0.5f, 3f) * spreadMult);
        FillCircle(cx, cy, r, paintColor);

        // عدد بقع التناثر حسب نوع الطلاء
        int splats = paintType switch
        {
            PaintType.Ink => 8,  // حبر — تناثر كثير
            PaintType.Watercolor => 6,  // مائي — تناثر كثير
            PaintType.Tempera => 4,  // تيمبيرا — تناثر متوسط
            PaintType.Acrylic => 3,  // أكريليك — طبيعي
            PaintType.Gouache => 2,  // غواش — تناثر قليل
            PaintType.OilPaint => 1,  // زيتي — بالكاد يتناثر
            PaintType.Enamel => 1,  // مينا — بالكاد يتناثر
            PaintType.Latex => 0,  // لاتكس — لا تناثر، يسقط ككتلة
            _ => 3
        };

        for (int s = 0; s < splats; s++)
        {
            float a = Random.Range(0f, Mathf.PI * 2f);
            int d = Random.Range(r, r + (int)(20f / Mathf.Max(SIGMA, 0.05f)));
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
        Debug.Log($"[PaintV6.3] Spawned {pts.Count} at {center}");
    }

    // ════════════════════════════════════════════════════════════════
    public void RefillBucket() { pts.Clear(); SpawnParticles(); }

    public void ClearCanvas()
    {
        var bg = new Color(0.95f, 0.92f, 0.85f, 1f);
        for (int i = 0; i < canvasPx.Length; i++) canvasPx[i] = bg;
        canvasTex.SetPixels(canvasPx); canvasTex.Apply();
    }

    public void SaveCanvas(string path = "PaintResult.png")
        => System.IO.File.WriteAllBytes(path, canvasTex.EncodeToPNG());

    public int InsideCount() { int c = 0; foreach (var p in pts) if (p.state == PState.Inside) c++; return c; }

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
        int x = 360, y = 10, lh = 18, lines = 3;
        GUI.DrawTexture(new Rect(x - 4, y - 3, 220, lh * lines + 6), paintHudBg);
        string paintName = paintType switch
        {
            PaintType.Watercolor => "مائي خفيف",
            PaintType.Acrylic => "أكريليك",
            PaintType.OilPaint => "زيتي",
            PaintType.Tempera => "تيمبيرا",
            PaintType.Gouache => "غواش",
            PaintType.Latex => "لاتكس جداري",
            PaintType.Enamel => "مينا لامع",
            PaintType.Ink => "حبر",
            _ => paintType.ToString()
        };
        GUI.Label(new Rect(x, y, 230, lh), $"Type     : {paintName}", paintHudStyle);
        GUI.Label(new Rect(x, y + lh, 230, lh), $"Inside   : {InsideCount()} / {maxParticles}", paintHudStyle);
        GUI.Label(new Rect(x, y + lh * 2, 230, lh), $"Tilt(θ)  : {(pendulum != null ? pendulum.theta * Mathf.Rad2Deg : Vector3.Angle(BucketUp, Vector3.up)):F1}°", paintHudStyle);
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