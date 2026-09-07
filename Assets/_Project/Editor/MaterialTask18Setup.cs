using Game.Core;
using Game.Materials;
using UnityEditor;

namespace Game.EditorTools
{
    /// <summary>
    /// Task 18 的确定性 Authoring 入口。Asset 必须由 Unity 创建以获得合法 GUID，
    /// 因此这里不手写 .meta/YAML；重复执行只校正字段，不会复制 Catalog 或 Definition。
    /// </summary>
    [InitializeOnLoad]
    internal static class MaterialTask18Setup
    {
        private const string RootFolder = "Assets/_Project/ScriptableObjects/Materials";
        private const string CatalogPath = RootFolder + "/MaterialCatalog_Default.asset";
        private const string SessionKey = "Game.Materials.Task18.SetupOnce";

        static MaterialTask18Setup()
        {
            if (!SessionState.GetBool(SessionKey, false))
            {
                SessionState.SetBool(SessionKey, true);
                EditorApplication.delayCall += Setup;
            }
        }

        [MenuItem("Tools/Material World/Setup Task 18 Catalog")]
        private static void Setup()
        {
            EnsureFolder("Assets/_Project/ScriptableObjects", "Materials");

            MaterialDefinition water = CreateOrUpdateDefinition(
                RootFolder + "/Material_Water.asset",
                MaterialId.Water,
                MaterialBehaviorKind.Liquid);
            MaterialDefinition fire = CreateOrUpdateDefinition(
                RootFolder + "/Material_Fire.asset",
                MaterialId.Fire,
                MaterialBehaviorKind.ReactiveField);
            MaterialDefinition poison = CreateOrUpdateDefinition(
                RootFolder + "/Material_Poison.asset",
                MaterialId.Poison,
                MaterialBehaviorKind.Liquid);
            MaterialDefinition sticky = CreateOrUpdateDefinition(
                RootFolder + "/Material_Sticky.asset",
                MaterialId.Sticky,
                MaterialBehaviorKind.Liquid);

            MaterialCatalog catalog = AssetDatabase.LoadAssetAtPath<MaterialCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = UnityEngine.ScriptableObject.CreateInstance<MaterialCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            var serialized = new SerializedObject(catalog);
            SerializedProperty definitions = serialized.FindProperty("_definitions");
            // 该迁移器会在每个 Editor Session 自动执行，因此必须覆盖当前完整 Catalog。
            // 若仍停留在 Task 18 的三项版本，重启 Unity 会悄悄删除后续 Task 加入的 Sticky。
            definitions.arraySize = 4;
            definitions.GetArrayElementAtIndex(0).objectReferenceValue = water;
            definitions.GetArrayElementAtIndex(1).objectReferenceValue = fire;
            definitions.GetArrayElementAtIndex(2).objectReferenceValue = poison;
            definitions.GetArrayElementAtIndex(3).objectReferenceValue = sticky;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            GameLog.Info("Task 18 Material Catalog assets are ready.", "Editor");
        }

        private static MaterialDefinition CreateOrUpdateDefinition(
            string path,
            MaterialId id,
            MaterialBehaviorKind behavior)
        {
            MaterialDefinition definition = AssetDatabase.LoadAssetAtPath<MaterialDefinition>(path);
            if (definition == null)
            {
                definition = UnityEngine.ScriptableObject.CreateInstance<MaterialDefinition>();
                AssetDatabase.CreateAsset(definition, path);
            }

            var serialized = new SerializedObject(definition);
            serialized.FindProperty("_id").intValue = (byte)id;
            serialized.FindProperty("_behavior").intValue = (byte)behavior;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
            return definition;
        }

        private static void EnsureFolder(string parent, string child)
        {
            string path = parent + "/" + child;
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, child);
        }
    }
}
