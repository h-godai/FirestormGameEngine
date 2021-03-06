// Firestorm Game Engine MeshCombiner  Copyright 2022 TECHNICAL ARTS h.godai
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
    [ExecuteInEditMode]
    public class MeshCombiner : MonoBehaviour
    {
        [Tooltip("メッシュを生成する場所を指定します。")]
        public string GeneratedMeshDirectory = "Generated/Mesh";
        [Tooltip("メッシュを結合するGameObjectを指定します。省略するとアタッチされたGameObjectになります。")]
        public GameObject OriginalObject;
        [Tooltip("生成するメッシュのIndexのサイズを指定します。")]
        public IndexFormat IndexFormat = IndexFormat.UInt32;
        [Tooltip("OcclusionAreaから見えない面を削除します。")]
        public bool CullBackSurface = false;
        [Tooltip("カメラの移動範囲を指定します。")]
        public OcclusionArea OcclusionArea;
        [Tooltip("CullBackSurfaceで削除された面のメッシュを生成します。")]
        public bool MakeCulledSurfaceObject = false; // カリングされた裏側のオブジェクトも生成する
        [Tooltip("Areasで指定した範囲でポリゴンを分割します。")]
        public bool UseAreaCutter = false;
        [Tooltip("分割するエリアを指定します。これらに含まれない面はデフォルトのMeshで生成されます。")]
        public AreaInfo[] Areas;
        [Space]
        [Tooltip("以下のプロパティで生成されるMeshRendererの属性を上書きします。")]
        public bool OverrideRendererProperty = false;
        [HideInInspector]
        public LightProbeUsage LightProbeUsage = LightProbeUsage.Off;
        [HideInInspector]
        public bool ReceiveShadows = false;
        [HideInInspector]
        public ShadowCastingMode ShadowCastingMode = ShadowCastingMode.On;
        [HideInInspector]
        public ReflectionProbeUsage ReflectionProbeUsage = ReflectionProbeUsage.Off;
        [HideInInspector]
        public bool ContributeGI = false;
        [HideInInspector]
        public bool DynamicOcclusion = false;

        private const int MaxMeshVertexCount = 500000; // これ以上大きなMeshはCombindしない

        private GameObject generatedObject = null;
        private GameObject generatedCulledObject = null;
        private string GeneratedObjectName => "Combined_" + this.OriginalObject.name;

        private MeshUtil MeshUtil = new MeshUtil();

        [System.Serializable]
        public class AreaInfo
        {
            public string Name;
            public Vector3 CenterPos;
            public Vector3 AreaSize;
            public GameObject Generated;
        }

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

            // 前回生成されたものがあったら削除
            var genobj = GameObject.Find(this.GeneratedObjectName);
            if (genobj != null)
            {
                DestroyImmediate(genobj);
            }

            // 生成する結合済みオブジェクトの親
            this.generatedObject = new GameObject(this.GeneratedObjectName);
            this.generatedObject.isStatic = true;
            this.generatedObject.transform.position = this.OriginalObject.transform.position;

            // 排除した裏面用オブジェクト
            if (this.CullBackSurface && this.MakeCulledSurfaceObject)
            {
                this.generatedCulledObject = new GameObject(this.GeneratedObjectName + "_Culled");
                this.generatedCulledObject.isStatic = true;
                this.generatedCulledObject.transform.position = this.OriginalObject.transform.position;
            }

            // AreaCutterによるカットされたオブジェクト
            if (this.UseAreaCutter)
            {
                foreach (var area in this.Areas)
                {
                    area.Generated = new GameObject(this.GeneratedObjectName + $"_{area.Name}");
                    area.Generated.isStatic = true;
                    area.Generated.transform.position = this.OriginalObject.transform.position + area.CenterPos;
                }
            }

            var invTrans = this.generatedObject.transform.localToWorldMatrix.inverse;

            // 結合前のメッシュのリストを生成
            var meshFilters = this.OriginalObject.GetComponentsInChildren<MeshFilter>(true);
            var mashesByMaterials = new Dictionary<Material, (GameObject gobj, List<CombineInstance> comblines)>();
            Debug.Log($"{meshFilters.Length} meshes detected.");

            this.MeshUtil.ResetCount();

            // Material毎に、CombineInstanceのリストを作る
            foreach (var meshfilter in meshFilters)
            {
                if (meshfilter.sharedMesh.vertexCount > MaxMeshVertexCount)
                {
                    Debug.LogWarning($"Mash: {meshfilter.name} too many vertex count: {meshfilter.sharedMesh.vertexCount}");
                    continue;
                }
                Debug.Log($"parse mesh :{meshfilter.name} vertex count: {meshfilter.sharedMesh.vertexCount}");
                var renderer = meshfilter.GetComponent<Renderer>();
                if (renderer == null || !renderer.enabled|| !meshfilter.gameObject.activeSelf) continue;
                var mats = renderer.sharedMaterials;
                if (mats == null) continue; // マテリアルがないMeshは無視
                if (mats.Length != meshfilter.sharedMesh.subMeshCount)
                {
                    Debug.LogWarning($"Submesh Count:{meshfilter.sharedMesh.subMeshCount} != Material Count: {mats.Length}");
                }
                int submeshIndex = 0;
                foreach(var mat in mats)
                {
                    (GameObject gobj, List<CombineInstance> comblines) meshes;
                    if (!mashesByMaterials.TryGetValue(mat, out meshes))
                    {
                        meshes.comblines = new List<CombineInstance>();
                        meshes.gobj = meshfilter.gameObject;
                        mashesByMaterials.Add(mat, meshes);
                    }
                    var cmesh = new CombineInstance();
                    cmesh.transform = invTrans * meshfilter.transform.localToWorldMatrix; 
                    if (mats.Length == 1)
                    {    // SubMeshなし
                        cmesh.mesh = meshfilter.sharedMesh;
                    }
                    else
                    {   // SubMeshあり
                        cmesh.mesh = GetSubMesh(meshfilter.sharedMesh, submeshIndex);
                        ++submeshIndex;
                    }
                    meshes.comblines.Add(cmesh);
                }
            }

            // マテリアル毎にMeshObjectを作る
            foreach (var dic in mashesByMaterials)
            {
                var newObject = new GameObject(dic.Key.name);
                var meshrenderer = newObject.AddComponent<MeshRenderer>();
                var meshfilter = newObject.AddComponent<MeshFilter>();

                meshrenderer.material = dic.Key;
                {
                    if (this.OverrideRendererProperty)
                    {   // インスペクタの設定を上書き
                        newObject.isStatic = true;
                        meshrenderer.lightProbeUsage = this.LightProbeUsage;
                        meshrenderer.receiveShadows = this.ReceiveShadows;
                        meshrenderer.shadowCastingMode = this.ShadowCastingMode;
                        meshrenderer.reflectionProbeUsage = this.ReflectionProbeUsage;
                        meshrenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                        meshrenderer.allowOcclusionWhenDynamic = this.DynamicOcclusion;
                        StaticEditorFlags flags = (StaticEditorFlags)0x7f;
                        if (!this.ContributeGI) flags &= ~StaticEditorFlags.ContributeGI;
                        GameObjectUtility.SetStaticEditorFlags(newObject, flags);
                    }
                    else
                    {   // オリジナルをコピー
                        var mr = dic.Value.gobj.GetComponent<MeshRenderer>();
                        meshrenderer.lightProbeUsage = mr.lightProbeUsage;
                        meshrenderer.receiveShadows = mr.receiveShadows;
                        meshrenderer.shadowCastingMode = mr.shadowCastingMode;
                        meshrenderer.reflectionProbeUsage = mr.reflectionProbeUsage;
                        meshrenderer.motionVectorGenerationMode = mr.motionVectorGenerationMode;
                        meshrenderer.stitchLightmapSeams = mr.stitchLightmapSeams;
                        meshrenderer.allowOcclusionWhenDynamic = mr.allowOcclusionWhenDynamic;
                        GameObjectUtility.SetStaticEditorFlags(newObject, GameObjectUtility.GetStaticEditorFlags(dic.Value.gobj));
                    }
                }

                var mesh = new Mesh();
                mesh.name = dic.Key.name;
                mesh.indexFormat = this.IndexFormat;
                mesh.CombineMeshes(dic.Value.comblines.ToArray());
                Unwrapping.GenerateSecondaryUVSet(mesh);

                newObject.transform.parent = this.generatedObject.transform;
                newObject.transform.localPosition = Vector3.zero;

                var dir = $"Assets/{this.GeneratedMeshDirectory}/{this.name}";
                System.IO.Directory.CreateDirectory(dir);

                if (this.CullBackSurface)
                {
                    var (front, back) = this.MeshUtil.CullingMesh(mesh, newObject.transform, this.OcclusionArea);
                    mesh = front;
                    if (this.MakeCulledSurfaceObject)
                    {
                        var culledObj = Instantiate(newObject);
                        culledObj.transform.parent = this.generatedCulledObject.transform;
                        culledObj.transform.localPosition = Vector3.zero;
                        culledObj.GetComponent<MeshFilter>().sharedMesh = back;
                        AssetDatabase.CreateAsset(back, $"{dir}/{dic.Key.name}_culled.asset");
                    }
                }

                if (this.UseAreaCutter)
                {
                    var meshes = this.MeshUtil.MeshAreaCutter(mesh, newObject.transform, this.Areas.Select(a => (a.CenterPos, a.AreaSize)).ToArray());
                    for (int i = 0; i < this.Areas.Length + 1; ++i)
                    {
                        if (meshes[i].vertexCount == 0)
                        {
                            if (i == this.Areas.Length) DestroyImmediate(newObject);
                            continue;
                        }
                        if (i == this.Areas.Length)
                        {   // 本体(その他)
                            meshfilter.sharedMesh = meshes[i];
                            AssetDatabase.CreateAsset(mesh, $"{dir}/{dic.Key.name}.asset");
                        }
                        else
                        {   // Areaに入っているもの
                            var areaObj = Instantiate(newObject);
                            areaObj.transform.parent = this.Areas[i].Generated.transform;
                            areaObj.transform.localPosition = Vector3.zero;
                            var vertices = meshes[i].vertices;
                            for (int v = 0; v < vertices.Length; ++v)
                            {
                                vertices[v] -= this.Areas[i].CenterPos;
                            }
                            meshes[i].vertices = vertices;
                            areaObj.GetComponent<MeshFilter>().sharedMesh = meshes[i];
                            AssetDatabase.CreateAsset(meshes[i], $"{dir}/{dic.Key.name}_{this.Areas[i].Name}.asset");
                        }
                    }
                }
                else
                {
                    meshfilter.sharedMesh = mesh;
                    AssetDatabase.CreateAsset(mesh, $"{dir}/{dic.Key.name}.asset");
                }
            }

            Debug.Log($"Original {this.MeshUtil.TotalMeshCount[0] + this.MeshUtil.TotalMeshCount[1]} Vertices. New Vertices: {this.MeshUtil.TotalMeshCount[0]}, Culled:{this.MeshUtil.TotalMeshCount[1]}:");

            // RefrectionProbeを複製する
            foreach(var probe in this.OriginalObject.GetComponentsInChildren<ReflectionProbe>())
            {
                var obj = Instantiate(probe);
                obj.transform.SetParent(this.generatedObject.transform);
                obj.transform.position = probe.transform.position;
            }

            EditorUtility.SetDirty(this.generatedObject);
            this.generatedObject.SetActive(true);
            this.OriginalObject.SetActive(false);
            if (this.generatedCulledObject) this.generatedCulledObject.SetActive(false);
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        }


        Mesh GetSubMesh(Mesh parent, int index)
        {
            // サブメッシュの三角形リスト取得
            var triangles = parent.GetTriangles(index);

            // サブメッシュのユニークな頂点リストを取得
            var uniqueVertices = triangles.Distinct().ToList();

            //サブメッシュの頂点位置とテクスチャ座標の切り出し
            var vertices = uniqueVertices.Select(x => parent.vertices[x]).ToArray();
            var uv = uniqueVertices.Select(x => parent.uv[x]).ToArray();
            var normals = uniqueVertices.Select(x => parent.normals[x]).ToArray();
            var tangents = uniqueVertices.Select(x => parent.tangents[x]).ToArray();

            //三角形リストの値をサブメッシュに合わせてシフト
            var triangleNewIndexes = uniqueVertices.Select((x, i) => new { OldIndex = x, NewIndex = i }).ToDictionary(x => x.OldIndex, x => x.NewIndex);
            var triangleConv = triangles.Select(x => triangleNewIndexes[x]).ToArray();

            //メッシュ作成
            var mesh = new Mesh();
            mesh.indexFormat = this.IndexFormat;
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.normals = normals;
            mesh.tangents = tangents;
            mesh.triangles = triangleConv;

            //バウンディングボリュームと法線の再計算
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        private void Update()
        {
            DrawArea();
        }

        public void DrawArea()
        {
            if (this.Areas == null) return;
            var col = Color.red;
            foreach (var area in this.Areas)
            {
                var szH = area.AreaSize; szH.y = 0;
                var hy = area.CenterPos.y + area.AreaSize.y * 0.5f;
                var ly = hy - area.AreaSize.y;
                var p1 = area.CenterPos - szH * 0.5f; p1.y = hy;
                var p2 = p1; p2.x += szH.x;
                var p3 = p1; p3 += szH;
                var p4 = p1; p4.z += szH.z;
                var p5 = p1; p5.y = ly;
                var p6 = p2; p6.y = ly;
                var p7 = p3; p7.y = ly;
                var p8 = p4; p8.y = ly;
                Debug.DrawLine(p1, p2, col);
                Debug.DrawLine(p2, p3, col);
                Debug.DrawLine(p3, p4, col);
                Debug.DrawLine(p4, p1, col);
                Debug.DrawLine(p1, p5, col);
                Debug.DrawLine(p2, p6, col);
                Debug.DrawLine(p3, p7, col);
                Debug.DrawLine(p4, p8, col);
            }
        }


    } // class MeshCombiner


    // インスペクタに追加するボタンの定義
    [CustomEditor(typeof(MeshCombiner))]
    public class MeshCombinerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            MeshCombiner self = target as MeshCombiner;

            if (self.OverrideRendererProperty)
            {
                self.LightProbeUsage = (LightProbeUsage)EditorGUILayout.EnumPopup("LightProbeUsage", self.LightProbeUsage);
                self.ShadowCastingMode = (ShadowCastingMode)EditorGUILayout.EnumPopup("ShadowCastingMode", self.ShadowCastingMode);
                self.ReflectionProbeUsage = (ReflectionProbeUsage)EditorGUILayout.EnumPopup("ReflectionProbeUsage", self.ReflectionProbeUsage);
                self.ContributeGI = EditorGUILayout.Toggle("ContributeGI", self.ContributeGI); 
                self.DynamicOcclusion = EditorGUILayout.Toggle("DynamicOcclusion", self.DynamicOcclusion);
            }

            if (GUILayout.Button("Pack Children"))
            {
                self.PackChildren();
            }

            if (self.CullBackSurface && self.OcclusionArea == null)
            {
                var ocarea = GameObject.Find("Occlusion Area");
                if (ocarea != null)
                {
                    self.OcclusionArea = ocarea.GetComponent<OcclusionArea>();
                }
            }
            self.DrawArea();

        }
    } // class

} // namespace

#endif
