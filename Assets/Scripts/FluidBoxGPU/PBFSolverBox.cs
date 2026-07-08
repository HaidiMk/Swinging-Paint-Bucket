using UnityEngine;


public class PBFSolverBox : MonoBehaviour
{
    [Header("Compute (اسحب PBFSolverBox.compute هنا)")]
    public ComputeShader pbfComputeShader;

    [Header("Fluid — نفس إعدادات حلّال الدلو")]
    public int maxParticles = 1500;
    public float h = 0.08f;             
    public float restDensity = 6f;
    public int solverIterations = 2;
    public float epsilon = 600f;
    public float viscosity = 0.10f;
    public float gravity = 9.81f;
    public float gravityScale = 0.6f;
    public float bucketInfluence = 0.5f; 

    [Header("Sloshing — نفس متغيرات تمايل السائل بالدلو")]
    public bool enableParticleSloshing = true;
    [Range(0f, 2f)] public float particleSloshStrength = 1.15f;
    [Range(0f, 20f)] public float particleSloshResponse = 11.5f;
    [Range(0f, 10f)] public float particleSloshDamping = 3.6f;
    [Range(0f, 12f)] public float particleSloshForce = 5.8f;
    [Range(0f, 14f)] public float particleSloshLiftForce = 6.5f;
    [Range(0f, 1.5f)] public float particleSloshSurfaceSlope = 0.32f;
    [Range(0f, 1f)] public float particleSloshSurfaceBias = 0.88f;

    [Header("Cohesion — نفس تماسك الجزيئات بالدلو")]
    public bool enableParticleCohesion = true;
    [Range(0f, 8f)] public float particleCohesionStrength = 2.2f;
    [Range(0.2f, 2f)] public float particleCohesionRadius = 0.85f;
    [Range(0f, 2f)] public float particleCohesionRepulsion = 0.45f;
    [Range(0f, 2f)] public float particleCohesionDamping = 0.55f;

    [Header("Box (متوازي المستطيلات)")]
    public Vector3 boxHalfExtents = new Vector3(0.35f, 0.35f, 0.35f);
    public bool autoCreateBoxVisual = true;

    [Header("Controls")]
    public float moveSpeed = 2.0f;
    public float rotateSpeed = 70f;

    [Header("Render")]
    public Color fluidColor = new Color(0.20f, 0.55f, 1f, 1f);
    public float particleRenderRadius = 0.045f;
    public float renderScale = 1.7f;


    const int MAX_PER_CELL = 128;
    const int MAX_NEIGHBORS = 64;
    const int INSIDE = 0;

    ComputeBuffer positionsBuffer, predictedPosBuffer, velocitiesBuffer, deltaPosBuffer;
    ComputeBuffer lambdasBuffer, statesBuffer, neighborListBuffer, neighborCountBuffer;
    ComputeBuffer gridCountBuffer, gridParticlesBuffer;

    int kPredict, kClearGrid, kFillGrid, kFindNeighbors, kSolve, kCorrect,
        kUpdateVel, kViscosity, kCohesion, kBoundaryBox, kClampPredicted;

    int gridSizeX, gridSizeY, gridSizeZ, totalCells;
    Vector3 gridOrigin; float cellSize;

    Vector3 prevPos, prevVel, boxAccel;
    Vector3 particleSloshVector = Vector3.zero;
    Vector3 particleSloshVelocity = Vector3.zero;

    Vector3[] positionsCPU;
    Mesh sphereMesh; Material fluidMat; Matrix4x4[] matrices;
    bool ready;

    Vector3 BoxCenter => transform.position;
    Vector3 BoxUp => transform.up;
    Vector3 BoxRight => transform.right;
    Vector3 BoxForward => transform.forward;

    void Start()
    {
        if (pbfComputeShader == null)
        {
            Debug.LogError("[PBFSolverBox] عيّن PBFSolverBox.compute على الحقل Pbf Compute Shader.");
            enabled = false; return;
        }
        InitGrid();
        InitBuffers();
        SpawnParticles();
        CalibrateRestDensity();  
        SetupRendering();
        if (autoCreateBoxVisual) CreateBoxVisual();
        prevPos = transform.position;
        ready = true;
        Debug.Log($"[PBFSolverBox] Ready — Particles={maxParticles} | Grid={gridSizeX}x{gridSizeY}x{gridSizeZ}");
    }

    void InitGrid()
    {
        cellSize = h;
        float margin = h * 2f;
        float w = boxHalfExtents.x * 2f + margin * 2f;
        float ht = boxHalfExtents.y * 2f + margin * 2f;
        float d = boxHalfExtents.z * 2f + margin * 2f;
        gridSizeX = Mathf.CeilToInt(w / cellSize) + 2;
        gridSizeY = Mathf.CeilToInt(ht / cellSize) + 2;
        gridSizeZ = Mathf.CeilToInt(d / cellSize) + 2;
        gridOrigin = new Vector3(
            -(gridSizeX * cellSize) / 2f,
            -(gridSizeY * cellSize) / 2f,
            -(gridSizeZ * cellSize) / 2f);
        totalCells = gridSizeX * gridSizeY * gridSizeZ;
    }

    void InitBuffers()
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

        kPredict = pbfComputeShader.FindKernel("PredictPositions");
        kClearGrid = pbfComputeShader.FindKernel("ClearGrid");
        kFillGrid = pbfComputeShader.FindKernel("FillGrid");
        kFindNeighbors = pbfComputeShader.FindKernel("FindNeighbors");
        kSolve = pbfComputeShader.FindKernel("SolveConstraints");
        kCorrect = pbfComputeShader.FindKernel("ApplyCorrection");
        kUpdateVel = pbfComputeShader.FindKernel("UpdateVelocity");
        kViscosity = pbfComputeShader.FindKernel("ApplyViscosity");
        kCohesion = pbfComputeShader.FindKernel("ApplyCohesion"); 
        kBoundaryBox = pbfComputeShader.FindKernel("EnforceBoundaryBox");
        kClampPredicted = pbfComputeShader.FindKernel("ClampPredictedBox");

        int[] kernels = { kPredict, kClearGrid, kFillGrid, kFindNeighbors,
                          kSolve, kCorrect, kUpdateVel, kViscosity, kCohesion, kBoundaryBox, kClampPredicted };
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
        positionsCPU = new Vector3[n];
    }

    void SpawnParticles()
    {
        var initPos = new Vector3[maxParticles];
        var initVel = new Vector3[maxParticles];
        var initSt = new int[maxParticles];
        for (int i = 0; i < maxParticles; i++)
        {
            float bx = Random.Range(-boxHalfExtents.x * 0.9f, boxHalfExtents.x * 0.9f);
            float bz = Random.Range(-boxHalfExtents.z * 0.9f, boxHalfExtents.z * 0.9f);
            float by = Random.Range(-boxHalfExtents.y * 0.9f, boxHalfExtents.y * 0.1f);
            initPos[i] = new Vector3(bx, by, bz); 
            initVel[i] = Vector3.zero;
            initSt[i] = INSIDE;
        }
        positionsBuffer.SetData(initPos);
        velocitiesBuffer.SetData(initVel);
        statesBuffer.SetData(initSt);
        initPos.CopyTo(positionsCPU, 0);
    }




    void CalibrateRestDensity()
    {
        float poly6C = 315f / (64f * Mathf.PI * Mathf.Pow(h, 9));
        float hSq = h * h;
        float sum = 0f;
        for (int i = 0; i < maxParticles; i++)
        {
            float rho = 0f;
            for (int j = 0; j < maxParticles; j++)
            {
                float r2 = (positionsCPU[i] - positionsCPU[j]).sqrMagnitude;
                if (r2 < hSq)
                    rho += poly6C * Mathf.Pow(hSq - r2, 3);
            }
            sum += rho;
        }
        restDensity = Mathf.Max(1f, sum / maxParticles);
        Debug.Log($"[PBFSolverBox] restDensity المحسوب تلقائيًا = {restDensity:F2}");
    }

    void FixedUpdate()
    {
        if (!ready) return;
        float dt = Time.fixedDeltaTime;

        HandleMovement(dt);

        Vector3 vel = (transform.position - prevPos) / Mathf.Max(dt, 1e-4f);
        boxAccel = (vel - prevVel) / Mathf.Max(dt, 1e-4f);
        boxAccel = Vector3.ClampMagnitude(boxAccel, 8f);
        prevVel = vel; prevPos = transform.position;

        SetConstants(dt);

        Dispatch(kPredict);
        Dispatch(kClampPredicted);   
        DispatchGrid(kClearGrid);
        Dispatch(kFillGrid);
        Dispatch(kFindNeighbors);
        for (int it = 0; it < solverIterations; it++)
        {
            Dispatch(kSolve);
            Dispatch(kCorrect);
            Dispatch(kClampPredicted);
        }
        Dispatch(kUpdateVel);
        if (viscosity > 0f) Dispatch(kViscosity);
        Dispatch(kBoundaryBox);
    }


    void UpdateParticleSloshing(float dt)
    {
        if (!enableParticleSloshing)
        {
            particleSloshVector = Vector3.Lerp(particleSloshVector, Vector3.zero, Mathf.Clamp01(dt * 8f));
            particleSloshVelocity = Vector3.Lerp(particleSloshVelocity, Vector3.zero, Mathf.Clamp01(dt * 8f));
            return;
        }

        Vector3 up = BoxUp;

        Vector3 lateralPseudo = Vector3.ProjectOnPlane(-boxAccel, up);
        Vector3 lateralVel = Vector3.ProjectOnPlane(prevVel, up);

        Vector3 sloshDrive = lateralPseudo;
        if (lateralVel.sqrMagnitude > 0.0001f)
            sloshDrive += -lateralVel * 0.35f;

        float mag = sloshDrive.magnitude;
        Vector3 desired = Vector3.zero;

        if (mag > 0.03f)
        {
            float amount = Mathf.InverseLerp(0.18f, 7.0f, mag) * particleSloshStrength;
            desired = sloshDrive.normalized * Mathf.Clamp01(amount);
        }

        Vector3 error = desired - particleSloshVector;
        particleSloshVelocity += error * particleSloshResponse * dt;
        particleSloshVelocity *= Mathf.Exp(-particleSloshDamping * dt);
        particleSloshVector += particleSloshVelocity * dt;

        if (particleSloshVector.magnitude > 1f)
            particleSloshVector = particleSloshVector.normalized;
    }

    void SetConstants(float dt)
    {
        var cs = pbfComputeShader;

    
        Vector3 gravityWorld = new Vector3(0f, -gravity * gravityScale, 0f);
        Vector3 gravityLocal = transform.InverseTransformDirection(gravityWorld);
        Vector3 bucketAccelLocal = transform.InverseTransformDirection(boxAccel);

        cs.SetFloat("dt", dt);
        cs.SetFloat("h", h);
        cs.SetFloat("restDensity", restDensity);
        cs.SetFloat("epsilon", epsilon);
        cs.SetFloat("bucketInfluence", bucketInfluence);
        cs.SetFloat("gravity", gravity);
        cs.SetFloat("viscosity", viscosity);
        cs.SetVector("gravityLocal", gravityLocal);
        cs.SetVector("bucketAccelLocal", bucketAccelLocal);

        cs.SetInt("enableParticleSloshing", enableParticleSloshing ? 1 : 0);
        cs.SetVector("particleSloshVector", particleSloshVector);
        cs.SetFloat("particleSloshForce", particleSloshForce);
        cs.SetFloat("particleSloshLiftForce", particleSloshLiftForce);
        cs.SetFloat("particleSloshSurfaceSlope", particleSloshSurfaceSlope);
        cs.SetFloat("particleSloshSurfaceBias", particleSloshSurfaceBias);

        cs.SetInt("enableParticleCohesion", enableParticleCohesion ? 1 : 0);
        cs.SetFloat("particleCohesionStrength", particleCohesionStrength);
        cs.SetFloat("particleCohesionRadius", particleCohesionRadius);
        cs.SetFloat("particleCohesionRepulsion", particleCohesionRepulsion);
        cs.SetFloat("particleCohesionDamping", particleCohesionDamping);

        cs.SetInt("particleCount", maxParticles);
        cs.SetInt("maxNeighbors", MAX_NEIGHBORS);
        cs.SetInt("maxPerCell", MAX_PER_CELL);

        cs.SetVector("bucketCenter", Vector3.zero);
        cs.SetVector("bucketUp", Vector3.up);
        cs.SetVector("boxHalfExtents", boxHalfExtents);
        cs.SetFloat("bucketRadius", boxHalfExtents.x);
        cs.SetFloat("bucketTop", boxHalfExtents.y);
        cs.SetFloat("bucketBottom", -boxHalfExtents.y);

        cs.SetInt("gridSizeX", gridSizeX);
        cs.SetInt("gridSizeY", gridSizeY);
        cs.SetInt("gridSizeZ", gridSizeZ);
        cs.SetVector("gridOrigin", gridOrigin); 
        cs.SetFloat("cellSize", cellSize);
    }

    void Dispatch(int kernel)
        => pbfComputeShader.Dispatch(kernel, Mathf.CeilToInt(maxParticles / 256f), 1, 1);

    void DispatchGrid(int kernel)
        => pbfComputeShader.Dispatch(kernel, Mathf.CeilToInt(totalCells / 256f), 1, 1);

    void Update()
    {
        if (!ready) return;
        HandleSpaceImpulse();
        RenderParticles();
    }

    void HandleMovement(float dt)
    {
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
    }

    void HandleSpaceImpulse()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            var v = new Vector3[maxParticles];
            velocitiesBuffer.GetData(v);
            for (int i = 0; i < maxParticles; i++) v[i] += Random.insideUnitSphere * 3f;
            velocitiesBuffer.SetData(v);
        }
    }

    void RenderParticles()
    {
        if (sphereMesh == null || fluidMat == null) return;
        positionsBuffer.GetData(positionsCPU);          
        float s = particleRenderRadius * 2f * renderScale;
        Vector3 scale = new Vector3(s, s, s);
        int drawn = 0;
        while (drawn < maxParticles)
        {
            int batch = Mathf.Min(1023, maxParticles - drawn);
            for (int k = 0; k < batch; k++)
            {
                Vector3 world = transform.TransformPoint(positionsCPU[drawn + k]); 
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
        fluidMat = new Material(sh) { enableInstancing = true, color = fluidColor };
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
        cube.transform.localScale = boxHalfExtents * 2f;
        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        Color glass = new Color(0.7f, 0.85f, 1f, 0.12f);
        m.SetFloat("_Surface", 1f);
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
        Gizmos.DrawWireCube(Vector3.zero, boxHalfExtents * 2f);
    }

    void OnDestroy()
    {
        positionsBuffer?.Release(); predictedPosBuffer?.Release(); velocitiesBuffer?.Release();
        deltaPosBuffer?.Release(); lambdasBuffer?.Release(); statesBuffer?.Release();
        neighborListBuffer?.Release(); neighborCountBuffer?.Release();
        gridCountBuffer?.Release(); gridParticlesBuffer?.Release();
    }
}