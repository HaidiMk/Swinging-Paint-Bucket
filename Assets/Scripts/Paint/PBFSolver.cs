using UnityEngine;
using System.Runtime.InteropServices;

// ════════════════════════════════════════════════════════════════════
//  PBFSolver.cs  v1.0
//  Position Based Fluids — GPU Solver
//
//  هاد السكربت يدير الـ Compute Shader على GPU.
//  يحل محل PaintSimulation.cs القديم بالكامل.
//
//  المعمارية:
//  CPU  → يعطي الـ GPU البيانات (Buffers)
//  GPU  → يحسب كل شي (Compute Kernels)
//  CPU  → يقرأ النتائج فقط عند الحاجة (Canvas Impact)
//
//  لماذا PBF وليس SPH؟
//  SPH  : يحسب القوى ثم يكمّل (Explicit) → قد ينفجر عددياً
//  PBF  : يحل الـ constraints مباشرة (Implicit) → مستقر دائماً
//         مناسب لأعداد كبيرة (100K-500K) بدون تعديل
// ════════════════════════════════════════════════════════════════════

public class PBFSolver : MonoBehaviour
{
    // ══════════════════════════════════════════════════════════════
    [Header("References — المراجع")]
    public SphericalPendulum pendulum;
    public Transform bucketTransform;
    public Transform canvasTransform;
    public Renderer canvasRenderer;
    public ComputeShader pbfComputeShader;   // PBFSolver.compute

    // ══════════════════════════════════════════════════════════════
    [Header("Paint Type — نوع الطلاء")]
    public PaintType paintType = PaintType.Acrylic;

    public enum PaintType
    { Watercolor, Acrylic, OilPaint, Tempera, Gouache, Latex, Enamel, Ink }

    // ══════════════════════════════════════════════════════════════
    [Header("PBF Settings — إعدادات المحاكاة")]
    [Tooltip("عدد الجزيئات — RTX 2050 يدعم 50K-200K بسهولة")]
    [Range(1000, 200000)]
    public int maxParticles = 10000;

    [Tooltip("نصف قطر التأثير بين الجزيئات")]
    [Range(0.05f, 0.3f)]
    public float h = 0.18f;

    [Tooltip("الكثافة الطبيعية للسائل — أكبر = أكثف")]
    [Range(1f, 20f)]
    public float restDensity = 6f;

    [Tooltip("عدد iterations لحل الـ constraints — أكثر = أدق وأبطأ")]
    [Range(1, 10)]
    public int solverIterations = 3;

    [Tooltip("ε لتجنب القسمة على صفر في معادلة λ")]
    public float epsilon = 600f;

    public float G = 9.81f;

    // ══════════════════════════════════════════════════════════════
    [Header("Bucket Size — حجم الدلو")]
    public float bucketWorldRadius = 0.25f;
    public float bucketWorldHeight = 0.4f;
    public Vector3 bucketCenterOffset = new Vector3(0f, -0.15f, 0f);

    // ══════════════════════════════════════════════════════════════
    [Header("Canvas — اللوحة")]
    public int canvasWidth = 1024;
    public int canvasHeight = 1024;
    public bool canvasIsHorizontal = true;
    public int baseBrushSize = 8;
    [Range(0.1f, 2f)] public float speedToSizeMultiplier = 0.3f;
    [Range(0, 20)] public int splashDroplets = 6;
    [Range(5, 80)] public int splashMaxRadius = 30;
    [Range(0.1f, 0.8f)] public float dropletSizeRatio = 0.3f;

    // ══════════════════════════════════════════════════════════════
    [Header("Visual — بصري")]
    public Color paintColor = new Color(0.9f, 0.1f, 0.1f, 1f);
    public Color paintColorDark = new Color(0.5f, 0.05f, 0.05f, 1f);
    public float visualParticleSize = 0.015f;

    [Header("Debug")]
    public bool showDebugGizmos = true;

    // ══════════════════════════════════════════════════════════════
    //  Paint Parameters
    // ══════════════════════════════════════════════════════════════
    [HideInInspector] public float gravityScale = 0.6f;
    [HideInInspector] public float bucketInfluence = 0.3f;
    [HideInInspector] public float SIGMA = 0.3f;

    // ══════════════════════════════════════════════════════════════
    //  GPU Buffers — كل البيانات على كرت الشاشة
    // ══════════════════════════════════════════════════════════════
    ComputeBuffer positionsBuffer;
    ComputeBuffer predictedPosBuffer;
    ComputeBuffer velocitiesBuffer;
    ComputeBuffer deltaPosBuffer;
    ComputeBuffer lambdasBuffer;
    ComputeBuffer statesBuffer;
    ComputeBuffer neighborListBuffer;
    ComputeBuffer neighborCountBuffer;
    ComputeBuffer gridCountBuffer;
    ComputeBuffer gridParticlesBuffer;

    // ══════════════════════════════════════════════════════════════
    //  Kernel IDs — أرقام الـ kernels في الـ Compute Shader
    // ══════════════════════════════════════════════════════════════
    int kPredictPositions;
    int kClearGrid;
    int kFillGrid;
    int kFindNeighbors;
    int kSolveConstraints;
    int kApplyCorrection;
    int kUpdateVelocity;
    int kEnforceBoundary;
    int kFallingStep;

    // ══════════════════════════════════════════════════════════════
    //  Grid
    // ══════════════════════════════════════════════════════════════
    int gridSizeX, gridSizeY, gridSizeZ;
    Vector3 gridOrigin;
    float cellSize;
    const int MAX_PER_CELL = 32;
    const int MAX_NEIGHBORS = 64;
    int totalCells;

    // ══════════════════════════════════════════════════════════════
    //  CPU-side data — cached copy تتحدث كل 5 frames
    // ══════════════════════════════════════════════════════════════
    Vector3[] positionsCPU;
    int[] statesCPU;
    Vector3[] velocitiesCPU;

    // Cached values — للـ BucketHole بدون GetData كل frame
    int cachedInsideCount = 0;
    float cachedLiquidHeight = 0.5f;
    bool cpuDataReady = false;

    // حالات الجزيء
    const int INSIDE = 0;
    const int FALLING = 1;
    const int ON_CANVAS = 2;

    // Canvas
    Texture2D canvasTex;
    Color[] canvasPx;
    bool canvasDirty;
    float canvasHalfX = 0.5f;
    float canvasHalfZ = 0.5f;

    // Trail
    Vector3 lastTrailPoint;
    bool hasLastTrailPoint = false;

    // Dynamics
    Vector3 prevBucketVel = Vector3.zero;
    Vector3 bucketAccelWorld = Vector3.zero;

    // Visual PS
    ParticleSystem visualPS;
    ParticleSystem.Particle[] visualParticles;
    bool visualDirty = true;
    bool initialized = false;

    int frameCount = 0;

    // ══════════════════════════════════════════════════════════════
    //  Bucket Helpers
    // ══════════════════════════════════════════════════════════════
    public Vector3 BucketCenter => bucketTransform.position
                                 + bucketTransform.TransformDirection(bucketCenterOffset);
    Vector3 BucketUp => bucketTransform.TransformDirection(Vector3.up).normalized;
    Vector3 BucketRight => bucketTransform.TransformDirection(Vector3.right).normalized;
    Vector3 BucketForward => bucketTransform.TransformDirection(Vector3.forward).normalized;

    public Vector3 HolePosition => BucketCenter - BucketUp * (bucketWorldHeight * 0.5f);
    public Vector3 TopPosition => BucketCenter + BucketUp * (bucketWorldHeight * 0.5f);

    // ── Accessors for the GPU instanced renderer ──
    public ComputeBuffer PositionsBuffer => positionsBuffer;
    public ComputeBuffer StatesBuffer => statesBuffer;
    public int ParticleCount => maxParticles;
    public Vector3 BucketUpDir => BucketUp;

    // ════════════════════════════════════════════════════════════════
    void Start()
    {
        if (pbfComputeShader == null)
        {
            Debug.LogError("[PBF] Compute Shader غير مربوط! اربط PBFSolver.compute بالـ Inspector.");
            return;
        }

        ApplyPaintType();
        InitGrid();
        InitGPUBuffers();
        InitCanvas();
        // InitVisualPS();   // ← off: replaced by GPU instanced renderer (PaintParticleRenderer)
        SpawnParticles();

        initialized = true;
        Debug.Log($"[PBFSolver] Initialized — Particles={maxParticles} | GPU=RTX2050 | Iterations={solverIterations}");
    }

    // ════════════════════════════════════════════════════════════════
    void OnDestroy()
    {
        // لازم تحرر GPU Buffers — بتسبب memory leak إذا ما تحررت
        ReleaseBuffers();
    }

    void ReleaseBuffers()
    {
        positionsBuffer?.Release();
        predictedPosBuffer?.Release();
        velocitiesBuffer?.Release();
        deltaPosBuffer?.Release();
        lambdasBuffer?.Release();
        statesBuffer?.Release();
        neighborListBuffer?.Release();
        neighborCountBuffer?.Release();
        gridCountBuffer?.Release();
        gridParticlesBuffer?.Release();
    }

    // ════════════════════════════════════════════════════════════════
    //  InitGrid — حجم الشبكة المكانية
    // ════════════════════════════════════════════════════════════════
    void InitGrid()
    {
        cellSize = h;
        float margin = h * 2f;
        float w = (bucketWorldRadius + margin) * 2f;
        float ht = bucketWorldHeight + margin * 2f;

        gridSizeX = Mathf.CeilToInt(w / cellSize) + 2;
        gridSizeY = Mathf.CeilToInt(ht / cellSize) + 2;
        gridSizeZ = Mathf.CeilToInt(w / cellSize) + 2;

        gridOrigin = new Vector3(
            -(gridSizeX * cellSize) / 2f,
            -(gridSizeY * cellSize) / 2f,
            -(gridSizeZ * cellSize) / 2f
        );

        totalCells = gridSizeX * gridSizeY * gridSizeZ;
        Debug.Log($"[PBF] Grid: {gridSizeX}x{gridSizeY}x{gridSizeZ} = {totalCells} cells");
    }

    // ════════════════════════════════════════════════════════════════
    //  InitGPUBuffers — يحجز الذاكرة على GPU
    // ════════════════════════════════════════════════════════════════
    void InitGPUBuffers()
    {
        int n = maxParticles;

        // كل Buffer = array على GPU
        // sizeof(float3) = 12 bytes ، sizeof(float) = 4 bytes
        positionsBuffer = new ComputeBuffer(n, sizeof(float) * 3);
        predictedPosBuffer = new ComputeBuffer(n, sizeof(float) * 3);
        velocitiesBuffer = new ComputeBuffer(n, sizeof(float) * 3);
        deltaPosBuffer = new ComputeBuffer(n, sizeof(float) * 3);
        lambdasBuffer = new ComputeBuffer(n, sizeof(float));
        statesBuffer = new ComputeBuffer(n, sizeof(int));
        neighborListBuffer = new ComputeBuffer(n * MAX_NEIGHBORS, sizeof(int));
        neighborCountBuffer = new ComputeBuffer(n, sizeof(int));
        gridCountBuffer = new ComputeBuffer(totalCells, sizeof(int));
        gridParticlesBuffer = new ComputeBuffer(totalCells * MAX_PER_CELL, sizeof(int));

        // احسب IDs الـ kernels مرة وحدة
        kPredictPositions = pbfComputeShader.FindKernel("PredictPositions");
        kClearGrid = pbfComputeShader.FindKernel("ClearGrid");
        kFillGrid = pbfComputeShader.FindKernel("FillGrid");
        kFindNeighbors = pbfComputeShader.FindKernel("FindNeighbors");
        kSolveConstraints = pbfComputeShader.FindKernel("SolveConstraints");
        kApplyCorrection = pbfComputeShader.FindKernel("ApplyCorrection");
        kUpdateVelocity = pbfComputeShader.FindKernel("UpdateVelocity");
        kEnforceBoundary = pbfComputeShader.FindKernel("EnforceBoundary");
        kFallingStep = pbfComputeShader.FindKernel("FallingStep");

        // اربط الـ Buffers بكل الـ kernels
        BindAllBuffers();

        // احجز CPU arrays للقراءة عند الحاجة
        positionsCPU = new Vector3[n];
        statesCPU = new int[n];
        velocitiesCPU = new Vector3[n];

        long totalBytes = (long)n * (12 * 7 + 4 + 4)
                       + (long)totalCells * (4 + 4 * MAX_PER_CELL)
                       + (long)n * MAX_NEIGHBORS * 4;
        Debug.Log($"[PBF] GPU Buffers allocated — {totalBytes / 1024 / 1024} MB on GPU");
    }

    // ربط كل Buffer بكل kernel
    void BindAllBuffers()
    {
        int[] kernels = {
            kPredictPositions, kClearGrid, kFillGrid,
            kFindNeighbors, kSolveConstraints, kApplyCorrection,
            kUpdateVelocity, kEnforceBoundary, kFallingStep
        };

        foreach (int k in kernels)
        {
            pbfComputeShader.SetBuffer(k, "positions", positionsBuffer);
            pbfComputeShader.SetBuffer(k, "predictedPos", predictedPosBuffer);
            pbfComputeShader.SetBuffer(k, "velocities", velocitiesBuffer);
            pbfComputeShader.SetBuffer(k, "deltaPos", deltaPosBuffer);
            pbfComputeShader.SetBuffer(k, "lambdas", lambdasBuffer);
            pbfComputeShader.SetBuffer(k, "states", statesBuffer);
            pbfComputeShader.SetBuffer(k, "neighborList", neighborListBuffer);
            pbfComputeShader.SetBuffer(k, "neighborCount", neighborCountBuffer);
            pbfComputeShader.SetBuffer(k, "gridCount", gridCountBuffer);
            pbfComputeShader.SetBuffer(k, "gridParticles", gridParticlesBuffer);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  SpawnParticles — ينشئ الجزيئات على CPU ثم يرفعها للـ GPU
    // ════════════════════════════════════════════════════════════════
    void SpawnParticles()
    {
        Vector3[] initPos = new Vector3[maxParticles];
        Vector3[] initVel = new Vector3[maxParticles];
        int[] initSt = new int[maxParticles];

        Vector3 center = BucketCenter;
        Vector3 up = BucketUp, right = BucketRight, fwd = BucketForward;

        for (int i = 0; i < maxParticles; i++)
        {
            float a = Random.Range(0f, Mathf.PI * 2f);
            float r = Random.Range(0f, bucketWorldRadius * 0.9f);
            float ht = Random.Range(-bucketWorldHeight * 0.45f, bucketWorldHeight * 0.15f);

            initPos[i] = center
                       + right * (Mathf.Cos(a) * r)
                       + fwd * (Mathf.Sin(a) * r)
                       + up * ht;
            initVel[i] = Vector3.zero;
            initSt[i] = INSIDE;
        }

        // رفع البيانات من CPU → GPU
        positionsBuffer.SetData(initPos);
        velocitiesBuffer.SetData(initVel);
        statesBuffer.SetData(initSt);

        Debug.Log($"[PBF] Spawned {maxParticles} particles → GPU");
    }

    // ════════════════════════════════════════════════════════════════
    //  FixedUpdate — Pipeline الكامل على GPU
    // ════════════════════════════════════════════════════════════════
    void FixedUpdate()
    {
        if (!initialized || !bucketTransform) return;

        float dt = Time.fixedDeltaTime;
        frameCount++;

        // حساب تسارع الدلو على CPU
        Vector3 curVel = pendulum != null ? pendulum.GetBucketVelocity() : Vector3.zero;
        bucketAccelWorld = (curVel - prevBucketVel) / Mathf.Max(dt, 0.001f);
        bucketAccelWorld = Vector3.ClampMagnitude(bucketAccelWorld, 8f);
        prevBucketVel = curVel;

        if (pendulum != null && pendulum.fadeOutStarted)
        {
            float fp = Mathf.Clamp01(pendulum.fadeOutTimer / pendulum.fadeOutDuration);
            bucketAccelWorld *= (1f - fp);
        }

        // تحديث Constants في الـ Compute Shader
        SetShaderConstants(dt);

        // ── GPU Pipeline ─────────────────────────────────────────

        // Pass 0: توقع المواقع الجديدة
        Dispatch(kPredictPositions);

        // Pass 1: بناء الشبكة المكانية
        DispatchGrid(kClearGrid);   // ClearGrid يعمل على totalCells وليس maxParticles
        Dispatch(kFillGrid);

        // Pass 2: إيجاد الجيران
        Dispatch(kFindNeighbors);

        // Pass 3: حل الـ Constraints (عدة iterations = دقة أعلى)
        for (int iter = 0; iter < solverIterations; iter++)
        {
            Dispatch(kSolveConstraints);
            Dispatch(kApplyCorrection);
        }

        // Pass 4: تحديث السرعة والموقع
        Dispatch(kUpdateVelocity);

        // Pass 5: حدود الدلو
        Dispatch(kEnforceBoundary);

        // Pass 6: الجزيئات الساقطة
        Dispatch(kFallingStep);

        // ── CPU Readback ─────────────────────────────────────────
        // كل فريم: نقرأ positions+states للعرض البصري
        // كل 5 frames: نضيف velocities + canvas impact + cached values
        if (frameCount % 5 == 0)
            CPUReadback();
        else if (!cpuDataReady)
            CPUReadback(); // أول مرة نشتغل فيها — نجبر readback فوري

        visualDirty = true;
    }

    // ════════════════════════════════════════════════════════════════
    //  SetShaderConstants — يحدث المتغيرات في الـ Compute Shader
    // ════════════════════════════════════════════════════════════════
    void SetShaderConstants(float dt)
    {
        Vector3 center = BucketCenter;
        Vector3 up = BucketUp;
        Vector3 gridOrig = center + gridOrigin;

        pbfComputeShader.SetFloat("dt", dt);
        pbfComputeShader.SetFloat("h", h);
        pbfComputeShader.SetFloat("restDensity", restDensity);
        pbfComputeShader.SetFloat("epsilon", epsilon);
        pbfComputeShader.SetFloat("gravity", G);
        pbfComputeShader.SetFloat("gravityScale", gravityScale);
        pbfComputeShader.SetFloat("bucketInfluence", bucketInfluence);
        pbfComputeShader.SetVector("bucketAccel", bucketAccelWorld);
        pbfComputeShader.SetInt("particleCount", maxParticles);
        pbfComputeShader.SetInt("maxNeighbors", MAX_NEIGHBORS);
        pbfComputeShader.SetInt("maxPerCell", MAX_PER_CELL);

        // Bucket
        pbfComputeShader.SetVector("bucketCenter", center);
        pbfComputeShader.SetVector("bucketUp", up);
        pbfComputeShader.SetFloat("bucketRadius", bucketWorldRadius * 0.88f);
        pbfComputeShader.SetFloat("bucketTop", bucketWorldHeight * 0.5f * 0.55f);
        pbfComputeShader.SetFloat("bucketBottom", -bucketWorldHeight * 0.5f + h * 0.4f);

        // Grid
        pbfComputeShader.SetInt("gridSizeX", gridSizeX);
        pbfComputeShader.SetInt("gridSizeY", gridSizeY);
        pbfComputeShader.SetInt("gridSizeZ", gridSizeZ);
        pbfComputeShader.SetVector("gridOrigin", gridOrig);
        pbfComputeShader.SetFloat("cellSize", cellSize);
    }

    // Dispatch — يشغل kernel على كل الجزيئات
    void Dispatch(int kernel)
    {
        int groups = Mathf.CeilToInt(maxParticles / 256f);
        pbfComputeShader.Dispatch(kernel, groups, 1, 1);
    }

    // Dispatch للـ Grid (حجم مختلف)
    void DispatchGrid(int kernel)
    {
        int groups = Mathf.CeilToInt(totalCells / 256f);
        pbfComputeShader.Dispatch(kernel, groups, 1, 1);
    }

    // ════════════════════════════════════════════════════════════════
    //  CheckCanvasImpacts — اقرأ البيانات من GPU وتحقق من الاصطدامات
    //
    //  GPU Readback بطيء (يوقف GPU) — لهيك نعمله كل 5 frames
    //  الحل المثالي: AsyncGPUReadback (غير متزامن) — للتطوير المستقبلي
    // ════════════════════════════════════════════════════════════════
    // ════════════════════════════════════════════════════════════════
    //  CPUReadback — استدعاء GetData مرة وحدة كل 5 frames
    //  يشمل: cached values + canvas impacts
    // ════════════════════════════════════════════════════════════════
    void CPUReadback()
    {
        // قراءة البيانات من GPU → CPU (مرة وحدة فقط)
        positionsBuffer.GetData(positionsCPU);
        statesBuffer.GetData(statesCPU);
        velocitiesBuffer.GetData(velocitiesCPU);

        // ── 1) تحديث Cached Values للـ BucketHole ────────────────
        Vector3 holePos = HolePosition;
        float sampleRad = bucketWorldRadius * 0.6f;
        UpdateCachedValues(holePos, sampleRad);

        // ── 2) اكتشاف اصطدامات اللوحة ────────────────────────────
        if (canvasTransform == null) return;

        bool anyChange = false;

        for (int i = 0; i < maxParticles; i++)
        {
            if (statesCPU[i] != FALLING) continue;

            Vector3 wp = positionsCPU[i];
            Vector3 lp = canvasTransform.InverseTransformPoint(wp);

            bool hitCanvas = canvasIsHorizontal
                ? (lp.y < 0.05f && lp.y > -0.1f)
                : (Mathf.Abs(lp.z) < 0.05f);
            bool inBounds = Mathf.Abs(lp.x) < 0.5f && Mathf.Abs(lp.z) < 0.5f;

            if (hitCanvas && inBounds)
            {
                float speed = velocitiesCPU[i].magnitude;
                DrawSplash(wp, speed);
                statesCPU[i] = ON_CANVAS;
                anyChange = true;
            }

            if (wp.y < canvasTransform.position.y - 1f)
            {
                statesCPU[i] = ON_CANVAS;
                anyChange = true;
            }
        }

        if (anyChange)
            statesBuffer.SetData(statesCPU);

        visualDirty = true; // نحدّث الـ visual بعد كل readback

        // Debug: تحقق من توزيع حالات الجزيئات كل 60 فريم
        if (frameCount % 60 == 0)
        {
            int ins = 0, fall = 0, onC = 0;
            for (int i = 0; i < maxParticles; i++)
            {
                if (statesCPU[i] == INSIDE) ins++;
                else if (statesCPU[i] == FALLING) fall++;
                else onC++;
            }
            Debug.Log($"[PBF States F={frameCount}] Inside={ins} | Falling={fall} | OnCanvas={onC} | LiqH={cachedLiquidHeight:F3}");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  SpawnExitParticle — يحول جزيء داخلي لساقط
    // ════════════════════════════════════════════════════════════════
    public void SpawnExitParticle(bool isFromTop)
    {
        // اقرأ states من GPU لنختار جزيء
        statesBuffer.GetData(statesCPU);
        positionsBuffer.GetData(positionsCPU);

        Vector3 exit = isFromTop ? TopPosition : HolePosition;
        int bestIdx = -1;
        float bestDist = float.MaxValue;
        int found = 0;

        for (int attempt = 0; attempt < 80 && found < 20; attempt++)
        {
            int i = Random.Range(0, maxParticles);
            if (statesCPU[i] != INSIDE) continue;
            float d = (positionsCPU[i] - exit).sqrMagnitude;
            if (d < bestDist) { bestDist = d; bestIdx = i; }
            found++;
        }

        if (bestIdx < 0) return;

        // حدّث على CPU
        statesCPU[bestIdx] = FALLING;
        // DEBUG: تحقق أن الجزيء اتحول فعلاً
        if (frameCount % 20 == 0)
            Debug.Log($"[Spawn] Particle {bestIdx} → FALLING | from={'T' + (isFromTop ? "op" : "Hole")} | found={found}");
        positionsCPU[bestIdx] = exit + Random.insideUnitSphere * (bucketWorldRadius * 0.15f);

        Vector3 bucketVel = pendulum != null ? pendulum.GetBucketVelocity() : Vector3.zero;
        Vector3 exitDir = isFromTop ? BucketUp : -BucketUp;
        float exitSpeed = Mathf.Lerp(1.5f, 0.3f, SIGMA / 1.2f);
        velocitiesCPU[bestIdx] = bucketVel + exitDir * exitSpeed
                               + Random.insideUnitSphere * 0.08f;

        // ارفع للـ GPU
        statesBuffer.SetData(statesCPU);
        positionsBuffer.SetData(positionsCPU);
        velocitiesBuffer.SetData(velocitiesCPU);
    }

    // ════════════════════════════════════════════════════════════════
    //  DrawSplash — رذاذ حقيقي على اللوحة
    // ════════════════════════════════════════════════════════════════
    void DrawSplash(Vector3 worldPos, float speed)
    {
        if (canvasTransform == null) return;

        Vector3 lp = canvasTransform.InverseTransformPoint(worldPos);
        int cx = Mathf.RoundToInt(Mathf.Clamp01((lp.x + canvasHalfX) / (canvasHalfX * 2f)) * canvasWidth);
        int cy = Mathf.RoundToInt(Mathf.Clamp01((lp.z + canvasHalfZ) / (canvasHalfZ * 2f)) * canvasHeight);

        float spreadMult = GetSpreadMult();
        int mainR = Mathf.Max(Mathf.RoundToInt(
            baseBrushSize
            * Mathf.Clamp(Mathf.Sqrt(speed) * speedToSizeMultiplier, 0.4f, 4f)
            * spreadMult), 1);

        FillCircle(cx, cy, mainR, paintColor);

        // قطرات رذاذ
        int dropCount = Mathf.RoundToInt(splashDroplets * Mathf.Clamp01(speed / 3f));
        for (int s = 0; s < dropCount; s++)
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float dist = Random.Range(mainR + 2, mainR + splashMaxRadius);
            int dropR = Mathf.Max(Mathf.RoundToInt(mainR * dropletSizeRatio), 1);
            Color dc = paintColor * Random.Range(0.6f, 0.9f);
            dc.a = paintColor.a * 0.7f;
            FillCircle(
                cx + Mathf.RoundToInt(Mathf.Cos(angle) * dist),
                cy + Mathf.RoundToInt(Mathf.Sin(angle) * dist),
                dropR, dc);
        }

        // خيوط (Streaks) عند السرعات العالية
        if (speed > 1.5f)
        {
            int streaks = Mathf.Min(Mathf.RoundToInt(3 + speed * 2f), 12);
            for (int s = 0; s < streaks; s++)
            {
                float angle = (s / (float)streaks) * Mathf.PI * 2f
                            + Random.Range(-0.2f, 0.2f);
                float len = Random.Range(mainR * 1.2f, mainR + splashMaxRadius * 0.7f);
                DrawLinePixels(cx, cy,
                    cx + Mathf.RoundToInt(Mathf.Cos(angle) * len),
                    cy + Mathf.RoundToInt(Mathf.Sin(angle) * len),
                    paintColor * 0.8f);
            }
        }

        canvasDirty = true;
    }

    // ════════════════════════════════════════════════════════════════
    //  Trail System
    // ════════════════════════════════════════════════════════════════
    public void UpdateTrail(Vector3 pointWorld, float thickness)
    {
        if (canvasTransform == null) return;
        if (!IsPointOnCanvas(pointWorld)) { hasLastTrailPoint = false; return; }

        int r = Mathf.Max(1, Mathf.RoundToInt(thickness));
        if (hasLastTrailPoint)
            DrawLineOnCanvas(lastTrailPoint, pointWorld, r, paintColor);
        else
        {
            Vector3 lp = canvasTransform.InverseTransformPoint(pointWorld);
            int px = Mathf.RoundToInt(Mathf.Clamp01((lp.x + canvasHalfX) / (canvasHalfX * 2f)) * canvasWidth);
            int py = Mathf.RoundToInt(Mathf.Clamp01((lp.z + canvasHalfZ) / (canvasHalfZ * 2f)) * canvasHeight);
            FillCircle(px, py, r, paintColor);
        }

        lastTrailPoint = pointWorld;
        hasLastTrailPoint = true;
        canvasDirty = true;
    }

    public void BreakTrail() => hasLastTrailPoint = false;

    // ════════════════════════════════════════════════════════════════
    //  Canvas & Visual Helpers
    // ════════════════════════════════════════════════════════════════
    void Update()
    {
        // if (visualDirty) { UpdateVisualPS(); visualDirty = false; }   // ← off: GPU instanced renderer now handles visuals
        if (canvasDirty)
        {
            canvasTex.SetPixels(canvasPx);
            canvasTex.Apply();
            canvasDirty = false;
        }
    }

    void FillCircle(int cx, int cy, int r, Color c)
    {
        int r2 = r * r;
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                if (dx * dx + dy * dy > r2) continue;
                int px = Mathf.Clamp(cx + dx, 0, canvasWidth - 1);
                int py = Mathf.Clamp(cy + dy, 0, canvasHeight - 1);
                canvasPx[py * canvasWidth + px] = Color.Lerp(canvasPx[py * canvasWidth + px], c, c.a * 0.85f);
            }
    }

    void DrawLinePixels(int x0, int y0, int x1, int y1, Color c)
    {
        int steps = Mathf.Min(Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0), 1), 200);
        for (int s = 0; s <= steps; s++)
        {
            float t = (float)s / steps;
            int px = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(x0, x1, t)), 0, canvasWidth - 1);
            int py = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(y0, y1, t)), 0, canvasHeight - 1);
            canvasPx[py * canvasWidth + px] = Color.Lerp(canvasPx[py * canvasWidth + px], c, c.a * 0.6f);
        }
    }

    void DrawLineOnCanvas(Vector3 fromW, Vector3 toW, int radius, Color c)
    {
        Vector3 lpA = canvasTransform.InverseTransformPoint(fromW);
        Vector3 lpB = canvasTransform.InverseTransformPoint(toW);
        int ax = Mathf.RoundToInt(Mathf.Clamp01((lpA.x + canvasHalfX) / (canvasHalfX * 2f)) * canvasWidth);
        int ay = Mathf.RoundToInt(Mathf.Clamp01((lpA.z + canvasHalfZ) / (canvasHalfZ * 2f)) * canvasHeight);
        int bx = Mathf.RoundToInt(Mathf.Clamp01((lpB.x + canvasHalfX) / (canvasHalfX * 2f)) * canvasWidth);
        int by = Mathf.RoundToInt(Mathf.Clamp01((lpB.z + canvasHalfZ) / (canvasHalfZ * 2f)) * canvasHeight);
        int steps = Mathf.Min(Mathf.Max(Mathf.Abs(bx - ax), Mathf.Abs(by - ay), 1), 300);
        for (int s = 0; s <= steps; s++)
        {
            float t = (float)s / steps;
            FillCircle(Mathf.RoundToInt(Mathf.Lerp(ax, bx, t)),
                       Mathf.RoundToInt(Mathf.Lerp(ay, by, t)), radius, c);
        }
        canvasDirty = true;
    }

    public Vector3 ProjectOntoCanvas(Vector3 worldPos)
    {
        Vector3 lp = canvasTransform.InverseTransformPoint(worldPos);
        if (canvasIsHorizontal) lp.y = 0f; else lp.z = 0f;
        return canvasTransform.TransformPoint(lp);
    }

    public bool IsPointOnCanvas(Vector3 worldPos)
    {
        Vector3 lp = canvasTransform.InverseTransformPoint(worldPos);
        return Mathf.Abs(lp.x) <= canvasHalfX && Mathf.Abs(lp.z) <= canvasHalfZ;
    }

    // ════════════════════════════════════════════════════════════════
    //  Public Queries (للـ BucketHole)
    // ════════════════════════════════════════════════════════════════
    public int InsideCount()
    {
        // نستخدم الـ cached value بدل GetData كل مرة (بطيء مع 80K)
        return cpuDataReady ? cachedInsideCount : maxParticles;
    }

    public float GetLiquidHeightAtHole(Vector3 holeWorldPos, float sampleRadius)
    {
        // cachedLiquidHeight = fillRatio × bucketWorldHeight
        // يتناقص مع خروج الجزيئات — Torricelli يتغير تدريجياً
        return cpuDataReady ? cachedLiquidHeight : bucketWorldHeight;
    }

    // يُستدعى من CPUReadback لحساب القيم المخزنة
    void UpdateCachedValues(Vector3 holeWorldPos, float sampleRadius)
    {
        int insideC = 0;
        for (int i = 0; i < maxParticles; i++)
            if (statesCPU[i] == INSIDE) insideC++;

        cachedInsideCount = insideC;

        // حساب LiqH من نسبة الجزيئات المتبقية × ارتفاع الدلو الكامل
        // هذا أدق من البحث الهندسي لأن الجزيئات موزعة بشكل موحد داخل الدلو
        float fillRatio = (float)insideC / Mathf.Max(maxParticles, 1);
        cachedLiquidHeight = fillRatio * bucketWorldHeight;

        if (frameCount % 30 == 0)
            Debug.Log($"[PBF-Cache F={frameCount}] " +
                      $"Inside={insideC}/{maxParticles} | " +
                      $"Fill={fillRatio:F3} | " +
                      $"LiqH={cachedLiquidHeight:F3}");

        cpuDataReady = true;
    }

    public void RefillBucket()
    {
        statesBuffer.GetData(statesCPU);
        for (int i = 0; i < maxParticles; i++)
            if (statesCPU[i] == FALLING) statesCPU[i] = INSIDE;
        statesBuffer.SetData(statesCPU);
    }

    public void ClearCanvas()
    {
        var bg = new Color(0.95f, 0.92f, 0.85f, 1f);
        for (int i = 0; i < canvasPx.Length; i++) canvasPx[i] = bg;
        canvasTex.SetPixels(canvasPx);
        canvasTex.Apply();
        BreakTrail();
    }

    public void SaveCanvas(string path = "PaintResult.png")
        => System.IO.File.WriteAllBytes(path, canvasTex.EncodeToPNG());

    // ════════════════════════════════════════════════════════════════
    //  Init Helpers
    // ════════════════════════════════════════════════════════════════
    void InitCanvas()
    {
        canvasTex = new Texture2D(canvasWidth, canvasHeight, TextureFormat.RGBA32, false);
        canvasPx = new Color[canvasWidth * canvasHeight];
        ClearCanvas();
        if (canvasRenderer) canvasRenderer.material.mainTexture = canvasTex;

        if (canvasRenderer != null)
        {
            MeshFilter mf = canvasRenderer.GetComponent<MeshFilter>();
            if (mf?.sharedMesh != null)
            {
                canvasHalfX = mf.sharedMesh.bounds.extents.x;
                canvasHalfZ = mf.sharedMesh.bounds.extents.z;
            }
        }
    }

    void InitVisualPS()
    {
        var go = new GameObject("PBFVisualPS");
        go.transform.SetParent(transform);
        visualPS = go.AddComponent<ParticleSystem>();

        var main = visualPS.main;
        main.loop = false;
        main.playOnAwake = false;
        main.maxParticles = Mathf.Min(maxParticles, 50000) + 100; // PS لها حد
        main.startLifetime = float.MaxValue;
        main.startSize = visualParticleSize;
        main.startColor = paintColor;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var em = visualPS.emission; em.enabled = false;
        var shapeModule = visualPS.shape; shapeModule.enabled = false;

        var ren = visualPS.GetComponent<ParticleSystemRenderer>();
        ren.renderMode = ParticleSystemRenderMode.Billboard;
        Shader paintShader = Shader.Find("Particles/Standard Unlit")
                          ?? Shader.Find("Sprites/Default")
                          ?? Shader.Find("Unlit/Color");
        ren.material = new Material(paintShader) { color = paintColor };

        visualParticles = new ParticleSystem.Particle[Mathf.Min(maxParticles, 50000) + 100];
        visualPS.Play();
    }

    void UpdateVisualPS()
    {
        if (visualPS == null || !cpuDataReady) return;

        // نستخدم الـ cached CPU data (تتحدث كل 5 frames في CPUReadback)
        // بدل GetData كل فريم — يوفر bandwidth كبير
        Vector3 center = BucketCenter;
        Vector3 up = BucketUp;
        float sz = h * 1.6f;
        int vIdx = 0;
        int maxVis = visualParticles.Length - 1;

        for (int i = 0; i < maxParticles && vIdx < maxVis; i++)
        {
            if (statesCPU[i] == ON_CANVAS) continue;

            Color c;
            float pSz;
            Vector3 pos = positionsCPU[i];

            if (statesCPU[i] == INSIDE)
            {
                float depth = Vector3.Dot(pos - center, up);
                float t = Mathf.InverseLerp(-bucketWorldHeight * 0.5f, 0f, depth);
                c = Color.Lerp(paintColorDark, paintColor, t);
                pSz = sz * Random.Range(0.9f, 1.1f);
            }
            else
            {
                c = paintColor; c.a = 0.85f;
                pSz = sz * 0.7f;
            }

            visualParticles[vIdx].position = pos;
            visualParticles[vIdx].startColor = c;
            visualParticles[vIdx].startSize = pSz;
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
    //  Paint Type Settings
    // ════════════════════════════════════════════════════════════════
    void ApplyPaintType()
    {
        switch (paintType)
        {
            case PaintType.Watercolor:
                restDensity = 3f; SIGMA = 0.04f; gravityScale = 0.9f; bucketInfluence = 0.6f; break;
            case PaintType.Acrylic:
                restDensity = 4.5f; SIGMA = 0.3f; gravityScale = 0.6f; bucketInfluence = 0.3f; break;
            case PaintType.OilPaint:
                restDensity = 6.5f; SIGMA = 0.85f; gravityScale = 0.4f; bucketInfluence = 0.12f; break;
            case PaintType.Tempera:
                restDensity = 3.8f; SIGMA = 0.15f; gravityScale = 0.75f; bucketInfluence = 0.45f; break;
            case PaintType.Gouache:
                restDensity = 5.5f; SIGMA = 0.55f; gravityScale = 0.5f; bucketInfluence = 0.2f; break;
            case PaintType.Latex:
                restDensity = 7.5f; SIGMA = 1.1f; gravityScale = 0.35f; bucketInfluence = 0.1f; break;
            case PaintType.Enamel:
                restDensity = 7f; SIGMA = 1.0f; gravityScale = 0.38f; bucketInfluence = 0.11f; break;
            case PaintType.Ink:
                restDensity = 2.5f; SIGMA = 0.02f; gravityScale = 0.95f; bucketInfluence = 0.7f; break;
        }
    }

    float GetSpreadMult()
    {
        switch (paintType)
        {
            case PaintType.Ink: return 3.0f;
            case PaintType.Watercolor: return 2.5f;
            case PaintType.Tempera: return 1.5f;
            case PaintType.Acrylic: return 1.0f;
            case PaintType.Gouache: return 0.8f;
            case PaintType.OilPaint: return 0.6f;
            case PaintType.Enamel: return 0.5f;
            case PaintType.Latex: return 0.4f;
            default: return 1.0f;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Debug
    // ════════════════════════════════════════════════════════════════
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
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(HolePosition, 0.025f);
    }

    GUIStyle hudStyle; Texture2D hudBg;
    void OnGUI()
    {
        if (hudStyle == null)
        {
            hudBg = new Texture2D(1, 1);
            hudBg.SetPixel(0, 0, new Color(0, 0, 0, 0.6f)); hudBg.Apply();
            hudStyle = new GUIStyle { fontSize = 11 };
            hudStyle.padding = new RectOffset(4, 4, 2, 2);
            hudStyle.normal.textColor = Color.white;
        }
        int x = 360, y = 10, lh = 18;
        GUI.DrawTexture(new Rect(x - 4, y - 3, 230, lh * 6 + 6), hudBg);
        float tilt = pendulum != null ? pendulum.theta * Mathf.Rad2Deg : 0f;
        GUI.Label(new Rect(x, y, 230, lh), "Solver  : GPU PBF", hudStyle);
        GUI.Label(new Rect(x, y + lh, 230, lh), "Particles: " + maxParticles, hudStyle);
        GUI.Label(new Rect(x, y + lh * 2, 230, lh), "Iters   : " + solverIterations, hudStyle);
        GUI.Label(new Rect(x, y + lh * 3, 230, lh), "Type    : " + paintType, hudStyle);
        GUI.Label(new Rect(x, y + lh * 4, 230, lh), "Tilt    : " + tilt.ToString("F1") + "°", hudStyle);
        GUI.Label(new Rect(x, y + lh * 5, 230, lh), "Splash  : ON | GPU", hudStyle);
    }
}