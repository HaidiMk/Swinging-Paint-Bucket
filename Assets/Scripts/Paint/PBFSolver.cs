using UnityEngine;
using System.Runtime.InteropServices;
using System.Collections.Generic;

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

    [Header("Surface — نوع سطح اللوحة")]
    [Tooltip("نوع سطح الرسم: يغيّر شكل البقعة والانتشار والمزج")]
    public SurfaceType surfaceType = SurfaceType.Cloth;
    public enum SurfaceType
    { Paper, Cloth, Wood, Metal }   // ورق، قماش، خشب، معدن

    [Tooltip("تفعيل تأثير السطح بصرياً على أثر الطلاء، بدون فيزياء جاهزة")]
    public bool enableSurfaceEffects = true;

    [Tooltip("قوة ظهور فرق السطح على اللوحة")]
    [Range(0f, 1f)] public float surfaceEffectStrength = 1.0f;

    // ══════════════════════════════════════════════════════════════
    [Header("PBF Settings — إعدادات المحاكاة")]
    [Tooltip("عدد الجزيئات — RTX 2050 يدعم 50K-200K بسهولة")]
    [Range(1000, 200000)]
    public int maxParticles = 2500;

    [Tooltip("نصف قطر التأثير بين الجزيئات")]
    [Range(0.05f, 0.3f)]
    public float h = 0.06f;

    [Tooltip("احسب h تلقائيًا حسب عدد الجزيئات وحجم الدلو (بدل ما تظبطها يدوي كل مرة)")]
    public bool autoComputeH = true;
    [Tooltip("عدد الجيران المستهدف لكل جزيء لما autoComputeH مفعّل")]
    public int targetNeighborCount = 42;

    [Tooltip("الكثافة الطبيعية للسائل — أكبر = أكثف")]
    [Range(1f, 20f)]
    public float restDensity = 6f;

    [Tooltip("عدد iterations لحل الـ constraints — أكثر = أدق وأبطأ")]
    [Range(1, 10)]
    public int solverIterations = 5;

    [Tooltip("ε لتجنب القسمة على صفر في معادلة λ")]
    public float epsilon = 600f;

    public float G = 9.81f;

    // ══════════════════════════════════════════════════════════════
    [Header("Bucket Size — حجم الدلو")]
    public float bucketWorldRadius = 0.25f;
    public float bucketWorldHeight = 0.4f;
    public Vector3 bucketCenterOffset = new Vector3(0f, 0f, -0.15f);

    // ══════════════════════════════════════════════════════════════
    [Header("Canvas — اللوحة")]
    public int canvasWidth = 384;
    public int canvasHeight = 384;
    public bool canvasIsHorizontal = true;
    public int baseBrushSize = 5;
    [Range(0.1f, 2f)] public float speedToSizeMultiplier = 0.3f;
    [Range(0, 20)] public int splashDroplets = 0;
    [Range(5, 80)] public int splashMaxRadius = 8;
    [Range(1, 20)] public int readbackInterval = 6; // أخف: نكشف الاصطدام كل عدة frames
    [Range(0.1f, 0.8f)] public float dropletSizeRatio = 0.3f;

    [Header("Impact mark — أثر السقوط")]
    public bool sprayEnabled = false;         // افتراضياً مطفأ للأداء والواقعية
    [Range(1, 12)] public int exactDotSize = 3; // حجم نقطة محل السقوط
    public bool flipMarkU = false;            // إذا الأثر معكوس أفقيًا عن الجزيئة
    public bool flipMarkV = false;            // إذا الأثر معكوس عموديًا عن الجزيئة

    // ══════════════════════════════════════════════════════════════
    [Header("Visual — بصري")]
    public Color paintColor = new Color(0.9f, 0.1f, 0.1f, 1f);
    public Color paintColorDark = new Color(0.5f, 0.05f, 0.05f, 1f);
    public float visualParticleSize = 0.012f;

    [Header("Layered Paint Colors — ألوان طبقات داخل الدلو")]
    public bool enableLayeredPaintColors = true;
    public Color[] layerPaintColors = new Color[4]
    {
        new Color(0.95f, 0.05f, 0.04f, 1f), // Red
        new Color(0.05f, 0.20f, 1.00f, 1f), // Blue
        new Color(1.00f, 0.82f, 0.04f, 1f), // Yellow
        new Color(0.05f, 0.65f, 0.16f, 1f)  // Green
    };

    [Tooltip("كم جزيئة تخرج قبل الانتقال للطبقة التالية. اللون لا يتبدل بالوقت.")]
    [Range(25, 5000)] public int particlesPerLayer = 500;

    [Tooltip("إظهار الجزيئات داخل الدلو بلون الطبقة الحالية فقط، بدون قوس قزح.")]
    public bool simpleLayerVisuals = true;

    [Header("Light Canvas Mixing — مزج خفيف على اللوحة")]
    public bool enableLightCanvasMixing = true;
    [Range(0f, 1f)] public float canvasMixStrength = 0.82f;
    [Range(0f, 1f)] public float paintDepositStrength = 0.72f;
    [Range(1, 10)] public int canvasApplyEveryNFrames = 4;
    [Range(1, 200)] public int maxImpactsPerReadback = 30;
    public bool verboseDebugLogs = false;

    [Header("Particle Sloshing Inside Bucket — حركة الجزيئات داخل الدلو")]
    [Tooltip("يحرك الجزيئات نفسها داخل الدلو عند تبدل اتجاه الحركة، بدون إضافة سطح مرئي وهمي.")]
    public bool enableParticleSloshing = true;

    [Tooltip("قوة ميلان/اندفاع السائل داخل الدلو عند أقصى اليمين واليسار.")]
    [Range(0f, 1.5f)] public float particleSloshStrength = 1.15f;

    [Tooltip("سرعة استجابة السائل لتسارع الدلو. قيمة أعلى = يميل أسرع.")]
    [Range(1f, 18f)] public float particleSloshResponse = 11.5f;

    [Tooltip("تخميد التموّج. قيمة أعلى = اهتزاز أقل.")]
    [Range(0.5f, 12f)] public float particleSloshDamping = 3.6f;

    [Tooltip("قوة الدفع الجانبي للجزيئات قرب سطح السائل.")]
    [Range(0f, 12f)] public float particleSloshForce = 5.8f;

    [Tooltip("قوة رفع/خفض الجزيئات قرب الأطراف حتى يظهر السائل مائلاً فعلياً لا ككتلة مسطحة.")]
    [Range(0f, 12f)] public float particleSloshLiftForce = 6.5f;

    [Tooltip("تعزيز التموّج لحظة تغير الاتجاه عند أقصى اليمين/اليسار.")]
    [Range(0f, 1.5f)] public float particleSloshTurnBoost = 0.55f;

    [Tooltip("كم يسمح لمستوى الجزيئات أن يرتفع من جهة وينخفض من الجهة الأخرى.")]
    [Range(0f, 0.45f)] public float particleSloshSurfaceSlope = 0.32f;

    [Tooltip("يعزز تأثير التموّج على الجزيئات القريبة من سطح السائل أكثر من الجزيئات السفلية.")]
    [Range(0f, 1f)] public float particleSloshSurfaceBias = 0.88f;

    [Tooltip("إظهار سهم فقط لاتجاه اندفاع الجزيئات. لا يضيف سطح سائل مرئي.")]
    public bool showParticleSloshGizmo = true;

    [Header("Debug")]
    public bool showDebugGizmos = true;

    // ══════════════════════════════════════════════════════════════
    //  Paint Parameters
    // ══════════════════════════════════════════════════════════════
    [HideInInspector] public float gravityScale = 0.6f;
    [HideInInspector] public float bucketInfluence = 0.3f;
    [HideInInspector] public float SIGMA = 0.3f;
    [HideInInspector] public float viscosity = 0.3f; // تُضبط حسب نوع الدهان

    [Header("Particle Cohesion — تماسك الجزيئات")]
    [Tooltip("يجعل الجزيئات القريبة تنجذب لبعضها بشكل خفيف حتى يظهر الطلاء ككتلة سائلة لا كحبيبات منفصلة.")]
    public bool enableParticleCohesion = true;

    [Tooltip("قوة التماسك بين الجزيئات. ارفعها إذا كان السائل مفككاً، وخففها إذا صار مثل الجل.")]
    [Range(0f, 8f)] public float particleCohesionStrength = 2.2f;

    [Tooltip("مسافة تأثير التماسك بالنسبة إلى h. قيمة أكبر = تماسك أوسع لكن أبطأ قليلاً.")]
    [Range(0.35f, 1.3f)] public float particleCohesionRadius = 0.85f;

    [Tooltip("يمنع التكتل الزائد عندما تكون الجزيئات قريبة جداً.")]
    [Range(0f, 2f)] public float particleCohesionRepulsion = 0.45f;

    [Tooltip("تخميد إضافي بسيط يمنع اهتزاز كتلة السائل بعد إضافة التماسك.")]
    [Range(0f, 4f)] public float particleCohesionDamping = 0.55f;

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
    int kApplyViscosity;
    int kEnforceBoundary;
    int kFallingStep;
    int kApplyCohesion;
    int kClampPredicted;

    // ══════════════════════════════════════════════════════════════
    //  Grid
    // ══════════════════════════════════════════════════════════════
    int gridSizeX, gridSizeY, gridSizeZ;
    Vector3 gridOrigin;
    float cellSize;
    const int MAX_PER_CELL = 128;   // مهم جداً فوق 15K: يمنع overflow داخل خلايا الـ grid
    const int MAX_NEIGHBORS = 128;  // فوق 15K الجزيئة قد ترى أكثر من 64 جار
    int totalCells;

    // ══════════════════════════════════════════════════════════════
    //  CPU-side data — cached copy تتحدث كل 5 frames
    // ══════════════════════════════════════════════════════════════
    Vector3[] positionsCPU;
    int[] statesCPU;
    Vector3[] velocitiesCPU;
    int[] colorLayerCPU;

    // Single-element upload arrays: بدل رفع كل المصفوفة للـ GPU عند كل قطرة
    readonly int[] oneIntUpload = new int[1];
    readonly Vector3[] oneVectorUpload = new Vector3[1];

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
    public string saveFolder = @"C:\Users\Haidi\VR_Project"; // مسار حفظ الصورة والتقرير
    int paintedSplats = 0;     // عدد المسارات (بقع نزلت ع اللوحة)
    float motionElapsed = 0f;  // زمن الحركة
    float canvasHalfX = 0.5f;
    float canvasHalfZ = 0.5f;

    // Trail
    Vector3 lastTrailPoint;
    bool hasLastTrailPoint = false;

    // Dynamics
    Vector3 prevBucketVel = Vector3.zero;
    Vector3 bucketAccelWorld = Vector3.zero;

    // Particle sloshing state: اتجاه وكمية ميلان الجزيئات داخل الدلو
    Vector3 particleSloshVector = Vector3.zero;
    Vector3 particleSloshVelocity = Vector3.zero;

    // Visual PS
    ParticleSystem visualPS;
    ParticleSystem.Particle[] visualParticles;
    bool visualDirty = true;
    bool initialized = false;

    int frameCount = 0;
    int releasedPaintParticles = 0;
    int lastCanvasApplyFrame = 0;

    // ══════════════════════════════════════════════════════════════
    //  Bucket Helpers
    // ══════════════════════════════════════════════════════════════
    public Vector3 BucketCenter => bucketTransform.position
                                 + bucketTransform.TransformDirection(bucketCenterOffset);
    Vector3 BucketUp => bucketTransform.TransformDirection(-Vector3.forward).normalized;
    Vector3 BucketRight => bucketTransform.TransformDirection(Vector3.right).normalized;
    Vector3 BucketForward => bucketTransform.TransformDirection(Vector3.up).normalized;

    public Vector3 HolePosition => BucketCenter - BucketUp * (bucketWorldHeight * 0.5f);
    public Vector3 TopPosition => BucketCenter + BucketUp * (bucketWorldHeight * 0.5f);

    // ── Accessors for the GPU instanced renderer ──
    public ComputeBuffer PositionsBuffer => positionsBuffer;
    public ComputeBuffer StatesBuffer => statesBuffer;
    public int ParticleCount => maxParticles;
    public Vector3 BucketUpDir => BucketUp;

    // ════════════════════════════════════════════════════════════════
    // بيحسب h تلقائيًا حتى عدد الجيران المتوقع لكل جزيء يضل قريب من targetNeighborCount
    // مهما تغيّر عدد الجزيئات (maxParticles) — بدل ما نحسبها يدوي كل مرة.
    // القانون: عدد الجيران ≈ (N/V) × (4/3 × π × h³)  →  نعكسها لنحل h
    void AutoComputeH()
    {
        if (!autoComputeH) return;

        float rMax = bucketWorldRadius * 0.9f;      // نفس نطاق نصف القطر بـ SpawnParticles
        float heightSpan = bucketWorldHeight * 0.6f; // نفس نطاق الارتفاع بـ SpawnParticles (0.45+0.15)
        float volume = Mathf.PI * rMax * rMax * heightSpan;
        float density = maxParticles / Mathf.Max(volume, 0.0001f);

        float sphereVolumeNeeded = targetNeighborCount / Mathf.Max(density, 0.0001f);
        h = Mathf.Pow(sphereVolumeNeeded / (4f / 3f * Mathf.PI), 1f / 3f);

        Debug.Log($"[PBFSolver] h تلقائي = {h:F4} (جيران مستهدفة={targetNeighborCount}, كثافة={density:F0} جزيء/م3)");
    }

    void Start()
    {
        if (pbfComputeShader == null)
        {
            Debug.LogError("[PBF] Compute Shader غير مربوط! اربط PBFSolver.compute بالـ Inspector.");
            return;
        }

        AutoComputeH();   // لازم قبل InitGrid لأنو الشبكة بتعتمد على h
        ApplyPaintType();
        InitGrid();
        InitGPUBuffers();
        InitCanvas();
        // InitVisualPS();   // ← off: replaced by GPU instanced renderer (PaintParticleRenderer)
        SpawnParticles();
        CalibrateRestDensityScale();   // يصحّح restDensity حسب h والكثافة الفعلية (يحافظ على نسب أنواع الطلاء)

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
        kApplyViscosity = pbfComputeShader.FindKernel("ApplyViscosity");
        kEnforceBoundary = pbfComputeShader.FindKernel("EnforceBoundary");
        kFallingStep = pbfComputeShader.FindKernel("FallingStep");
        kApplyCohesion = pbfComputeShader.FindKernel("ApplyCohesion");
        kClampPredicted = pbfComputeShader.FindKernel("ClampPredicted");

        // اربط الـ Buffers بكل الـ kernels
        BindAllBuffers();

        // احجز CPU arrays للقراءة عند الحاجة
        positionsCPU = new Vector3[n];
        statesCPU = new int[n];
        velocitiesCPU = new Vector3[n];
        colorLayerCPU = new int[n];

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
            kUpdateVelocity, kEnforceBoundary, kFallingStep,
            kApplyViscosity,
            kApplyCohesion, kClampPredicted
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
    // ── يصحّح restDensity حسب الكثافة الحقيقية (Poly6) لتوزيع الجزيئات الفعلي و h الحالي ──
    // بدل ما نستبدل القيمة اليدوية (يلي فيها فرق بين أنواع الطلاء)، منحسب "نسبة تصحيح"
    // ونضربها بالقيمة الحالية — هيك منحافظ على الفرق بين watercolor/oil/... الخ.
    void CalibrateRestDensityScale()
    {
        float poly6C = 315f / (64f * Mathf.PI * Mathf.Pow(h, 9));
        float hSq = h * h;

        // شبكة بسيطة على الـ CPU (بدل مقارنة كل جزيء بكل الجزيئات — كانت بطيئة كتير
        // وبتعلّق Unity مع عدد كبير من الجزيئات، لأنها O(n^2))
        var cellMap = new Dictionary<Vector3Int, List<int>>();
        Vector3Int CellOf(Vector3 p) => new Vector3Int(
            Mathf.FloorToInt(p.x / h), Mathf.FloorToInt(p.y / h), Mathf.FloorToInt(p.z / h));

        for (int i = 0; i < maxParticles; i++)
        {
            var c = CellOf(positionsCPU[i]);
            if (!cellMap.TryGetValue(c, out var list))
            {
                list = new List<int>();
                cellMap[c] = list;
            }
            list.Add(i);
        }

        float sum = 0f;
        for (int i = 0; i < maxParticles; i++)
        {
            float rho = 0f;
            Vector3Int c = CellOf(positionsCPU[i]);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        var nc = new Vector3Int(c.x + dx, c.y + dy, c.z + dz);
                        if (!cellMap.TryGetValue(nc, out var neighbors)) continue;
                        foreach (int j in neighbors)
                        {
                            float r2 = (positionsCPU[i] - positionsCPU[j]).sqrMagnitude;
                            if (r2 < hSq)
                                rho += poly6C * Mathf.Pow(hSq - r2, 3);
                        }
                    }
            sum += rho;
        }
        float measuredDensity = sum / maxParticles;
        float scale = measuredDensity / Mathf.Max(0.01f, restDensity);
        restDensity *= scale;
        Debug.Log($"[PBFSolver] restDensity بعد التصحيح = {restDensity:F2} (نسبة التصحيح ×{scale:F2})");
    }

    void SpawnParticles()
    {
        Vector3[] initPos = new Vector3[maxParticles];
        Vector3[] initVel = new Vector3[maxParticles];
        int[] initSt = new int[maxParticles];
        int[] initLayer = new int[maxParticles];

        Vector3 center = BucketCenter;
        Vector3 up = BucketUp, right = BucketRight, fwd = BucketForward;

        // توزيع FluidBox-style: شبكة طبقات منتظمة داخل أسطوانة الدلو بدل Random.
        // السبب: فوق 15K أي تكتل عشوائي بسيط يعمل ضغط زائد وخلايا grid مزدحمة، فيظهر الانفجار.
        float radius = bucketWorldRadius * 0.86f;
        float bottom = -bucketWorldHeight * 0.45f + h * 0.55f;
        float top = bucketWorldHeight * 0.15f - h * 0.35f;
        float height = Mathf.Max(top - bottom, h * 2f);

        float volume = Mathf.PI * radius * radius * height;
        float spacing = Mathf.Pow(volume / Mathf.Max(1, maxParticles), 1f / 3f) * 0.92f;
        spacing = Mathf.Clamp(spacing, h * 0.42f, h * 0.78f);

        int count = 0;

        // نحاول نملأ بشكل منتظم. إذا لم يكفِ العدد نقلل المسافة قليلاً ونعيد.
        for (int attempt = 0; attempt < 6 && count < maxParticles; attempt++)
        {
            count = 0;
            int layer = 0;
            float s = spacing * Mathf.Pow(0.92f, attempt);
            float rowH = s * 0.8660254f;       // hex spacing in XZ
            float layerH = s * 0.92f;

            for (float y = bottom; y <= top && count < maxParticles; y += layerH, layer++)
            {
                int zi = 0;
                for (float z = -radius; z <= radius && count < maxParticles; z += rowH, zi++)
                {
                    float zOff = ((zi + layer) & 1) == 0 ? 0f : s * 0.5f;
                    for (float x = -radius + zOff; x <= radius && count < maxParticles; x += s)
                    {
                        if (x * x + z * z > radius * radius) continue;

                        // jitter صغير جداً حتى لا تظهر الجزيئات كشبكة صلبة، لكنه لا يسبب تداخل عشوائي.
                        float jx = Random.Range(-0.035f, 0.035f) * s;
                        float jy = Random.Range(-0.020f, 0.020f) * s;
                        float jz = Random.Range(-0.035f, 0.035f) * s;

                        float lx = x + jx;
                        float ly = y + jy;
                        float lz = z + jz;

                        initPos[count] = center + right * lx + fwd * lz + up * ly;
                        initVel[count] = Vector3.zero;
                        initSt[count] = INSIDE;
                        initLayer[count] = InitialLayerForParticle(count, ly);
                        count++;
                    }
                }
            }
        }

        // fallback فقط إذا كانت الإعدادات ضيقة جداً. نضيف نقاطاً منتظمة شبه عشوائية بدون تكديس بالمركز.
        int guard = 0;
        while (count < maxParticles && guard++ < maxParticles * 4)
        {
            float a = Random.Range(0f, Mathf.PI * 2f);
            float r = radius * Mathf.Sqrt(Random.Range(0f, 1f));
            float y = Random.Range(bottom, top);
            initPos[count] = center + right * (Mathf.Cos(a) * r) + fwd * (Mathf.Sin(a) * r) + up * y;
            initVel[count] = Vector3.zero;
            initSt[count] = INSIDE;
            initLayer[count] = InitialLayerForParticle(count, y);
            count++;
        }

        positionsCPU = initPos;
        velocitiesCPU = initVel;
        statesCPU = initSt;
        colorLayerCPU = initLayer;
        releasedPaintParticles = 0;

        positionsBuffer.SetData(initPos);
        velocitiesBuffer.SetData(initVel);
        statesBuffer.SetData(initSt);

        Debug.Log($"[PBF] Spawned {maxParticles} particles with stable lattice spacing={spacing:F4} → GPU");
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

        UpdateParticleSloshing(dt);

        // تحديث Constants في الـ Compute Shader
        SetShaderConstants(dt);

        // ── GPU Pipeline ─────────────────────────────────────────

        // Pass 0: توقع المواقع الجديدة
        Dispatch(kPredictPositions);
        Dispatch(kClampPredicted);   // قصّ فوري بعد التوقع، قبل بناء الـ grid

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
            Dispatch(kClampPredicted);   // يطبّق التصحيح على predictedPos ويقصّه كل iteration
        }

        // Pass 4: تحديث السرعة والموقع
        Dispatch(kUpdateVelocity);

        // Pass 4ب: اللزوجة (تماسك داخل الدلو)
        if (viscosity > 0f)
            Dispatch(kApplyViscosity);

        // Pass 4ج: تماسك سطحي بين الجزيئات حتى لا يظهر السائل كحبيبات منفصلة
        if (enableParticleCohesion && particleCohesionStrength > 0f)
            Dispatch(kApplyCohesion);

        // Pass 5: حدود الدلو
        Dispatch(kEnforceBoundary);

        // Pass 6: الجزيئات الساقطة
        Dispatch(kFallingStep);

        // ── CPU Readback ─────────────────────────────────────────
        // كل فريم: نقرأ positions+states للعرض البصري
        // كل 5 frames: نضيف velocities + canvas impact + cached values
        if (frameCount % readbackInterval == 0)
            CPUReadback();
        else if (!cpuDataReady)
            CPUReadback(); // أول مرة نشتغل فيها — نجبر readback فوري

        visualDirty = true;
    }

    // ════════════════════════════════════════════════════════════════
    //  UpdateParticleSloshing — يميل الجزيئات نفسها داخل الدلو
    // ════════════════════════════════════════════════════════════════
    void UpdateParticleSloshing(float dt)
    {
        if (!enableParticleSloshing || bucketTransform == null)
        {
            particleSloshVector = Vector3.Lerp(particleSloshVector, Vector3.zero, Mathf.Clamp01(dt * 8f));
            particleSloshVelocity = Vector3.Lerp(particleSloshVelocity, Vector3.zero, Mathf.Clamp01(dt * 8f));
            return;
        }

        Vector3 up = BucketUp;

        // السائل لا يتبع الدلو فوراً: ندمج بين القوة الوهمية الناتجة عن التسارع
        // وبين lag بسيط عكس سرعة الدلو، فيظهر التموّج خصوصاً عند أقصى اليمين/اليسار.
        Vector3 lateralPseudo = Vector3.ProjectOnPlane(-bucketAccelWorld, up);
        Vector3 bucketVel = pendulum != null ? pendulum.GetBucketVelocity() : Vector3.zero;
        Vector3 lateralVel = Vector3.ProjectOnPlane(bucketVel, up);

        Vector3 sloshDrive = lateralPseudo;
        if (lateralVel.sqrMagnitude > 0.0001f)
            sloshDrive += -lateralVel * 0.35f; // تأخر السائل عن حركة الدلو

        float accelMag = lateralPseudo.magnitude;
        float velMag = lateralVel.magnitude;
        float turnBoost = Mathf.InverseLerp(0.85f, 0.05f, velMag) * Mathf.InverseLerp(1.0f, 8.0f, accelMag) * particleSloshTurnBoost;
        float mag = sloshDrive.magnitude;

        Vector3 desired = Vector3.zero;
        if (mag > 0.03f)
        {
            float amount = Mathf.InverseLerp(0.18f, 7.0f, mag) * particleSloshStrength;
            amount = Mathf.Clamp01(amount + turnBoost);
            desired = sloshDrive.normalized * amount;
        }

        // نموذج نابضي: يعطي قصور ذاتي وovershoot بدل حركة جامدة.
        Vector3 error = desired - particleSloshVector;
        particleSloshVelocity += error * particleSloshResponse * dt;
        particleSloshVelocity *= Mathf.Exp(-particleSloshDamping * dt);
        particleSloshVector += particleSloshVelocity * dt;

        if (particleSloshVector.magnitude > 1f)
            particleSloshVector = particleSloshVector.normalized;
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
        pbfComputeShader.SetFloat("viscosity", viscosity);
        pbfComputeShader.SetInt("enableParticleCohesion", enableParticleCohesion ? 1 : 0);
        pbfComputeShader.SetFloat("particleCohesionStrength", particleCohesionStrength);
        pbfComputeShader.SetFloat("particleCohesionRadius", particleCohesionRadius);
        pbfComputeShader.SetFloat("particleCohesionRepulsion", particleCohesionRepulsion);
        pbfComputeShader.SetFloat("particleCohesionDamping", particleCohesionDamping);
        pbfComputeShader.SetVector("bucketAccel", bucketAccelWorld);
        pbfComputeShader.SetInt("enableParticleSloshing", enableParticleSloshing ? 1 : 0);
        pbfComputeShader.SetVector("particleSloshVector", particleSloshVector);
        pbfComputeShader.SetFloat("particleSloshForce", particleSloshForce);
        pbfComputeShader.SetFloat("particleSloshLiftForce", particleSloshLiftForce);
        pbfComputeShader.SetFloat("particleSloshSurfaceSlope", particleSloshSurfaceSlope);
        pbfComputeShader.SetFloat("particleSloshSurfaceBias", particleSloshSurfaceBias);
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
        int impactsThisReadback = 0;

        for (int i = 0; i < maxParticles; i++)
        {
            if (statesCPU[i] != FALLING) continue;

            Vector3 wp = positionsCPU[i];
            Vector3 lp = canvasTransform.InverseTransformPoint(wp);

            // وصلت للسطح أو عبرتو؟ (بدون نافذة رفيعة → ما في تهرّب)
            bool reached = canvasIsHorizontal ? (lp.y <= 0.08f) : (Mathf.Abs(lp.z) <= 0.08f);
            if (!reached) continue;

            bool inBounds = Mathf.Abs(lp.x) < canvasHalfX && Mathf.Abs(lp.z) < canvasHalfZ;
            if (inBounds)
                DrawSplash(ProjectOntoCanvas(wp), velocitiesCPU[i], i);

            statesCPU[i] = ON_CANVAS;
            anyChange = true;
            impactsThisReadback++;

            // حد أعلى للرسم بكل readback حتى ما يعلق الجهاز
            if (impactsThisReadback >= maxImpactsPerReadback) break;
        }

        if (anyChange)
            statesBuffer.SetData(statesCPU);

        visualDirty = true; // نحدّث الـ visual بعد كل readback

        // Debug: تحقق من توزيع حالات الجزيئات كل 60 فريم
        if (verboseDebugLogs && frameCount % 120 == 0)
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
        if (statesCPU == null || positionsCPU == null || velocitiesCPU == null) return;

        Vector3 exit = isFromTop ? TopPosition : HolePosition;
        int bestIdx = -1;
        float bestDist = float.MaxValue;

        // اختيار خفيف من النسخة CPU الموجودة عندنا، بدون GetData من GPU
        for (int attempt = 0; attempt < 90; attempt++)
        {
            int i = Random.Range(0, maxParticles);
            if (statesCPU[i] != INSIDE) continue;

            float d = (positionsCPU[i] - exit).sqrMagnitude;
            if (d < bestDist)
            {
                bestDist = d;
                bestIdx = i;
            }
        }

        // fallback سريع في حال العشوائي ما لقى جزيئة
        if (bestIdx < 0)
        {
            int scanLimit = Mathf.Min(maxParticles, 400);
            for (int i = 0; i < scanLimit; i++)
            {
                if (statesCPU[i] == INSIDE)
                {
                    bestIdx = i;
                    break;
                }
            }
        }

        if (bestIdx < 0) return;

        int layer = CurrentLayerIndex();
        colorLayerCPU[bestIdx] = layer;
        statesCPU[bestIdx] = FALLING;
        positionsCPU[bestIdx] = exit + Random.insideUnitSphere * (bucketWorldRadius * 0.10f);

        Vector3 bucketVel = pendulum != null ? pendulum.GetBucketVelocity() : Vector3.zero;
        Vector3 exitDir = isFromTop ? BucketUp : -BucketUp;
        float exitSpeed = Mathf.Lerp(1.3f, 0.25f, SIGMA / 1.2f);
        velocitiesCPU[bestIdx] = bucketVel + exitDir * exitSpeed + Random.insideUnitSphere * 0.04f;

        // رفع عنصر واحد فقط للـ GPU بدل المصفوفات كلها — أخف بكثير
        oneIntUpload[0] = statesCPU[bestIdx];
        statesBuffer.SetData(oneIntUpload, 0, bestIdx, 1);

        oneVectorUpload[0] = positionsCPU[bestIdx];
        positionsBuffer.SetData(oneVectorUpload, 0, bestIdx, 1);

        oneVectorUpload[0] = velocitiesCPU[bestIdx];
        velocitiesBuffer.SetData(oneVectorUpload, 0, bestIdx, 1);

        releasedPaintParticles++;

        if (verboseDebugLogs && frameCount % 120 == 0)
            Debug.Log($"[Layer Paint] Released={releasedPaintParticles}, Layer={layer + 1}");
    }

    // ════════════════════════════════════════════════════════════════
    //  DrawSplash — رذاذ حقيقي على اللوحة
    // ════════════════════════════════════════════════════════════════
    void DrawSplash(Vector3 worldPos, Vector3 worldVel, int particleIndex)
    {
        if (canvasTransform == null) return;
        paintedSplats++;

        Vector3 lp = canvasTransform.InverseTransformPoint(worldPos);
        int cx = Mathf.RoundToInt(Mathf.Clamp01((lp.x + canvasHalfX) / (canvasHalfX * 2f)) * canvasWidth);
        int cy = Mathf.RoundToInt(Mathf.Clamp01((lp.z + canvasHalfZ) / (canvasHalfZ * 2f)) * canvasHeight);
        if (flipMarkU) cx = canvasWidth - 1 - cx;
        if (flipMarkV) cy = canvasHeight - 1 - cy;

        Color col = GetParticleColor(particleIndex);
        float speed = worldVel.magnitude;

        // تأثير السطح واللزوجة بشكل خفيف على الأداء
        float surfaceSpread, surfaceSoftness, surfaceAlpha;
        GetSurfacePaintResponse(out surfaceSpread, out surfaceSoftness, out surfaceAlpha);

        float viscSize = Mathf.Lerp(0.8f, 1.7f, Mathf.Clamp01(viscosity));
        int r = Mathf.Max(1, Mathf.RoundToInt(baseBrushSize * surfaceSpread * viscSize * Mathf.Clamp(0.65f + speed * 0.18f, 0.75f, 1.8f)));

        // بصمة رئيسية + اختلاف واضح حسب نوع السطح
        Color main = col;
        main.a = paintDepositStrength * surfaceAlpha;
        if (enableSurfaceEffects)
        {
            Vector3 lv = canvasTransform.InverseTransformVector(worldVel);
            Vector2 dir = new Vector2(lv.x, lv.z);
            float baseAng = dir.sqrMagnitude > 0.0001f ? Mathf.Atan2(dir.y, dir.x) : 0f;
            float forwardness = Mathf.Clamp01(speed * 0.18f);

            // cap يحافظ على الأداء، بس يستعمل البصمات القديمة الواضحة لكل سطح
            int visibleR = Mathf.Clamp(r, 2, 10);
            float dropMul = Mathf.Clamp01(surfaceEffectStrength);
            DrawSurfaceMark(cx, cy, visibleR, baseAng, forwardness, speed, main, surfaceSoftness, dropMul);
        }
        else
        {
            FillCircleSoft(cx, cy, r, main, surfaceSoftness);
        }

        // رذاذ اختياري خفيف جداً، مو عشرات البقع
        if (sprayEnabled && splashDroplets > 0)
        {
            Vector3 lv = canvasTransform.InverseTransformVector(worldVel);
            Vector2 dir = new Vector2(lv.x, lv.z);
            float a0 = dir.sqrMagnitude > 0.0001f ? Mathf.Atan2(dir.y, dir.x) : Random.Range(0f, Mathf.PI * 2f);
            int n = Mathf.Min(splashDroplets, 2);
            for (int k = 0; k < n; k++)
            {
                float a = a0 + Random.Range(-0.7f, 0.7f);
                float d = Random.Range(r * 1.0f, r * 2.2f);
                Color drop = col; drop.a = paintDepositStrength * 0.6f;
                FillCircleSoft(cx + Mathf.RoundToInt(Mathf.Cos(a) * d),
                               cy + Mathf.RoundToInt(Mathf.Sin(a) * d),
                               Mathf.Max(1, r / 2), drop, 0.65f);
            }
        }

        canvasDirty = true;
    }

    // استجابة خفيفة لكل نوع سطح: انتشار، نعومة الحافة، كمية اللون
    void GetSurfacePaintResponse(out float spread, out float softness, out float alpha)
    {
        // القيم مصممة لتبان بالعين بدون ما تثقل المحاكاة
        switch (surfaceType)
        {
            case SurfaceType.Paper: // يمتص: بقعة عريضة ناعمة وفاتحة
                spread = 1.75f; softness = 0.96f; alpha = 0.58f; break;
            case SurfaceType.Cloth: // نسيج: انتشار متوسط مع خيوط واضحة
                spread = 1.18f; softness = 0.58f; alpha = 0.88f; break;
            case SurfaceType.Wood:  // خشب: أثر ممدود باتجاه العروق
                spread = 1.05f; softness = 0.32f; alpha = 0.98f; break;
            default:                // معدن: حاد ولمّاع وقطرات صغيرة
                spread = 0.68f; softness = 0.04f; alpha = 1.00f; break;
        }

        float k = Mathf.Clamp01(surfaceEffectStrength);
        spread = Mathf.Lerp(1.0f, spread, k);
        softness = Mathf.Lerp(0.45f, softness, k);
        alpha = Mathf.Lerp(1.0f, alpha, k);
    }

    // بصمة سطح خفيفة للأداء: لا نرسم عشرات البقع، فقط تفاصيل قليلة
    void DrawLightSurfaceStamp(int cx, int cy, int r, Color col, Vector3 worldVel, float softness)
    {
        switch (surfaceType)
        {
            case SurfaceType.Paper:
                {
                    // الورق: امتصاص وهالة ناعمة باهتة حول البقعة
                    Color halo = Color.Lerp(col, Color.white, 0.35f);
                    halo.a = col.a * 0.22f;
                    FillCircleSoft(cx, cy, Mathf.Max(1, Mathf.RoundToInt(r * 1.55f)), halo, 0.95f);
                    FillCircleSoft(cx, cy, r, col, softness);
                    break;
                }

            case SurfaceType.Cloth:
                {
                    // القماش: البقعة الأساسية + خطوط نسيج قليلة جداً
                    FillCircleSoft(cx, cy, r, col, softness);
                    Color thread = Color.Lerp(col, Color.black, 0.18f);
                    thread.a = col.a * 0.22f;
                    int step = Mathf.Max(2, r / 2);
                    for (int o = -r; o <= r; o += step)
                    {
                        DrawLinePixels(cx - r, cy + o, cx + r, cy + o, thread);
                        DrawLinePixels(cx + o, cy - r, cx + o, cy + r, thread);
                    }
                    break;
                }

            case SurfaceType.Wood:
                {
                    // الخشب: تمدد أفقي مع عروق بسيطة
                    for (int k = -2; k <= 2; k++)
                    {
                        Color c2 = col;
                        c2.a *= (k == 0 ? 0.85f : 0.35f);
                        FillCircleSoft(cx + k * Mathf.Max(1, r / 2), cy, Mathf.Max(1, Mathf.RoundToInt(r * 0.75f)), c2, softness);
                    }
                    Color grain = Color.Lerp(col, Color.black, 0.30f);
                    grain.a = col.a * 0.28f;
                    DrawLinePixels(cx - r * 2, cy - Mathf.Max(1, r / 3), cx + r * 2, cy - Mathf.Max(1, r / 3), grain);
                    DrawLinePixels(cx - r * 2, cy + Mathf.Max(1, r / 3), cx + r * 2, cy + Mathf.Max(1, r / 3), grain);
                    break;
                }

            default:
                {
                    // المعدن: حافة حادة ولمعة صغيرة
                    FillCircleSoft(cx, cy, r, col, 0.08f);
                    Color hi = Color.Lerp(col, Color.white, 0.80f);
                    hi.a = col.a * 0.35f;
                    FillCircle(cx - Mathf.Max(1, r / 3), cy - Mathf.Max(1, r / 3), Mathf.Max(1, r / 4), hi);
                    break;
                }
        }
    }

    // بصمة بصرية مميّزة وواقعية لكل نوع سطح
    void DrawSurfaceMark(int cx, int cy, int mainR, float baseAng, float forwardness,
                         float speed, Color col, float soft, float dropMul)
    {
        switch (surfaceType)
        {
            // ── ورق: ألوان مائية — مركز مغسول فاتح + حلقة تغميق بالحافة + نشّ وأصابع ──
            case SurfaceType.Paper:
                {
                    Color wash = Color.Lerp(col, Color.white, 0.35f); wash.a = col.a * 0.55f; // مركز مغسول
                    Color edge = Color.Lerp(col, Color.black, 0.18f); edge.a = col.a * 0.75f; // صبغة متجمّعة بالحافة
                    Color halo = col; halo.a = col.a * 0.12f;

                    FillCircleSoft(cx, cy, Mathf.RoundToInt(mainR * 2.4f), halo, 0.98f);      // نشّ واسع
                    for (int i = 0; i < 4; i++)                                               // أصابع نشّ غير منتظمة — مخففة للأداء
                    {
                        float a = Random.Range(0f, Mathf.PI * 2f);
                        float d = mainR * Random.Range(1.1f, 2.1f);
                        FillCircleSoft(cx + Mathf.RoundToInt(Mathf.Cos(a) * d),
                                       cy + Mathf.RoundToInt(Mathf.Sin(a) * d),
                                       Mathf.Max(Mathf.RoundToInt(mainR * Random.Range(0.25f, 0.55f)), 1), halo, 0.97f);
                    }
                    FillCircleSoft(cx, cy, Mathf.RoundToInt(mainR * 1.3f), edge, 0.7f);       // طبقة الحافة الغامقة
                    FillCircleSoft(cx, cy, Mathf.RoundToInt(mainR * 1.0f), wash, 0.85f);      // المركز الفاتح فوقها → رِم غامق
                    for (int i = 0; i < 4; i++)                                               // تحبّب الصبغة — مخفف
                    {
                        float a = Random.Range(0f, Mathf.PI * 2f);
                        float d = mainR * Random.Range(0f, 1.1f);
                        FillCircle(cx + Mathf.RoundToInt(Mathf.Cos(a) * d),
                                   cy + Mathf.RoundToInt(Mathf.Sin(a) * d), 1, edge);
                    }
                    break;
                }

            // ── قماش: نسيج خيوط متعامد (سداء/لحمة) بظلال ──
            case SurfaceType.Cloth:
                {
                    FillCircleWeave(cx, cy, Mathf.RoundToInt(mainR * 1.2f), col);
                    int n = 2 + Mathf.RoundToInt(dropMul * 2f);
                    for (int i = 0; i < n; i++)
                    {
                        float a = baseAng + Random.Range(-1.2f, 1.2f);
                        float d = mainR * Random.Range(1.2f, 2.3f);
                        FillCircleWeave(cx + Mathf.RoundToInt(Mathf.Cos(a) * d),
                                        cy + Mathf.RoundToInt(Mathf.Sin(a) * d),
                                        Mathf.Max(Mathf.RoundToInt(mainR * 0.45f), 1), col);
                    }
                    break;
                }

            // ── خشب: بقعة ممدودة بقوة باتجاه العرق + عروق دقيقة متعددة + تجمّع بالأخاديد ──
            case SurfaceType.Wood:
                {
                    for (int k = -4; k <= 4; k++)   // تمدّد أفقي أوسع
                    {
                        int ox = cx + k * Mathf.Max(Mathf.RoundToInt(mainR * 0.55f), 1);
                        FillCircleSoft(ox, cy, Mathf.Max(Mathf.RoundToInt(mainR * 0.7f), 1), col, 0.45f);
                    }
                    for (int g = 0; g < 3; g++)     // عروق دقيقة بدرجات مختلفة — مخففة
                    {
                        int oy = cy + Random.Range(-mainR, mainR);
                        Color gl = col * Random.Range(0.5f, 0.72f); gl.a = col.a * 0.55f;
                        DrawLinePixels(cx - mainR * 3, oy, cx + mainR * 3, oy, gl);
                    }
                    for (int i = 0; i < 3; i++)     // تجمّع غامق بالأخاديد — مخفف
                    {
                        int ox = cx + Random.Range(-mainR * 3, mainR * 3);
                        int oy = cy + Random.Range(-mainR, mainR);
                        Color dk = col * 0.5f; dk.a = col.a * 0.5f;
                        FillCircle(ox, oy, 1, dk);
                    }
                    break;
                }

            // ── معدن: حبيبات لمّاعة حادّة — حافة meniscus غامقة + لمعة بيضا قوية ──
            default:
                {
                    int beads = Mathf.Clamp(3 + Mathf.RoundToInt(dropMul * 2f + speed * 0.35f), 3, 7);
                    for (int b = 0; b < beads; b++)
                    {
                        float a = baseAng + Random.Range(-2.2f, 2.2f);
                        float d = Random.Range(0f, mainR * (1.4f + forwardness));
                        int bx = cx + Mathf.RoundToInt(Mathf.Cos(a) * d);
                        int by = cy + Mathf.RoundToInt(Mathf.Sin(a) * d);
                        int br = Mathf.Max(Mathf.RoundToInt(mainR * Random.Range(0.35f, 0.75f)), 2);

                        Color rim = Color.Lerp(col, Color.black, 0.28f); rim.a = col.a; // حافة الحبّة الغامقة
                        FillCircle(bx, by, br + 1, rim);
                        FillCircle(bx, by, br, col);                                    // جسم الحبّة الحادّ
                        Color hi = Color.Lerp(col, Color.white, 0.85f); hi.a = col.a;
                        int hr = Mathf.Max(br / 4, 1);
                        FillCircle(bx - Mathf.Max(br / 3, 1), by - Mathf.Max(br / 3, 1), hr, hi); // لمعة قوية
                    }
                    break;
                }
        }
    }

    // نسيج خيوط متعامد بظلال (سداء عمودي + لحمة أفقية) — للسطح القماشي
    void FillCircleWeave(int cx, int cy, int r, Color c)
    {
        if (r < 1) r = 1;
        int r2 = r * r;
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                if (dx * dx + dy * dy > r2) continue;
                int px = Mathf.Clamp(cx + dx, 0, canvasWidth - 1);
                int py = Mathf.Clamp(cy + dy, 0, canvasHeight - 1);
                int u = px % 6, v = py % 6;
                bool warp = u < 3, weft = v < 3;
                float shade = (warp ^ weft) ? 1f : 0.55f;        // فوق-تحت
                if (u == 0 || v == 0) shade *= 0.4f;             // فراغات الخيوط (ظل)
                float a = c.a * 0.85f * shade;
                BlendCanvasPixel(py * canvasWidth + px, c, a);
            }
    }

    // ════════════════════════════════════════════════════════════════
    //  Lightweight Layered Color + Canvas Mixing Helpers
    // ════════════════════════════════════════════════════════════════
    int PaletteCount()
    {
        if (!enableLayeredPaintColors || layerPaintColors == null || layerPaintColors.Length == 0) return 1;
        return Mathf.Clamp(layerPaintColors.Length, 1, 4);
    }

    int InitialLayerForParticle(int particleIndex, float localHeight)
    {
        if (!enableLayeredPaintColors) return 0;
        int count = PaletteCount();
        float t = Mathf.InverseLerp(-bucketWorldHeight * 0.45f, bucketWorldHeight * 0.15f, localHeight);
        return Mathf.Clamp(Mathf.FloorToInt(t * count), 0, count - 1);
    }

    public int CurrentLayerIndex()
    {
        if (!enableLayeredPaintColors) return 0;
        int count = PaletteCount();
        int perLayer = Mathf.Max(1, particlesPerLayer);
        // لا نعيد الدوران: طبقة أولى، ثم ثانية، ثم ثالثة، ثم رابعة
        return Mathf.Clamp(releasedPaintParticles / perLayer, 0, count - 1);
    }

    public Color GetLayerColor(int layer)
    {
        if (!enableLayeredPaintColors || layerPaintColors == null || layerPaintColors.Length == 0)
            return paintColor;
        return layerPaintColors[Mathf.Clamp(layer, 0, PaletteCount() - 1)];
    }

    public Color CurrentPaintColor()
    {
        return enableLayeredPaintColors ? GetLayerColor(CurrentLayerIndex()) : paintColor;
    }

    Color GetParticleColor(int particleIndex)
    {
        if (!enableLayeredPaintColors || colorLayerCPU == null || particleIndex < 0 || particleIndex >= colorLayerCPU.Length)
            return paintColor;
        return GetLayerColor(colorLayerCPU[particleIndex]);
    }

    void BlendCanvasPixel(int index, Color incoming, float strength)
    {
        strength = Mathf.Clamp01(strength);
        if (!enableLightCanvasMixing)
        {
            canvasPx[index] = Color.Lerp(canvasPx[index], incoming, strength);
            return;
        }

        Color old = canvasPx[index];
        bool blank = old.r > 0.94f && old.g > 0.94f && old.b > 0.94f;

        // أول طبقة: نرسب اللون مثل المعتاد بدون لمس الأداء العام
        if (blank)
        {
            canvasPx[index] = Color.Lerp(old, incoming, Mathf.Clamp01(strength * paintDepositStrength));
            return;
        }

        // في حال وجود لون سابق: نُظهر المزج بوضوح أكبر، لكن بدون أن نصير ثقيلين.
        // coverage: كلما صار البكسل أغمق/مصبوغ أكثر نرفع المزج قليلاً.
        float coverage = 1f - Mathf.Clamp01((old.r + old.g + old.b) / 3f);
        float overlapBoost = Mathf.Lerp(1.00f, 1.30f, coverage);

        Color pigmentMixed = PigmentMixApprox(old, incoming);
        Color target = Color.Lerp(pigmentMixed, incoming, 0.18f * paintDepositStrength);

        float mixAmount = Mathf.Clamp01(strength * canvasMixStrength * overlapBoost + 0.06f);
        canvasPx[index] = Color.Lerp(old, target, mixAmount);
    }

    Color PigmentMixApprox(Color a, Color b)
    {
        // تقريب خفيف لمزج الأصباغ، بدون خرائط wetness أو عمليات ثقيلة
        bool aRed = a.r > 0.55f && a.g < 0.45f && a.b < 0.45f;
        bool bRed = b.r > 0.55f && b.g < 0.45f && b.b < 0.45f;
        bool aBlue = a.b > 0.50f && a.r < 0.45f && a.g < 0.55f;
        bool bBlue = b.b > 0.50f && b.r < 0.45f && b.g < 0.55f;
        bool aYellow = a.r > 0.65f && a.g > 0.55f && a.b < 0.35f;
        bool bYellow = b.r > 0.65f && b.g > 0.55f && b.b < 0.35f;

        if ((aYellow && bBlue) || (aBlue && bYellow))
            return new Color(0.12f, 0.62f, 0.18f, 1f); // أصفر + أزرق = أخضر

        if ((aRed && bBlue) || (aBlue && bRed))
            return new Color(0.45f, 0.10f, 0.60f, 1f); // أحمر + أزرق = بنفسجي

        if ((aRed && bYellow) || (aYellow && bRed))
            return new Color(0.95f, 0.38f, 0.06f, 1f); // أحمر + أصفر = برتقالي

        Color avg = (a + b) * 0.5f;
        avg.a = 1f;
        // fallback أهدأ من مزج RGB الخام، لكنه يظهر نتيجة واضحة على اللوحة
        Color softened = new Color(
            Mathf.Clamp01(avg.r * 0.96f),
            Mathf.Clamp01(avg.g * 0.96f),
            Mathf.Clamp01(avg.b * 0.96f),
            1f
        );
        return Color.Lerp(softened, b, 0.28f);
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
            DrawLineOnCanvas(lastTrailPoint, pointWorld, r, CurrentPaintColor());
        else
        {
            Vector3 lp = canvasTransform.InverseTransformPoint(pointWorld);
            int px = Mathf.RoundToInt(Mathf.Clamp01((lp.x + canvasHalfX) / (canvasHalfX * 2f)) * canvasWidth);
            int py = Mathf.RoundToInt(Mathf.Clamp01((lp.z + canvasHalfZ) / (canvasHalfZ * 2f)) * canvasHeight);
            FillCircle(px, py, r, CurrentPaintColor());
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
        // مؤقّت زمن الحركة + اختصار الحفظ
        if (pendulum != null && pendulum.IsRunning) motionElapsed += Time.deltaTime;
        if (Input.GetKeyDown(KeyCode.F9)) SaveExperiment();

        // if (visualDirty) { UpdateVisualPS(); visualDirty = false; }   // ← off: GPU instanced renderer now handles visuals
        if (canvasDirty && frameCount - lastCanvasApplyFrame >= Mathf.Max(1, canvasApplyEveryNFrames))
        {
            canvasTex.SetPixels(canvasPx);
            canvasTex.Apply(false);
            canvasDirty = false;
            lastCanvasApplyFrame = frameCount;
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
                BlendCanvasPixel(py * canvasWidth + px, c, c.a * 0.85f);
            }
    }

    // مثل FillCircle بس بحافة ناعمة: softness=0 حادّة (معدن)، softness~0.9 ممتصّة (ورق)
    void FillCircleSoft(int cx, int cy, int r, Color c, float softness)
    {
        if (r < 1) r = 1;
        float inner = 1f - Mathf.Clamp01(softness); // نسبة نصف القطر المعتمة بالكامل
        float fade = Mathf.Max(1f - inner, 1e-3f);
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                float dist = Mathf.Sqrt(dx * dx + dy * dy) / r; // 0..1
                if (dist > 1f) continue;
                float edge = dist <= inner ? 1f : Mathf.Clamp01(1f - (dist - inner) / fade);
                int px = Mathf.Clamp(cx + dx, 0, canvasWidth - 1);
                int py = Mathf.Clamp(cy + dy, 0, canvasHeight - 1);
                float a = c.a * 0.85f * edge;
                BlendCanvasPixel(py * canvasWidth + px, c, a);
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
            BlendCanvasPixel(py * canvasWidth + px, c, c.a * 0.6f);
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
        paintedSplats = 0;
        motionElapsed = 0f;
        releasedPaintParticles = 0;
        lastCanvasApplyFrame = frameCount;
    }

    public void SaveCanvas(string path = "PaintResult.png")
        => System.IO.File.WriteAllBytes(path, canvasTex.EncodeToPNG());

    // مساحة انتشار اللون (m²) + النسبة المئوية من اللوحة
    public float CoverageArea(out float percent)
    {
        var bg = new Color(0.95f, 0.92f, 0.85f, 1f);
        int painted = 0;
        for (int i = 0; i < canvasPx.Length; i++)
        {
            Color c = canvasPx[i];
            if (Mathf.Abs(c.r - bg.r) + Mathf.Abs(c.g - bg.g) + Mathf.Abs(c.b - bg.b) > 0.06f)
                painted++;
        }
        percent = 100f * painted / canvasPx.Length;

        float sx = canvasTransform ? Mathf.Abs(canvasTransform.lossyScale.x) : 1f;
        float sz = canvasTransform ? Mathf.Abs(canvasTransform.lossyScale.z) : 1f;
        float pixelArea = (2f * canvasHalfX * sx / canvasWidth) * (2f * canvasHalfZ * sz / canvasHeight);
        return painted * pixelArea;
    }

    // Detailed English experiment report (inputs + outputs)
    string BuildReport()
    {
        float pct; float cov = CoverageArea(out pct);
        var hole = FindFirstObjectByType<BucketHole>();
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("============================================================");
        sb.AppendLine("     SWINGING PAINT BUCKET  -  EXPERIMENT REPORT");
        sb.AppendLine("============================================================");
        sb.AppendLine("Date / Time : " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine();

        sb.AppendLine("------------------------------------------------------------");
        sb.AppendLine(" INPUTS");
        sb.AppendLine("------------------------------------------------------------");

        sb.AppendLine("[A] BUCKET");
        if (pendulum != null)
        {
            sb.AppendLine($"    Mass                : {pendulum.bucketMass:F3} kg");
            sb.AppendLine($"    Radius              : {pendulum.bucketRadius:F3} m");
            sb.AppendLine($"    Fluid mass          : {pendulum.fluidMass:F3} kg");
        }
        sb.AppendLine($"    Paint capacity      : {maxParticles} particles");
        if (hole != null)
            sb.AppendLine($"    Exit hole diameter  : {hole.holeDiameter:F4} m");
        sb.AppendLine();

        sb.AppendLine("[B] SUSPENSION");
        if (pendulum != null)
        {
            sb.AppendLine($"    Rope length         : {pendulum.restLength:F3} m");
            sb.AppendLine($"    Rope stiffness      : {pendulum.ropeStiffness:F1}");
            sb.AppendLine($"    Twist stiffness     : {pendulum.ropeTwistStiffness:F1}");
            sb.AppendLine($"    Pivot point         : {pendulum.pivotPosition}");
        }
        sb.AppendLine();

        sb.AppendLine("[C] MOTION");
        if (pendulum != null)
        {
            sb.AppendLine($"    Start angle (theta) : {pendulum.thetaDeg:F1} deg");
            sb.AppendLine($"    Direction (phi)     : {pendulum.phiDeg:F1} deg");
            sb.AppendLine($"    Initial theta vel   : {pendulum.thetaVel0:F3} rad/s");
            sb.AppendLine($"    Initial phi vel     : {pendulum.phiVel0:F3} rad/s");
            sb.AppendLine($"    Target swings       : {pendulum.n_swings}");
        }
        sb.AppendLine();

        sb.AppendLine("[D] ENVIRONMENT");
        if (pendulum != null)
        {
            sb.AppendLine($"    Gravity             : {pendulum.gravity:F3} m/s^2");
            sb.AppendLine($"    Air density         : {pendulum.airDensity:F3} kg/m^3");
            sb.AppendLine($"    Drag coefficient    : {pendulum.dragCoefficient:F3}");
            sb.AppendLine($"    Air humidity        : {pendulum.humidity * 100f:F0} %");
            sb.AppendLine($"    Joint friction      : {pendulum.jointFriction:F3}");
        }
        sb.AppendLine();

        sb.AppendLine("[E] PAINT");
        sb.AppendLine($"    Type                : {paintType}");
        sb.AppendLine($"    Color               : {ColorHex(paintColor)}  (RGBA {paintColor.r:F2},{paintColor.g:F2},{paintColor.b:F2},{paintColor.a:F2})");
        sb.AppendLine($"    Rest density        : {restDensity:F2}");
        if (hole != null)
            sb.AppendLine($"    Flow rate (base)    : {hole.particlesPerSecond:F1} particles/s");
        sb.AppendLine();

        sb.AppendLine("[F] CANVAS");
        sb.AppendLine($"    Resolution          : {canvasWidth} x {canvasHeight} px");
        sb.AppendLine($"    Surface type        : {surfaceType}");
        float sx = canvasTransform ? Mathf.Abs(canvasTransform.lossyScale.x) : 1f;
        float sz = canvasTransform ? Mathf.Abs(canvasTransform.lossyScale.z) : 1f;
        sb.AppendLine($"    Physical size       : {2f * canvasHalfX * sx:F2} x {2f * canvasHalfZ * sz:F2} m");
        sb.AppendLine($"    Orientation         : {(canvasIsHorizontal ? "Horizontal" : "Vertical")}");
        sb.AppendLine();

        sb.AppendLine("------------------------------------------------------------");
        sb.AppendLine(" OUTPUTS / RESULTS");
        sb.AppendLine("------------------------------------------------------------");
        sb.AppendLine($"    Motion time         : {motionElapsed:F2} s");
        if (pendulum != null)
        {
            sb.AppendLine($"    Swings completed    : {pendulum.SwingCount} / {pendulum.n_swings}");
            sb.AppendLine($"    Theoretical period  : {pendulum.TheoreticalPeriod():F3} s");
            sb.AppendLine($"    Energy at start     : {pendulum.EnergyAtStart:F3} J");
            sb.AppendLine($"    Energy now          : {pendulum.TotalEnergy():F3} J");
            sb.AppendLine($"    Bucket speed now    : {pendulum.GetBucketVelocity().magnitude:F3} m/s");
        }
        sb.AppendLine($"    Paths drawn (splats): {paintedSplats}");
        sb.AppendLine($"    Particles inside    : {InsideCount()} / {maxParticles}");
        sb.AppendLine($"    Color spread area   : {cov:F4} m^2");
        sb.AppendLine($"    Canvas coverage     : {pct:F2} %");
        sb.AppendLine();
        sb.AppendLine("============================================================");
        return sb.ToString();
    }

    string ColorHex(Color c) => $"#{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}";

    // حفظ التجربة: صورة PNG + تقرير نصّي في مجلّد واحد
    public void SaveExperiment()
    {
        if (canvasTex == null) return;
        string dir = string.IsNullOrEmpty(saveFolder)
            ? System.IO.Path.Combine(Application.persistentDataPath, "PaintResults")
            : saveFolder;
        System.IO.Directory.CreateDirectory(dir);
        string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string png = System.IO.Path.Combine(dir, $"canvas_{stamp}.png");
        string txt = System.IO.Path.Combine(dir, $"report_{stamp}.txt");
        System.IO.File.WriteAllBytes(png, canvasTex.EncodeToPNG());
        System.IO.File.WriteAllText(txt, BuildReport());
        Debug.Log($"[Experiment] تم الحفظ في:\n{png}\n{txt}");
    }

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
                restDensity = 3f; SIGMA = 0.04f; gravityScale = 0.9f; bucketInfluence = 0.6f; viscosity = 0.18f; break;
            case PaintType.Acrylic:
                restDensity = 4.5f; SIGMA = 0.3f; gravityScale = 0.6f; bucketInfluence = 0.3f; viscosity = 0.44f; break;
            case PaintType.OilPaint:
                restDensity = 6.5f; SIGMA = 0.85f; gravityScale = 0.4f; bucketInfluence = 0.12f; viscosity = 0.70f; break;
            case PaintType.Tempera:
                restDensity = 3.8f; SIGMA = 0.15f; gravityScale = 0.75f; bucketInfluence = 0.45f; viscosity = 0.31f; break;
            case PaintType.Gouache:
                restDensity = 5.5f; SIGMA = 0.55f; gravityScale = 0.5f; bucketInfluence = 0.2f; viscosity = 0.57f; break;
            case PaintType.Latex:
                restDensity = 7.5f; SIGMA = 1.1f; gravityScale = 0.35f; bucketInfluence = 0.1f; viscosity = 0.95f; break;
            case PaintType.Enamel:
                restDensity = 7f; SIGMA = 1.0f; gravityScale = 0.38f; bucketInfluence = 0.11f; viscosity = 0.83f; break;
            case PaintType.Ink:
                restDensity = 2.5f; SIGMA = 0.02f; gravityScale = 0.95f; bucketInfluence = 0.7f; viscosity = 0.05f; break;
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


        if (showParticleSloshGizmo && enableParticleSloshing && bucketTransform != null && particleSloshVector.sqrMagnitude > 0.0001f)
        {
            Gizmos.color = Color.magenta;
            Vector3 sloshCenter = BucketCenter;
            Vector3 dir = particleSloshVector.normalized;
            float len = bucketWorldRadius * Mathf.Lerp(0.3f, 1.4f, Mathf.Clamp01(particleSloshVector.magnitude));
            Gizmos.DrawLine(sloshCenter, sloshCenter + dir * len);
            Gizmos.DrawSphere(sloshCenter + dir * len, bucketWorldRadius * 0.06f);
        }
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
        GUI.Label(new Rect(x, y + lh * 4, 230, lh), "Surface : " + surfaceType, hudStyle);
        GUI.Label(new Rect(x, y + lh * 5, 230, lh), "Splash  : ON | GPU", hudStyle);
    }
}