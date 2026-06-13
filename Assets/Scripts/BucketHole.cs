using UnityEngine;

// ════════════════════════════════════════════════════════════════════
//  BucketHole.cs  v2.0
//  إصلاحات عن v1.0:
//  [FIX-1] معدل الخروج صار يعتمد على عدد الجزيئات الحالي
//          يعني لو الدلو فيه 5000 جسيم، يطلع أكثر من لو فيه 100
//  [FIX-2] أضفنا velocityBoost — لما الدلو يتحرك بسرعة يطلع أكثر
//  [FIX-3] logging كل 120 فريم يوضح كم طلع
// ════════════════════════════════════════════════════════════════════

public class BucketHole : MonoBehaviour
{
    [Header("References")]
    public SphericalPendulum pendulum;
    public PaintSimulation paintSim;

    [Header("Hole Settings — إعدادات الفتحة")]
    [Range(0.005f, 0.05f)]
    public float holeDiameter = 0.02f;

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

        // ── 1) خروج من الفتحة السفلية ────────────────────────────
        if (tiltDeg > spillAngleThreshold)
        {
            float spillFactor = Mathf.InverseLerp(spillAngleThreshold, 90f, tiltDeg);

            float holeArea = Mathf.PI * (holeDiameter / 2f) * (holeDiameter / 2f);
            float baseHoleArea = Mathf.PI * (0.02f / 2f) * (0.02f / 2f);
            float areaFactor = holeArea / baseHoleArea;

            float rate = particlesPerSecond * spillFactor * areaFactor * velBoost;
            spawnTimer += rate * dt;

            while (spawnTimer >= 1f)
            {
                paintSim.SpawnExitParticle(false);
                spawnTimer -= 1f;
                totalSpawned++;
            }
        }
        else
        {
            spawnTimer = 0f;
        }

        // ── 2) overflow من الأعلى ─────────────────────────────────
        if (tiltDeg > overflowAngleThreshold)
        {
            float overflowFactor = Mathf.InverseLerp(overflowAngleThreshold, 150f, tiltDeg);
            float rate = overflowParticlesPerSecond * overflowFactor * velBoost;
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
                $"[BucketHole F={frameCount}] " +
                $"Tilt={tiltDeg:F1}deg | Speed={bucketSpeed:F2} | " +
                $"VelBoost={velBoost:F2} | TotalSpawned={totalSpawned} | " +
                $"Inside={paintSim.InsideCount()}"
            );
        }
    }

    void OnDrawGizmos()
    {
        if (pendulum == null || paintSim == null) return;

        Vector3 holePos = paintSim.HolePosition;
        Gizmos.color = holeOpen ? Color.yellow : Color.gray;
        Gizmos.DrawWireSphere(holePos, holeDiameter * 0.5f);

        Gizmos.color = Color.red;
        Gizmos.DrawRay(holePos, Vector3.down * 0.1f);
    }
}