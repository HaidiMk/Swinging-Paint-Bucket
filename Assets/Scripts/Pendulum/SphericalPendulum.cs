using UnityEngine;



public class SphericalPendulum : MonoBehaviour
{
    public Transform bucket;
    public Transform pivot;
    public PBFSolver paintSolver;  

    [Header("Bucket Properties — خصائص الدلو")]
    [Tooltip("كتلة الدلو + الطلاء (kg).")]
    public float bucketMass = 1.0f;

    [Tooltip("نصف قطر الدلو (m).")]
    public float bucketRadius = 0.1f;

    float BucketCrossArea => Mathf.PI * bucketRadius * bucketRadius;

    [Tooltip("معامل السحب Cd")]
    [Range(0f, 1.5f)] public float dragCoefficient = 0.8f;

    [Header("Bucket Visual Scale — الحجم البصري")]
    public float meshBaseScale = 0.4f;
    public float meshBaseRadius = 0.1f;
    public bool autoScaleBucket = true;

    [Header("Fluid Sloshing — تمايل السائل")]
    public bool enableFluidSloshing = true;
    public float fluidMass = 0.6f;
    public float sloshPendulumLength = 0.15f;
    [Range(0f, 10f)] public float sloshDamping = 2f;
    public float sloshMaxDisplacement = 0.12f;

    float sloshThetaDir, sloshThetaDirVel;
    float sloshPhiDir, sloshPhiDirVel;

    [Header("Bucket Vibration — اهتزاز الدلو (بصري)")]
    public bool enableBucketVibration = true;
    public float vibrationAmplitude = 0.03f;
    public float vibrationFrequency = 12f;
    public float vibrationDamping = 2f;

    float vibrationPhase;
    float vibrationStrength;

    [Header("Rope Properties — خصائص التعليق")]
    public Vector3 pivotPosition = new Vector3(0f, 3f, 0f);

    [Tooltip("طول الحبل المرتخي L₀ (m).")]
    public float restLength = 1.5f;

    [Tooltip("صلابة الحبل k (N/m). خُفِّضت لتسمح بامتداد ملموس وواقعي.")]
  
    public float ropeStiffness = 200f;

    [Tooltip("تخميد التمدد الطولي للحبل.")]
    public float radialDamping = 2f;

    [Tooltip("الحبل يشدّ فقط ولا يدفع.")]
    public bool ropeOnlyPullsNoPush = true;

    [Header("Rope Twist — فتل الحبل (بصري)")]
    public bool enableRopeTwist = true;
    public float ropeTwistStiffness = 8f;
    public float ropeTwistDamping = 1f;

    float ropeTwistAngle;
    float ropeTwistVelocity;
    float r, rVel;

    [Header("Initial Conditions — خصائص الحركة")]
    [Range(0.01f, 179f)] public float thetaDeg = 45f;
    [Range(0f, 360f)] public float phiDeg = 0f;
    public float thetaVel0 = 0f;
  
    public float phiVel0 = 0.4f;

    public float theta, phi, thetaVel, phiVel;

    [Header("Environment — خصائص البيئة")]
    public float gravity = 9.81f;
    public float airDensity = 1.225f;
    public float jointFriction = 0.05f;
    [Range(0f, 1f)] public float humidity = 0.5f;
    public Vector3 windVelocity = Vector3.zero;

    [Header("Simulation Control — التحكم بالمحاكاة")]
    public int n_swings = 10;
    public float floorY = -1.5f;
    [Range(0f, 1f)] public float bounce = 0.3f;
    [Range(0.1f, 3f)] public float timeScale = 1f;

    [Header("FadeOut — التخميد التدريجي عند التوقف")]
    [Tooltip("مدة التخميد التدريجي بعد انتهاء التأرجح (ثانية).")]
    public float fadeOutDuration = 6.0f;

    [Tooltip("أقصى احتكاك إضافي وقت التخميد. القيمة القديمة (3) كانت كبيرة كتير مقارنة بـ jointFriction (0.05)، فكانت الطاقة تخلص خلال 3-4 ثواني بس من أصل 6 — فيحس المستخدم إنو وقف فجأة بدل ما يشوف تباطؤ واضح. 0.6-1.0 بيمدد التباطؤ المرئي على كل مدة fadeOutDuration تقريبًا.")]
    [Range(0.1f, 3f)] public float fadeMaxFriction = 0.6f;

    [HideInInspector] public bool fadeOutStarted = false;
    [HideInInspector] public float fadeOutTimer = 0f;

    float fadeOutFriction = 0f;

    [Header("Camera Follow — تتبع الكاميرا")]
    public bool enableCameraFollow = true;
    public Camera mainCamera;
    public Vector3 cameraOffset = new Vector3(0f, 1f, -5f);
    public float cameraSmoothing = 5f;

    [Header("Trail — مسار الدلو")]
    public bool showTrail = true;
    public float trailTime = 5f;
    public float trailStartWidth = 0.05f;
    public float trailEndWidth = 0.005f;
    public Color trailColor = new Color(1f, 0.4f, 0.1f, 1f);

    TrailRenderer trail;

    int zeroCrossCount = 0;
    float prevThetaVel;
    bool simRunning = true;
    bool returningToRest = false; 
    string stopReason = "—";
    float energyAtStart = 0f;
    float maxEnergyDrift = 0f;
    int nanFrameCount = 0;

    Vector3 lastBucketVelocity = Vector3.zero;
    Vector3 sloshPrevVelocity = Vector3.zero;

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
        sloshPrevVelocity = LinearVelocity(theta, phi, r, thetaVel, phiVel, rVel);

        SetupTrail();

        if (enableCameraFollow && mainCamera == null)
            mainCamera = Camera.main;

        UpdatePosition();
        energyAtStart = TotalEnergy();

        Debug.Log($"[Pendulum v2.6] pivot={pivotPosition} | g={gravity} | " +
                  $"E0={energyAtStart:F3} J | T={TheoreticalPeriod():F3} s");
    }

    void Update()
    {
        if (!returningToRest) return;

        float speed = 2.5f;
        theta = Mathf.Lerp(theta, 0f, Time.deltaTime * speed);


        ropeTwistAngle = Mathf.Lerp(ropeTwistAngle, 0f, Time.deltaTime * speed);
        vibrationStrength = Mathf.Lerp(vibrationStrength, 0f, Time.deltaTime * speed);

        UpdatePosition();

        if (theta < 0.002f)
        {
            theta = 0f;
            ropeTwistAngle = 0f;
            ropeTwistVelocity = 0f;
            vibrationStrength = 0f;
            returningToRest = false;
            UpdatePosition();
        }
    }

    void FixedUpdate()
    {
        if (!simRunning) return;

        float dt = Time.fixedDeltaTime * timeScale;

        RK4Step(dt);
        UpdateSecondaryEffects(dt);
        UpdateSloshing(dt);

        if (HasNaN())
        {
            nanFrameCount++;
            Debug.LogError($"[Pendulum] NaN frame={Time.frameCount}");
            theta = Mathf.Clamp(float.IsNaN(theta) ? 0.5f : theta, 0.01f, Mathf.PI - 0.01f);
            phi = float.IsNaN(phi) ? 0f : phi;
            r = float.IsNaN(r) ? restLength : Mathf.Max(r, 0.2f);
            thetaVel = float.IsNaN(thetaVel) ? 0f : Mathf.Clamp(thetaVel, -20f, 20f);
            phiVel = float.IsNaN(phiVel) ? 0f : Mathf.Clamp(phiVel, -20f, 20f);
            rVel = float.IsNaN(rVel) ? 0f : Mathf.Clamp(rVel, -10f, 10f);
            if (nanFrameCount >= 3) { StopSimulation("NaN persistent"); return; }
        }
        else nanFrameCount = 0;

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

        if (r < 0.2f) { r = 0.2f; rVel = Mathf.Abs(rVel); }
        float maxStretch = restLength * 1.5f;
        if (r > maxStretch) { r = maxStretch; if (rVel > 0f) rVel = 0f; }

        UpdatePosition();

        if (prevThetaVel * thetaVel < 0f && Mathf.Abs(thetaVel) > 0.01f)
            zeroCrossCount++;
        prevThetaVel = thetaVel;

        HandleCollision();
        CheckStopConditions();

        if (jointFriction == 0f && airDensity == 0f)
        {
            float drift = Mathf.Abs(TotalEnergy() - energyAtStart)
                        / Mathf.Max(energyAtStart, 0.001f) * 100f;
            if (drift > maxEnergyDrift) maxEnergyDrift = drift;
        }
    }

    void UpdateSecondaryEffects(float dt)
    {
        if (enableBucketVibration)
        {
            float accel = Mathf.Abs(thetaVel) + Mathf.Abs(phiVel) + Mathf.Abs(rVel);
            vibrationStrength += accel * 0.02f;
            vibrationStrength *= Mathf.Exp(-vibrationDamping * dt);
            vibrationStrength = Mathf.Clamp01(vibrationStrength);
            vibrationPhase += vibrationFrequency * dt;
        }

        if (enableRopeTwist)
        {
            float twistAcc =
                -ropeTwistStiffness * ropeTwistAngle
                - ropeTwistDamping * ropeTwistVelocity
                + phiVel * 0.3f;
            ropeTwistVelocity += twistAcc * dt;
            ropeTwistAngle += ropeTwistVelocity * dt;
            ropeTwistAngle = Mathf.Clamp(ropeTwistAngle,
                -20f * Mathf.Deg2Rad, 20f * Mathf.Deg2Rad);
        }
    }

    void UpdateSloshing(float dt)
    {
        if (!enableFluidSloshing || fluidMass <= 0f) return;

        float Lf = Mathf.Max(sloshPendulumLength, 0.01f);
        float ks = fluidMass * gravity / Lf;
        float cs = sloshDamping + fadeOutFriction * 2f; 

        Vector3 vNow = LinearVelocity(theta, phi, r, thetaVel, phiVel, rVel);
        Vector3 aWorld = (vNow - sloshPrevVelocity) / Mathf.Max(dt, 1e-4f);
        sloshPrevVelocity = vNow;

        Vector3 eR, eTheta, ePhi;
        GetUnitVectors(theta, phi, out eR, out eTheta, out ePhi);

        float aTheta = Mathf.Clamp(Vector3.Dot(aWorld, eTheta), -30f, 30f);
        float aPhi = Mathf.Clamp(Vector3.Dot(aWorld, ePhi), -30f, 30f);

        float sAccTheta = (-ks * sloshThetaDir - cs * sloshThetaDirVel - fluidMass * aTheta) / fluidMass;
        float sAccPhi = (-ks * sloshPhiDir - cs * sloshPhiDirVel - fluidMass * aPhi) / fluidMass;

        sloshThetaDirVel = Mathf.Clamp(sloshThetaDirVel + sAccTheta * dt, -2f, 2f);
        sloshPhiDirVel = Mathf.Clamp(sloshPhiDirVel + sAccPhi * dt, -2f, 2f);

        sloshThetaDir = Mathf.Clamp(sloshThetaDir + sloshThetaDirVel * dt,
                        -sloshMaxDisplacement, sloshMaxDisplacement);
        sloshPhiDir = Mathf.Clamp(sloshPhiDir + sloshPhiDirVel * dt,
                        -sloshMaxDisplacement, sloshMaxDisplacement);
    }

    public float SloshMagnitude =>
        Mathf.Sqrt(sloshThetaDir * sloshThetaDir + sloshPhiDir * sloshPhiDir);

    void UpdatePosition()
    {
        float x = r * Mathf.Sin(theta) * Mathf.Cos(phi);
        float z = r * Mathf.Sin(theta) * Mathf.Sin(phi);
        float y = -r * Mathf.Cos(theta);

        Vector3 worldPos = pivotPosition + new Vector3(x, y, z);

        if (enableBucketVibration && bucket != null)
        {
            float vib = vibrationAmplitude * vibrationStrength * Mathf.Sin(vibrationPhase);
            worldPos += bucket.right * vib;
        }

        bucket.position = worldPos;

        if (autoScaleBucket && meshBaseRadius > 0f)
        {
            float s = Mathf.Max(meshBaseScale * (bucketRadius / meshBaseRadius), 0.01f);
            bucket.localScale = new Vector3(s, s, s);
        }

       
        Vector3 eR, eTheta, ePhi;
        GetUnitVectors(theta, phi, out eR, out eTheta, out ePhi);
        Quaternion swingRot = Quaternion.LookRotation(eR, eTheta);
        if (enableRopeTwist)
            swingRot *= Quaternion.AngleAxis(ropeTwistAngle * Mathf.Rad2Deg, Vector3.up);
        bucket.rotation = swingRot;
    }

    void HandleCollision()
    {
        float cosT = Mathf.Cos(theta);
        float bucketBottomY = pivotPosition.y - r * cosT - bucketRadius;

        if (bucketBottomY >= floorY) return;

        float bounceNow = bounce;
        if (fadeOutStarted)
            bounceNow = bounce * (1f - Mathf.Clamp01(fadeOutTimer / fadeOutDuration));

        float newR = (Mathf.Abs(cosT) > 0.01f)
            ? (pivotPosition.y - floorY - bucketRadius) / cosT
            : r;
        newR = Mathf.Max(newR, 0.2f);
        r = newR;

        Vector3 vel = LinearVelocity(theta, phi, r, thetaVel, phiVel, rVel);
        float impactSpeed = vel.magnitude;

        if (rVel < 0f)
            rVel = -rVel * bounceNow;

     
        float energyRetain = Mathf.Sqrt(Mathf.Clamp01(
            bounceNow * bounceNow + (1f - bounceNow * bounceNow) * Mathf.Abs(cosT)));
        thetaVel *= energyRetain;
        phiVel *= energyRetain;

        if (enableBucketVibration)
            vibrationStrength = Mathf.Clamp01(vibrationStrength
                + Mathf.Clamp01(impactSpeed * 0.15f));

        Debug.Log($"[Pendulum] Floor collision | speed={impactSpeed:F2} m/s" +
                  $" | bounce={bounceNow:F2} | retain={energyRetain:F2}");
    }

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

    Vector6 Derivatives(Vector6 s)
    {
        float th = s.a, ph = s.b, rr = s.c;
        float thVel = s.d, phVel = s.e, rrVel = s.f;

        float sinT = Mathf.Sin(th);
        if (Mathf.Abs(sinT) < 0.05f)
            sinT = (sinT >= 0f ? 1f : -1f) * 0.05f;
        float cosT = Mathf.Cos(th);

        rr = Mathf.Max(rr, 0.1f);
        float mass = Mathf.Max(bucketMass, 0.01f);

        float rhoEff = airDensity * (1f + humidity * 0.01f);

       
        float humidFriction = jointFriction * humidity * 0.4f;

        Vector3 vBucket = LinearVelocity(th, ph, rr, thVel, phVel, rrVel);
        Vector3 vRel = vBucket - windVelocity;
        float vRelMag = vRel.magnitude;

        Vector3 dragForce = Vector3.zero;
        if (vRelMag > 1e-4f)
            dragForce = -0.5f * rhoEff * dragCoefficient * BucketCrossArea
                        * vRelMag * vRel;

        Vector3 eR, eTheta, ePhi;
        GetUnitVectors(th, ph, out eR, out eTheta, out ePhi);

        float dragTheta = Mathf.Clamp(Vector3.Dot(dragForce, eTheta) / (mass * rr), -100f, 100f);
        float dragPhi = Mathf.Clamp(Vector3.Dot(dragForce, ePhi) / (mass * rr * sinT), -100f, 100f);
        float dragR = Mathf.Clamp(Vector3.Dot(dragForce, eR) / mass, -100f, 100f);

        float sloshTheta = 0f, sloshPhi = 0f;
        if (enableFluidSloshing && fluidMass > 0f)
        {
            float Lf = Mathf.Max(sloshPendulumLength, 0.01f);
            float ks = fluidMass * gravity / Lf;
            float cs = sloshDamping;
            float Ftheta = ks * sloshThetaDir + cs * sloshThetaDirVel;
            float Fphi = ks * sloshPhiDir + cs * sloshPhiDirVel;
            float mTotal = mass + fluidMass;

         
            float sloshFadeScale = 1f;
            if (fadeOutStarted)
                sloshFadeScale = 1f - Mathf.Clamp01(fadeOutTimer / fadeOutDuration);

            sloshTheta = Mathf.Clamp(-Ftheta / (mTotal * rr), -8f, 8f) * sloshFadeScale;
            sloshPhi = Mathf.Clamp(-Fphi / (mTotal * rr * sinT), -8f, 8f) * sloshFadeScale;
        }

        float totalFriction = jointFriction + fadeOutFriction + humidFriction;

        float thetaAcc =
              sinT * cosT * phVel * phVel
            - (gravity / rr) * sinT
            - (2f * rrVel / rr) * thVel
            - totalFriction * thVel
            + dragTheta
            + sloshTheta;

        float phiAcc =
            -2f * (cosT / sinT) * thVel * phVel
            - (2f * rrVel / rr) * phVel
            - totalFriction * phVel
            + dragPhi
            + sloshPhi;

        float springForce = (ropeOnlyPullsNoPush && rr < restLength)
            ? 0f
            : -(ropeStiffness / mass) * (rr - restLength);

        float rAcc =
              rr * (thVel * thVel + sinT * sinT * phVel * phVel)
            + gravity * cosT
            + springForce
            - ((radialDamping + fadeOutFriction) / mass) * rrVel
            + dragR;

        thetaAcc = Mathf.Clamp(thetaAcc, -50f, 50f);
        phiAcc = Mathf.Clamp(phiAcc, -50f, 50f);
        rAcc = Mathf.Clamp(rAcc, -200f, 200f);

        return new Vector6(thVel, phVel, rrVel, thetaAcc, phiAcc, rAcc);
    }

    
    void GetUnitVectors(float th, float ph,
                        out Vector3 eR, out Vector3 eTheta, out Vector3 ePhi)
    {
        float sinT = Mathf.Sin(th), cosT = Mathf.Cos(th);
        float sinP = Mathf.Sin(ph), cosP = Mathf.Cos(ph);
        eR = new Vector3(sinT * cosP, -cosT, sinT * sinP);
        eTheta = new Vector3(cosT * cosP, sinT, cosT * sinP);
        ePhi = new Vector3(-sinP, 0f, cosP);
    }

    Vector3 LinearVelocity(float th, float ph, float rr,
                           float thVel, float phVel, float rrVel)
    {
        float sinT = Mathf.Sin(th), cosT = Mathf.Cos(th);
        float sinP = Mathf.Sin(ph), cosP = Mathf.Cos(ph);
        float vx = rrVel * sinT * cosP + rr * thVel * cosT * cosP - rr * phVel * sinT * sinP;
        float vy = -rrVel * cosT + rr * thVel * sinT;
        float vz = rrVel * sinT * sinP + rr * thVel * cosT * sinP + rr * phVel * sinT * cosP;
        return new Vector3(vx, vy, vz);
    }

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
        simRunning = false;
        returningToRest = true;
        stopReason = reason;

        
        thetaVel = 0f;
        phiVel = 0f;
        rVel = 0f;

        Debug.Log($"[Pendulum] Finished — Swings:{zeroCrossCount / 2} | {reason}");
        Debug.Log($"[Pendulum] Max Energy Drift: {maxEnergyDrift:F4}%");
        if (trail != null) trail.emitting = false;
    }

    void CheckStopConditions()
    {
        bool swingsReached = zeroCrossCount >= 2 * n_swings;
     
        float kineticEnergy = 0.5f * bucketMass * (
            r * r * thetaVel * thetaVel +
            r * r * Mathf.Sin(theta) * Mathf.Sin(theta) * phiVel * phiVel +
            rVel * rVel);
        bool naturalStop = kineticEnergy < 0.001f && TotalEnergy() < 0.005f;
        bool energyGone = TotalEnergy() < 0.0001f;

        if ((swingsReached || naturalStop || energyGone) && !fadeOutStarted)
        {
            fadeOutStarted = true;
            fadeOutTimer = 0f;
            fadeOutFriction = 0f;
            Debug.Log($"[Pendulum v2.6] FadeOut started — " +
                      $"reason: {(swingsReached ? "n_swings" : naturalStop ? "natural" : "energy")} | " +
                      $"duration={fadeOutDuration}s");
        }

        if (fadeOutStarted && simRunning)
        {
            fadeOutTimer += Time.fixedDeltaTime * timeScale;
            float t = Mathf.Clamp01(fadeOutTimer / fadeOutDuration);

            fadeOutFriction = Mathf.Lerp(0f, fadeMaxFriction, t);

            float angularEnergy =
                  Mathf.Abs(thetaVel)
                + Mathf.Abs(phiVel)
                + Mathf.Abs(rVel);

            bool fullyDone =
                  angularEnergy < 0.0005f
               || fadeOutTimer >= fadeOutDuration;

            if (fullyDone)
            {
                string reason = swingsReached ? "n_swings reached"
                              : naturalStop ? "pendulum stopped naturally"
                              : "energy depleted";
                StopSimulation(reason);
            }
        }
    }

    void LateUpdate()
    {
        if (!enableCameraFollow || mainCamera == null || bucket == null) return;
        Vector3 targetPos = bucket.position + cameraOffset;
        mainCamera.transform.position = Vector3.Lerp(
            mainCamera.transform.position, targetPos,
            Time.deltaTime * cameraSmoothing);
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

    public float TotalEnergy()
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
        lastBucketVelocity = LinearVelocity(theta, phi, r, thetaVel, phiVel, rVel);
        return lastBucketVelocity;
    }

    public float TheoreticalPeriod()
        => 2f * Mathf.PI * Mathf.Sqrt(restLength / Mathf.Max(gravity, 0.01f));

    public void SetFluidMass(float m) { fluidMass = Mathf.Max(0f, m); }

    public float TorsionAngleDeg => ropeTwistAngle * Mathf.Rad2Deg;
    public float VibrationStrength => vibrationStrength;
    public bool IsRunning => simRunning;
    public int SwingCount => zeroCrossCount / 2;
    public float EnergyAtStart => energyAtStart;

   
    public void ResetSimulation()
    {
        theta = thetaDeg * Mathf.Deg2Rad;
        phi = phiDeg * Mathf.Deg2Rad;
        thetaVel = thetaVel0;
        phiVel = phiVel0;
        r = restLength;
        rVel = 0f;

        sloshThetaDir = 0f;
        sloshThetaDirVel = 0f;
        sloshPhiDir = 0f;
        sloshPhiDirVel = 0f;
        sloshPrevVelocity = LinearVelocity(theta, phi, r, thetaVel, phiVel, rVel);

        ropeTwistAngle = 0f;
        ropeTwistVelocity = 0f;
        vibrationPhase = 0f;
        vibrationStrength = 0f;

        zeroCrossCount = 0;
        prevThetaVel = thetaVel;
        simRunning = true;
        nanFrameCount = 0;
        maxEnergyDrift = 0f;

        fadeOutStarted = false;
        fadeOutTimer = 0f;
        fadeOutFriction = 0f;

        if (trail != null)
        {
            trail.Clear();
            trail.emitting = true;
        }

        if (pivot != null) pivot.position = pivotPosition;

        UpdatePosition();
        energyAtStart = TotalEnergy();

        Debug.Log($"[Pendulum v2.6] Reset | θ={thetaDeg}° | r={restLength}m | E0={energyAtStart:F3}J");
    }

    GUIStyle hudStyle;
    Texture2D hudBg;

    void InitHudStyle()
    {
        hudBg = new Texture2D(1, 1);
        hudBg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.6f));
        hudBg.Apply();
        hudStyle = new GUIStyle
        {
            fontSize = 11,
            fontStyle = FontStyle.Normal,
            padding = new RectOffset(4, 4, 2, 2)
        };
        hudStyle.normal.textColor = Color.white;
    }

    void OnGUI()
    {
        if (hudStyle == null) InitHudStyle();
        int x = 10, y = 10, lh = 18, lines = 16;
        GUI.DrawTexture(new Rect(x - 4, y - 3, 230, lh * lines + 6), hudBg);
        float currentScale = (meshBaseRadius > 0f)
            ? meshBaseScale * (bucketRadius / meshBaseRadius) : meshBaseScale;
        GUI.Label(new Rect(x, y, 230, lh), $"θ          : {theta * Mathf.Rad2Deg:F1}°", hudStyle);
        GUI.Label(new Rect(x, y + lh, 230, lh), $"φ          : {phi * Mathf.Rad2Deg % 360f:F1}°", hudStyle);
        GUI.Label(new Rect(x, y + lh * 2, 230, lh), $"r          : {r:F3} m", hudStyle);
        GUI.Label(new Rect(x, y + lh * 3, 230, lh), $"|v|        : {GetBucketVelocity().magnitude:F2} m/s", hudStyle);
        GUI.Label(new Rect(x, y + lh * 4, 230, lh), $"Swings     : {zeroCrossCount / 2} / {n_swings}", hudStyle);
        GUI.Label(new Rect(x, y + lh * 5, 230, lh), $"T_theory   : {TheoreticalPeriod():F3} s", hudStyle);
        GUI.Label(new Rect(x, y + lh * 6, 230, lh), $"Running    : {simRunning}", hudStyle);
        GUI.Label(new Rect(x, y + lh * 7, 230, lh), $"FadeOut    : {(fadeOutStarted ? $"{fadeOutTimer:F1}/{fadeOutDuration:F1}s" : "—")}", hudStyle);
        GUI.Label(new Rect(x, y + lh * 8, 230, lh), $"FadeFric   : {fadeOutFriction:F2}", hudStyle);
        GUI.Label(new Rect(x, y + lh * 9, 230, lh), $"Radius     : {bucketRadius:F3} → Scale:{currentScale:F3}", hudStyle);
        GUI.Label(new Rect(x, y + lh * 10, 230, lh), $"CrossArea  : {BucketCrossArea:F5} m²", hudStyle);
        GUI.Label(new Rect(x, y + lh * 11, 230, lh), $"Humidity   : {humidity * 100f:F0}% → +{humidity * 40f:F1}% friction", hudStyle);
        GUI.Label(new Rect(x, y + lh * 12, 230, lh), $"Slosh      : {SloshMagnitude:F3} m", hudStyle);
        GUI.Label(new Rect(x, y + lh * 13, 230, lh), $"Energy     : {TotalEnergy():F3} J", hudStyle);
        if (GUI.Button(new Rect(x, y + lh * 14 + 4, 100, 22), "Reset"))
            ResetSimulation();
        if (GUI.Button(new Rect(x + 108, y + lh * 14 + 4, 100, 22), "حفظ / Save"))
        {
            if (paintSolver == null) paintSolver = FindFirstObjectByType<PBFSolver>();
            if (paintSolver != null) paintSolver.SaveExperiment();
        }
    }
}
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