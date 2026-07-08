using UnityEngine;


public class PaintParticleRenderer : MonoBehaviour
{
    [Header("References")]
    public PBFSolver solver;
    public Material material;                       

    [Header("Look")]
    [Range(0.005f, 0.08f)] public float particleScale = 0.018f;

    Mesh quadMesh;
    ComputeBuffer argsBuffer;
    Bounds bounds;
    int cachedCount = -1;

    void Start()
    {
        quadMesh = BuildQuad();
        bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
    }

    void LateUpdate()
    {
        if (solver == null || material == null) return;
        if (solver.PositionsBuffer == null || solver.StatesBuffer == null) return;

        int count = solver.ParticleCount;
        if (count != cachedCount) { BuildArgs(count); cachedCount = count; }

        material.SetBuffer("_Positions", solver.PositionsBuffer);
        material.SetBuffer("_States", solver.StatesBuffer);

        material.SetFloat("_Scale", particleScale);
        material.SetVector("_BucketCenter", solver.BucketCenter);
        material.SetVector("_BucketRight", solver.BucketRightDir);
        material.SetVector("_BucketUp", solver.BucketUpDir);
        material.SetVector("_BucketForward", solver.BucketForwardDir);
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
        args[0] = quadMesh.GetIndexCount(0);   
        args[1] = (uint)count;                
        args[2] = quadMesh.GetIndexStart(0);
        args[3] = quadMesh.GetBaseVertex(0);
        argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint),
                                       ComputeBufferType.IndirectArguments);
        argsBuffer.SetData(args);
    }


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