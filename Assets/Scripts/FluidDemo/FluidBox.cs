using System.Collections.Generic;
using UnityEngine;

// ════════════════════════════════════════════════════════════════════
//  FluidBox.cs  —  محاكاة سائل مستقلة داخل متوازي مستطيلات شفّاف قابل للحركة
//  Position Based Fluids (PBF) على الـ CPU — مستقلة تمامًا عن مشروع البندول.
//
//  الإعداد (دقيقتين):
//   1. مشهد جديد (أو نفس المشهد) → GameObject فاضي → سمّيه "FluidBox".
//   2. أضِف له هذا السكربت (Add Component → FluidBox).
//   3. شغّل ▶ . رح ينعمل صندوق شفّاف + سائل جوّاه تلقائيًا.
//
//  التحكّم وقت التشغيل:
//   - Arrows / WASD : تحريك الصندوق (السائل يتمايل بالقصور الذاتي)
//   - Q / E         : تدوير الصندوق (السائل يسيل للجهة المنخفضة)
//   - R / F         : رفع / خفض الصندوق
//   - Space         : هزّة عشوائية (slosh)
// ════════════════════════════════════════════════════════════════════
public class FluidBox : MonoBehaviour
{
    [Header("Fluid")]
    public int particleCount = 450;
    public float particleRadius = 0.05f;
    public float smoothingRadius = 0.16f;   // h
    public int solverIterations = 4;
    public float relaxation = 600f;         // epsilon (CFM)
    public float viscosity = 0.08f;         // XSPH — تماسك السائل

    [Header("Box (inner size in meters)")]
    public Vector3 boxSize = new Vector3(1.4f, 1.4f, 1.4f);
    public bool autoCreateBoxVisual = true; // ينشئ صندوق شفّاف للعرض

    [Header("Controls")]
    public float moveSpeed = 2.2f;
    public float rotateSpeed = 70f;

    [Header("Render")]
    public Color fluidColor = new Color(0.20f, 0.55f, 1f, 1f);
    public float renderScale = 1.7f;        // حجم كرة الجزيء (× نصف القطر)

    // ── داخلي ──
    Vector3[] pos, predicted, vel;          // في الفضاء المحلي للصندوق
    float[] lambda;
    float restDensity = 1f;
    float poly6C, spikyC;

    // جيران عبر شبكة تجزئة
    int gx, gy, gz; float cell;
    List<int>[] grid;
    static readonly Vector3Int[] N27 = BuildN27();

    // عرض الجزيئات
    Mesh sphereMesh; Material fluidMat;
    Matrix4x4[] matrices;

    // حركة الصندوق (لحساب القصور الذاتي)
    Vector3 prevPos; Quaternion prevRot; Vector3 prevBoxVel;

    Transform boxVisual;

    void Start()
    {
        float h = smoothingRadius;
        poly6C = 315f / (64f * Mathf.PI * Mathf.Pow(h, 9));
        spikyC = -45f / (Mathf.PI * Mathf.Pow(h, 6));

        cell = h;
        gx = Mathf.Max(1, Mathf.CeilToInt(boxSize.x / cell));
        gy = Mathf.Max(1, Mathf.CeilToInt(boxSize.y / cell));
        gz = Mathf.Max(1, Mathf.CeilToInt(boxSize.z / cell));
        grid = new List<int>[gx * gy * gz];
        for (int i = 0; i < grid.Length; i++) grid[i] = new List<int>(8);

        SpawnParticles();
        CalibrateRestDensity();   // يضبط الكثافة المرجعية تلقائيًا (بلا معايرة يدوية)
        SetupRendering();
        if (autoCreateBoxVisual) CreateBoxVisual();

        prevPos = transform.position;
        prevRot = transform.rotation;
    }

    // ── إنشاء الجزيئات: كتلة في النصف السفلي من الصندوق ──
    void SpawnParticles()
    {
        pos = new Vector3[particleCount];
        predicted = new Vector3[particleCount];
        vel = new Vector3[particleCount];
        lambda = new float[particleCount];

        float spacing = particleRadius * 2.1f;
        Vector3 half = boxSize * 0.5f;
        // عمود ملء في الأسفل
        int perRow = Mathf.Max(1, Mathf.FloorToInt(boxSize.x / spacing));
        int perCol = Mathf.Max(1, Mathf.FloorToInt(boxSize.z / spacing));
        for (int i = 0; i < particleCount; i++)
        {
            int layer = i / (perRow * perCol);
            int rem = i % (perRow * perCol);
            int row = rem / perCol;
            int col = rem % perCol;
            float x = -half.x + particleRadius + (col + 0.5f) * spacing;
            float z = -half.z + particleRadius + (row + 0.5f) * spacing;
            float y = -half.y + particleRadius + (layer + 0.5f) * spacing;
            pos[i] = new Vector3(x, y, z) + Random.insideUnitSphere * particleRadius * 0.3f;
            vel[i] = Vector3.zero;
        }
    }

    void CalibrateRestDensity()
    {
        BuildGrid(pos);
        float sum = 0f; int cnt = 0;
        for (int i = 0; i < particleCount; i++)
        {
            float rho = 0f;
            ForEachNeighbor(pos, i, (j, r2) => { rho += poly6C * Mathf.Pow(smoothingRadius * smoothingRadius - r2, 3); });
            sum += rho; cnt++;
        }
        restDensity = Mathf.Max(1f, sum / Mathf.Max(1, cnt));
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;

        // تسارع الصندوق (للقصور الذاتي → تمايل عند التحريك)
        Vector3 boxVel = (transform.position - prevPos) / dt;
        Vector3 boxAccel = (boxVel - prevBoxVel) / dt;
        boxAccel = Vector3.ClampMagnitude(boxAccel, 30f);   // منع الانفجار عند ضغط مفاجئ
        prevBoxVel = boxVel;

        // الجاذبية + قوة وهمية بالإطار المتحرّك، محوّلتان للفضاء المحلي
        Vector3 aWorld = Physics.gravity - boxAccel;                        // قوة وهمية = -a_box
        Vector3 aLocal = transform.InverseTransformDirection(aWorld);

        Step(dt, aLocal);

        prevPos = transform.position;
        prevRot = transform.rotation;
    }

    // ── خطوة PBF كاملة (في الفضاء المحلي للصندوق) ──
    void Step(float dt, Vector3 accel)
    {
        for (int i = 0; i < particleCount; i++)
        {
            vel[i] += accel * dt;
            predicted[i] = pos[i] + vel[i] * dt;
            ClampToBox(ref predicted[i]);
        }

        BuildGrid(predicted);

        for (int iter = 0; iter < solverIterations; iter++)
        {
            // λ لكل جزيء
            for (int i = 0; i < particleCount; i++)
            {
                float rho = 0f; float sumGrad2 = 0f; Vector3 gradI = Vector3.zero;
                ForEachNeighbor(predicted, i, (j, r2) =>
                {
                    float h2 = smoothingRadius * smoothingRadius;
                    rho += poly6C * Mathf.Pow(h2 - r2, 3);
                    if (j != i)
                    {
                        Vector3 d = predicted[i] - predicted[j];
                        float r = Mathf.Sqrt(r2);
                        if (r > 1e-6f)
                        {
                            Vector3 g = (spikyC * (smoothingRadius - r) * (smoothingRadius - r) / r) * d / restDensity;
                            gradI += g; sumGrad2 += Vector3.Dot(g, g);
                        }
                    }
                });
                float C = rho / restDensity - 1f;
                sumGrad2 += Vector3.Dot(gradI, gradI);
                lambda[i] = -C / (sumGrad2 + relaxation);
            }

            // إزاحة المواقع
            for (int i = 0; i < particleCount; i++)
            {
                Vector3 dP = Vector3.zero;
                ForEachNeighbor(predicted, i, (j, r2) =>
                {
                    if (j == i) return;
                    Vector3 d = predicted[i] - predicted[j];
                    float r = Mathf.Sqrt(r2);
                    if (r > 1e-6f)
                    {
                        float spiky = spikyC * (smoothingRadius - r) * (smoothingRadius - r) / r;
                        dP += (lambda[i] + lambda[j]) * spiky * d;
                    }
                });
                predicted[i] += dP / restDensity;
                ClampToBox(ref predicted[i]);
            }
        }

        // تحديث السرعة + اللزوجة (XSPH)
        for (int i = 0; i < particleCount; i++)
        {
            vel[i] = (predicted[i] - pos[i]) / dt;
            pos[i] = predicted[i];
        }
        if (viscosity > 0f) ApplyXSPH();
    }

    void ApplyXSPH()
    {
        var add = new Vector3[particleCount];
        for (int i = 0; i < particleCount; i++)
        {
            Vector3 v = Vector3.zero; float w = 0f;
            ForEachNeighbor(pos, i, (j, r2) =>
            {
                if (j == i) return;
                float k = poly6C * Mathf.Pow(smoothingRadius * smoothingRadius - r2, 3);
                v += (vel[j] - vel[i]) * k; w += k;
            });
            if (w > 0f) add[i] = viscosity * v / w;
        }
        for (int i = 0; i < particleCount; i++) vel[i] += add[i];
    }

    // ── حدود الصندوق (AABB محلي) ──
    void ClampToBox(ref Vector3 p)
    {
        Vector3 half = boxSize * 0.5f - Vector3.one * particleRadius;
        if (p.x < -half.x) p.x = -half.x; else if (p.x > half.x) p.x = half.x;
        if (p.y < -half.y) p.y = -half.y; else if (p.y > half.y) p.y = half.y;
        if (p.z < -half.z) p.z = -half.z; else if (p.z > half.z) p.z = half.z;
    }

    // ── شبكة الجيران ──
    void BuildGrid(Vector3[] p)
    {
        for (int i = 0; i < grid.Length; i++) grid[i].Clear();
        Vector3 half = boxSize * 0.5f;
        for (int i = 0; i < particleCount; i++)
        {
            int cx = Mathf.Clamp((int)((p[i].x + half.x) / cell), 0, gx - 1);
            int cy = Mathf.Clamp((int)((p[i].y + half.y) / cell), 0, gy - 1);
            int cz = Mathf.Clamp((int)((p[i].z + half.z) / cell), 0, gz - 1);
            grid[(cz * gy + cy) * gx + cx].Add(i);
        }
    }

    void ForEachNeighbor(Vector3[] p, int i, System.Action<int, float> fn)
    {
        Vector3 half = boxSize * 0.5f;
        int cx = Mathf.Clamp((int)((p[i].x + half.x) / cell), 0, gx - 1);
        int cy = Mathf.Clamp((int)((p[i].y + half.y) / cell), 0, gy - 1);
        int cz = Mathf.Clamp((int)((p[i].z + half.z) / cell), 0, gz - 1);
        float h2 = smoothingRadius * smoothingRadius;
        for (int n = 0; n < 27; n++)
        {
            int nx = cx + N27[n].x, ny = cy + N27[n].y, nz = cz + N27[n].z;
            if (nx < 0 || ny < 0 || nz < 0 || nx >= gx || ny >= gy || nz >= gz) continue;
            var bucket = grid[(nz * gy + ny) * gx + nx];
            for (int b = 0; b < bucket.Count; b++)
            {
                int j = bucket[b];
                float r2 = (p[i] - p[j]).sqrMagnitude;
                if (r2 < h2) fn(j, r2);
            }
        }
    }

    // ── التحكّم + العرض ──
    void Update()
    {
        float dt = Time.deltaTime;
        Vector3 move = Vector3.zero;
        if (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A)) move.x -= 1;
        if (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D)) move.x += 1;
        if (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.W)) move.z += 1;
        if (Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S)) move.z -= 1;
        if (Input.GetKey(KeyCode.R)) move.y += 1;
        if (Input.GetKey(KeyCode.F)) move.y -= 1;
        transform.position += move * moveSpeed * dt;

        float rot = 0f;
        if (Input.GetKey(KeyCode.Q)) rot -= 1;
        if (Input.GetKey(KeyCode.E)) rot += 1;
        transform.Rotate(Vector3.forward, rot * rotateSpeed * dt, Space.Self);

        if (Input.GetKeyDown(KeyCode.Space))
            for (int i = 0; i < particleCount; i++) vel[i] += Random.insideUnitSphere * 3f;

        RenderParticles();
    }

    void RenderParticles()
    {
        if (sphereMesh == null || fluidMat == null) return;
        float s = particleRadius * 2f * renderScale;
        Vector3 scale = new Vector3(s, s, s);
        int drawn = 0;
        while (drawn < particleCount)
        {
            int batch = Mathf.Min(1023, particleCount - drawn);
            for (int k = 0; k < batch; k++)
            {
                Vector3 world = transform.TransformPoint(pos[drawn + k]);
                matrices[k] = Matrix4x4.TRS(world, Quaternion.identity, scale);
            }
            Graphics.DrawMeshInstanced(sphereMesh, 0, fluidMat, matrices, batch);
            drawn += batch;
        }
    }

    void SetupRendering()
    {
        var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
        Destroy(tmp);

        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        fluidMat = new Material(sh);
        fluidMat.enableInstancing = true;
        fluidMat.color = fluidColor;
        if (fluidMat.HasProperty("_BaseColor")) fluidMat.SetColor("_BaseColor", fluidColor);
        if (fluidMat.HasProperty("_Smoothness")) fluidMat.SetFloat("_Smoothness", 0.85f);

        matrices = new Matrix4x4[1023];
    }

    void CreateBoxVisual()
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "BoxVisual";
        Destroy(cube.GetComponent<Collider>());
        cube.transform.SetParent(transform, false);
        cube.transform.localScale = boxSize;
        boxVisual = cube.transform;

        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        Color glass = new Color(0.7f, 0.85f, 1f, 0.12f);
        // إعداد الشفافية لـ URP
        m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", 0f);
        m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetInt("_ZWrite", 0);
        m.DisableKeyword("_SURFACE_TYPE_OPAQUE");
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = 3000;
        m.color = glass;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", glass);
        cube.GetComponent<MeshRenderer>().material = m;
    }

    void OnDrawGizmos()
    {
        Gizmos.color = Color.cyan;
        Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, boxSize);
    }

    static Vector3Int[] BuildN27()
    {
        var list = new List<Vector3Int>();
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                    list.Add(new Vector3Int(x, y, z));
        return list.ToArray();
    }
}