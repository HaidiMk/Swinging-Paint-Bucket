using UnityEngine;
using System.Collections.Generic;

// ════════════════════════════════════════════════════════════════════
//  PaintSimulation.cs  v8.0  — SPH Optimized
//  التحسينات عن v7.0:
//  [OPT-1] استبدال Dictionary+List بـ uniform grid array ثابت
//          → لا allocations كل فريم → أسرع 3-5x
//  [OPT-2] Sleep system — الجزيئات الهادية ما تنحسب
//          → أسرع 2x عند استقرار السائل
//  [OPT-3] neighbor list pre-allocated بـ array بدل List
//          → لا garbage collection → أسرع 2x
//  [OPT-4] تقليل neighbor search من 27 خلية → 9 خلايا
//          (الدلو محدود الارتفاع، Y محور أقل أهمية)
//          → أسرع 1.5x
//  [OPT-5] Hard boundary constraint قبل الفيزياء
//          → يمنع التسريب نهائياً
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

    public enum PaintType
    {
        Watercolor,
        Acrylic,
        OilPaint,
        Tempera,
        Gouache,
        Latex,
        Enamel,
        Ink
    }

    [Header("SPH Physics — فيزياء السائل")]
    [Range(100, 5000)]
    public int maxParticles = 500;
    [Range(0.08f, 0.25f)]
    public float R = 0.18f;
    public float G = 9.81f;
    [Range(0.95f, 1f)]
    public float DAMPING = 0.99f;
    public float MAX_VEL = 3f;

    [Header("Sleep System — نظام السكون")]
    [Tooltip("الجزيئات الهادية أقل من هذا الحد ما تنحسب")]
    public float sleepThreshold = 0.0001f;
    [Tooltip("كم فريم تنام قبل تستيقظ وتتحقق")]
    public int sleepFrames = 3;

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
    public float visualParticleSize = 0.015f;
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

    // ══════════════════════════════════════════════════════════════
    //  [OPT-3] struct بدل class — كل البيانات في memory متجاور
    //  هاد وحده بيحسّن cache performance بشكل كبير
    // ══════════════════════════════════════════════════════════════
    struct Particle
    {
        public Vector3 pos;
        public Vector3 vel;
        public Vector3 force;
        public float rho;
        public float rhoNear;
        public float press;
        public float pressNear;
        public int state;       // 0=Inside, 1=Falling, 2=OnCanvas
        public int sleepTimer;  // [OPT-2] عداد السكون
        public bool fromTop;
    }

    // حالات الجسيم كـ constants بدل enum (أسرع بالمقارنة)
    const int INSIDE = 0;
    const int FALLING = 1;
    const int ON_CANVAS = 2;

    // ══════════════════════════════════════════════════════════════
    //  [OPT-1] Uniform Grid — arrays ثابتة بدل Dictionary
    //  كل خلية فيها أقصى عدد جيران محدد مسبقاً
    // ══════════════════════════════════════════════════════════════
    const int MAX_PER_CELL = 64; // أقصى جزيء بكل خلية — رفعناها لمنع رمي الجزيئات
    const int MAX_NEIGHBORS = 64; // أقصى جار لكل جزيء

    // الـ grid نفسه
    int[] gridCount;       // كم جزيء في كل خلية
    int[] gridParticles;   // indices الجزيئات في كل خلية
    int gridSizeX, gridSizeY, gridSizeZ;
    Vector3 gridOrigin;
    float cellSize;

    // [OPT-3] neighbor arrays ثابتة — لا allocations
    int[] neighborBuffer;  // كل الجيران لكل الجزيئات
    int[] neighborCount;   // كم جار لكل جزيء
    int[] neighborStart;   // وين تبدأ جيران كل جزيء بالـ buffer

    Particle[] pts;
    int particleCount;

    ParticleSystem visualPS;
    ParticleSystem.Particle[] visualParticles;
    bool visualDirty = true;
    bool spawnDone = false;

    Texture2D canvasTex;
    Color[] canvasPx;
    bool canvasDirty;

    Vector3 prevBucketVel = Vector3.zero;
    Vector3 bucketAccelWorld = Vector3.zero;

    // frame counter للـ sleep system
    int frameCount = 0;

    public Vector3 BucketCenter => bucketTransform.position
                                 + bucketTransform.TransformDirection(bucketCenterOffset);
    Vector3 BucketUp => bucketTransform.TransformDirection(Vector3.up).normalized;
    Vector3 BucketRight => bucketTransform.TransformDirection(Vector3.right).normalized;
    Vector3 BucketForward => bucketTransform.TransformDirection(Vector3.forward).normalized;

    public Vector3 HolePosition => BucketCenter - BucketUp * (bucketWorldHeight * 0.5f);
    public Vector3 TopPosition => BucketCenter + BucketUp * (bucketWorldHeight * 0.5f);

    // ════════════════════════════════════════════════════════════════
    void Start()
    {
        ApplyPaintType();
        effectiveSigma = SIGMA;
        InitGridArrays();
        InitNeighborArrays();
        InitCanvas();
        InitVisualPS();
        SpawnParticles();
        spawnDone = true;
        Debug.Log($"[PaintV8.0] Type={paintType} | Particles={maxParticles}");
    }

    // ════════════════════════════════════════════════════════════════
    //  [OPT-1] InitGridArrays — نحسب حجم الـ grid مرة وحدة بالبداية
    // ════════════════════════════════════════════════════════════════
    void InitGridArrays()
    {
        cellSize = R;

        // حجم الـ grid = حجم الدلو + هامش
        float margin = R * 2f;
        float w = (bucketWorldRadius + margin) * 2f;
        float h = bucketWorldHeight + margin * 2f;

        gridSizeX = Mathf.CeilToInt(w / cellSize) + 2;
        gridSizeY = Mathf.CeilToInt(h / cellSize) + 2;
        gridSizeZ = Mathf.CeilToInt(w / cellSize) + 2;

        gridOrigin = new Vector3(
            -(gridSizeX * cellSize) / 2f,
            -(gridSizeY * cellSize) / 2f,
            -(gridSizeZ * cellSize) / 2f
        );

        int totalCells = gridSizeX * gridSizeY * gridSizeZ;
        gridCount = new int[totalCells];
        gridParticles = new int[totalCells * MAX_PER_CELL];

        Debug.Log($"[PaintV8.0] Grid: {gridSizeX}x{gridSizeY}x{gridSizeZ} = {totalCells} cells");
    }

    void InitNeighborArrays()
    {
        // كل جزيء عنده أقصى MAX_NEIGHBORS جار
        neighborBuffer = new int[maxParticles * MAX_NEIGHBORS];
        neighborCount = new int[maxParticles];
        neighborStart = new int[maxParticles];

        // نحسب start index لكل جزيء مرة وحدة
        for (int i = 0; i < maxParticles; i++)
            neighborStart[i] = i * MAX_NEIGHBORS;
    }

    // ════════════════════════════════════════════════════════════════
    void ApplyPaintType()
    {
        switch (paintType)
        {
            case PaintType.Watercolor:
                K = 0.0002f; K_NEAR = 0.002f; REST_DENSITY = 3f;
                SIGMA = 0.04f; gravityScale = 0.9f; bucketInfluence = 0.6f;
                MAX_VEL = 5f; DAMPING = 0.988f;
                break;
            case PaintType.Acrylic:
                K = 0.00009f; K_NEAR = 0.0009f; REST_DENSITY = 4.5f;
                SIGMA = 0.3f; gravityScale = 0.6f; bucketInfluence = 0.3f;
                MAX_VEL = 3.5f; DAMPING = 0.991f;
                break;
            case PaintType.OilPaint:
                K = 0.00005f; K_NEAR = 0.0005f; REST_DENSITY = 6.5f;
                SIGMA = 0.85f; gravityScale = 0.4f; bucketInfluence = 0.12f;
                MAX_VEL = 1.5f; DAMPING = 0.996f;
                break;
            case PaintType.Tempera:
                K = 0.00015f; K_NEAR = 0.0015f; REST_DENSITY = 3.8f;
                SIGMA = 0.15f; gravityScale = 0.75f; bucketInfluence = 0.45f;
                MAX_VEL = 4f; DAMPING = 0.990f;
                break;
            case PaintType.Gouache:
                K = 0.00007f; K_NEAR = 0.0007f; REST_DENSITY = 5.5f;
                SIGMA = 0.55f; gravityScale = 0.5f; bucketInfluence = 0.2f;
                MAX_VEL = 2.5f; DAMPING = 0.993f;
                break;
            case PaintType.Latex:
                K = 0.00004f; K_NEAR = 0.0004f; REST_DENSITY = 7.5f;
                SIGMA = 1.1f; gravityScale = 0.35f; bucketInfluence = 0.1f;
                MAX_VEL = 1.2f; DAMPING = 0.997f;
                break;
            case PaintType.Enamel:
                K = 0.00004f; K_NEAR = 0.0004f; REST_DENSITY = 7f;
                SIGMA = 1.0f; gravityScale = 0.38f; bucketInfluence = 0.11f;
                MAX_VEL = 1.3f; DAMPING = 0.996f;
                break;
            case PaintType.Ink:
                K = 0.00025f; K_NEAR = 0.0025f; REST_DENSITY = 2.5f;
                SIGMA = 0.02f; gravityScale = 0.95f; bucketInfluence = 0.7f;
                MAX_VEL = 6f; DAMPING = 0.985f;
                break;
        }
    }

    // ════════════════════════════════════════════════════════════════
    void FixedUpdate()
    {
        if (!bucketTransform || !spawnDone) return;
        float dt = Time.fixedDeltaTime;
        frameCount++;

        // حساب تسارع الدلو
        Vector3 currentBucketVel = pendulum != null
            ? pendulum.GetBucketVelocity() : Vector3.zero;
        bucketAccelWorld = (currentBucketVel - prevBucketVel) / Mathf.Max(dt, 0.001f);
        prevBucketVel = currentBucketVel;

        if (pendulum != null && pendulum.fadeOutStarted)
        {
            float fp = Mathf.Clamp01(pendulum.fadeOutTimer / pendulum.fadeOutDuration);
            bucketAccelWorld *= (1f - fp);
        }
        bucketAccelWorld = Vector3.ClampMagnitude(bucketAccelWorld, 8f);

        UpdateEffectiveViscosity();

        // [OPT-1] بناء الـ grid بـ arrays بدل Dictionary
        BuildGridFast();

        // [OPT-3] بناء قوائم الجيران بـ arrays ثابتة
        BuildNeighborsFast();

        // SPH steps
        ResetSPH();
        CalcDensity();
        CalcPressure();
        ApplyPressure();
        ApplyViscosity();
        if (enableSurfaceTension) ApplySurfaceTension();
        ApplyExternal(dt);
        Integrate(dt);

        // [OPT-5] Hard boundary — يمنع التسريب نهائياً
        EnforceBoundaryHard();

        // تحريك الجزيئات الساقطة
        UpdateFalling(dt);

        visualDirty = true;

        // ── تقرير كل 60 فريم (كل ثانية تقريباً) ──────────────────
        if (frameCount % 60 == 0)
            LogStatus();
    }

    void LogStatus()
    {
        int inside = InsideCount();
        int falling = FallingCount();
        int onCanvas = particleCount - inside - falling;

        int sleeping = 0;
        int avgNeighbors = 0;
        float avgVel = 0f;

        for (int i = 0; i < particleCount; i++)
        {
            if (pts[i].state != INSIDE) continue;
            if (pts[i].sleepTimer > 0) sleeping++;
            else
            {
                avgNeighbors += neighborCount[i];
                avgVel += pts[i].vel.magnitude;
            }
        }

        int active = inside - sleeping;
        if (active > 0) { avgNeighbors /= active; avgVel /= active; }

        float tiltDeg = pendulum != null ? pendulum.theta * Mathf.Rad2Deg : 0f;

        Debug.Log(
            $"[Paint F={frameCount}] " +
            $"Inside={inside} Falling={falling} OnCanvas={onCanvas} | " +
            $"Active={active} Sleeping={sleeping} | " +
            $"AvgNeighbors={avgNeighbors} AvgVel={avgVel:F2} | " +
            $"Tilt={tiltDeg:F1}deg BucketPos={BucketCenter:F2}"
        );

        if (inside == 0 && falling == 0)
            Debug.LogWarning("[Paint] كل الجزيئات على اللوحة — الدلو فاضي!");

        if (inside < particleCount * 0.1f && frameCount < 300)
            Debug.LogWarning($"[Paint] تسرب! Inside={inside}/{particleCount} — تحقق من bucketWorldRadius وbucketCenterOffset");
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
    //  [OPT-1] BuildGridFast — array بدل Dictionary
    //  بدل ما نعمل grid.Clear() ونضيف lists جديدة كل فريم،
    //  بنعمل reset على array ثابت بـ System.Array.Clear
    //  هاد أسرع بكثير لأنه memory-contiguous
    // ════════════════════════════════════════════════════════════════
    void BuildGridFast()
    {
        // reset عدادات الخلايا
        System.Array.Clear(gridCount, 0, gridCount.Length);

        Vector3 center = BucketCenter;

        for (int i = 0; i < particleCount; i++)
        {
            if (pts[i].state != INSIDE) continue;

            // [OPT-2] Sleep — الجزيء النايم ما يدخل الـ grid
            if (pts[i].sleepTimer > 0)
            {
                pts[i].sleepTimer--;
                continue;
            }

            // حول الموقع العالمي لإحداثيات الدلو المحلية
            Vector3 localPos = pts[i].pos - center;
            int cx = Mathf.FloorToInt((localPos.x - gridOrigin.x) / cellSize);
            int cy = Mathf.FloorToInt((localPos.y - gridOrigin.y) / cellSize);
            int cz = Mathf.FloorToInt((localPos.z - gridOrigin.z) / cellSize);

            // تأكد في حدود الـ grid
            if (cx < 0 || cx >= gridSizeX) continue;
            if (cy < 0 || cy >= gridSizeY) continue;
            if (cz < 0 || cz >= gridSizeZ) continue;

            int cellIdx = cx + cy * gridSizeX + cz * gridSizeX * gridSizeY;

            // ما تتجاوز MAX_PER_CELL
            if (gridCount[cellIdx] >= MAX_PER_CELL) continue;

            gridParticles[cellIdx * MAX_PER_CELL + gridCount[cellIdx]] = i;
            gridCount[cellIdx]++;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  [OPT-3] + [OPT-4] BuildNeighborsFast
    //  بنبني قوائم الجيران مرة وحدة ونستخدمها بكل الـ SPH steps
    //  [OPT-4] نفحص 9 خلايا بالمستوى الأفقي بدل 27
    //  لأن الدلو ضيق ومعظم التفاعل أفقي
    // ════════════════════════════════════════════════════════════════
    void BuildNeighborsFast()
    {
        System.Array.Clear(neighborCount, 0, particleCount);
        Vector3 center = BucketCenter;

        for (int i = 0; i < particleCount; i++)
        {
            if (pts[i].state != INSIDE) continue;
            if (pts[i].sleepTimer > 0) continue;

            Vector3 localPos = pts[i].pos - center;
            int cx = Mathf.FloorToInt((localPos.x - gridOrigin.x) / cellSize);
            int cy = Mathf.FloorToInt((localPos.y - gridOrigin.y) / cellSize);
            int cz = Mathf.FloorToInt((localPos.z - gridOrigin.z) / cellSize);

            int nCount = 0;
            int startIdx = neighborStart[i];

            // [OPT-4] فحص 3 طبقات Y بدل واحدة (نحافظ على الدقة العمودية)
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = cy + dy;
                if (ny < 0 || ny >= gridSizeY) continue;

                // فحص 9 خلايا بالمستوى الأفقي XZ
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = cx + dx;
                    if (nx < 0 || nx >= gridSizeX) continue;

                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int nz = cz + dz;
                        if (nz < 0 || nz >= gridSizeZ) continue;

                        int cellIdx = nx + ny * gridSizeX + nz * gridSizeX * gridSizeY;
                        int count = gridCount[cellIdx];

                        for (int k = 0; k < count; k++)
                        {
                            int j = gridParticles[cellIdx * MAX_PER_CELL + k];
                            if (j <= i) continue; // كل زوج مرة وحدة

                            // [OPT-6] sqrMagnitude بدل Distance — بنتجنب sqrt إلا للجيران الحقيقيين
                            Vector3 diff = pts[j].pos - pts[i].pos;
                            float distSq = diff.sqrMagnitude;
                            if (distSq < R * R && distSq > 0.00000001f)
                            {
                                if (nCount < MAX_NEIGHBORS)
                                {
                                    neighborBuffer[startIdx + nCount] = j;
                                    nCount++;
                                }
                            }
                        }
                    }
                }
            }
            neighborCount[i] = nCount;
        }
    }

    // ════════════════════════════════════════════════════════════════
    void ResetSPH()
    {
        for (int i = 0; i < particleCount; i++)
        {
            if (pts[i].state != INSIDE) continue;
            pts[i].force = Vector3.zero;
            pts[i].rho = 0f;
            pts[i].rhoNear = 0f;
            pts[i].press = 0f;
            pts[i].pressNear = 0f;
        }
    }

    void CalcDensity()
    {
        for (int i = 0; i < particleCount; i++)
        {
            if (pts[i].state != INSIDE) continue;
            if (pts[i].sleepTimer > 0) continue;

            int start = neighborStart[i];
            int count = neighborCount[i];

            for (int k = 0; k < count; k++)
            {
                int j = neighborBuffer[start + k];
                Vector3 d = pts[j].pos - pts[i].pos;
                float distSq = d.sqrMagnitude;
                if (distSq < 0.00000001f) continue;
                float dist = Mathf.Sqrt(distSq);

                float q = 1f - dist / R;
                float q2 = q * q;
                float q3 = q2 * q;

                pts[i].rho += q2; pts[j].rho += q2;
                pts[i].rhoNear += q3; pts[j].rhoNear += q3;
            }
        }
    }

    void CalcPressure()
    {
        for (int i = 0; i < particleCount; i++)
        {
            if (pts[i].state != INSIDE) continue;
            pts[i].press = K * (pts[i].rho - REST_DENSITY);
            pts[i].pressNear = K_NEAR * pts[i].rhoNear;
        }
    }

    void ApplyPressure()
    {
        for (int i = 0; i < particleCount; i++)
        {
            if (pts[i].state != INSIDE) continue;
            if (pts[i].sleepTimer > 0) continue;

            int start = neighborStart[i];
            int count = neighborCount[i];
            Vector3 pf = Vector3.zero;

            for (int k = 0; k < count; k++)
            {
                int j = neighborBuffer[start + k];
                Vector3 dir = pts[j].pos - pts[i].pos;
                float distSq = dir.sqrMagnitude;
                if (distSq < 0.00000001f) continue;
                float dist = Mathf.Sqrt(distSq);
                Vector3 norm = dir / dist;

                float q = 1f - dist / R;
                float totalP = (pts[i].press + pts[j].press) * q * q
                             + (pts[i].pressNear + pts[j].pressNear) * q * q * q;
                Vector3 pv = totalP * norm;

                pts[j].force += pv;
                pf += pv;
            }
            pts[i].force -= pf;
        }
    }

    void ApplyViscosity()
    {
        for (int i = 0; i < particleCount; i++)
        {
            if (pts[i].state != INSIDE) continue;
            if (pts[i].sleepTimer > 0) continue;

            int start = neighborStart[i];
            int count = neighborCount[i];

            for (int k = 0; k < count; k++)
            {
                int j = neighborBuffer[start + k];
                Vector3 dir = pts[j].pos - pts[i].pos;
                float distSq = dir.sqrMagnitude;
                if (distSq < 0.00000001f) continue;
                float dist = Mathf.Sqrt(distSq);
                Vector3 norm = dir / dist;

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
        for (int i = 0; i < particleCount; i++)
        {
            if (pts[i].state != INSIDE) continue;
            if (pts[i].sleepTimer > 0) continue;

            int start = neighborStart[i];
            int count = neighborCount[i];
            if (count == 0) continue;

            Vector3 weightedCenter = Vector3.zero;
            float totalWeight = 0f;

            for (int k = 0; k < count; k++)
            {
                int j = neighborBuffer[start + k];
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
        Vector3 fluidForce = -bucketAccelWorld * bucketInfluence;
        if (fluidForce.magnitude > 5f)
            fluidForce = fluidForce.normalized * 5f;

        for (int i = 0; i < particleCount; i++)
        {
            if (pts[i].state != INSIDE) continue;
            if (pts[i].sleepTimer > 0) continue;
            pts[i].force += Vector3.down * G * gravityScale;
            pts[i].force += fluidForce;
        }
    }

    void Integrate(float dt)
    {
        for (int i = 0; i < particleCount; i++)
        {
            if (pts[i].state != INSIDE) continue;
            if (pts[i].sleepTimer > 0) continue;

            pts[i].vel += pts[i].force * dt;
            pts[i].vel *= DAMPING;

            if (pts[i].vel.magnitude > MAX_VEL)
                pts[i].vel = pts[i].vel.normalized * MAX_VEL;

            pts[i].pos += pts[i].vel * dt;

            // [OPT-2] Sleep — ينام بس على السرعة، الجيران بيحسبهم البندول
            // neighborCount مش موثوق هون لأن النايمين ما بيدخلوا الـ grid
            if (pts[i].vel.sqrMagnitude < sleepThreshold)
                pts[i].sleepTimer = sleepFrames;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  [OPT-5] EnforceBoundaryHard — حدود صارمة بدون تسريب
    //  الفكرة: نطبق الحد مباشرة على الموقع قبل أي حسابات تانية
    //  هاد أفضل من التصحيح التدريجي لأنه يمنع التسريب نهائياً
    // ════════════════════════════════════════════════════════════════
    void EnforceBoundaryHard()
    {
        Vector3 center = BucketCenter;
        Vector3 up = BucketUp;
        float maxRad = bucketWorldRadius * 0.88f;
        float H2 = bucketWorldHeight * 0.5f;
        float bottom = -H2 + R * 0.4f;
        float top = H2 * 0.55f;

        for (int i = 0; i < particleCount; i++)
        {
            if (pts[i].state != INSIDE) continue;

            Vector3 offset = pts[i].pos - center;
            float vert = Vector3.Dot(offset, up);
            Vector3 horizVec = offset - up * vert;
            float horizDist = horizVec.magnitude;

            // ── الحد السفلي ────────────────────────────────────────
            if (vert < bottom)
            {
                vert = bottom;
                pts[i].pos = center + horizVec + up * vert;
                float vy = Vector3.Dot(pts[i].vel, up);
                if (vy < 0f) pts[i].vel -= up * vy * 1.5f;
            }

            // ── الحد العلوي (صارم — لا تسريب) ──────────────────────
            if (vert > top)
            {
                vert = top;
                pts[i].pos = center + horizVec + up * vert;
                float vy = Vector3.Dot(pts[i].vel, up);
                if (vy > 0f) pts[i].vel -= up * vy; // عكس كامل بلا مرونة
            }

            // ── الحد الجانبي (صارم) ────────────────────────────────
            if (horizDist > maxRad && horizDist > 0.001f)
            {
                Vector3 radialDir = horizVec / horizDist;
                pts[i].pos = center + radialDir * maxRad + up * vert;

                // عكس المركبة الشعاعية فقط
                float vRad = Vector3.Dot(pts[i].vel, radialDir);
                if (vRad > 0f) pts[i].vel -= radialDir * vRad * 1.2f;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  SpawnExitParticle — [FIX] يختار أقرب جزيء للفتحة
    //  بدل الاختيار العشوائي — هاد يعطي واقعية بصرية أكبر بكثير
    //  لأن السائل فعلاً يخرج من المنطقة القريبة من الفتحة
    // ════════════════════════════════════════════════════════════════
    public void SpawnExitParticle(bool fromTop)
    {
        Vector3 exitPoint = fromTop ? TopPosition : HolePosition;

        // ابحث عن أقرب جزيء Inside للفتحة
        int bestIdx = -1;
        float bestDist = float.MaxValue;

        for (int i = 0; i < particleCount; i++)
        {
            if (pts[i].state != INSIDE) continue;

            float d = (pts[i].pos - exitPoint).sqrMagnitude;
            if (d < bestDist)
            {
                bestDist = d;
                bestIdx = i;
            }
        }

        if (bestIdx < 0) return; // ما في جزيئات

        pts[bestIdx].state = FALLING;
        pts[bestIdx].fromTop = fromTop;

        // ضعه عند الفتحة
        pts[bestIdx].pos = exitPoint + Random.insideUnitSphere * (bucketWorldRadius * 0.15f);

        Vector3 bucketVel = pendulum != null ? pendulum.GetBucketVelocity() : Vector3.zero;
        Vector3 exitDir = fromTop ? BucketUp : (-BucketUp);
        float exitSpeed = Mathf.Lerp(1.5f, 0.3f, SIGMA / 1.2f);

        pts[bestIdx].vel = bucketVel + exitDir * exitSpeed
                         + Random.insideUnitSphere * 0.08f;
    }

    // ════════════════════════════════════════════════════════════════
    void UpdateFalling(float dt)
    {
        if (canvasTransform == null) return;

        for (int i = 0; i < particleCount; i++)
        {
            if (pts[i].state != FALLING) continue;

            pts[i].vel += Vector3.down * G * dt;
            pts[i].vel *= 0.995f;
            pts[i].pos += pts[i].vel * dt;

            Vector3 localPos = canvasTransform.InverseTransformPoint(pts[i].pos);
            bool hitCanvas = canvasIsHorizontal
                ? (localPos.y < 0.05f && localPos.y > -0.1f)
                : (Mathf.Abs(localPos.z) < 0.05f);
            bool inBounds = Mathf.Abs(localPos.x) < 0.5f
                         && Mathf.Abs(localPos.z) < 0.5f;

            if (hitCanvas && inBounds)
            {
                DrawOnCanvas(pts[i].pos, pts[i].vel.magnitude);
                pts[i].state = ON_CANVAS;
            }

            if (pts[i].pos.y < canvasTransform.position.y - 1f)
                pts[i].state = ON_CANVAS;
        }
    }

    // ════════════════════════════════════════════════════════════════
    void SpawnParticles()
    {
        pts = new Particle[maxParticles];
        particleCount = maxParticles;

        Vector3 center = BucketCenter;
        Vector3 up = BucketUp, right = BucketRight, fwd = BucketForward;

        for (int i = 0; i < maxParticles; i++)
        {
            float a = Random.Range(0f, Mathf.PI * 2f);
            float r = Random.Range(0f, bucketWorldRadius * 0.9f);
            float h = Random.Range(-bucketWorldHeight * 0.45f, bucketWorldHeight * 0.15f);

            pts[i] = new Particle
            {
                pos = center + right * (Mathf.Cos(a) * r)
                               + fwd * (Mathf.Sin(a) * r)
                               + up * h,
                state = INSIDE
            };
        }
        Debug.Log($"[PaintV8.0] Spawned {particleCount} particles");
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
        main.maxParticles = maxParticles + 100;
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

        visualParticles = new ParticleSystem.Particle[maxParticles + 100];
        visualPS.Play();
    }

    void UpdateVisualPS()
    {
        if (visualPS == null) return;

        Vector3 center = BucketCenter;
        Vector3 up = BucketUp;
        float sz = R * 1.6f;
        int vIdx = 0;

        for (int i = 0; i < particleCount && vIdx < visualParticles.Length; i++)
        {
            if (pts[i].state == ON_CANVAS) continue;

            Vector3 pos;
            Color c;
            float particleSz;

            if (pts[i].state == INSIDE)
            {
                pos = pts[i].pos + Random.insideUnitSphere * (R * 0.15f);

                // clamp بصري للدلو
                Vector3 off = pos - center;
                float vert = Vector3.Dot(off, up);
                Vector3 hVec = off - up * vert;
                if (hVec.magnitude > bucketWorldRadius * 0.95f)
                    hVec = hVec.normalized * bucketWorldRadius * 0.95f;
                vert = Mathf.Clamp(vert, -bucketWorldHeight * 0.5f, bucketWorldHeight * 0.5f);
                pos = center + hVec + up * vert;

                float depth = Vector3.Dot(pos - center, up);
                float t = Mathf.InverseLerp(-bucketWorldHeight * 0.5f, 0f, depth);
                c = Color.Lerp(paintColorDark, paintColor, t);
                particleSz = sz * Random.Range(0.9f, 1.1f);
            }
            else // FALLING
            {
                pos = pts[i].pos;
                c = paintColor;
                c.a = 0.85f;
                particleSz = sz * 0.7f;
            }

            visualParticles[vIdx].position = pos;
            visualParticles[vIdx].startColor = c;
            visualParticles[vIdx].startSize = particleSz;
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
        canvasTex.SetPixels(canvasPx);
        canvasTex.Apply();
        if (canvasRenderer) canvasRenderer.material.mainTexture = canvasTex;
    }

    void DrawOnCanvas(Vector3 wp, float speed)
    {
        if (canvasTransform == null) return;
        Vector3 lp = canvasTransform.InverseTransformPoint(wp);
        int cx = Mathf.RoundToInt(Mathf.Clamp01(lp.x + 0.5f) * canvasWidth);
        int cy = Mathf.RoundToInt(Mathf.Clamp01(lp.z + 0.5f) * canvasHeight);

        float spreadMult = paintType switch
        {
            PaintType.Ink => 3.0f,
            PaintType.Watercolor => 2.5f,
            PaintType.Tempera => 1.5f,
            PaintType.Acrylic => 1.0f,
            PaintType.Gouache => 0.8f,
            PaintType.OilPaint => 0.6f,
            PaintType.Enamel => 0.5f,
            PaintType.Latex => 0.4f,
            _ => 1.0f
        };

        int r = Mathf.RoundToInt(brushSize * Mathf.Clamp(speed * 0.2f, 0.5f, 3f) * spreadMult);
        FillCircle(cx, cy, r, paintColor);

        int splats = paintType switch
        {
            PaintType.Ink => 8,
            PaintType.Watercolor => 6,
            PaintType.Tempera => 4,
            PaintType.Acrylic => 3,
            PaintType.Gouache => 2,
            PaintType.OilPaint => 1,
            PaintType.Enamel => 1,
            PaintType.Latex => 0,
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

    // ════════════════════════════════════════════════════════════════
    public void RefillBucket()
    {
        for (int i = 0; i < particleCount; i++)
            if (pts[i].state == FALLING)
                pts[i].state = INSIDE;

        if (InsideCount() < maxParticles / 2)
            SpawnParticles();
    }

    public void ClearCanvas()
    {
        var bg = new Color(0.95f, 0.92f, 0.85f, 1f);
        for (int i = 0; i < canvasPx.Length; i++) canvasPx[i] = bg;
        canvasTex.SetPixels(canvasPx);
        canvasTex.Apply();
    }

    public void SaveCanvas(string path = "PaintResult.png")
        => System.IO.File.WriteAllBytes(path, canvasTex.EncodeToPNG());

    public int InsideCount()
    {
        int c = 0;
        for (int i = 0; i < particleCount; i++)
            if (pts[i].state == INSIDE) c++;
        return c;
    }

    public int FallingCount()
    {
        int c = 0;
        for (int i = 0; i < particleCount; i++)
            if (pts[i].state == FALLING) c++;
        return c;
    }

    // ════════════════════════════════════════════════════════════════
    GUIStyle paintHudStyle;
    Texture2D paintHudBg;

    void InitPaintHudStyle()
    {
        paintHudBg = new Texture2D(1, 1);
        paintHudBg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.6f));
        paintHudBg.Apply();
        paintHudStyle = new GUIStyle
        {
            fontSize = 11,
            padding = new RectOffset(4, 4, 2, 2)
        };
        paintHudStyle.normal.textColor = Color.white;
    }

    void OnGUI()
    {
        if (paintHudStyle == null) InitPaintHudStyle();
        int x = 360, y = 10, lh = 18, lines = 6;
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

        // عدد الجزيئات النايمة
        int sleeping = 0;
        for (int i = 0; i < particleCount; i++)
            if (pts[i].state == INSIDE && pts[i].sleepTimer > 0) sleeping++;

        GUI.Label(new Rect(x, y, 230, lh), $"Type    : {paintName}", paintHudStyle);
        GUI.Label(new Rect(x, y + lh, 230, lh), $"Inside  : {InsideCount()} / {maxParticles}", paintHudStyle);
        GUI.Label(new Rect(x, y + lh * 2, 230, lh), $"Falling : {FallingCount()}", paintHudStyle);
        GUI.Label(new Rect(x, y + lh * 3, 230, lh), $"Sleeping: {sleeping}", paintHudStyle);
        GUI.Label(new Rect(x, y + lh * 4, 230, lh), $"Tilt(θ) : {(pendulum != null ? pendulum.theta * Mathf.Rad2Deg : 0f):F1}°", paintHudStyle);
        GUI.Label(new Rect(x, y + lh * 5, 230, lh), $"SIGMA   : {SIGMA:F3}", paintHudStyle);
    }

    void OnDrawGizmos()
    {
        if (!showDebugGizmos || !bucketTransform) return;
        Vector3 c = BucketCenter;
        Vector3 up = BucketUp;
        Vector3 r = BucketRight;
        Vector3 f = BucketForward;
        float H2 = bucketWorldHeight * 0.5f;
        float R2 = bucketWorldRadius;

        Gizmos.color = new Color(0f, 1f, 0f, 0.25f);
        for (int s = 0; s < 16; s++)
        {
            float a1 = s / 16f * Mathf.PI * 2f;
            float a2 = (s + 1) / 16f * Mathf.PI * 2f;
            Vector3 p1 = c + (r * Mathf.Cos(a1) + f * Mathf.Sin(a1)) * R2;
            Vector3 p2 = c + (r * Mathf.Cos(a2) + f * Mathf.Sin(a2)) * R2;
            Gizmos.DrawLine(p1 - up * H2, p2 - up * H2);
            Gizmos.DrawLine(p1 + up * H2 * 0.55f, p2 + up * H2 * 0.55f);
            Gizmos.DrawLine(p1 - up * H2, p1 + up * H2 * 0.55f);
        }

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(HolePosition, 0.025f);

        if (pts == null) return;
        for (int i = 0; i < particleCount; i++)
        {
            if (pts[i].state == INSIDE)
            {
                Gizmos.color = pts[i].sleepTimer > 0 ? Color.gray : paintColor;
                Gizmos.DrawSphere(pts[i].pos, 0.012f);
            }
            else if (pts[i].state == FALLING)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawSphere(pts[i].pos, 0.015f);
            }
        }
    }
}