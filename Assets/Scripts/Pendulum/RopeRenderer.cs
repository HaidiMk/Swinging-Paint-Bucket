using UnityEngine;

// ════════════════════════════════════════════════════════════════════
//  RopeRenderer.cs  v2.2  — حبل مرن بصري محسَّن
//  الإصلاحات عن v2.1:
//  [FIX-1] swayDir — محور ثابت (Vector3.up) بدل Vector3.forward
//          يمنع انعكاس اتجاه التموّج المفاجئ عند مرور الحبل عبر المحور
//  [FIX-2] smoothedBend — معادلة تكاملية بدل Lerp
//          تعطي استجابة أكثر واقعية (حبل ذو قصور ذاتي)
//  ملاحظة: تأثير بصري بحت — لا يؤثر على فيزياء البندول.
// ════════════════════════════════════════════════════════════════════

[RequireComponent(typeof(LineRenderer))]
public class RopeRenderer : MonoBehaviour
{
    // ═══════════════════════════════════════════════
    [Header("References — المراجع")]
    // ═══════════════════════════════════════════════
    public Transform pivot;
    public Transform bucket;

    // ═══════════════════════════════════════════════
    [Header("Rope Shape — شكل الحبل")]
    // ═══════════════════════════════════════════════
    [Tooltip("عدد نقاط الرسم — أكثر = أنعم.")]
    public int ropeSegments = 24;

    [Tooltip("ترهّل الحبل بسبب وزنه (catenary). 0 = مشدود تماماً.")]
    public float sagAmount = 0.05f;

    // ═══════════════════════════════════════════════
    [Header("Dynamic Bend — الانحناء الديناميكي")]
    // ═══════════════════════════════════════════════
    [Tooltip("تفعيل انحناء الحبل عكس اتجاه تسارع الدلو.")]
    public bool enableDynamicBend = true;

    [Tooltip("شدة الانحناء — أكبر = حبل أثقل / أكثر مرونة.")]
    public float bendStrength = 0.06f;

    [Tooltip("تنعيم استجابة الانحناء (0.05 بطيء ← 0.5 سريع).")]
    [Range(0.02f, 0.6f)] public float bendSmoothing = 0.15f;

    // ═══════════════════════════════════════════════
    [Header("Transverse Sway — الاهتزاز العرضي")]
    // ═══════════════════════════════════════════════
    [Tooltip("تفعيل تموّج الحبل العرضي عند الحركة العنيفة.")]
    public bool enableSway = true;

    [Tooltip("شدة التموّج العرضي.")]
    public float swayStrength = 0.04f;

    [Tooltip("عدد موجات التموّج على طول الحبل.")]
    public float swayWaves = 3f;

    [Tooltip("سرعة انتشار التموّج.")]
    public float swaySpeed = 8f;

    [Tooltip("معدّل تلاشي التموّج — أكبر = يهدأ أسرع.")]
    public float swayDamping = 3f;

    // ─── داخلي ────────────────────────────────────
    LineRenderer line;

    Vector3 prevBucketPos;
    Vector3 prevBucketVel;

    // [FIX-2] smoothedBend يُدار كتكامل وليس Lerp بسيط
    Vector3 smoothedBend;

    float swayPhase;
    float swayStrengthCurrent;

    // ════════════════════════════════════════════════════════════════
    void Start()
    {
        line = GetComponent<LineRenderer>();
        line.positionCount = ropeSegments;

        if (bucket != null)
        {
            prevBucketPos = bucket.position;
            prevBucketVel = Vector3.zero;
        }

        smoothedBend = Vector3.zero;
    }

    void LateUpdate()
    {
        if (pivot == null || bucket == null) return;

        UpdateDynamics();
        DrawRope();
    }

    // ════════════════════════════════════════════════════════════════
    //  حساب التسارع ذاتياً من موقع الدلو
    // ════════════════════════════════════════════════════════════════
    void UpdateDynamics()
    {
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);

        Vector3 vel = (bucket.position - prevBucketPos) / dt;
        Vector3 accel = (vel - prevBucketVel) / dt;
        prevBucketPos = bucket.position;
        prevBucketVel = vel;

        // [FIX-6] سقف التسارع — عند نقاط التعكس (أقصى يمين/يسار) التسارع
        // المحسوب من فرق المواقع قد يُضخَّم بسبب القفزات العددية بين frames
        // نحدّه بـ 20 m/s² لمنع الرجّة المبالغ فيها
        accel = Vector3.ClampMagnitude(accel, 20f);

        Vector3 ropeDir = (bucket.position - pivot.position).normalized;

        // ── الانحناء الديناميكي ─────────────────────────────────────
        if (enableDynamicBend)
        {
            Vector3 lateralAccel = accel - Vector3.Dot(accel, ropeDir) * ropeDir;
            Vector3 targetBend = -lateralAccel * bendStrength;
            targetBend = Vector3.ClampMagnitude(targetBend, 0.3f); // [FIX-6] خُفِّض من 0.5
            smoothedBend += (targetBend - smoothedBend) * bendSmoothing;
        }
        else
        {
            smoothedBend = Vector3.zero;
        }

        // ── التموّج العرضي ───────────────────────────────────────────
        if (enableSway)
        {
            // [FIX-6] نشحن التموّج من المركبة الجانبية فقط
            // التسارع الطولي (على محور الحبل) لا يُنتج تموّجاً عرضياً
            Vector3 lateralOnly = accel - Vector3.Dot(accel, ropeDir) * ropeDir;
            float lateralMag = Mathf.Min(lateralOnly.magnitude, 10f);

            swayStrengthCurrent += lateralMag * 0.005f;              // [FIX-6] خُفِّض من 0.01
            swayStrengthCurrent *= Mathf.Exp(-swayDamping * dt);
            swayStrengthCurrent = Mathf.Clamp(swayStrengthCurrent, 0f, 0.4f); // سقف 0.4
            swayPhase += swaySpeed * dt;
        }
        else
        {
            swayStrengthCurrent = 0f;
        }
    }

    // ════════════════════════════════════════════════════════════════
    void DrawRope()
    {
        Vector3 start = pivot.position;
        Vector3 end = bucket.position;

        Vector3 ropeDir = (end - start).normalized;

        // ── [FIX-1] محور ثابت لاتجاه التموّج ─────────────────────────
        // Vector3.up أكثر استقراراً من forward لأنه لا يتغير مع دوران الحبل
        // يمنع انعكاساً مفاجئاً في اتجاه swayDir عند مرور الحبل عبر المحور
        Vector3 swayDir = Vector3.Cross(ropeDir, Vector3.up);
        if (swayDir.sqrMagnitude < 0.001f)
            swayDir = Vector3.Cross(ropeDir, Vector3.right);
        swayDir.Normalize();

        for (int i = 0; i < ropeSegments; i++)
        {
            float t = (float)i / (ropeSegments - 1);
            Vector3 pt = Vector3.Lerp(start, end, t);

            // قوس الانحناء: أقوى في المنتصف، يصفر عند الطرفين
            float arc = Mathf.Sin(t * Mathf.PI);

            // 1) الترهّل بسبب وزن الحبل
            pt.y -= arc * sagAmount;

            // 2) الانحناء الديناميكي عكس التسارع
            pt += smoothedBend * arc;

            // 3) التموّج العرضي المتلاشي
            if (enableSway && swayStrengthCurrent > 0.001f)
            {
                float wave = Mathf.Sin(t * Mathf.PI * swayWaves + swayPhase);
                pt += swayDir * (wave * arc * swayStrength * swayStrengthCurrent);
            }

            line.SetPosition(i, pt);
        }
    }
}