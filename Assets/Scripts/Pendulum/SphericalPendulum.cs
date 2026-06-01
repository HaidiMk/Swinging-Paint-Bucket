using UnityEngine;

public class SphericalPendulum : MonoBehaviour
{
    public Transform bucket;
    public Transform pivot;

    // ═══════════════════════════════════════════════
    [Header("Bucket Properties — خصائص الدلو")]
    // ═══════════════════════════════════════════════
    public float bucketMass = 1.0f;
    public float bucketRadius = 0.1f;

    // ═══════════════════════════════════════════════
    [Header("Bucket Vibration — اهتزاز الدلو")]
    // ═══════════════════════════════════════════════
    public bool enableBucketVibration = true;
    public float vibrationAmplitude = 0.03f;
    public float vibrationFrequency = 12f;
    public float vibrationDamping = 2f;

    float vibrationPhase;
    float vibrationStrength;

    // ═══════════════════════════════════════════════
    [Header("Rope Properties — خصائص التعليق")]
    // ═══════════════════════════════════════════════
    public Vector3 pivotPosition = new Vector3(0f, 3f, 0f);
    public float restLength = 1.5f;
    public float ropeStiffness = 10000f;
    public float radialDamping = 0.1f;

    // ═══════════════════════════════════════════════
    [Header("Rope Twist — فتل الحبل")]
    // ═══════════════════════════════════════════════
    public bool enableRopeTwist = true;
    public float ropeTwistStiffness = 8f;
    public float ropeTwistDamping = 1f;

    float ropeTwistAngle;
    float ropeTwistVelocity;

    float r;
    float rVel;

    // ═══════════════════════════════════════════════
    [Header("Initial Conditions — خصائص الحركة")]
    // ═══════════════════════════════════════════════
    [Range(0.01f, 179f)] public float thetaDeg = 45f;
    [Range(0f, 360f)] public float phiDeg = 0f;
    public float thetaVel0 = 0f;
    public float phiVel0 = 0f;

    float theta, phi, thetaVel, phiVel;

    // ═══════════════════════════════════════════════
    [Header("Environment — خصائص البيئة")]
    // ═══════════════════════════════════════════════
    public float gravity = 9.81f;
    public float angularDamping = 0.05f;
    public float airDragCoeff = 0.01f;
    [Range(0f, 1f)]
    public float humidity = 0.5f;
    public Vector3 windForce = Vector3.zero;

    // ═══════════════════════════════════════════════
    [Header("Simulation Control — التحكم بالمحاكاة")]
    // ═══════════════════════════════════════════════
    public int n_swings = 10;
    public float floorY = -1.5f;
    public float bounce = 0.3f;

    // ═══════════════════════════════════════════════
    [Header("Camera Follow — تتبع الكاميرا")]
    // ═══════════════════════════════════════════════
    public bool enableCameraFollow = true;
    public Camera mainCamera;
    public Vector3 cameraOffset = new Vector3(0f, 1f, -5f);
    public float cameraSmoothing = 5f;

    // ═══════════════════════════════════════════════
    [Header("Trail — مسار الدلو")]
    // ═══════════════════════════════════════════════
    public bool showTrail = true;
    public float trailTime = 5f;
    public float trailStartWidth = 0.05f;
    public float trailEndWidth = 0.005f;
    public Color trailColor = new Color(1f, 0.4f, 0.1f, 1f);

    TrailRenderer trail;

    // ═══════════════════════════════════════════════
    // متغيرات داخلية
    // ═══════════════════════════════════════════════
    int zeroCrossCount = 0;
    float prevThetaVel;
    bool simRunning = true;
    string stopReason = "—";
    float energyAtStart = 0f;
    float maxEnergyDrift = 0f;
    int nanFrameCount = 0;

    // ════════════════════════════════════════════════════════════════
    void Start()
    {
        if (pivot != null) pivot.position = pivotPosition;

        theta = thetaDeg * Mathf.Deg2Rad;
        phi = phiDeg * Mathf.Deg2Rad;
        thetaVel = thetaVel0;
        phiVel = phiVel0;
        r = restLength;
        rVel = 0f;

        prevThetaVel = thetaVel;
        SetupTrail();

        if (enableCameraFollow && mainCamera == null)
            mainCamera = Camera.main;

        UpdatePosition();
        energyAtStart = TotalEnergy();

        Debug.Log($"[Pendulum] Start → pivot={pivotPosition} | " +
                  $"bucket={bucket.position} | floorY={floorY} | E0={energyAtStart:F3} J");
    }

    // ════════════════════════════════════════════════════════════════
    void FixedUpdate()
    {
        if (!simRunning) return;

        float dt = Time.fixedDeltaTime;

        RK4Step(dt);

        // ── التأثيرات البصرية الثانوية (فوق RK4 — لا تؤثر على الفيزياء الأساسية)
        UpdateSecondaryEffects(dt);

        // ── حماية NaN ────────────────────────────────────────────────
        if (HasNaN())
        {
            nanFrameCount++;
            Debug.LogError($"[Pendulum] NaN at frame {Time.frameCount}! " +
                           $"θ={theta:F3} φ={phi:F3} r={r:F3}");
            theta = Mathf.Clamp(float.IsNaN(theta) ? 0.5f : theta, 0.01f, Mathf.PI - 0.01f);
            phi = float.IsNaN(phi) ? 0f : phi;
            r = float.IsNaN(r) ? restLength : Mathf.Max(r, 0.2f);
            thetaVel = float.IsNaN(thetaVel) ? 0f : Mathf.Clamp(thetaVel, -20f, 20f);
            phiVel = float.IsNaN(phiVel) ? 0f : Mathf.Clamp(phiVel, -20f, 20f);
            rVel = float.IsNaN(rVel) ? 0f : Mathf.Clamp(rVel, -10f, 10f);
            if (nanFrameCount >= 3) { StopSimulation("NaN persistent"); return; }
        }
        else nanFrameCount = 0;

        // ── حماية theta ──────────────────────────────────────────────
        if (theta < 0.001f)
        {
            theta = 0.001f;
            thetaVel = Mathf.Abs(thetaVel);
        }
        else if (theta > Mathf.PI - 0.001f)
        {
            theta = Mathf.PI - 0.001f;
            thetaVel = -Mathf.Abs(thetaVel);
        }

        // ── حماية r ──────────────────────────────────────────────────
        if (r < 0.2f) { r = 0.2f; rVel = Mathf.Abs(rVel); }

        UpdatePosition();

        if (prevThetaVel * thetaVel < 0f) zeroCrossCount++;
        prevThetaVel = thetaVel;

        HandleCollision();
        CheckStopConditions();

        // ── تتبع انحراف الطاقة ───────────────────────────────────────
        if (angularDamping == 0f && airDragCoeff == 0f)
        {
            float drift = Mathf.Abs(TotalEnergy() - energyAtStart)
                        / Mathf.Max(energyAtStart, 0.001f) * 100f;
            if (drift > maxEnergyDrift) maxEnergyDrift = drift;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // التأثيرات الثانوية — Visual Physics Layer
    // مستقلة تماماً عن RK4 — لا تؤثر على theta/phi/r
    // ════════════════════════════════════════════════════════════════
    void UpdateSecondaryEffects(float dt)
    {
        // ── اهتزاز الدلو ─────────────────────────────────────────────
        if (enableBucketVibration)
        {
            // الاهتزاز يزيد مع السرعات الزاوية
            float accel = Mathf.Abs(thetaVel)
                        + Mathf.Abs(phiVel)
                        + Mathf.Abs(rVel);

            vibrationStrength += accel * 0.02f;
            vibrationStrength *= Mathf.Exp(-vibrationDamping * dt);
            vibrationStrength = Mathf.Clamp01(vibrationStrength);
            vibrationPhase += vibrationFrequency * dt;
        }

        // ── فتل الحبل ────────────────────────────────────────────────
        if (enableRopeTwist)
        {
            // الفتل يتراكم مع phiVel — ثم نابض يعيده
            float twistAcc =
                -ropeTwistStiffness * ropeTwistAngle
                - ropeTwistDamping * ropeTwistVelocity
                + phiVel * 0.3f;

            ropeTwistVelocity += twistAcc * dt;
            ropeTwistAngle += ropeTwistVelocity * dt;

            // حد أقصى للفتل — 20 درجة
            ropeTwistAngle = Mathf.Clamp(
                ropeTwistAngle,
                -20f * Mathf.Deg2Rad,
                 20f * Mathf.Deg2Rad);
        }
    }

    // ════════════════════════════════════════════════════════════════
    void UpdatePosition()
    {
        float x = r * Mathf.Sin(theta) * Mathf.Cos(phi);
        float z = r * Mathf.Sin(theta) * Mathf.Sin(phi);
        float y = -r * Mathf.Cos(theta);

        Vector3 worldPos = pivotPosition + new Vector3(x, y, z);

        // ── إضافة الاهتزاز البصري ────────────────────────────────────
        if (enableBucketVibration && bucket != null)
        {
            float vib = vibrationAmplitude
                           * vibrationStrength
                           * Mathf.Sin(vibrationPhase);
            Vector3 shake = bucket.right * vib;
            worldPos += shake;
        }

        bucket.position = worldPos;

        // ── دوران الدلو: يواجه اتجاه الحركة + فتل الحبل ─────────────
        Vector3 ropeDir = (worldPos - pivotPosition).normalized;
        if (ropeDir.sqrMagnitude > 0.001f)
        {
            Quaternion swingRot = Quaternion.LookRotation(ropeDir, Vector3.up);

            if (enableRopeTwist)
            {
                swingRot *= Quaternion.AngleAxis(
                    ropeTwistAngle * Mathf.Rad2Deg,
                    Vector3.up);
            }

            bucket.rotation = swingRot;
        }
    }

    // ════════════════════════════════════════════════════════════════
    void HandleCollision()
    {
        float bucketBottom = bucket.position.y - bucketRadius;
        if (bucketBottom < floorY)
        {
            Vector3 pos = bucket.position;
            pos.y = floorY + bucketRadius;
            bucket.position = pos;

            rVel = -rVel * bounce;
            thetaVel *= 0.7f;
            phiVel *= 0.7f;

            // الاصطدام يزيد الاهتزاز
            if (enableBucketVibration)
                vibrationStrength = Mathf.Clamp01(vibrationStrength + 0.5f);

            Debug.Log($"[Pendulum] Floor collision at t={Time.time:F2}s");
        }
    }

    // ════════════════════════════════════════════════════════════════
    void RK4Step(float dt)
    {
        Vector6 s0 = new Vector6(theta, phi, r, thetaVel, phiVel, rVel);
        Vector6 k1 = Derivatives(s0);
        Vector6 k2 = Derivatives(s0 + k1 * (0.5f * dt));
        Vector6 k3 = Derivatives(s0 + k2 * (0.5f * dt));
        Vector6 k4 = Derivatives(s0 + k3 * dt);
        Vector6 result = s0 + (k1 + k2 * 2f + k3 * 2f + k4) * (dt / 6f);
        theta = result.a; phi = result.b; r = result.c;
        thetaVel = result.d; phiVel = result.e; rVel = result.f;
    }

    // ════════════════════════════════════════════════════════════════
    Vector6 Derivatives(Vector6 s)
    {
        float th = s.a, ph = s.b, rr = s.c;
        float thVel = s.d, phVel = s.e, rrVel = s.f;

        float sinT = Mathf.Sin(th);
        if (Mathf.Abs(sinT) < 0.01f)
            sinT = (sinT >= 0f ? 1f : -1f) * 0.01f;
        float cosT = Mathf.Cos(th);

        rr = Mathf.Max(rr, 0.1f);
        float mass = Mathf.Max(bucketMass, 0.01f);
        float effDamp = angularDamping * (1f + humidity * 0.2f);

        float dragTheta = -airDragCoeff * thVel * Mathf.Abs(thVel);
        float dragPhi = -airDragCoeff * phVel * Mathf.Abs(phVel);
        float dragR = -airDragCoeff * rrVel * Mathf.Abs(rrVel);

        float windTheta = 0f, windPhi = 0f;
        if (windForce.sqrMagnitude > 0f)
        {
            windTheta = (windForce.x * Mathf.Cos(ph) + windForce.z * Mathf.Sin(ph))
                      / (mass * rr);
            windPhi = (-windForce.x * Mathf.Sin(ph) + windForce.z * Mathf.Cos(ph))
                      / (mass * rr * sinT);
            windTheta = Mathf.Clamp(windTheta, -50f, 50f);
            windPhi = Mathf.Clamp(windPhi, -50f, 50f);
        }

        float thetaAcc =
              sinT * cosT * phVel * phVel
            - (gravity / rr) * sinT
            - (2f * rrVel / rr) * thVel
            - effDamp * thVel
            + dragTheta / mass
            + windTheta;

        float phiAcc =
            -2f * (cosT / sinT) * thVel * phVel
            - (2f * rrVel / rr) * phVel
            - effDamp * phVel
            + dragPhi / mass
            + windPhi;

        float rAcc =
              rr * (thVel * thVel + sinT * sinT * phVel * phVel)
            + gravity * cosT
            - (ropeStiffness / mass) * (rr - restLength)
            - (radialDamping / mass) * rrVel
            + dragR / mass;

        thetaAcc = Mathf.Clamp(thetaAcc, -200f, 200f);
        phiAcc = Mathf.Clamp(phiAcc, -200f, 200f);
        rAcc = Mathf.Clamp(rAcc, -200f, 200f);

        return new Vector6(thVel, phVel, rrVel, thetaAcc, phiAcc, rAcc);
    }

    // ════════════════════════════════════════════════════════════════
    bool HasNaN()
    {
        return float.IsNaN(theta) || float.IsInfinity(theta)
            || float.IsNaN(phi) || float.IsInfinity(phi)
            || float.IsNaN(r) || float.IsInfinity(r)
            || float.IsNaN(thetaVel) || float.IsInfinity(thetaVel)
            || float.IsNaN(phiVel) || float.IsInfinity(phiVel)
            || float.IsNaN(rVel) || float.IsInfinity(rVel);
    }

    void StopSimulation(string reason)
    {
        simRunning = false; stopReason = reason;
        Debug.Log($"[Pendulum] Finished — Swings: {zeroCrossCount / 2} | Reason: {reason}");
        Debug.Log($"[Pendulum] Max Energy Drift: {maxEnergyDrift:F4} %");
        if (trail != null) trail.emitting = false;
    }

    void CheckStopConditions()
    {
        bool stopSwings = (zeroCrossCount >= 2 * n_swings);
        bool stopStill = (theta < 0.05f
                        && Mathf.Abs(thetaVel) < 0.005f
                        && Mathf.Abs(phiVel) < 0.005f
                        && Mathf.Abs(rVel) < 0.005f);
        bool stopEnergy = (TotalEnergy() < 0.001f);
        if (stopSwings) StopSimulation("n_swings reached");
        else if (stopStill) StopSimulation("pendulum stopped naturally");
        else if (stopEnergy) StopSimulation("energy depleted");
    }

    void LateUpdate()
    {
        if (!enableCameraFollow || mainCamera == null || bucket == null) return;
        Vector3 targetPos = bucket.position + cameraOffset;
        mainCamera.transform.position = Vector3.Lerp(
            mainCamera.transform.position, targetPos, Time.deltaTime * cameraSmoothing);
        mainCamera.transform.LookAt(bucket.position);
    }

    void SetupTrail()
    {
        if (!showTrail || bucket == null) return;
        trail = bucket.GetComponent<TrailRenderer>();
        if (trail == null) trail = bucket.gameObject.AddComponent<TrailRenderer>();
        trail.time = trailTime;
        trail.startWidth = trailStartWidth;
        trail.endWidth = trailEndWidth;
        trail.material = new Material(Shader.Find("Sprites/Default"));
        Gradient g = new Gradient();
        g.SetKeys(
            new GradientColorKey[] { new GradientColorKey(trailColor, 0f),
                                     new GradientColorKey(trailColor, 1f) },
            new GradientAlphaKey[] { new GradientAlphaKey(1f, 0f),
                                     new GradientAlphaKey(0f, 1f) });
        trail.colorGradient = g;
    }

    float TotalEnergy()
    {
        float sinT = Mathf.Sin(theta);
        float kinetic = 0.5f * bucketMass * (
            rVel * rVel +
            r * r * thetaVel * thetaVel +
            r * r * sinT * sinT * phiVel * phiVel);
        float potGravity = bucketMass * gravity * r * (1f - Mathf.Cos(theta));
        float potSpring = 0.5f * ropeStiffness * Mathf.Pow(r - restLength, 2f);
        return kinetic + potGravity + potSpring;
    }

    public float GetGeff()
    {
        float sinT = Mathf.Sin(theta);
        return gravity * Mathf.Cos(theta)
             + r * thetaVel * thetaVel
             + r * sinT * sinT * phiVel * phiVel;
    }

    public Vector3 GetBucketVelocity()
    {
        float sinT = Mathf.Sin(theta), cosT = Mathf.Cos(theta);
        float sinP = Mathf.Sin(phi), cosP = Mathf.Cos(phi);
        float vx = r * (thetaVel * cosT * cosP - phiVel * sinT * sinP) + rVel * sinT * cosP;
        float vy = -r * thetaVel * sinT - rVel * cosT;
        float vz = r * (thetaVel * cosT * sinP + phiVel * sinT * cosP) + rVel * sinT * sinP;
        return new Vector3(vx, vy, vz);
    }

    // خصائص للقراءة من سكربتات أخرى
    public float TorsionAngleDeg => ropeTwistAngle * Mathf.Rad2Deg;
    public float VibrationStrength => vibrationStrength;
    public bool IsRunning => simRunning;

    // ════════════════════════════════════════════════════════════════
    void OnGUI()
    {
        int x = 10, y = 10, lh = 22;
        GUI.Label(new Rect(x, y, 320, lh), $"Energy      : {TotalEnergy():F4} J");
        GUI.Label(new Rect(x, y + lh, 320, lh), $"EnergyDrift : {maxEnergyDrift:F4} %");
        GUI.Label(new Rect(x, y + lh * 2, 320, lh), $"r           : {r:F3} m");
        GUI.Label(new Rect(x, y + lh * 3, 320, lh), $"θ           : {theta * Mathf.Rad2Deg:F1} °");
        GUI.Label(new Rect(x, y + lh * 4, 320, lh), $"φ           : {phi * Mathf.Rad2Deg % 360f:F1} °");
        GUI.Label(new Rect(x, y + lh * 5, 320, lh), $"g_eff       : {GetGeff():F3} m/s²");
        GUI.Label(new Rect(x, y + lh * 6, 320, lh), $"|v_bucket|  : {GetBucketVelocity().magnitude:F3} m/s");
        GUI.Label(new Rect(x, y + lh * 7, 320, lh), $"Swings      : {zeroCrossCount / 2} / {n_swings}");
        GUI.Label(new Rect(x, y + lh * 8, 320, lh), $"Humidity    : {humidity * 100f:F0} %");
        GUI.Label(new Rect(x, y + lh * 9, 320, lh), $"pivot Y     : {pivotPosition.y:F1} m");
        GUI.Label(new Rect(x, y + lh * 10, 320, lh), $"bucket Y    : {bucket.position.y:F2} m");
        GUI.Label(new Rect(x, y + lh * 11, 320, lh), $"Torsion     : {TorsionAngleDeg:F1} °");
        GUI.Label(new Rect(x, y + lh * 12, 320, lh), $"Vibration   : {VibrationStrength * 100f:F1} %");
        GUI.Label(new Rect(x, y + lh * 13, 320, lh), $"NaN count   : {nanFrameCount}");
        GUI.Label(new Rect(x, y + lh * 14, 320, lh), $"StopReason  : {stopReason}");
        GUI.Label(new Rect(x, y + lh * 15, 320, lh), $"Running     : {simRunning}");
    }
}

// ════════════════════════════════════════════════════════════════════
public struct Vector6
{
    public float a, b, c, d, e, f;
    public Vector6(float A, float B, float C, float D, float E, float F)
    { a = A; b = B; c = C; d = D; e = E; f = F; }
    public static Vector6 operator +(Vector6 u, Vector6 v)
        => new Vector6(u.a + v.a, u.b + v.b, u.c + v.c, u.d + v.d, u.e + v.e, u.f + v.f);
    public static Vector6 operator *(Vector6 u, float s)
        => new Vector6(u.a * s, u.b * s, u.c * s, u.d * s, u.e * s, u.f * s);
}