using UnityEngine;
using System.Runtime.InteropServices;
using System.Collections.Generic;



public class PBFSolver : MonoBehaviour
{
    [Header("References — المراجع")]
    public SphericalPendulum pendulum;
    public Transform bucketTransform;
    public Transform canvasTransform;
    public Renderer canvasRenderer;
    public ComputeShader pbfComputeShader;   

    [Header("Paint Type — نوع الطلاء")]
    public PaintType paintType = PaintType.Acrylic;

    public enum PaintType
    { Watercolor, Acrylic, OilPaint, Tempera, Gouache, Latex, Enamel, Ink }

    [Header("Surface — نوع سطح اللوحة")]
    [Tooltip("نوع سطح الرسم: يغيّر شكل البقعة والانتشار والمزج")]
    public SurfaceType surfaceType = SurfaceType.Cloth;
    public enum SurfaceType
    { Paper, Cloth, Wood, Metal }  

    [Tooltip("تفعيل تأثير السطح بصرياً على أثر الطلاء، بدون فيزياء جاهزة")]
    public bool enableSurfaceEffects = true;

    [Tooltip("قوة ظهور فرق السطح على اللوحة")]
    [Range(0f, 1f)] public float surfaceEffectStrength = 1.0f;

    [Header("PBF Settings — إعدادات المحاكاة")]
    [Tooltip("عدد الجزيئات — RTX 2050 يدعم 50K-200K بسهولة")]
    [Range(1000, 200000)]
    public int maxParticles = 40000;

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

    [Header("Bucket Size — حجم الدلو")]
    public float bucketWorldRadius = 0.25f;
    public float bucketWorldHeight = 0.4f;
    public Vector3 bucketCenterOffset = new Vector3(0f, 0f, -0.15f);

    [Header("Canvas — اللوحة")]
    public int canvasWidth = 384;
    public int canvasHeight = 384;
    public bool canvasIsHorizontal = true;
    public int baseBrushSize = 5;
    [Range(0.1f, 2f)] public float speedToSizeMultiplier = 0.3f;
    [Range(0, 20)] public int splashDroplets = 0;
    [Range(5, 80)] public int splashMaxRadius = 8;
    [Range(1, 20)] public int readbackInterval = 6; 
    [Range(0.1f, 0.8f)] public float dropletSizeRatio = 0.3f;

    [Header("Canvas Projection — إسقاط الدهان على اللوحة")]
    [Tooltip("يرسم على نقطة تقاطع مسار الجزيئة مع مستوى اللوحة بدل الإسقاط العمودي من موقعها الحالي. هذا يصحح الإزاحة عند ميلان اللوحة أو عند readbackInterval كبير.")]
    public bool useTrajectoryCanvasProjection = true;

    [Tooltip("سماحية اصطدام صغيرة حول مستوى اللوحة حتى لا تفلت الجزيئات بين فريمات القراءة.")]
    [Range(0.005f, 0.25f)] public float canvasImpactTolerance = 0.08f;

    [Tooltip("كم نرجع للخلف على مسار الجزيئة عند حساب التقاطع. ارفعه قليلاً إذا كان readbackInterval كبيراً.")]
    [Range(0.5f, 3f)] public float impactBacktrackMultiplier = 1.35f;

    [Header("Canvas Tilt & Dripping — ميلان اللوحة ونزول الدهان")]
    [Tooltip("يسمح بتغيير ميلان اللوحة من الواجهة بدون تغيير منطق السائل أو البندول.")]
    public bool enableCanvasTiltControls = false;

    [Tooltip("ميلان اللوحة للأمام/الخلف بالدرجات. يعمل على Rotation X المحلي للوحة.")]
    [Range(-75f, 75f)] public float canvasTiltXDeg = 0f;

    [Tooltip("ميلان اللوحة يمين/يسار بالدرجات. يعمل على Rotation Z المحلي للوحة.")]
    [Range(-75f, 75f)] public float canvasTiltZDeg = 0f;

    [Tooltip("تفعيل نزول الدهان على اللوحة حسب الميلان، نوع الدهان، اللزوجة، ونوع السطح.")]
    public bool enablePaintDripping = true;

    [Tooltip("قوة سيلان الدهان على اللوحة. ارفعها إذا بدك خطوط نزول أوضح.")]
    [Range(0f, 2f)] public float dripStrength = 0.75f;

    [Tooltip("كل كم فريم نطبق خطوة سيلان على Texture اللوحة. رقم أكبر = أداء أخف.")]
    [Range(1, 30)] public int dripEveryNFrames = 6;

    [Tooltip("أقصى عدد بكسلات يمكن للدهان أن ينزلها بكل خطوة.")]
    [Range(1, 10)] public int maxDripPixelsPerStep = 4;

    [Tooltip("أقل كمية لون مطلوبة قبل ما البقعة تبدأ تنزل. ارفعه إذا السيلان صار كثير.")]
    [Range(0.01f, 0.35f)] public float dripThreshold = 0.07f;

    [Tooltip("مدى جفاف/تثبيت اللون أثناء السيلان. الورق والقماش يجففان أكثر من المعدن.")]
    [Range(0f, 1f)] public float dripDrying = 0.18f;

    [Tooltip("قوة مزج ألوان الدهان أثناء نزوله فقط على اللوحة المائلة. لا يغيّر مزج اللوحة الأفقية.")]
    [Range(0f, 2f)] public float dripMixBoost = 1.35f;

    [Tooltip("يعكس اتجاه السيلان على محور V فقط. يفيد عندما تكون نقطة الاصطدام صحيحة لكن نزول الدهان يظهر للأعلى بصرياً بسبب اتجاه UV/Texture على اللوحة.")]
    public bool invertCanvasDripV = true;

    [Header("Impact mark — أثر السقوط")]
    public bool sprayEnabled = false;        
    [Range(1, 12)] public int exactDotSize = 3; 
    public bool flipMarkU = false;        
    public bool flipMarkV = false;            

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
    [Tooltip("فعّلها فقط وقت التشخيص. إطفاؤها يحذف اللوجات المتكررة ويحسن الأداء مع أعداد كبيرة.")]
    public bool verboseDebugLogs = false;

    [Header("Particle Sloshing Inside Bucket — حركة الجزيئات داخل الدلو")]
    [Tooltip("يحرك الجزيئات نفسها داخل الدلو عند تبدل اتجاه الحركة، بدون إضافة سطح مرئي وهمي.")]
    public bool enableParticleSloshing = true;

    [Tooltip("قوة ميلان/اندفاع السائل داخل الدلو عند أقصى اليمين واليسار.")]
    [Range(0f, 1.5f)] public float particleSloshStrength = 1.15f;

    [Tooltip("سرعة استجابة السائل لتسارع الدلو. قيمة أعلى = يميل أسرع.")]
    [Range(1f, 18f)] public float particleSloshResponse = 11.5f;

    [Tooltip("تخميد التموّج. قيمة أعلى = اهتزاز أقل.")]
    [Range(0.5f, 12f)] public float particleSloshDamping = 5.8f;

    [Tooltip("قوة الدفع الجانبي للجزيئات قرب سطح السائل.")]
    [Range(0f, 12f)] public float particleSloshForce = 3.2f;

    [Tooltip("قوة رفع/خفض الجزيئات قرب الأطراف حتى يظهر السائل مائلاً فعلياً لا ككتلة مسطحة.")]
    [Range(0f, 12f)] public float particleSloshLiftForce = 1.6f;

    [Tooltip("تعزيز التموّج لحظة تغير الاتجاه عند أقصى اليمين/اليسار.")]
    [Range(0f, 1.5f)] public float particleSloshTurnBoost = 0.55f;

    [Tooltip("كم يسمح لمستوى الجزيئات أن يرتفع من جهة وينخفض من الجهة الأخرى.")]
    [Range(0f, 0.45f)] public float particleSloshSurfaceSlope = 0.22f;

    [Tooltip("يعزز تأثير التموّج على الجزيئات القريبة من سطح السائل أكثر من الجزيئات السفلية.")]
    [Range(0f, 1f)] public float particleSloshSurfaceBias = 0.88f;

    [Tooltip("إظهار سهم فقط لاتجاه اندفاع الجزيئات. لا يضيف سطح سائل مرئي.")]
    public bool showParticleSloshGizmo = true;

    [Header("Fluid Settling — منع الغليان بعد توقف الدلو")]
    [Tooltip("تخميد عام خفيف دائماً. يمنع تراكم طاقة رقمية داخل الـ PBF.")]
    [Range(0f, 8f)] public float insideVelocityDamping = 1.35f;

    [Tooltip("تخميد إضافي عندما تكون حركة الدلو شبه متوقفة. ارفعه إذا ظل السائل يغلي.")]
    [Range(0f, 24f)] public float stillSettleDamping = 12.0f;

    [Tooltip("سرعة تعتبر تحتها الجزيئة ساكنة ويتم تصفيرها حتى لا تبقى ترجف.")]
    [Range(0.001f, 0.08f)] public float velocitySleepThreshold = 0.028f;

    [Tooltip("أصغر حركة للدلو قبل اعتبار الدلو متحركاً. ارفعه إذا تريد هدوء أسرع.")]
    [Range(0.01f, 0.6f)] public float bucketStillSpeed = 0.08f;

    [Tooltip("أصغر تسارع للدلو قبل اعتبار الدلو متحركاً. ارفعه إذا تريد هدوء أسرع.")]
    [Range(0.1f, 5f)] public float bucketStillAccel = 0.65f;

    [Header("Debug")]
    public bool showDebugGizmos = true;


    [HideInInspector] public float gravityScale = 0.6f;
    [HideInInspector] public float bucketInfluence = 0.3f;
    [HideInInspector] public float SIGMA = 0.3f;
    [HideInInspector] public float viscosity = 0.42f; 

    [Header("Particle Cohesion — تماسك الجزيئات")]
    [Tooltip("يجعل الجزيئات القريبة تنجذب لبعضها بشكل خفيف حتى يظهر الطلاء ككتلة سائلة لا كحبيبات منفصلة.")]
    public bool enableParticleCohesion = true;

    [Tooltip("قوة التماسك بين الجزيئات. ارفعها إذا كان السائل مفككاً، وخففها إذا صار مثل الجل.")]
    [Range(0f, 8f)] public float particleCohesionStrength = 1.55f;

    [Tooltip("مسافة تأثير التماسك بالنسبة إلى h. قيمة أكبر = تماسك أوسع لكن أبطأ قليلاً.")]
    [Range(0.35f, 1.3f)] public float particleCohesionRadius = 0.85f;

    [Tooltip("يمنع التكتل الزائد عندما تكون الجزيئات قريبة جداً.")]
    [Range(0f, 2f)] public float particleCohesionRepulsion = 0.45f;

    [Tooltip("تخميد إضافي بسيط يمنع اهتزاز كتلة السائل بعد إضافة التماسك.")]
    [Range(0f, 4f)] public float particleCohesionDamping = 1.35f;


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
    int kDampInsideFluid;

    int gridSizeX, gridSizeY, gridSizeZ;
    Vector3 gridOrigin;
    float cellSize;
    const int MAX_PER_CELL = 128;   
    const int MAX_NEIGHBORS = 128; 
    int totalCells;


    Vector3[] positionsCPU;
    int[] statesCPU;
    Vector3[] velocitiesCPU;
    int[] colorLayerCPU;

   
    readonly int[] oneIntUpload = new int[1];
    readonly Vector3[] oneVectorUpload = new Vector3[1];

 
    int cachedInsideCount = 0;
    float cachedLiquidHeight = 0.5f;
    bool cpuDataReady = false;

  
    const int INSIDE = 0;
    const int FALLING = 1;
    const int ON_CANVAS = 2;

   
    Texture2D canvasTex;
    Color[] canvasPx;
    Color[] dripScratch;
    int lastDripFrame = 0;
    bool canvasDirty;
    public string saveFolder = @"C:\Users\Haidi\VR_Project"; 
    int paintedSplats = 0;    
    float motionElapsed = 0f;  

    public int PaintedSplats => paintedSplats;
    public float MotionElapsed => motionElapsed;
    float canvasHalfX = 0.5f;
    float canvasHalfZ = 0.5f;

    Vector3 lastTrailPoint;
    bool hasLastTrailPoint = false;

    Vector3 prevBucketVel = Vector3.zero;
    Vector3 prevBucketCenter = Vector3.zero;
    bool bucketMotionInitialized = false;
    Vector3 bucketAccelWorld = Vector3.zero;
    float bucketMotionAmount = 0f;

    Vector3 particleSloshVector = Vector3.zero;
    Vector3 particleSloshVelocity = Vector3.zero;

    ParticleSystem visualPS;
    ParticleSystem.Particle[] visualParticles;
    bool visualDirty = true;
    bool initialized = false;

    int frameCount = 0;
    int releasedPaintParticles = 0;
    int lastCanvasApplyFrame = 0;

    public Vector3 BucketCenter => bucketTransform.position
                                 + bucketTransform.TransformDirection(bucketCenterOffset);
    Vector3 BucketUp => bucketTransform.TransformDirection(-Vector3.forward).normalized;
    Vector3 BucketRight => bucketTransform.TransformDirection(Vector3.right).normalized;
    Vector3 BucketForward => bucketTransform.TransformDirection(Vector3.up).normalized;

    public Vector3 HolePosition => BucketCenter - BucketUp * (bucketWorldHeight * 0.5f);
    public Vector3 TopPosition => BucketCenter + BucketUp * (bucketWorldHeight * 0.5f);

    public ComputeBuffer PositionsBuffer => positionsBuffer;
    public ComputeBuffer StatesBuffer => statesBuffer;
    public int ParticleCount => maxParticles;
    public Vector3 BucketUpDir => BucketUp;
    public Vector3 BucketRightDir => BucketRight;
    public Vector3 BucketForwardDir => BucketForward;

  
    public Vector3 BucketFluidLocalToWorld(Vector3 local)
    {
        return BucketCenter + BucketRight * local.x + BucketUp * local.y + BucketForward * local.z;
    }

    public Vector3 BucketFluidWorldToLocal(Vector3 world)
    {
        Vector3 d = world - BucketCenter;
        return new Vector3(Vector3.Dot(d, BucketRight), Vector3.Dot(d, BucketUp), Vector3.Dot(d, BucketForward));
    }

    public Vector3 BucketFluidLocalDirToWorld(Vector3 localDir)
    {
        return BucketRight * localDir.x + BucketUp * localDir.y + BucketForward * localDir.z;
    }

    public Vector3 BucketFluidWorldDirToLocal(Vector3 worldDir)
    {
        return new Vector3(Vector3.Dot(worldDir, BucketRight), Vector3.Dot(worldDir, BucketUp), Vector3.Dot(worldDir, BucketForward));
    }

    
    void AutoComputeH()
    {
        if (!autoComputeH) return;

        float rMax = bucketWorldRadius * 0.9f;     
        float heightSpan = bucketWorldHeight * 0.6f;
        float volume = Mathf.PI * rMax * rMax * heightSpan;
        float density = maxParticles / Mathf.Max(volume, 0.0001f);

        float sphereVolumeNeeded = targetNeighborCount / Mathf.Max(density, 0.0001f);
        h = Mathf.Pow(sphereVolumeNeeded / (4f / 3f * Mathf.PI), 1f / 3f);

        if (verboseDebugLogs)
            Debug.Log($"[PBFSolver] h تلقائي = {h:F4} (جيران مستهدفة={targetNeighborCount}, كثافة={density:F0} جزيء/م3)");
    }

 
    public void RestartSimulation()
    {
        ReleaseBuffers();
        AutoComputeH();
        ApplyPaintType();
        InitGrid();
        InitGPUBuffers();
        ClearCanvas();
        SpawnParticles();
        CalibrateRestDensityScale();
        ResetBucketMotionTracking();
        initialized = true;
        if (verboseDebugLogs)
            Debug.Log($"[PBFSolver] أعيد التشغيل — Particles={maxParticles}");
    }

    void Start()
    {
        if (pbfComputeShader == null)
        {
            Debug.LogError("[PBF] Compute Shader غير مربوط! اربط PBFSolver.compute بالـ Inspector.");
            return;
        }

        AutoComputeH();  
        ApplyPaintType();
        InitGrid();
        InitGPUBuffers();
        InitCanvas();
        // InitVisualPS();  
        SpawnParticles();
        CalibrateRestDensityScale();   
        ResetBucketMotionTracking();

        initialized = true;
        if (verboseDebugLogs)
            Debug.Log($"[PBFSolver] Initialized — Particles={maxParticles} | Iterations={solverIterations}");
    }


    void ResetBucketMotionTracking()
    {
        prevBucketCenter = bucketTransform ? BucketCenter : Vector3.zero;
        prevBucketVel = Vector3.zero;
        bucketAccelWorld = Vector3.zero;
        bucketMotionAmount = 0f;
        bucketMotionInitialized = bucketTransform != null;
        particleSloshVector = Vector3.zero;
        particleSloshVelocity = Vector3.zero;
    }

    void OnDestroy()
    {
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
        if (verboseDebugLogs)
            Debug.Log($"[PBF] Grid: {gridSizeX}x{gridSizeY}x{gridSizeZ} = {totalCells} cells");
    }


    void InitGPUBuffers()
    {
        int n = maxParticles;


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
        kDampInsideFluid = pbfComputeShader.FindKernel("DampInsideFluid");

        BindAllBuffers();

        positionsCPU = new Vector3[n];
        statesCPU = new int[n];
        velocitiesCPU = new Vector3[n];
        colorLayerCPU = new int[n];

        long totalBytes = (long)n * (12 * 7 + 4 + 4)
                       + (long)totalCells * (4 + 4 * MAX_PER_CELL)
                       + (long)n * MAX_NEIGHBORS * 4;
        if (verboseDebugLogs)
            Debug.Log($"[PBF] GPU Buffers allocated — {totalBytes / 1024 / 1024} MB on GPU");
    }

    void BindAllBuffers()
    {
        int[] kernels = {
            kPredictPositions, kClearGrid, kFillGrid,
            kFindNeighbors, kSolveConstraints, kApplyCorrection,
            kUpdateVelocity, kEnforceBoundary, kFallingStep,
            kApplyViscosity,
            kApplyCohesion, kClampPredicted, kDampInsideFluid
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

    void CalibrateRestDensityScale()
    {
        float poly6C = 315f / (64f * Mathf.PI * Mathf.Pow(h, 9));
        float hSq = h * h;

      
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
        if (verboseDebugLogs)
            Debug.Log($"[PBFSolver] restDensity بعد التصحيح = {restDensity:F2} (نسبة التصحيح ×{scale:F2})");
    }

    void SpawnParticles()
    {
        Vector3[] initPos = new Vector3[maxParticles];
        Vector3[] initVel = new Vector3[maxParticles];
        int[] initSt = new int[maxParticles];
        int[] initLayer = new int[maxParticles];

        float radius = bucketWorldRadius * 0.86f;
        float bottom = -bucketWorldHeight * 0.45f + h * 0.55f;
        float top = bucketWorldHeight * 0.15f - h * 0.35f;
        float height = Mathf.Max(top - bottom, h * 2f);

        float volume = Mathf.PI * radius * radius * height;
        float spacing = Mathf.Pow(volume / Mathf.Max(1, maxParticles), 1f / 3f) * 0.92f;
        spacing = Mathf.Clamp(spacing, h * 0.42f, h * 0.78f);

        int count = 0;

        for (int attempt = 0; attempt < 6 && count < maxParticles; attempt++)
        {
            count = 0;
            int layer = 0;
            float s = spacing * Mathf.Pow(0.92f, attempt);
            float rowH = s * 0.8660254f;    
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

                        float jx = Random.Range(-0.035f, 0.035f) * s;
                        float jy = Random.Range(-0.020f, 0.020f) * s;
                        float jz = Random.Range(-0.035f, 0.035f) * s;

                        float lx = x + jx;
                        float ly = y + jy;
                        float lz = z + jz;

                        initPos[count] = new Vector3(lx, ly, lz);
                        initVel[count] = Vector3.zero;
                        initSt[count] = INSIDE;
                        initLayer[count] = InitialLayerForParticle(count, ly);
                        count++;
                    }
                }
            }
        }

        int guard = 0;
        while (count < maxParticles && guard++ < maxParticles * 4)
        {
            float a = Random.Range(0f, Mathf.PI * 2f);
            float r = radius * Mathf.Sqrt(Random.Range(0f, 1f));
            float y = Random.Range(bottom, top);
            initPos[count] = new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
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

        if (verboseDebugLogs)
            Debug.Log($"[PBF] Spawned {maxParticles} particles with stable lattice spacing={spacing:F4} → GPU");
    }


    void FixedUpdate()
    {
        if (!initialized || !bucketTransform) return;

        float dt = Time.fixedDeltaTime;
        frameCount++;

     
        Vector3 curCenter = BucketCenter;
        if (!bucketMotionInitialized)
        {
            prevBucketCenter = curCenter;
            prevBucketVel = Vector3.zero;
            bucketAccelWorld = Vector3.zero;
            bucketMotionInitialized = true;
        }

        Vector3 measuredVel = (curCenter - prevBucketCenter) / Mathf.Max(dt, 0.001f);
        Vector3 pendulumVel = (pendulum != null && pendulum.IsRunning) ? pendulum.GetBucketVelocity() : measuredVel;
        Vector3 curVel = Vector3.Lerp(measuredVel, pendulumVel, 0.35f);

        Vector3 rawAccel = (curVel - prevBucketVel) / Mathf.Max(dt, 0.001f);
        rawAccel = Vector3.ClampMagnitude(rawAccel, 10f);
        bucketAccelWorld = Vector3.Lerp(bucketAccelWorld, rawAccel, 1f - Mathf.Exp(-14f * dt));

        prevBucketCenter = curCenter;
        prevBucketVel = curVel;

        if (pendulum != null && pendulum.fadeOutStarted)
        {
            float fp = Mathf.Clamp01(pendulum.fadeOutTimer / Mathf.Max(0.001f, pendulum.fadeOutDuration));
            bucketAccelWorld *= (1f - fp);
        }

        float speed01 = Mathf.InverseLerp(bucketStillSpeed, bucketStillSpeed * 5f, curVel.magnitude);
        float accel01 = Mathf.InverseLerp(bucketStillAccel, bucketStillAccel * 5f, bucketAccelWorld.magnitude);
        bucketMotionAmount = Mathf.Clamp01(Mathf.Max(speed01, accel01));

        UpdateParticleSloshing(dt);

        SetShaderConstants(dt);

      
        Dispatch(kPredictPositions);
        Dispatch(kClampPredicted);  

        DispatchGrid(kClearGrid);   
        Dispatch(kFillGrid);

        Dispatch(kFindNeighbors);

      
        for (int iter = 0; iter < solverIterations; iter++)
        {
            Dispatch(kSolveConstraints);
            Dispatch(kApplyCorrection);
            Dispatch(kClampPredicted);   
        }

        Dispatch(kUpdateVelocity);

        if (viscosity > 0f)
            Dispatch(kApplyViscosity);

        if (enableParticleCohesion && particleCohesionStrength > 0f)
            Dispatch(kApplyCohesion);

        Dispatch(kEnforceBoundary);

        Dispatch(kDampInsideFluid);

        Dispatch(kFallingStep);

    
        if (frameCount % readbackInterval == 0)
            CPUReadback();
        else if (!cpuDataReady)
            CPUReadback(); 

        visualDirty = true;
    }


    void UpdateParticleSloshing(float dt)
    {
        if (!enableParticleSloshing || bucketTransform == null)
        {
            float off = 1f - Mathf.Exp(-10f * dt);
            particleSloshVector = Vector3.Lerp(particleSloshVector, Vector3.zero, off);
            particleSloshVelocity = Vector3.Lerp(particleSloshVelocity, Vector3.zero, off);
            return;
        }

        Vector3 up = BucketUp;
        Vector3 lateralPseudo = Vector3.ProjectOnPlane(-bucketAccelWorld, up);
        Vector3 lateralVel = Vector3.ProjectOnPlane(prevBucketVel, up);

        
        Vector3 sloshDrive = lateralPseudo - lateralVel * 0.22f;
        float driveMag = sloshDrive.magnitude;

        Vector3 desired = Vector3.zero;
        if (driveMag > 0.035f)
        {
            float amount = Mathf.InverseLerp(0.20f, 6.0f, driveMag) * particleSloshStrength;
            desired = sloshDrive.normalized * Mathf.Clamp01(amount);
        }

        float still01 = 1f - bucketMotionAmount;
        float response = Mathf.Lerp(particleSloshResponse, particleSloshResponse * 0.55f, still01);
        float damping = particleSloshDamping + stillSettleDamping * still01;

        Vector3 error = desired - particleSloshVector;
        particleSloshVelocity += error * response * dt;
        particleSloshVelocity *= Mathf.Exp(-damping * dt);
        particleSloshVector += particleSloshVelocity * dt;

        if (bucketMotionAmount < 0.08f && desired.sqrMagnitude < 0.0004f)
        {
            float settle = Mathf.Exp(-stillSettleDamping * dt);
            particleSloshVector *= settle;
            particleSloshVelocity *= settle;

            if (particleSloshVector.sqrMagnitude < 0.00025f)
                particleSloshVector = Vector3.zero;
            if (particleSloshVelocity.sqrMagnitude < 0.00025f)
                particleSloshVelocity = Vector3.zero;
        }

        if (particleSloshVector.magnitude > 1f)
            particleSloshVector = particleSloshVector.normalized;
    }


    void SetShaderConstants(float dt)
    {
   
        Vector3 gravityLocal = BucketFluidWorldDirToLocal(Vector3.down * G * gravityScale);
        Vector3 accelLocal = BucketFluidWorldDirToLocal(bucketAccelWorld);
        Vector3 sloshLocal = BucketFluidWorldDirToLocal(particleSloshVector);
        sloshLocal.y = 0f; 

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
        pbfComputeShader.SetFloat("insideVelocityDamping", insideVelocityDamping);
        pbfComputeShader.SetFloat("stillSettleDamping", stillSettleDamping);
        pbfComputeShader.SetFloat("velocitySleepThreshold", velocitySleepThreshold);
        pbfComputeShader.SetFloat("bucketMotionAmount", bucketMotionAmount);
        pbfComputeShader.SetVector("gravityLocal", gravityLocal);
        pbfComputeShader.SetVector("bucketAccelLocal", accelLocal);
        pbfComputeShader.SetVector("bucketAccel", bucketAccelWorld);
        pbfComputeShader.SetInt("enableParticleSloshing", enableParticleSloshing ? 1 : 0);
        pbfComputeShader.SetVector("particleSloshVector", sloshLocal);
        pbfComputeShader.SetFloat("particleSloshForce", particleSloshForce);
        pbfComputeShader.SetFloat("particleSloshLiftForce", particleSloshLiftForce);
        pbfComputeShader.SetFloat("particleSloshSurfaceSlope", particleSloshSurfaceSlope);
        pbfComputeShader.SetFloat("particleSloshSurfaceBias", particleSloshSurfaceBias);
        pbfComputeShader.SetInt("particleCount", maxParticles);
        pbfComputeShader.SetInt("maxNeighbors", MAX_NEIGHBORS);
        pbfComputeShader.SetInt("maxPerCell", MAX_PER_CELL);

        pbfComputeShader.SetVector("bucketCenter", Vector3.zero);
        pbfComputeShader.SetVector("bucketUp", Vector3.up);
        pbfComputeShader.SetFloat("bucketRadius", bucketWorldRadius * 0.88f);
        pbfComputeShader.SetFloat("bucketTop", bucketWorldHeight * 0.5f * 0.55f);
        pbfComputeShader.SetFloat("bucketBottom", -bucketWorldHeight * 0.5f + h * 0.4f);

        pbfComputeShader.SetInt("gridSizeX", gridSizeX);
        pbfComputeShader.SetInt("gridSizeY", gridSizeY);
        pbfComputeShader.SetInt("gridSizeZ", gridSizeZ);
        pbfComputeShader.SetVector("gridOrigin", gridOrigin);
        pbfComputeShader.SetFloat("cellSize", cellSize);
    }

    void Dispatch(int kernel)
    {
        int groups = Mathf.CeilToInt(maxParticles / 256f);
        pbfComputeShader.Dispatch(kernel, groups, 1, 1);
    }

    void DispatchGrid(int kernel)
    {
        int groups = Mathf.CeilToInt(totalCells / 256f);
        pbfComputeShader.Dispatch(kernel, groups, 1, 1);
    }

   
    void CPUReadback()
    {
        positionsBuffer.GetData(positionsCPU);
        statesBuffer.GetData(statesCPU);
        velocitiesBuffer.GetData(velocitiesCPU);

        Vector3 holePos = HolePosition;
        float sampleRad = bucketWorldRadius * 0.6f;
        UpdateCachedValues(holePos, sampleRad);
        if (canvasTransform == null) return;

        bool anyChange = false;
        int impactsThisReadback = 0;

        for (int i = 0; i < maxParticles; i++)
        {
            if (statesCPU[i] != FALLING) continue;

            Vector3 wp = positionsCPU[i];
            Vector3 vel = velocitiesCPU[i];

       
            if (!TryGetCanvasImpactPoint(wp, vel, out Vector3 hitWorld, out bool hitInsideCanvas))
                continue;

            if (hitInsideCanvas)
                DrawSplash(hitWorld, vel, i);

            statesCPU[i] = ON_CANVAS;
            anyChange = true;
            impactsThisReadback++;

            if (impactsThisReadback >= maxImpactsPerReadback) break;
        }

        if (anyChange)
            statesBuffer.SetData(statesCPU);

        visualDirty = true; 

      
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


    public void SpawnExitParticle(bool isFromTop)
    {
        if (statesCPU == null || positionsCPU == null || velocitiesCPU == null) return;

        Vector3 exit = isFromTop ? TopPosition : HolePosition;
        Vector3 exitLocal = new Vector3(0f, isFromTop ? bucketWorldHeight * 0.5f * 0.55f : -bucketWorldHeight * 0.5f + h * 0.4f, 0f);
        int bestIdx = -1;
        float bestDist = float.MaxValue;

        for (int attempt = 0; attempt < 90; attempt++)
        {
            int i = Random.Range(0, maxParticles);
            if (statesCPU[i] != INSIDE) continue;

            float d = (positionsCPU[i] - exitLocal).sqrMagnitude;
            if (d < bestDist)
            {
                bestDist = d;
                bestIdx = i;
            }
        }

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

       
        float exitSpread = bucketWorldRadius * 0.045f;
        positionsCPU[bestIdx] = exit + Random.insideUnitSphere * exitSpread;

        Vector3 bucketVel = pendulum != null ? pendulum.GetBucketVelocity() : Vector3.zero;
        Vector3 exitDir = isFromTop ? BucketUp : -BucketUp;
        float exitSpeed = Mathf.Lerp(1.3f, 0.25f, SIGMA / 1.2f);
        velocitiesCPU[bestIdx] = bucketVel + exitDir * exitSpeed + Random.insideUnitSphere * 0.012f;

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

    void DrawSplash(Vector3 worldPos, Vector3 worldVel, int particleIndex)
    {
        if (canvasTransform == null) return;
        paintedSplats++;

        if (!WorldToCanvasPixel(worldPos, out int cx, out int cy, true)) return;

        Color col = GetParticleColor(particleIndex);
        float speed = worldVel.magnitude;

        float surfaceSpread, surfaceSoftness, surfaceAlpha;
        GetSurfacePaintResponse(out surfaceSpread, out surfaceSoftness, out surfaceAlpha);

        float viscSize = Mathf.Lerp(0.8f, 1.7f, Mathf.Clamp01(viscosity));
        int r = Mathf.Max(1, Mathf.RoundToInt(baseBrushSize * surfaceSpread * viscSize * Mathf.Clamp(0.65f + speed * 0.18f, 0.75f, 1.8f)));

        Color main = col;
        main.a = paintDepositStrength * surfaceAlpha;
        if (enableSurfaceEffects)
        {
            Vector3 lv = canvasTransform.InverseTransformVector(worldVel);
            Vector2 dir = new Vector2(lv.x, lv.z);
            float baseAng = dir.sqrMagnitude > 0.0001f ? Mathf.Atan2(dir.y, dir.x) : 0f;
            float forwardness = Mathf.Clamp01(speed * 0.18f);

            int visibleR = Mathf.Clamp(r, 2, 10);
            float dropMul = Mathf.Clamp01(surfaceEffectStrength);
            DrawSurfaceMark(cx, cy, visibleR, baseAng, forwardness, speed, main, surfaceSoftness, dropMul);
        }
        else
        {
            FillCircleSoft(cx, cy, r, main, surfaceSoftness);
        }

       
        if (enablePaintDripping)
            DrawInitialGravityRun(cx, cy, r, col, speed);

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



    Color CanvasBackgroundColor() => new Color(0.95f, 0.92f, 0.85f, 1f);

    void ApplyCanvasTiltTransform()
    {
        if (!enableCanvasTiltControls || canvasTransform == null) return;

        Vector3 e = canvasTransform.localEulerAngles;
       
        canvasTransform.localRotation = Quaternion.Euler(canvasTiltXDeg, e.y, canvasTiltZDeg);
    }

    bool TryGetCanvasDownDirection(out Vector2 dir, out float tilt01)
    {
        dir = Vector2.zero;
        tilt01 = 0f;
        if (canvasTransform == null) return false;

      
        Vector3 gLocal = canvasTransform.InverseTransformDirection(Vector3.down);

   
        Vector2 raw = canvasIsHorizontal
            ? new Vector2(gLocal.x, gLocal.z)
            : new Vector2(gLocal.x, gLocal.y);
        if (invertCanvasDripV)
            raw.y = -raw.y;

        float m = raw.magnitude;
        tilt01 = Mathf.Clamp01(m);

        if (m < 0.025f) return false; 
        dir = raw / m;
        return true;
    }

    float GetPaintDripMobility()
    {
        switch (paintType)
        {
            case PaintType.Ink: return 1.55f;
            case PaintType.Watercolor: return 1.35f;
            case PaintType.Tempera: return 0.82f;
            case PaintType.Acrylic: return 0.62f;
            case PaintType.Gouache: return 0.48f;
            case PaintType.Enamel: return 0.38f;
            case PaintType.OilPaint: return 0.30f;
            case PaintType.Latex: return 0.22f;
            default: return 0.6f;
        }
    }

    void GetSurfaceDripResponse(out float mobility, out float absorb, out float sideSpread)
    {
      
        switch (surfaceType)
        {
            case SurfaceType.Paper:
                mobility = 0.34f; absorb = 0.92f; sideSpread = 0.45f; break;
            case SurfaceType.Cloth:
                mobility = 0.46f; absorb = 0.72f; sideSpread = 0.32f; break;
            case SurfaceType.Wood:
                mobility = 0.70f; absorb = 0.38f; sideSpread = 0.18f; break;
            default: // Metal
                mobility = 1.18f; absorb = 0.08f; sideSpread = 0.08f; break;
        }

        float k = Mathf.Clamp01(surfaceEffectStrength);
        mobility = Mathf.Lerp(0.65f, mobility, k);
        absorb = Mathf.Lerp(0.35f, absorb, k);
        sideSpread = Mathf.Lerp(0.12f, sideSpread, k);
    }

    float PixelPaintAmount(Color c)
    {
        Color bg = CanvasBackgroundColor();
        float d = Mathf.Abs(c.r - bg.r) + Mathf.Abs(c.g - bg.g) + Mathf.Abs(c.b - bg.b);
        return Mathf.Clamp01(d / 1.65f);
    }

    void BlendDripInto(ref Color dst, Color incoming, float strength)
    {
      
        float boosted = Mathf.Clamp01(strength * Mathf.Max(0f, dripMixBoost));
        dst = BlendDripColorOnly(dst, incoming, boosted);
        dst.a = 1f;
    }

    Color BlendDripColorOnly(Color old, Color incoming, float strength)
    {
        strength = Mathf.Clamp01(strength);
        if (!enableLightCanvasMixing)
        {
            Color simple = Color.Lerp(old, incoming, strength);
            simple.a = 1f;
            return simple;
        }

        bool blank = old.r > 0.94f && old.g > 0.94f && old.b > 0.94f;
        if (blank)
        {
            Color deposited = Color.Lerp(old, incoming, Mathf.Clamp01(strength * paintDepositStrength));
            deposited.a = 1f;
            return deposited;
        }

        float coverage = 1f - Mathf.Clamp01((old.r + old.g + old.b) / 3f);
        float overlapBoost = Mathf.Lerp(1.00f, 1.30f, coverage);
        Color pigmentMixed = PigmentMixApprox(old, incoming);
        
        Color target = Color.Lerp(pigmentMixed, incoming, 0.10f * paintDepositStrength);
        float mixAmount = Mathf.Clamp01(strength * canvasMixStrength * overlapBoost + 0.16f);

        bool oldIsConfidentGreen = old.g > 0.35f && old.r < 0.28f && old.b < 0.28f;
        bool oldIsConfidentPurple = old.r > 0.22f && old.r < 0.42f && old.b > 0.38f && old.g < 0.15f;
        bool oldIsConfidentOrange = old.r > 0.75f && old.g > 0.20f && old.g < 0.60f && old.b < 0.10f;
        if (oldIsConfidentGreen || oldIsConfidentPurple || oldIsConfidentOrange)
        {
            pigmentMixed.a = 1f;
            return pigmentMixed;
        }

        Color result = Color.Lerp(old, target, mixAmount);
        result.a = 1f;
        return result;
    }

    void FadeDripSource(ref Color dst, float amount)
    {
        dst = Color.Lerp(dst, CanvasBackgroundColor(), Mathf.Clamp01(amount));
        dst.a = 1f;
    }

    void DepositDripPixel(Color[] buffer, int x, int y, Color col, float strength)
    {
        if (x < 0 || x >= canvasWidth || y < 0 || y >= canvasHeight) return;
        int idx = y * canvasWidth + x;
        BlendDripInto(ref buffer[idx], col, strength);
    }

    void DrawInitialGravityRun(int cx, int cy, int r, Color col, float impactSpeed)
    {
        if (!TryGetCanvasDownDirection(out Vector2 dir, out float tilt01)) return;

        float surfaceMob, absorb, sideSpread;
        GetSurfaceDripResponse(out surfaceMob, out absorb, out sideSpread);

        float viscosityMob = Mathf.Lerp(1.25f, 0.22f, Mathf.Clamp01(viscosity));
        float mobility = dripStrength * GetPaintDripMobility() * surfaceMob * viscosityMob;
        float response = tilt01 * mobility * Mathf.Clamp(0.55f + impactSpeed * 0.12f, 0.55f, 1.7f);
        if (response < dripThreshold) return;

        if (surfaceType == SurfaceType.Wood)
            dir = Vector2.Lerp(dir, new Vector2(Mathf.Sign(dir.x == 0f ? 1f : dir.x), 0f), 0.35f).normalized;

        int length = Mathf.Clamp(Mathf.RoundToInt(r * Mathf.Lerp(1.2f, 5.0f, response)), 1, 42);
        int radius = Mathf.Max(1, Mathf.RoundToInt(r * Mathf.Lerp(0.38f, 0.12f, absorb)));
        Color run = col;
        run.a = paintDepositStrength * Mathf.Lerp(0.28f, 0.62f, 1f - absorb);

        Vector2 perp = new Vector2(-dir.y, dir.x);
        int sideRepeats = surfaceType == SurfaceType.Paper ? 2 : surfaceType == SurfaceType.Cloth ? 1 : 0;

        for (int i = 1; i <= length; i++)
        {
            float t = (float)i / length;
            int x = cx + Mathf.RoundToInt(dir.x * i);
            int y = cy + Mathf.RoundToInt(dir.y * i);
            Color stepCol = run;
            stepCol.a *= (1f - t) * Mathf.Lerp(0.35f, 1f, response);
            FillCircleSoft(x, y, Mathf.Max(1, Mathf.RoundToInt(radius * (1f - t * 0.65f))), stepCol, Mathf.Lerp(0.85f, 0.25f, 1f - absorb));

            for (int s = 1; s <= sideRepeats; s++)
            {
                int off = Mathf.RoundToInt(s * sideSpread * r);
                if (off <= 0) continue;
                Color side = stepCol; side.a *= 0.35f;
                FillCircleSoft(x + Mathf.RoundToInt(perp.x * off), y + Mathf.RoundToInt(perp.y * off), 1, side, 0.9f);
                FillCircleSoft(x - Mathf.RoundToInt(perp.x * off), y - Mathf.RoundToInt(perp.y * off), 1, side, 0.9f);
            }
        }
    }

    void ApplyCanvasPaintDrips()
    {
        lastDripFrame = frameCount;
        if (canvasPx == null || canvasPx.Length == 0 || canvasWidth <= 0 || canvasHeight <= 0) return;
        if (!TryGetCanvasDownDirection(out Vector2 dir, out float tilt01)) return;

        if (dripScratch == null || dripScratch.Length != canvasPx.Length)
            dripScratch = new Color[canvasPx.Length];

        System.Array.Copy(canvasPx, dripScratch, canvasPx.Length);

        float surfaceMob, absorb, sideSpread;
        GetSurfaceDripResponse(out surfaceMob, out absorb, out sideSpread);
        float viscosityMob = Mathf.Lerp(1.25f, 0.22f, Mathf.Clamp01(viscosity));
        float mobility = dripStrength * GetPaintDripMobility() * surfaceMob * viscosityMob;
        float baseFlow = tilt01 * mobility;
        if (baseFlow < dripThreshold * 0.35f) return;

        Vector2 perp = new Vector2(-dir.y, dir.x);
        bool changed = false;

        for (int y = 0; y < canvasHeight; y++)
        {
            for (int x = 0; x < canvasWidth; x++)
            {
                int idx = y * canvasWidth + x;
                Color src = canvasPx[idx];
                float amount = PixelPaintAmount(src);
                if (amount < dripThreshold) continue;

                float flow = amount * baseFlow;
                if (flow < dripThreshold) continue;

                int step = Mathf.Clamp(Mathf.RoundToInt(flow * maxDripPixelsPerStep), 1, Mathf.Max(1, maxDripPixelsPerStep));
                int tx = x + Mathf.RoundToInt(dir.x * step);
                int ty = y + Mathf.RoundToInt(dir.y * step);

                float transfer = Mathf.Clamp(flow * 0.16f, 0.012f, 0.18f);
                float sourceFade = transfer * Mathf.Lerp(0.22f, 1.10f, absorb) * Mathf.Lerp(0.25f, 1f, dripDrying);

                if (tx < 0 || tx >= canvasWidth || ty < 0 || ty >= canvasHeight)
                {
                    FadeDripSource(ref dripScratch[idx], sourceFade * 1.25f);
                    changed = true;
                    continue;
                }

                FadeDripSource(ref dripScratch[idx], sourceFade);
                DepositDripPixel(dripScratch, tx, ty, src, transfer);

                float side = transfer * sideSpread * 0.45f;
                if (side > 0.006f)
                {
                    int sx = Mathf.RoundToInt(perp.x);
                    int sy = Mathf.RoundToInt(perp.y);
                    DepositDripPixel(dripScratch, tx + sx, ty + sy, src, side);
                    DepositDripPixel(dripScratch, tx - sx, ty - sy, src, side);
                }

                changed = true;
            }
        }

        if (changed)
        {
            Color[] tmp = canvasPx;
            canvasPx = dripScratch;
            dripScratch = tmp;
            canvasDirty = true;
        }
    }

    void GetSurfacePaintResponse(out float spread, out float softness, out float alpha)
    {
        switch (surfaceType)
        {
            case SurfaceType.Paper: 
                spread = 1.75f; softness = 0.96f; alpha = 0.58f; break;
            case SurfaceType.Cloth: 
                spread = 1.18f; softness = 0.58f; alpha = 0.88f; break;
            case SurfaceType.Wood: 
                spread = 1.05f; softness = 0.32f; alpha = 0.98f; break;
            default:                
                spread = 0.68f; softness = 0.04f; alpha = 1.00f; break;
        }

        float k = Mathf.Clamp01(surfaceEffectStrength);
        spread = Mathf.Lerp(1.0f, spread, k);
        softness = Mathf.Lerp(0.45f, softness, k);
        alpha = Mathf.Lerp(1.0f, alpha, k);
    }

    void DrawLightSurfaceStamp(int cx, int cy, int r, Color col, Vector3 worldVel, float softness)
    {
        switch (surfaceType)
        {
            case SurfaceType.Paper:
                {
                    Color halo = Color.Lerp(col, Color.white, 0.35f);
                    halo.a = col.a * 0.22f;
                    FillCircleSoft(cx, cy, Mathf.Max(1, Mathf.RoundToInt(r * 1.55f)), halo, 0.95f);
                    FillCircleSoft(cx, cy, r, col, softness);
                    break;
                }

            case SurfaceType.Cloth:
                {
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
                    FillCircleSoft(cx, cy, r, col, 0.08f);
                    Color hi = Color.Lerp(col, Color.white, 0.80f);
                    hi.a = col.a * 0.35f;
                    FillCircle(cx - Mathf.Max(1, r / 3), cy - Mathf.Max(1, r / 3), Mathf.Max(1, r / 4), hi);
                    break;
                }
        }
    }


    void DrawSurfaceMark(int cx, int cy, int mainR, float baseAng, float forwardness,
                         float speed, Color col, float soft, float dropMul)
    {
        switch (surfaceType)
        {
            case SurfaceType.Paper:
                {
                    Color wash = Color.Lerp(col, Color.white, 0.35f); wash.a = col.a * 0.55f; 
                    Color edge = Color.Lerp(col, Color.black, 0.18f); edge.a = col.a * 0.75f; 
                    Color halo = col; halo.a = col.a * 0.12f;

                    FillCircleSoft(cx, cy, Mathf.RoundToInt(mainR * 2.4f), halo, 0.98f);      

                    for (int i = 0; i < 4; i++)                                            
                    {
                        float a = Random.Range(0f, Mathf.PI * 2f);
                        float d = mainR * Random.Range(1.1f, 2.1f);
                        FillCircleSoft(cx + Mathf.RoundToInt(Mathf.Cos(a) * d),
                                       cy + Mathf.RoundToInt(Mathf.Sin(a) * d),
                                       Mathf.Max(Mathf.RoundToInt(mainR * Random.Range(0.25f, 0.55f)), 1), halo, 0.97f);
                    }
                    FillCircleSoft(cx, cy, Mathf.RoundToInt(mainR * 1.3f), edge, 0.7f);       
                    FillCircleSoft(cx, cy, Mathf.RoundToInt(mainR * 1.0f), wash, 0.85f);      
                    for (int i = 0; i < 4; i++)                                              
                    {
                        float a = Random.Range(0f, Mathf.PI * 2f);
                        float d = mainR * Random.Range(0f, 1.1f);
                        FillCircle(cx + Mathf.RoundToInt(Mathf.Cos(a) * d),
                                   cy + Mathf.RoundToInt(Mathf.Sin(a) * d), 1, edge);
                    }
                    break;
                }

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

            case SurfaceType.Wood:
                {
                    for (int k = -4; k <= 4; k++)   
                    {
                        int ox = cx + k * Mathf.Max(Mathf.RoundToInt(mainR * 0.55f), 1);
                        FillCircleSoft(ox, cy, Mathf.Max(Mathf.RoundToInt(mainR * 0.7f), 1), col, 0.45f);
                    }
                    for (int g = 0; g < 3; g++)     
                    {
                        int oy = cy + Random.Range(-mainR, mainR);
                        Color gl = col * Random.Range(0.5f, 0.72f); gl.a = col.a * 0.55f;
                        DrawLinePixels(cx - mainR * 3, oy, cx + mainR * 3, oy, gl);
                    }
                    for (int i = 0; i < 3; i++)     
                    {
                        int ox = cx + Random.Range(-mainR * 3, mainR * 3);
                        int oy = cy + Random.Range(-mainR, mainR);
                        Color dk = col * 0.5f; dk.a = col.a * 0.5f;
                        FillCircle(ox, oy, 1, dk);
                    }
                    break;
                }

         
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

                        Color rim = Color.Lerp(col, Color.black, 0.28f); rim.a = col.a; 
                        FillCircle(bx, by, br + 1, rim);
                        FillCircle(bx, by, br, col);                                    
                        Color hi = Color.Lerp(col, Color.white, 0.85f); hi.a = col.a;
                        int hr = Mathf.Max(br / 4, 1);
                        FillCircle(bx - Mathf.Max(br / 3, 1), by - Mathf.Max(br / 3, 1), hr, hi); 
                    }
                    break;
                }
        }
    }

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
                float shade = (warp ^ weft) ? 1f : 0.55f;        
                if (u == 0 || v == 0) shade *= 0.4f;             
                float a = c.a * 0.85f * shade;
                BlendCanvasPixel(py * canvasWidth + px, c, a);
            }
    }

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

        if (blank)
        {
            canvasPx[index] = Color.Lerp(old, incoming, Mathf.Clamp01(strength * paintDepositStrength));
            return;
        }

       
        float coverage = 1f - Mathf.Clamp01((old.r + old.g + old.b) / 3f);
        float overlapBoost = Mathf.Lerp(1.00f, 1.30f, coverage);

        Color pigmentMixed = PigmentMixApprox(old, incoming);
        Color target = Color.Lerp(pigmentMixed, incoming, 0.18f * paintDepositStrength);

        float mixAmount = Mathf.Clamp01(strength * canvasMixStrength * overlapBoost + 0.10f);

     
        bool oldIsConfidentGreen = old.g > 0.35f && old.r < 0.28f && old.b < 0.28f;
        bool oldIsConfidentPurple = old.r > 0.22f && old.r < 0.42f && old.b > 0.38f && old.g < 0.15f;
        bool oldIsConfidentOrange = old.r > 0.75f && old.g > 0.20f && old.g < 0.60f && old.b < 0.10f;
        if (oldIsConfidentGreen || oldIsConfidentPurple || oldIsConfidentOrange)
        {
            canvasPx[index] = pigmentMixed;
            return;
        }

        canvasPx[index] = Color.Lerp(old, target, mixAmount);
    }

    Color PigmentMixApprox(Color a, Color b)
    {
        bool aRed = a.r > 0.55f && a.g < 0.45f && a.b < 0.45f;
        bool bRed = b.r > 0.55f && b.g < 0.45f && b.b < 0.45f;
        bool aBlue = a.b > 0.50f && a.r < 0.45f && a.g < 0.55f;
        bool bBlue = b.b > 0.50f && b.r < 0.45f && b.g < 0.55f;
        bool aYellow = a.r > 0.65f && a.g > 0.55f && a.b < 0.35f;
        bool bYellow = b.r > 0.65f && b.g > 0.55f && b.b < 0.35f;

      
        bool aPurple = a.r > 0.25f && a.r < 0.70f && a.b > 0.32f && a.g < 0.42f;
        bool bPurple = b.r > 0.25f && b.r < 0.70f && b.b > 0.32f && b.g < 0.42f;
        bool aGreen = a.g > 0.35f && a.r < 0.50f && a.b < 0.55f;
        bool bGreen = b.g > 0.35f && b.r < 0.50f && b.b < 0.55f;
        bool aOrange = a.r > 0.70f && a.g > 0.20f && a.g < 0.62f && a.b < 0.35f;
        bool bOrange = b.r > 0.70f && b.g > 0.20f && b.g < 0.62f && b.b < 0.35f;

        if ((aGreen && (bBlue || bYellow)) || (bGreen && (aBlue || aYellow)))
            return new Color(0.08f, 0.46f, 0.13f, 1f);
        if ((aPurple && (bBlue || bRed)) || (bPurple && (aBlue || aRed)))
            return new Color(0.30f, 0.03f, 0.46f, 1f);
        if ((aOrange && (bRed || bYellow)) || (bOrange && (aRed || aYellow)))
            return new Color(0.92f, 0.48f, 0.02f, 1f);

        if ((aYellow && bBlue) || (aBlue && bYellow))
            return new Color(0.08f, 0.46f, 0.13f, 1f); 

        if ((aRed && bBlue) || (aBlue && bRed))
            return new Color(0.30f, 0.03f, 0.46f, 1f);

        if ((aRed && bYellow) || (aYellow && bRed))
            return new Color(0.92f, 0.48f, 0.02f, 1f); 

        Color avg = (a + b) * 0.5f;
        avg.a = 1f;
        Color softened = new Color(
            Mathf.Clamp01(avg.r * 0.96f),
            Mathf.Clamp01(avg.g * 0.96f),
            Mathf.Clamp01(avg.b * 0.96f),
            1f
        );
       
        return Color.Lerp(softened, b, 0.12f);
    }


    public void UpdateTrail(Vector3 pointWorld, float thickness)
    {
        if (canvasTransform == null) return;
        if (!IsPointOnCanvas(pointWorld)) { hasLastTrailPoint = false; return; }

        int r = Mathf.Max(1, Mathf.RoundToInt(thickness));
        if (hasLastTrailPoint)
            DrawLineOnCanvas(lastTrailPoint, pointWorld, r, CurrentPaintColor());
        else
        {
            if (WorldToCanvasPixel(pointWorld, out int px, out int py, true))
                FillCircle(px, py, r, CurrentPaintColor());
        }

        lastTrailPoint = pointWorld;
        hasLastTrailPoint = true;
        canvasDirty = true;
    }

    public void BreakTrail() => hasLastTrailPoint = false;

 
    void Update()
    {
        if (pendulum != null && pendulum.IsRunning) motionElapsed += Time.deltaTime;
        if (Input.GetKeyDown(KeyCode.F9)) SaveExperiment();

        ApplyCanvasTiltTransform();

        if (enablePaintDripping && canvasPx != null && frameCount - lastDripFrame >= Mathf.Max(1, dripEveryNFrames))
            ApplyCanvasPaintDrips();

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

    void FillCircleSoft(int cx, int cy, int r, Color c, float softness)
    {
        if (r < 1) r = 1;
        float inner = 1f - Mathf.Clamp01(softness);
        float fade = Mathf.Max(1f - inner, 1e-3f);
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                float dist = Mathf.Sqrt(dx * dx + dy * dy) / r; 
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
        if (!WorldToCanvasPixel(fromW, out int ax, out int ay, true)) return;
        if (!WorldToCanvasPixel(toW, out int bx, out int by, true)) return;

        int steps = Mathf.Min(Mathf.Max(Mathf.Abs(bx - ax), Mathf.Abs(by - ay), 1), 300);
        for (int s = 0; s <= steps; s++)
        {
            float t = (float)s / steps;
            FillCircle(Mathf.RoundToInt(Mathf.Lerp(ax, bx, t)),
                       Mathf.RoundToInt(Mathf.Lerp(ay, by, t)), radius, c);
        }
        canvasDirty = true;
    }

    float CanvasPlaneCoord(Vector3 localPoint)
        => canvasIsHorizontal ? localPoint.y : localPoint.z;

    float CanvasVCoord(Vector3 localPoint)
        => canvasIsHorizontal ? localPoint.z : localPoint.y;

    bool IsLocalPointOnCanvas(Vector3 localPoint)
    {
        float u = localPoint.x;
        float v = CanvasVCoord(localPoint);
        return Mathf.Abs(u) <= canvasHalfX && Mathf.Abs(v) <= canvasHalfZ;
    }

    bool WorldToCanvasPixel(Vector3 worldPos, out int px, out int py, bool clampToCanvas)
    {
        px = 0;
        py = 0;
        if (canvasTransform == null) return false;

        Vector3 lp = canvasTransform.InverseTransformPoint(worldPos);
        float u = lp.x;
        float v = CanvasVCoord(lp);

        if (!clampToCanvas && (Mathf.Abs(u) > canvasHalfX || Mathf.Abs(v) > canvasHalfZ))
            return false;

        float nx = Mathf.Clamp01((u + canvasHalfX) / Mathf.Max(canvasHalfX * 2f, 0.0001f));
        float ny = Mathf.Clamp01((v + canvasHalfZ) / Mathf.Max(canvasHalfZ * 2f, 0.0001f));

        px = Mathf.RoundToInt(nx * (canvasWidth - 1));
        py = Mathf.RoundToInt(ny * (canvasHeight - 1));

        if (flipMarkU) px = canvasWidth - 1 - px;
        if (flipMarkV) py = canvasHeight - 1 - py;
        return true;
    }

    public Vector3 ProjectOntoCanvas(Vector3 worldPos)
    {
        Vector3 lp = canvasTransform.InverseTransformPoint(worldPos);
        if (canvasIsHorizontal) lp.y = 0f; else lp.z = 0f;
        return canvasTransform.TransformPoint(lp);
    }

    bool TryGetCanvasImpactPoint(Vector3 currentWorld, Vector3 worldVel, out Vector3 hitWorld, out bool hitInsideCanvas)
    {
        hitWorld = currentWorld;
        hitInsideCanvas = false;
        if (canvasTransform == null) return false;

        Vector3 curLocal = canvasTransform.InverseTransformPoint(currentWorld);
        float curPlane = CanvasPlaneCoord(curLocal);

        if (!useTrajectoryCanvasProjection || worldVel.sqrMagnitude < 0.000001f)
        {
            if (Mathf.Abs(curPlane) > canvasImpactTolerance && curPlane > 0f) return false;
            Vector3 projected = curLocal;
            if (canvasIsHorizontal) projected.y = 0f; else projected.z = 0f;
            hitWorld = canvasTransform.TransformPoint(projected);
            hitInsideCanvas = IsLocalPointOnCanvas(projected);
            return true;
        }

        float backDt = Time.fixedDeltaTime * Mathf.Max(1, readbackInterval) * impactBacktrackMultiplier;
        Vector3 prevWorld = currentWorld - worldVel * backDt;
        Vector3 prevLocal = canvasTransform.InverseTransformPoint(prevWorld);
        float prevPlane = CanvasPlaneCoord(prevLocal);

        bool crossedPlane = (prevPlane > 0f && curPlane <= canvasImpactTolerance) ||
                            (prevPlane < 0f && curPlane >= -canvasImpactTolerance) ||
                            (prevPlane * curPlane <= 0f);

        bool closeOrAlreadyPassed = Mathf.Abs(curPlane) <= canvasImpactTolerance || curPlane < 0f;
        if (!crossedPlane && !closeOrAlreadyPassed)
            return false;

        Vector3 hitLocal;
        float denom = prevPlane - curPlane;
        if (Mathf.Abs(denom) > 0.00001f)
        {
            float t = Mathf.Clamp01(prevPlane / denom);
            hitLocal = Vector3.Lerp(prevLocal, curLocal, t);
        }
        else
        {
            hitLocal = curLocal;
        }

        if (canvasIsHorizontal) hitLocal.y = 0f; else hitLocal.z = 0f;

        hitWorld = canvasTransform.TransformPoint(hitLocal);
        hitInsideCanvas = IsLocalPointOnCanvas(hitLocal);
        return true;
    }

    public bool IsPointOnCanvas(Vector3 worldPos)
    {
        Vector3 lp = canvasTransform.InverseTransformPoint(worldPos);
        return IsLocalPointOnCanvas(lp);
    }


    public int InsideCount()
    {
        return cpuDataReady ? cachedInsideCount : maxParticles;
    }

    public float GetLiquidHeightAtHole(Vector3 holeWorldPos, float sampleRadius)
    {
      
        return cpuDataReady ? cachedLiquidHeight : bucketWorldHeight;
    }

    
    void UpdateCachedValues(Vector3 holeWorldPos, float sampleRadius)
    {
        int insideC = 0;
        for (int i = 0; i < maxParticles; i++)
            if (statesCPU[i] == INSIDE) insideC++;

        cachedInsideCount = insideC;

        float fillRatio = (float)insideC / Mathf.Max(maxParticles, 1);
        cachedLiquidHeight = fillRatio * bucketWorldHeight;

       
        if (verboseDebugLogs && frameCount % 300 == 0)
            Debug.Log($"[PBF-Cache F={frameCount}] " +
                      $"Inside={insideC}/{maxParticles} | " +
                      $"Fill={fillRatio:F3} | " +
                      $"LiqH={cachedLiquidHeight:F3}");

        cpuDataReady = true;
    }

    public void RefillBucket()
    {
      
        SpawnParticles();
        CalibrateRestDensityScale();
        cpuDataReady = false;
    }

    public void ClearCanvas()
    {
        var bg = CanvasBackgroundColor();
        for (int i = 0; i < canvasPx.Length; i++) canvasPx[i] = bg;
        canvasTex.SetPixels(canvasPx);
        canvasTex.Apply();
        BreakTrail();
        paintedSplats = 0;
        motionElapsed = 0f;
        releasedPaintParticles = 0;
        lastCanvasApplyFrame = frameCount;
        lastDripFrame = frameCount;
    }

    public void SaveCanvas(string path = "PaintResult.png")
        => System.IO.File.WriteAllBytes(path, canvasTex.EncodeToPNG());

    public float CoverageArea(out float percent)
    {
        var bg = CanvasBackgroundColor();
        int painted = 0;
        for (int i = 0; i < canvasPx.Length; i++)
        {
            Color c = canvasPx[i];
            if (Mathf.Abs(c.r - bg.r) + Mathf.Abs(c.g - bg.g) + Mathf.Abs(c.b - bg.b) > 0.06f)
                painted++;
        }
        percent = 100f * painted / canvasPx.Length;

        float sx = canvasTransform ? Mathf.Abs(canvasTransform.lossyScale.x) : 1f;
        float sv = canvasTransform
            ? Mathf.Abs(canvasIsHorizontal ? canvasTransform.lossyScale.z : canvasTransform.lossyScale.y)
            : 1f;
        float pixelArea = (2f * canvasHalfX * sx / canvasWidth) * (2f * canvasHalfZ * sv / canvasHeight);
        return painted * pixelArea;
    }

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
        float sv = canvasTransform
            ? Mathf.Abs(canvasIsHorizontal ? canvasTransform.lossyScale.z : canvasTransform.lossyScale.y)
            : 1f;
        sb.AppendLine($"    Physical size       : {2f * canvasHalfX * sx:F2} x {2f * canvasHalfZ * sv:F2} m");
        if (canvasTransform != null)
        {
            Vector3 cLocal = canvasTransform.localPosition;
            Vector3 cWorld = canvasTransform.position;
            sb.AppendLine($"    Local position      : X={cLocal.x:F2}, Y={cLocal.y:F2}, Z={cLocal.z:F2}");
            sb.AppendLine($"    World position      : X={cWorld.x:F2}, Y={cWorld.y:F2}, Z={cWorld.z:F2}");
        }
        sb.AppendLine($"    Orientation         : {(canvasIsHorizontal ? "Horizontal" : "Vertical")}");
        sb.AppendLine($"    Tilt X / Z          : {canvasTiltXDeg:F1} deg / {canvasTiltZDeg:F1} deg");
        sb.AppendLine($"    Dripping enabled    : {enablePaintDripping}");
        sb.AppendLine($"    Drip strength       : {dripStrength:F2}");
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

    void InitCanvas()
    {
        canvasTex = new Texture2D(canvasWidth, canvasHeight, TextureFormat.RGBA32, false);
        canvasPx = new Color[canvasWidth * canvasHeight];
        dripScratch = new Color[canvasWidth * canvasHeight];
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
        main.maxParticles = Mathf.Min(maxParticles, 50000) + 100;
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


    public void ApplyPaintType()
    {
        switch (paintType)
        {
            case PaintType.Watercolor:
                restDensity = 3f; SIGMA = 0.04f; gravityScale = 0.9f; bucketInfluence = 0.6f; viscosity = 0.18f; break;
            case PaintType.Acrylic:
                restDensity = 4.5f; SIGMA = 0.3f; gravityScale = 0.75f; bucketInfluence = 0.45f; viscosity = 0.30f; break;
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