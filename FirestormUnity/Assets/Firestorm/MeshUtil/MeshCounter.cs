// Firestorm Game Engine MeshCounter  Copyright 2022 TECHNICAL ARTS h.godai
#if UNITY_EDITOR
// このスクリプトはUnityEditor専用です。

using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine.Rendering;
using UnityEditor.SceneManagement;
using System.Linq;

namespace FireStorm
{
    public class MeshCounter : MonoBehaviour
    {
        [Tooltip("メッシュを結合するGameObjectを指定します。省略するとアタッチされたGameObjectになります。")]
        public GameObject OriginalObject;

        public int VerticesCount = 0;
        public int TrianglesCount = 0;

        private const int MaxMeshVertexCount = 500000; // これ以上大きなMeshはCombindしない

        private MeshUtil MeshUtil = new MeshUtil();

        class MaterialSet
        {
            public string name;
            public Material[] materials;

            public static MaterialSet Create(Material[] mats)
            {
                return new MaterialSet { materials = mats, name = GetName(mats), };
            }
            public static string GetName(Material[] mats)
            {
                return string.Join('_', mats.Select(m => m.name));
            }
            public override int GetHashCode()
            {
                return this.name.GetHashCode();
            }
            public override bool Equals(object obj)
            {
                return this.name == ((MaterialSet)obj).name;
            }
        }

        public void PackChildren()
        {
            if (this.OriginalObject == null) this.OriginalObject = this.gameObject;


            // 結合前のメッシュのリストを生成
            var meshFilters = this.OriginalObject.GetComponentsInChildren<MeshFilter>(true);
            var mashesByMaterials = new Dictionary<Material, int>();
            Debug.Log($"{meshFilters.Length} meshes detected.");

            this.MeshUtil.ResetCount();

            this.VerticesCount = 0;
            this.TrianglesCount = 0;

            // Material毎に、CombineInstanceのリストを作る
            foreach (var meshfilter in meshFilters)
            {
                var vcnt = meshfilter.sharedMesh.vertexCount;
                Debug.Log($"parse mesh :{meshfilter.name} vertex count: {vcnt}");
                var renderer = meshfilter.GetComponent<Renderer>();
                if (renderer == null || !renderer.enabled|| !meshfilter.gameObject.activeSelf) continue;
                var mats = renderer.sharedMaterials;
                if (mats == null) continue; // マテリアルがないMeshは無視

                this.VerticesCount += vcnt;
                this.TrianglesCount += meshfilter.sharedMesh.triangles.Length / 3;

                foreach(var mat in mats)
                {
                    int meshes;
                    if (!mashesByMaterials.TryGetValue(mat, out meshes))
                    {
                        mashesByMaterials.Add(mat, 1);
                    }
                }
            }
        }
    } // class MeshCounter


    // インスペクタに追加するボタンの定義
    [CustomEditor(typeof(MeshCounter))]
    public class MeshCounterEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            MeshCounter self = target as MeshCounter;


            if (GUILayout.Button("Count Mesh"))
            {
                self.PackChildren();
            }
        }
    } // class

} // namespace

#endif
