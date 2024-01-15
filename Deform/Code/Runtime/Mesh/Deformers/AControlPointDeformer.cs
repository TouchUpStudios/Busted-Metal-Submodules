using UnityEngine;
using float3 = Unity.Mathematics.float3;

namespace Deform
{
    public abstract class  AControlPointDeformer : Deformer
    {
        public float3[] ControlPoints => controlPoints;
        [SerializeField] protected float3[] controlPoints;
    }
}