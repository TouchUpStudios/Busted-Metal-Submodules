using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;
using float3 = Unity.Mathematics.float3;
using float4x4 = Unity.Mathematics.float4x4;

namespace Deform
{
    [Deformer(Name = "Surface", Description = "Free-form deform a mesh using surface control points", Type = typeof(SurfaceDeformer))]
    [HelpURL("https://github.com/keenanwoodall/Deform/wiki/SurfaceDeformer")]
    public class SurfaceDeformer : Deformer
    {
        public float3[] ControlPointsBase => controlPointsBase;
        public float3[] ControlPoints => controlPoints;
        public MeshFilter Mesh => meshFilter;
        public float DistanceMin => distanceMin;

        [SerializeField, HideInInspector] private Transform target;
        [SerializeField] private float3[] controlPointsBase;
        [SerializeField] private float3[] controlPoints;
        [SerializeField] private MeshFilter meshFilter = null;
        [SerializeField] private float distanceMin = 0.1f;

        protected virtual void Reset()
        {
            GenerateControlPoints();
        }

        public void GenerateControlPoints()
        {
            List<Vector3> surfacePoints = new List<Vector3>();
            for(int i = 0; i < meshFilter.sharedMesh.vertexCount; i++)
            {
                Vector3 point = meshFilter.sharedMesh.vertices[i];
                if(!surfacePoints.Exists((Vector3 p) => Vector3.Distance(point, p) < distanceMin)){
                    surfacePoints.Add(point);
                }
            }
            controlPointsBase = new float3[surfacePoints.Count];
            controlPoints = new float3[surfacePoints.Count];
            for(int i = 0; i < surfacePoints.Count; i++)
            {
                controlPointsBase[i] = new float3(surfacePoints[i]);
                controlPoints[i] = new float3(surfacePoints[i]);
            }
        }

        public void SetMeshFilter(MeshFilter meshFilter){ this.meshFilter = meshFilter; }
        public void SetDistanceMin(float distanceMin){ this.distanceMin = distanceMin; }

        public override DataFlags DataFlags => DataFlags.Vertices;

        public override JobHandle Process(MeshData data, JobHandle dependency = default)
        {
            return new SurfaceJob
            {
                controlPointsBase = new NativeArray<float3>(controlPointsBase, Allocator.TempJob),
                controlPoints = new NativeArray<float3>(controlPoints, Allocator.TempJob),
                distanceMin = distanceMin,
                vertices = data.DynamicNative.VertexBuffer
            }.Schedule(data.Length, DEFAULT_BATCH_COUNT, dependency);
        }

        [BurstCompile(CompileSynchronously = COMPILE_SYNCHRONOUSLY)]
        public struct SurfaceJob : IJobParallelFor
        {
            [DeallocateOnJobCompletion, ReadOnly] public NativeArray<float3> controlPointsBase;
            [DeallocateOnJobCompletion, ReadOnly] public NativeArray<float3> controlPoints;
            [ReadOnly] public float distanceMin;
            public NativeArray<float3> vertices;

            public void Execute(int index)
            {
                float minDistance = float.MaxValue;
                int indexClosest = 0;
                for(int i = 0; i < controlPointsBase.Length; i++){
                    float dist = distance(vertices[index], controlPointsBase[i]);
                    if(dist < minDistance){
                        minDistance  = dist;
                        indexClosest = i;
                    }
                }
                
                var deltas = new NativeArray<float3>(controlPointsBase.Length, Allocator.Temp);
                var influence = new NativeArray<float>(controlPointsBase.Length, Allocator.Temp);
                float influenceTotal = 0;
                for(int i = 0; i < controlPointsBase.Length; i++){
                    float dist = distance(vertices[index], controlPointsBase[i]);
                    float ratio = clamp(1 - ((dist - minDistance) / min(minDistance, distanceMin)), 0, 1);
                    deltas[i] = controlPoints[i] - controlPointsBase[i];
                    influence[i] = ratio;
                    influenceTotal += ratio;
                }
                
                float3 delta = new float3(0, 0, 0);
                for(int i = 0; i < controlPointsBase.Length; i++){
                    delta += deltas[i] * (influence[i] / influenceTotal);
                }
                vertices[index] = vertices[index] + delta;

                deltas.Dispose();
                influence.Dispose();

                //float3 delta = new float3(0, 0, 0);
                //for(int i = 0; i < controlPointsBase.Length; i++){
                //    float dist = distance(vertices[index], controlPointsBase[i]);
                //    if(dist < distanceMin){
                //        float ratio = 1 - ((dist - minDistance) / min(minDistance, distanceMin));
                //        delta += ratio * (controlPoints[i] - controlPointsBase[i]);
                //    }
                //}
                //vertices[index] = vertices[index] + delta;



                //float3 delta = new float3(0, 0, 0);
                //for(int i = 0; i < controlPointsBase.Length; i++){
                //    float dist = distance(vertices[index], controlPointsBase[i]);
                //    if(dist < distanceMin){
                //        float ratio = pow(1 - (dist / distanceMin), 0.75f);
                //        delta += ratio * (controlPoints[i] - controlPointsBase[i]);
                //    }
                //}
                //vertices[index] = vertices[index] + delta;
            }
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.red;
            Gizmos.matrix = transform.localToWorldMatrix;
            for(int i = 0; i < controlPointsBase.Length; i++){
                Gizmos.DrawWireSphere(controlPointsBase[i], 0.05f);
                Gizmos.DrawLine(controlPointsBase[i], controlPoints[i]);
            }
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}