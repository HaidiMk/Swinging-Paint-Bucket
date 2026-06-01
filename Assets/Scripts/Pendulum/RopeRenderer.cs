using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class RopeRenderer : MonoBehaviour
{
  
    // REFERENCES


    // نقطة التعليق
    public Transform pivot;

    // الدلو
    public Transform bucket;

  
    // ROPE SETTINGS

    // عدد النقاط المرسومة للحبل
    public int ropeSegments = 20;

    // انحناء الحبل مقدار الترهل (الانحناء للأسفل).
    public float sagAmount = 0.05f;

    // INTERNAL

    LineRenderer line;

    // UNITY

    void Start()
    {
        line = GetComponent<LineRenderer>();

        // عدد نقاط الحبل
        line.positionCount = ropeSegments;
    }

    void LateUpdate()
    {
        DrawRope();
    }

    // DRAW ROPE

    void DrawRope()
    {
        if (pivot == null || bucket == null)
            return;

        Vector3 start = pivot.position;

        Vector3 end = bucket.position;

        for (int i = 0; i < ropeSegments; i++)
        {
            float t =
                (float)i / (ropeSegments - 1);

            // interpolation
            Vector3 point =
                Vector3.Lerp(start, end, t);

            // انحناء بسيط للحبل
            point.y -=
                Mathf.Sin(t * Mathf.PI)
                * sagAmount;

            line.SetPosition(i, point);
        }
    }
}