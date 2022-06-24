// Firestorm Game Engine MeshUtil  Copyright 2022 TECHNICAL ARTS h.godai
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Rendering;
using System.Linq;

namespace FireStorm
{
    // UnityのMeshを管理するクラス
    class MeshElements
    {
        public List<int> Triangles = new List<int>();
        public List<Vector3> Vertices = new List<Vector3>();
        public List<Vector2> Uv1 = new List<Vector2>();
        public List<Vector2> Uv2 = new List<Vector2>();
        public List<Vector3> Normals = new List<Vector3>();
        public List<Vector4> Tangents = new List<Vector4>();

        public int VtxCount => this.Vertices.Count;

        public MeshElements(Mesh mesh = null)
        {
            if (mesh != null)
            {
                this.Triangles = mesh.triangles.ToList();
                this.Vertices = mesh.vertices.ToList();
                this.Uv1 = mesh.uv.ToList();
                if (mesh.uv2 != null)
                {
                    this.Uv2 = mesh.uv2.ToList();
                    //Debug.Log($"mesh:{mesh.name} has UV2 {mesh.uv2.Length}");
                }
                this.Normals = mesh.normals.ToList();
                this.Tangents = mesh.tangents.ToList();

            }
        }

        public int NewVertex(MeshElements src, int index)
        {
            this.Vertices.Add(src.Vertices[index]);
            if (index < src.Uv1.Count) this.Uv1.Add(src.Uv1[index]);
            if (index < src.Uv2.Count) this.Uv2.Add(src.Uv2[index]);
            this.Normals.Add(src.Normals[index]);
            this.Tangents.Add(src.Tangents[index]);
            return this.Vertices.Count;
        }

        public Mesh CreateMesh(string name)
        {
            Mesh mesh = new Mesh();
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.name = name;
            mesh.vertices = this.Vertices.ToArray();
            mesh.uv = this.Uv1.ToArray();
            if (this.Uv2.Any()) mesh.uv2 = this.Uv2.ToArray();
            mesh.normals = this.Normals.ToArray();
            mesh.tangents = this.Tangents.ToArray();
            mesh.triangles = this.Triangles.ToArray();

            // バウンディングボリュームと法線の再計算
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();

            //Debug.Log($"Create Mesh:{mesh.name} {mesh.triangles.Length / 3} triangles {mesh.vertices.Length} vertices uv2:{mesh.uv2.Length}");

            return mesh;
        }

        public int AppendTriangle(MeshElements src, int[] v, Dictionary<int, int> dic)
        {
            for (int j = 0; j < 3; ++j)
            {
                if (dic.ContainsKey(v[j]))
                {
                    this.Triangles.Add(dic[v[j]]);
                }
                else
                {
                    int count = this.VtxCount;
                    dic.Add(v[j], count);
                    this.Triangles.Add(count);
                    NewVertex(src, v[j]);
                }
            }
            return this.VtxCount;
        }

    }

    // Mesh操作のツール
    public class MeshUtil
    {
        private const int MaxMeshCount = 256;
        public int[] TotalMeshCount = new int[MaxMeshCount];
        public void ResetCount()
        {
            this.TotalMeshCount = new int[MaxMeshCount];
        }

        // Meshをcenter/sizeのグループに分離する
        public Mesh[] MeshAreaCutter(Mesh src, Transform meshTransform, (Vector3 center, Vector3 size)[] areas)
        {
            System.Func<Vector3, int, bool> inside = (p, i) =>
            {
                p = p - areas[i].center;
                return Mathf.Abs(p.x) < areas[i].size.x * 0.5f &&
                        Mathf.Abs(p.z) < areas[i].size.z * 0.5f;
            };

            return MeshScan(src, areas.Length + 1, (v1, v2, v3) =>
            {
                var vv1 = trans(meshTransform, ref v1);
                var vv2 = trans(meshTransform, ref v2);
                var vv3 = trans(meshTransform, ref v3);
                for (int i = 0; i < areas.Length; ++i)
                {
                    if (inside(vv1, i) && inside(vv2, i) && inside(vv3, i)) return i;
                }
                return areas.Length;
            });

        }

        // occlusionAreaから裏面になるポリゴンを分離する
        public (Mesh front, Mesh back) CullingMesh(Mesh sourceMesh, Transform meshTransform, OcclusionArea occlusionArea)
        {
            var max = occlusionArea.center + (occlusionArea.size * 0.5f);
            var min = occlusionArea.center - (occlusionArea.size * 0.5f);
            var pos = new Vector3[]{ new Vector3(min.x, min.y, min.z),
                                     new Vector3(max.x, min.y, max.z),
                                     new Vector3(min.x, min.y, max.z),
                                     new Vector3(max.x, min.y, min.z),
                                     new Vector3(min.x, max.y, min.z),
                                     new Vector3(max.x, max.y, max.z),
                                     new Vector3(min.x, max.y, max.z),
                                     new Vector3(max.x, max.y, min.z) };

            var result = MeshScan(sourceMesh, 2, (v1, v2, v3) =>
            {
                var vv1 = trans(meshTransform, ref v1);
                var vv2 = trans(meshTransform, ref v2);
                var vv3 = trans(meshTransform, ref v3);
                return isFrontSurface(ref vv1, ref vv2, ref vv3, pos) ? 0 : 1;
            });
            return (result[0], result[1]);
        }

        // Meshをスキャンして操作を行う
        public Mesh[] MeshScan(Mesh sourceMesh, int outMeshCount, System.Func<Vector3, Vector3, Vector3, int> check)
        {
            MeshElements src = new MeshElements(sourceMesh);

            // 新しいメッシュの要素
            MeshElements[] newMesh = new MeshElements[outMeshCount];
            Dictionary<int, int>[] vtxMaps = new Dictionary<int, int>[outMeshCount];
            Mesh[] outMesh = new Mesh[outMeshCount];
            int[] meshCount = new int[outMeshCount];

            for (int i = 0; i < outMeshCount; ++i)
            {
                newMesh[i] = new MeshElements();
                vtxMaps[i] = new Dictionary<int, int>();
            }

            for (int i = 0; i < src.Triangles.Count; i += 3)
            {
                int[] v = { src.Triangles[i], src.Triangles[i + 1], src.Triangles[i + 2] };
                int num = check(src.Vertices[v[0]], src.Vertices[v[1]], src.Vertices[v[2]]);
                newMesh[num].AppendTriangle(src, v, vtxMaps[num]);
                TotalMeshCount[num] += 1;
                meshCount[num] += 1;
            }
            Debug.Log($"mesh:{sourceMesh.name} new Triangles: {meshCount[0]} culled Triangles: {meshCount[1]}");

            // メッシュ作成
            return newMesh.Select((m, i) => m.CreateMesh($"{sourceMesh.name}_{i}")).ToArray();
        }

        // Transfromの計算 Matrix使うより高速かも
        private Vector3 trans(Transform meshTransform, ref Vector3 v)
        {
            v.x = v.x * meshTransform.localScale.x;
            v.y = v.y * meshTransform.localScale.y;
            v.z = v.z * meshTransform.localScale.z;
            return (meshTransform.rotation * v) + meshTransform.position;
        }


        // posから見てtriangleが裏側か調べる
        private  bool isFrontSurface(ref Vector3 v1, ref Vector3 v2, ref Vector3 v3, Vector3[] pos)
        {
            // triangleの法線(外積)を求める
            var cross = Vector3.Cross(v2 - v1, v3 - v1);
            // posと法線の内積で向きがわかる
            foreach (var p in pos)
            {
                if (Vector3.Dot(cross, p - v1) >= 0.0f) return true;
            }
            return false;
        }

    }
}