using UnityEngine;

namespace EulerianAurora.Maps
{
    public sealed class MapGenerationSettings : ScriptableObject
    {
        private const string ResourceName = "MapGenerationSettings";
        [SerializeField] private MapGenerationOptions options = MapGenerationOptions.Default;

        // Unity 에셋은 메인 스레드에서 읽고, 생성 작업에는 값 복사본을 전달한다.
        public static MapGenerationOptions ReadOptions() =>
            Resources.Load<MapGenerationSettings>(ResourceName)?.options ?? MapGenerationOptions.Default;

#if UNITY_EDITOR
        [UnityEditor.CustomEditor(typeof(MapGenerationSettings))]
        private sealed class SettingsInspector : UnityEditor.Editor
        {
            [UnityEditor.MenuItem("Tools/Map Generation/Settings")]
            private static void Open()
            {
                var settings = Resources.Load<MapGenerationSettings>(ResourceName);
                if (settings == null)
                {
                    const string folder = "Assets/Resources";
                    if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
                        UnityEditor.AssetDatabase.CreateFolder("Assets", "Resources");
                    settings = CreateInstance<MapGenerationSettings>();
                    UnityEditor.AssetDatabase.CreateAsset(settings, folder + "/" + ResourceName + ".asset");
                }
                UnityEditor.Selection.activeObject = settings;
                UnityEditor.EditorGUIUtility.PingObject(settings);
            }

            public override void OnInspectorGUI()
            {
                var settings = (MapGenerationSettings)target;
                UnityEditor.EditorGUILayout.HelpBox(
                    "다음 맵 생성부터 적용됩니다. 난이도별 기믹 가중치와 공통 연속 회전 가중치는 각각 해당 후보가 가능할 때의 상대값입니다.",
                    UnityEditor.MessageType.Info);
                serializedObject.Update();
                var root = serializedObject.FindProperty(nameof(options));
                Draw(nameof(MapGenerationOptions.Costs), "공통 기믹 비용");
                Draw(nameof(MapGenerationOptions.Easy), "쉬움");
                Draw(nameof(MapGenerationOptions.Medium), "보통");
                Draw(nameof(MapGenerationOptions.Hard), "어려움");
                Draw(nameof(MapGenerationOptions.Search), "탐색 한도");
                Draw(nameof(MapGenerationOptions.Sampling), "공통 후보 설정");
                Draw(nameof(MapGenerationOptions.Layout), "경로 형태 균형");
                serializedObject.ApplyModifiedProperties();

                var error = settings.options.ValidationError();
                if (error != null) UnityEditor.EditorGUILayout.HelpBox(error, UnityEditor.MessageType.Error);
                if (GUILayout.Button("기본값 복원"))
                {
                    UnityEditor.Undo.RecordObject(settings, "Reset Map Generation Settings");
                    settings.options = MapGenerationOptions.Default;
                    UnityEditor.EditorUtility.SetDirty(settings);
                }

                void Draw(string field, string label) => UnityEditor.EditorGUILayout.PropertyField(
                    root.FindPropertyRelative(field), new GUIContent(label), includeChildren: true);
            }
        }
#endif
    }
}
