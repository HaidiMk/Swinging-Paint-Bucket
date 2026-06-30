using UnityEngine;

// Renders the PBF particles as GPU-instanced sphere-imposters.
// Reads positions/states directly from the solver's ComputeBuffers,
// so there is NO CPU readback and NO 50K ParticleSystem cap.
public class PaintParticleRenderer : MonoBehaviour
{
    [Header("References")]
    public PBFSolver solver;
    public Material material;                       // uses Custom/PaintParticleInstancedURP

    [Header("Look")]
    [Range(0.01f, 0.3f)] public float particleScale = 0.035f;

    Mesh quadMesh;
    ComputeBuffer argsBuffer;
    Bounds bounds;
    int cachedCount = -1;

    void Start()
    {
        quadMesh = BuildQuad();
        // Big bounds so Unity never frustum-culls the whole batch
        bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
    }

    void LateUpdate()
    {
        if (solver == null || material == null) return;
        if (solver.PositionsBuffer == null || solver.StatesBuffer == null) return;

        int count = solver.ParticleCount;
        if (count != cachedCount) { BuildArgs(count); cachedCount = count; }

        // Bind GPU buffers (these stay on the GPU � no readback)
        material.SetBuffer("_Positions", solver.PositionsBuffer);
        material.SetBuffer("_States", solver.StatesBuffer);

        // Per-frame params for size + depth colouring
        material.SetFloat("_Scale", particleScale);
        material.SetVector("_BucketCenter", solver.BucketCenter);
        material.SetVector("_BucketUp", solver.BucketUpDir);
        material.SetFloat("_BucketHeight", solver.bucketWorldHeight);
        Color current = solver.simpleLayerVisuals ? solver.CurrentPaintColor() : solver.paintColor;
        Color dark = Color.Lerp(current, Color.black, 0.45f);
        dark.a = current.a;
        material.SetColor("_PaintColor", current);
        material.SetColor("_PaintColorDark", dark);

        Graphics.DrawMeshInstancedIndirect(quadMesh, 0, material, bounds, argsBuffer);
    }

    void BuildArgs(int count)
    {
        argsBuffer?.Release();
        uint[] args = new uint[5] { 0, 0, 0, 0, 0 };
        args[0] = quadMesh.GetIndexCount(0);   // indices per instance (6)
        args[1] = (uint)count;                 // instance count
        args[2] = quadMesh.GetIndexStart(0);
        args[3] = quadMesh.GetBaseVertex(0);
        argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint),
                                       ComputeBufferType.IndirectArguments);
        argsBuffer.SetData(args);
    }

    // A 1x1 quad centered on the origin; the shader billboards + rounds it.
    Mesh BuildQuad()
    {
        Mesh m = new Mesh { name = "ParticleQuad" };
        m.vertices = new Vector3[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3( 0.5f, -0.5f, 0f),
            new Vector3( 0.5f,  0.5f, 0f),
            new Vector3(-0.5f,  0.5f, 0f),
        };
        m.uv = new Vector2[]
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(1f, 1f),
            new Vector2(0f, 1f),
        };
        m.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
        m.RecalculateBounds();
        return m;
    }

    void OnDestroy()
    {
        argsBuffer?.Release();
    }
}