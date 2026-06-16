using UnityEngine;

// ════════════════════════════════════════════════════════════════════
//  BucketHole.cs  v2.0 + Torricelli
//  إصلاحات عن v1.0:
//  [FIX-1] معدل الخروج صار يعتمد على عدد الجزيئات الحالي
//          يعني لو الدلو فيه 5000 جسيم، يطلع أكثر من لو فيه 100
//  [FIX-2] أضفنا velocityBoost — لما الدلو يتحرك بسرعة يطلع أكثر
//  [FIX-3] logging كل 120 فريم يوضح كم طلع
//  [TORRICELLI] التدفق يعتمد على ارتفاع السائل المتبقي v=sqrt(2gh)
// ════════════════════════════════════════════════════════════════════

public class BucketHole : MonoBehaviour
{
    [Header("References")]
    public SphericalPendulum pendulum;
    public PaintSimulation paintSim;

    [Header("Hole Settings — إعدادات الفتحة")]
    [Range(0.005f, 0.05f)]
    public float holeDiameter = 0.02f;

    [Tooltip("ارتفاع الفتحة عن قاع الدلو (متر) — 0 = الفتحة بالقاع تماماً\nكل ما زاد، أسرع ما يتوقف التدفق لأن السائل بيوصل لمستوى الفتحة بسرعة")]
    [Range(0f, 0.3f)]
    public float holeHeightFromBottom = 0f;

    [Tooltip("زاوية الميل اللي تبدأ بعدها يطلع طلاء من الفتحة (درجات)")]
    [Range(5f, 45f)]
    public float spillAngleThreshold = 15f;

    [Tooltip("زاوية الميل اللي يبدأ بعدها overflow من الأعلى (درجات)")]
    [Range(45f, 100f)]
    public float overflowAngleThreshold = 60f;

    [Header("Flow Rate — معدل التدفق")]
    [Range(1f, 100f)]
    public float particlesPerSecond = 15f;

    [Range(1f, 50f)]
    public float overflowParticlesPerSecond = 8f;

    [Header("Velocity Boost — تأثير سرعة الدلو")]
    [Tooltip("كلما تحرك الدلو بسرعة أكبر، طلع طلاء أكثر")]
    [Range(0f, 5f)]
    public float velocityBoostFactor = 1.5f;

    [Header("Torricelli Flow — تدفق توريتشيلي")]
    [Tooltip("يخلي معدل التدفق يعتمد على ارتفاع السائل المتبقي v=sqrt(2gh)\nيعني كل ما الدلو يفضى، التدفق يقل تدريجياً بشكل واقعي")]
    public bool enableTorricelli = true;

    [Header("Trail — المسار المتصل")]
    [Tooltip("رسم خط متصل يتبع نقطة خروج الطلاء (مسار لولبي حقيقي)")]
    public bool enableTrail = true;

    [Tooltip("أقل وأكبر سماكة للمسار بالبكسل")]
    [Range(1f, 5f)] public float trailMinThickness = 1.5f;
    [Range(2f, 20f)] public float trailMaxThickness = 8f;

    [Header("Debug")]
    public bool showLog = true;

    float spawnTimer = 0f;
    float overflowTimer = 0f;
    int totalSpawned = 0;
    int frameCount = 0;

    [HideInInspector] public bool holeOpen = true;

    void FixedUpdate()
    {
        if (pendulum == null || paintSim == null) return;
        if (!holeOpen) return;

        float dt = Time.fixedDeltaTime;
        frameCount++;

        float tiltDeg = pendulum.theta * Mathf.Rad2Deg;
        float bucketSpeed = pendulum.GetBucketVelocity().magnitude;

        // [FIX-2] boost بناءً على سرعة الدلو
        float velBoost = 1f + bucketSpeed * velocityBoostFactor;

        // ── [TORRICELLI - REAL SPH] التدفق يعتمد على ارتفاع السائل الفعلي فوق الفتحة ──
        // v = sqrt(2*g*h)  حيث h محسوبة من مواقع الجزيئات الفعلية قرب الفتحة
        // (مش من fillRatio الكلي) — هيك لو الدلو مايل واتجمع السائل
        // عند جهة الفتحة، بيطلع تدفق حقيقي حتى لو الكمية الكلية المتبقية قليلة
        float torricelliFactor = 1f;
        float fillRatio = 1f;
        bool belowHoleLevel = false;

        if (enableTorricelli)
        {
            fillRatio = Mathf.Clamp01(paintSim.InsideCount() / (float)paintSim.maxParticles);

            Vector3 holeWorldPos = paintSim.HolePosition;
            // نطاق العمود العمودي اللي بنفحص فيه وجود سائل فوق الفتحة
            float sampleRadius = paintSim.bucketWorldRadius * 0.6f;

            // الارتفاع الفعلي للسائل فوق نقطة الفتحة (من بيانات SPH الحقيقية)
            float liquidHeightAtHole = paintSim.GetLiquidHeightAtHole(holeWorldPos, sampleRadius);

            // الضغط الفعّال = ارتفاع السائل الحقيقي - ارتفاع الفتحة عن القاع
            float effectiveHeight = liquidHeightAtHole - holeHeightFromBottom;

            if (effectiveHeight <= 0f)
            {
                // ما في سائل فعلي فوق الفتحة — توقف كامل، لا تدفق
                torricelliFactor = 0f;
                belowHoleLevel = true;
            }
            else
            {
                float v = Mathf.Sqrt(2f * paintSim.G * effectiveHeight);

                // التطبيع بالنسبة لحالة "الدلو ملآن" حتى تبقى القيم الافتراضية
                // لـ particlesPerSecond متوافقة كالسابق
                float maxEffectiveHeight = Mathf.Max(paintSim.bucketWorldHeight - holeHeightFromBottom, 0.0001f);
                float vFull = Mathf.Sqrt(2f * paintSim.G * maxEffectiveHeight);

                torricelliFactor = Mathf.Clamp01(v / vFull);
            }
        }

        // ── 1) خروج من الفتحة السفلية ────────────────────────────
        if (tiltDeg > spillAngleThreshold)
        {
            float spillFactor = Mathf.InverseLerp(spillAngleThreshold, 90f, tiltDeg);

            float holeArea = Mathf.PI * (holeDiameter / 2f) * (holeDiameter / 2f);
            float baseHoleArea = Mathf.PI * (0.02f / 2f) * (0.02f / 2f);
            float areaFactor = holeArea / baseHoleArea;

            float rate = particlesPerSecond * spillFactor * areaFactor * velBoost * torricelliFactor;
            spawnTimer += rate * dt;

            while (spawnTimer >= 1f)
            {
                paintSim.SpawnExitParticle(false);
                spawnTimer -= 1f;
                totalSpawned++;
            }

            // ── [TRAIL] رسم خط متصل يتبع موقع الدلو فوق اللوحة ────
            if (enableTrail)
            {
                Vector3 holePos = paintSim.HolePosition;
                Vector3 trailPoint = paintSim.ProjectOntoCanvas(holePos);

                float thickness = Mathf.Lerp(trailMinThickness, trailMaxThickness, spillFactor)
                                 * Mathf.Sqrt(areaFactor);

                paintSim.UpdateTrail(trailPoint, thickness);
            }
        }
        else
        {
            spawnTimer = 0f;
            if (enableTrail) paintSim.BreakTrail(); // رفع القلم — pen up
        }

        // ── 2) overflow من الأعلى ─────────────────────────────────
        if (tiltDeg > overflowAngleThreshold)
        {
            float overflowFactor = Mathf.InverseLerp(overflowAngleThreshold, 150f, tiltDeg);
            float rate = overflowParticlesPerSecond * overflowFactor * velBoost * torricelliFactor;
            overflowTimer += rate * dt;

            while (overflowTimer >= 1f)
            {
                paintSim.SpawnExitParticle(true);
                overflowTimer -= 1f;
                totalSpawned++;
            }
        }
        else
        {
            overflowTimer = 0f;
        }

        // [FIX-3] logging كل 120 فريم
        if (showLog && frameCount % 120 == 0)
        {
            Debug.Log(
                "[BucketHole F=" + frameCount + "] " +
                "Tilt=" + tiltDeg.ToString("F1") + "deg | Speed=" + bucketSpeed.ToString("F2") + " | " +
                "VelBoost=" + velBoost.ToString("F2") + " | TotalSpawned=" + totalSpawned + " | " +
                "Inside=" + paintSim.InsideCount() + " | " +
                "FillRatio=" + fillRatio.ToString("F2") + " | TorricelliFactor=" + torricelliFactor.ToString("F2") + " | " +
                "BelowHoleLevel=" + belowHoleLevel
            );
        }
    }

    void OnDrawGizmos()
    {
        if (pendulum == null || paintSim == null) return;

        // الفتحة الفعلية = موقع القاع + ارتفاعها عن القاع
        Vector3 holePos = paintSim.HolePosition + Vector3.up * holeHeightFromBottom;

        Gizmos.color = holeOpen ? Color.yellow : Color.gray;
        Gizmos.DrawWireSphere(holePos, holeDiameter * 0.5f);

        Gizmos.color = Color.red;
        Gizmos.DrawRay(holePos, Vector3.down * 0.1f);

        // خط يوضح ارتفاع الفتحة عن القاع
        if (holeHeightFromBottom > 0.001f)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(paintSim.HolePosition, holePos);
        }
    }
}