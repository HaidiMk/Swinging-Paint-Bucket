using UnityEngine;



public class BucketHole : MonoBehaviour
{
    [Header("References")]
    public SphericalPendulum pendulum;
    public PBFSolver paintSim;   

    [Header("Hole Settings — إعدادات الفتحة")]
    [Range(0.005f, 0.05f)]
    public float holeDiameter = 0.02f;

    [Range(0f, 0.3f)]
    public float holeHeightFromBottom = 0f;

    [Range(5f, 45f)]
    public float spillAngleThreshold = 15f;

    [Range(45f, 100f)]
    public float overflowAngleThreshold = 60f;

    [Header("Flow Rate — معدل التدفق")]
    [Range(1f, 100f)]
    public float particlesPerSecond = 15f;

    [Range(1f, 50f)]
    public float overflowParticlesPerSecond = 8f;

    [Header("Velocity Boost")]
    [Range(0f, 5f)]
    public float velocityBoostFactor = 1.5f;

    [Header("Torricelli Flow")]
    public bool enableTorricelli = true;

    [Header("Trail — المسار المتصل")]
    public bool enableTrail = true;
    [Range(1f, 5f)] public float trailMinThickness = 1.5f;
    [Range(2f, 20f)] public float trailMaxThickness = 8f;

    [Header("Debug")]
    [Tooltip("فعّلها فقط وقت التشخيص. إطفاؤها أفضل للأداء مع أعداد كبيرة.")]
    public bool showLog = false;
    [Tooltip("عدد الفريمات بين كل Log إذا showLog مفعلة.")]
    [Range(60, 1800)] public int logEveryFrames = 600;

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
        float velBoost = 1f + bucketSpeed * velocityBoostFactor;

        float torricelliFactor = 1f;
        float fillRatio = 1f;

        if (enableTorricelli)
        {
            fillRatio = Mathf.Clamp01(paintSim.InsideCount() / (float)paintSim.maxParticles);

            Vector3 holeWorldPos = paintSim.HolePosition;
            float sampleRadius = paintSim.bucketWorldRadius * 0.6f;
            float liquidHeight = paintSim.GetLiquidHeightAtHole(holeWorldPos, sampleRadius);
            float effectiveH = liquidHeight - holeHeightFromBottom;

            if (effectiveH <= 0f)
            {
                torricelliFactor = 0f;
            }
            else
            {
                float v = Mathf.Sqrt(2f * paintSim.G * effectiveH);
                float maxEffH = Mathf.Max(paintSim.bucketWorldHeight - holeHeightFromBottom, 0.0001f);
                float vFull = Mathf.Sqrt(2f * paintSim.G * maxEffH);
                torricelliFactor = Mathf.Clamp01(v / vFull);
            }
        }

        if (tiltDeg > spillAngleThreshold)
        {
            float spillFactor = Mathf.InverseLerp(spillAngleThreshold, 90f, tiltDeg);
            float holeArea = Mathf.PI * (holeDiameter / 2f) * (holeDiameter / 2f);
            float baseArea = Mathf.PI * (0.02f / 2f) * (0.02f / 2f);
            float areaFactor = holeArea / baseArea;

            float viscFlow = Mathf.Lerp(1.2f, 0.15f, Mathf.Clamp01(paintSim.viscosity));
            float rate = particlesPerSecond * spillFactor * areaFactor * velBoost * torricelliFactor * viscFlow;
            spawnTimer += rate * dt;

            while (spawnTimer >= 1f)
            {
                paintSim.SpawnExitParticle(false);
                spawnTimer -= 1f;
                totalSpawned++;
            }

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
            if (enableTrail) paintSim.BreakTrail();
        }

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
        else overflowTimer = 0f;

        if (showLog && frameCount % Mathf.Max(60, logEveryFrames) == 0)
            Debug.Log($"[BucketHole] Tilt={tiltDeg:F1}° | Speed={bucketSpeed:F2} " +
                      $"| Torricelli={torricelliFactor:F2} | Spawned={totalSpawned}");
    }

    void OnDrawGizmos()
    {
        if (pendulum == null || paintSim == null) return;
        Vector3 holePos = paintSim.HolePosition + Vector3.up * holeHeightFromBottom;
        Gizmos.color = holeOpen ? Color.yellow : Color.gray;
        Gizmos.DrawWireSphere(holePos, holeDiameter * 0.5f);
        Gizmos.color = Color.red;
        Gizmos.DrawRay(holePos, Vector3.down * 0.1f);
    }
}