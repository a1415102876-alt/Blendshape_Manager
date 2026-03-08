using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Configuration;
using Il2CppInterop.Runtime.Injection;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace SimpleBlendshapePlugin
{
    public enum Language
    {
        Chinese,
        English,
        Japanese
    }

    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class Plugin : BasePlugin
    {
        public const string PluginGUID = "com.example.BlendshapeManager";
        public const string PluginName = "Blendshape Manager";
        public const string PluginVersion = "1.0.0";

        public static Plugin Instance;

        // 配置项：快捷键 & UI 参数
        public static ConfigEntry<KeyboardShortcut> CfgTogglePanelHotkey;
        public static ConfigEntry<KeyboardShortcut> CfgRefreshPanelHotkey;
        public static ConfigEntry<int> CfgPanelWidth;
        public static ConfigEntry<int> CfgPanelHeight;
        public static ConfigEntry<float> CfgPanelOpacity;
        public static ConfigEntry<Language> CfgLanguage;
        public static ConfigEntry<bool> CfgDisableFaceControllers;
        public static ConfigEntry<bool> CfgSyncEyeAndEyelash;

        public override void Load()
        {
            Instance = this;

            // Hotkeys
            CfgTogglePanelHotkey = Config.Bind(
                "Hotkeys",
                "Toggle panel",
                new KeyboardShortcut(KeyCode.F8),
                new ConfigDescription("Toggle blendshape panel on/off."));

            CfgRefreshPanelHotkey = Config.Bind(
                "Hotkeys",
                "Refresh list",
                new KeyboardShortcut(KeyCode.F9),
                new ConfigDescription("Rescan character and rebuild blendshape list."));

            // UI 外观（带范围，会在 ConfigurationManager 里变成滑条）
            CfgPanelWidth = Config.Bind(
                "UI",
                "Panel width",
                400,
                new ConfigDescription(
                    "Width of the blendshape panel.",
                    new AcceptableValueRange<int>(250, 800)));

            CfgPanelHeight = Config.Bind(
                "UI",
                "Panel height",
                500,
                new ConfigDescription(
                    "Height of the blendshape panel.",
                    new AcceptableValueRange<int>(300, 900)));

            CfgPanelOpacity = Config.Bind(
                "UI",
                "Panel opacity",
                1.0f,
                new ConfigDescription(
                    "Background opacity of the blendshape panel.",
                    new AcceptableValueRange<float>(0.3f, 1.0f)));

            CfgLanguage = Config.Bind(
                "UI",
                "Language",
                Language.Chinese,
                new ConfigDescription("UI language (Chinese / English / Japanese)."));

            CfgDisableFaceControllers = Config.Bind(
                "Control",
                "Disable built-in face controllers",
                false,
                new ConfigDescription(
                    "If enabled, built-in face/eye/lip controllers on the character will be disabled so manual BlendShape sliders take effect."));

            CfgSyncEyeAndEyelash = Config.Bind(
                "Control",
                "Sync eye and eyelash blendshapes",
                true,
                new ConfigDescription(
                    "If enabled, eye and eyelash blendshapes that share the same core name (e.g. eye_f00_def_cl) will change together."));

            ClassInjector.RegisterTypeInIl2Cpp<BlendshapeUI>();
            AddComponent<BlendshapeUI>();
            Log.LogInfo(PluginName + " " + PluginVersion + " loaded.");
        }
    }

    public class BlendshapeUI : MonoBehaviour
    {
        public BlendshapeUI(IntPtr ptr) : base(ptr) { }

        public static BlendshapeUI Instance;

        private class BlendshapePreset
        {
            public string Name;
            public Dictionary<string, float> Values = new Dictionary<string, float>();
        }

        private GameObject _targetCharacter;
        private readonly Dictionary<string, (SkinnedMeshRenderer renderer, int index)> _blendshapeMap
            = new Dictionary<string, (SkinnedMeshRenderer, int)>();

        // IMGUI 窗口参数（参考 HeelzGui）
        private Rect _windowRect;
        private bool _showUI = false;
        private GUI.WindowFunction _drawFunc;
        private Vector2 _scrollPos = Vector2.zero;
        private readonly List<string> _blendshapeKeys = new List<string>();
        private readonly Dictionary<string, string> _valueInputMap = new Dictionary<string, string>();
        private readonly Dictionary<string, List<string>> _categoryMap = new Dictionary<string, List<string>>();
        private readonly Dictionary<string, bool> _categoryFoldout = new Dictionary<string, bool>();
        private readonly List<MonoBehaviour> _disabledFaceControllers = new List<MonoBehaviour>();
        private bool _overrideFaceControllers = false;
        private readonly Dictionary<string, List<string>> _coreNameMap = new Dictionary<string, List<string>>();
        private bool _syncEyeAndEyelash = true;
        private readonly List<BlendshapePreset> _presets = new List<BlendshapePreset>();
        private int _presetNameCounter = 1;
        private string _newPresetName = "";

        private static readonly string[] CategoryOrder = new[]
        {
            "眼睛",
            "嘴巴",
            "眉毛",
            "脸部其它",
            "身体/其它"
        };

        private const int WINDOW_ID = 90001;

        private Language CurrentLanguage
        {
            get
            {
                return Plugin.CfgLanguage != null ? Plugin.CfgLanguage.Value : Language.Chinese;
            }
        }

        private void Awake()
        {
            Instance = this;
            _drawFunc = (GUI.WindowFunction)(Action<int>)DrawWindow;

            int w = (Plugin.CfgPanelWidth != null) ? Plugin.CfgPanelWidth.Value : 400;
            int h = (Plugin.CfgPanelHeight != null) ? Plugin.CfgPanelHeight.Value : 500;
            _windowRect = new Rect(50, 50, w, h);

            _overrideFaceControllers = Plugin.CfgDisableFaceControllers != null &&
                                       Plugin.CfgDisableFaceControllers.Value;
            _syncEyeAndEyelash = Plugin.CfgSyncEyeAndEyelash != null &&
                                 Plugin.CfgSyncEyeAndEyelash.Value;
        }

        void Start()
        {
            BuildCharacterAndUI();
            LoadPresets();
        }

        void Update()
        {
            // 使用配置的快捷键（兼容 ConfigurationManager）+ 回退到硬编码 F8/F9，方便调试
            bool togglePressed = false;
            bool refreshPressed = false;

            if (Plugin.CfgTogglePanelHotkey != null &&
                Plugin.CfgTogglePanelHotkey.Value.IsDown())
            {
                togglePressed = true;
            }
            if (Plugin.CfgRefreshPanelHotkey != null &&
                Plugin.CfgRefreshPanelHotkey.Value.IsDown())
            {
                refreshPressed = true;
            }

            // 回退：即使 KeyboardShortcut 配置坏了，直接按 F8/F9 也能生效
            if (Input.GetKeyDown(KeyCode.F8)) togglePressed = true;
            if (Input.GetKeyDown(KeyCode.F9)) refreshPressed = true;

            if (togglePressed)
            {
                _showUI = !_showUI;
                Debug.Log("[SimpleBlendshapePlugin] Toggle panel (IMGUI): " + _showUI);
            }

            if (refreshPressed)
            {
                BuildCharacterAndUI();
                Debug.Log("[SimpleBlendshapePlugin] Refresh blendshape list.");
            }

            // 如果 UI 打开，且鼠标在窗口内，则屏蔽游戏输入并显示鼠标（参考 HeelzGui）
            if (_showUI)
            {
                Vector2 mousePos = Input.mousePosition;
                mousePos.y = Screen.height - mousePos.y;

                if (_windowRect.Contains(mousePos))
                {
                    Input.ResetInputAxes();
                    Cursor.visible = true;
                    Cursor.lockState = CursorLockMode.None;
                }
            }
        }

        private void BuildCharacterAndUI()
        {
            FindCharacter();
            BuildBlendshapeCache();
            ApplyOverrideState();
        }

        private void FindCharacter()
        {
            _targetCharacter = null;

            // 1. 优先按照 CharaAnimeMgr 的命名规则查找（chaF/chaM/00）
            var allTransforms = UnityEngine.Object.FindObjectsOfType<Transform>();
            foreach (var t in allTransforms)
            {
                var go = t.gameObject;
                if (!go.activeInHierarchy) continue;

                if (go.name.Contains("chaF") || go.name.Contains("chaM") || go.name == "00")
                {
                    var renderers = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                    if (renderers != null && renderers.Length > 0)
                    {
                        // 只要任意一个 SkinnedMeshRenderer 有 BlendShape 就认作角色
                        if (renderers.Any(r => r != null && r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0))
                        {
                            _targetCharacter = go;
                            break;
                        }
                    }
                }
            }

            // 2. 回退：遍历全场所有 SkinnedMeshRenderer，找第一个带 BlendShape 的
            if (_targetCharacter == null)
            {
                var allRenderers = UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>(true);
                var candidate = allRenderers.FirstOrDefault(r =>
                    r != null &&
                    r.sharedMesh != null &&
                    r.sharedMesh.blendShapeCount > 0);

                if (candidate != null)
                {
                    _targetCharacter = candidate.transform.root.gameObject;
                }
            }

            if (_targetCharacter != null)
            {
                Debug.Log("[SimpleBlendshapePlugin] Target character: " + _targetCharacter.name);
            }
            else
            {
                Debug.LogWarning("[SimpleBlendshapePlugin] No character with SkinnedMeshRenderer (with BlendShapes) found.");
            }
        }

        private void BuildBlendshapeCache()
        {
            _blendshapeMap.Clear();
            _blendshapeKeys.Clear();
            _categoryMap.Clear();
            _categoryFoldout.Clear();
            _valueInputMap.Clear();
            _coreNameMap.Clear();

            if (_targetCharacter == null) return;

            var renderers = _targetCharacter.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var r in renderers)
            {
                if (r.sharedMesh == null) continue;

                var mesh = r.sharedMesh;
                int count = mesh.blendShapeCount;
                for (int i = 0; i < count; i++)
                {
                    string name = mesh.GetBlendShapeName(i);
                    if (string.IsNullOrEmpty(name)) continue;

                    string key = name.ToLowerInvariant();
                    _blendshapeMap[key] = (r, i);
                }
            }

            Debug.Log("[SimpleBlendshapePlugin] Blendshape count = " + _blendshapeMap.Count);

            _blendshapeKeys.AddRange(_blendshapeMap.Keys);
            _blendshapeKeys.Sort(StringComparer.Ordinal);

            foreach (var key in _blendshapeKeys)
            {
                var info = _blendshapeMap[key];
                string category = GetCategoryForBlendshape(key, info.renderer);
                List<string> list;
                if (!_categoryMap.TryGetValue(category, out list))
                {
                    list = new List<string>();
                    _categoryMap[category] = list;
                    _categoryFoldout[category] = true;
                }
                list.Add(key);

                // 记录核心名称映射，用于眼睛/睫毛同步
                string core = GetCoreName(key);
                if (!string.IsNullOrEmpty(core))
                {
                    List<string> sameList;
                    if (!_coreNameMap.TryGetValue(core, out sameList))
                    {
                        sameList = new List<string>();
                        _coreNameMap[core] = sameList;
                    }
                    sameList.Add(key);
                }
            }
        }

        public void SetBlendshape(string name, float value)
        {
            if (string.IsNullOrEmpty(name)) return;
            string key = name.ToLowerInvariant();

            if (_blendshapeMap.TryGetValue(key, out var info))
            {
                float v = Mathf.Clamp(value, 0f, 100f);
                info.renderer.SetBlendShapeWeight(info.index, v);
            }
            else
            {
                Debug.LogWarning("[SimpleBlendshapePlugin] Blendshape not found: " + name);
            }
        }

        private void OnGUI()
        {
            if (!_showUI) return;

            // 同步配置的宽高
            int wCfg = (Plugin.CfgPanelWidth != null) ? Plugin.CfgPanelWidth.Value : 400;
            int hCfg = (Plugin.CfgPanelHeight != null) ? Plugin.CfgPanelHeight.Value : 500;
            _windowRect.width = wCfg;
            _windowRect.height = hCfg;

            float opacity = (Plugin.CfgPanelOpacity != null) ? Plugin.CfgPanelOpacity.Value : 0.7f;

            Matrix4x4 oldMatrix = GUI.matrix;
            Color oldBg = GUI.backgroundColor;

            GUI.backgroundColor = new Color(0.1f, 0.1f, 0.1f, opacity);

            _windowRect = GUI.Window(
                WINDOW_ID,
                _windowRect,
                _drawFunc,
                GetText("WindowTitle"));

            // 防止窗口拖出屏幕
            _windowRect.x = Mathf.Clamp(_windowRect.x, 0, Screen.width - _windowRect.width);
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0, Screen.height - _windowRect.height);

            GUI.backgroundColor = oldBg;
            GUI.matrix = oldMatrix;
        }

        private void DrawWindow(int id)
        {
            // 顶部拖拽区域
            GUI.DragWindow(new Rect(0, 0, 10000, 20));

            GUILayout.BeginVertical();

            // 顶部关闭 + 刷新按钮
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUI.backgroundColor = Color.red;
            if (GUILayout.Button(GetText("BtnClose"), GUILayout.Width(60)))
            {
                _showUI = false;
            }
            GUI.backgroundColor = Color.white;
            if (GUILayout.Button(GetText("BtnRefresh"), GUILayout.Width(70)))
            {
                BuildCharacterAndUI();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(5);

            if (_targetCharacter == null)
            {
                GUILayout.Label(GetText("NoCharacter"), GUILayout.ExpandWidth(true));
            }
            else
            {
                GUILayout.Label(GetText("TargetCharacterPrefix") + _targetCharacter.name, GUILayout.ExpandWidth(true));
            }

            GUILayout.Space(5);

            // 接管表情控制开关（禁用游戏自带表情脚本）
            bool newOverride = GUILayout.Toggle(_overrideFaceControllers, GetText("ToggleOverride"));
            if (newOverride != _overrideFaceControllers)
            {
                _overrideFaceControllers = newOverride;
                if (Plugin.CfgDisableFaceControllers != null)
                    Plugin.CfgDisableFaceControllers.Value = newOverride;
                ApplyOverrideState();
            }

            // 眼睛 & 睫毛同步开关
            bool newSync = GUILayout.Toggle(_syncEyeAndEyelash, GetText("ToggleSync"));
            if (newSync != _syncEyeAndEyelash)
            {
                _syncEyeAndEyelash = newSync;
                if (Plugin.CfgSyncEyeAndEyelash != null)
                    Plugin.CfgSyncEyeAndEyelash.Value = newSync;
            }

            GUILayout.Space(5);

            if (_blendshapeMap.Count == 0)
            {
                GUILayout.Label(GetText("NoBlendshape"));
            }
            else
            {
                _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.ExpandHeight(true));

                foreach (var cat in CategoryOrder)
                {
                    List<string> list;
                    if (!_categoryMap.TryGetValue(cat, out list) || list == null || list.Count == 0)
                        continue;

                    bool isOpen;
                    if (!_categoryFoldout.TryGetValue(cat, out isOpen))
                    {
                        isOpen = true;
                    }

                    // 分类标题行
                    GUILayout.Space(4);
                    string catLabel = GetCategoryDisplayName(cat);
                    isOpen = GUILayout.Toggle(isOpen, "【" + catLabel + "】", "Button");
                    _categoryFoldout[cat] = isOpen;

                    if (!isOpen) continue;

                    foreach (var key in list)
                    {
                        if (!_blendshapeMap.TryGetValue(key, out var info) || info.renderer == null)
                            continue;

                        float current = info.renderer.GetBlendShapeWeight(info.index);

                        string inputText;
                        if (!_valueInputMap.TryGetValue(key, out inputText))
                        {
                            inputText = current.ToString("F1");
                            _valueInputMap[key] = inputText;
                        }

                        GUILayout.BeginHorizontal();
                        GUILayout.Label(key, GUILayout.Width(220));

                        // 滑条
                        float newValue = GUILayout.HorizontalSlider(current, 0f, 100f, GUILayout.ExpandWidth(true));

                        // 数字输入框
                        string newInput = GUILayout.TextField(inputText, GUILayout.Width(50));
                        if (newInput != inputText)
                        {
                            _valueInputMap[key] = newInput;
                            inputText = newInput;
                        }

                        // 一键重置按钮
                        if (GUILayout.Button("重置", GUILayout.Width(45)))
                        {
                            newValue = 0f;
                            inputText = "0";
                            _valueInputMap[key] = inputText;
                        }

                        GUILayout.EndHorizontal();

                        // 如果用户在文本框里输入了数值，则以文本为优先
                        float typed;
                        if (float.TryParse(inputText, out typed))
                        {
                            typed = Mathf.Clamp(typed, 0f, 100f);
                            if (Mathf.Abs(typed - current) > 0.001f)
                            {
                                newValue = typed;
                            }
                        }

                        // 应用最终数值
                        if (Mathf.Abs(newValue - current) > 0.001f)
                        {
                            info.renderer.SetBlendShapeWeight(info.index, newValue);
                            _valueInputMap[key] = newValue.ToString("F1");

                            // 如有需要，同步同核心名称的眼睛/睫毛形态键
                            if (_syncEyeAndEyelash)
                            {
                                SyncCoreGroup(key, newValue);
                            }
                        }
                    }
                }

                GUILayout.EndScrollView();
            }

            GUILayout.Space(8);

            // 预设管理区域
            GUILayout.Label(GetText("PresetsHeader"));

            int removeIndex = -1;
            for (int i = 0; i < _presets.Count; i++)
            {
                var preset = _presets[i];
                if (preset == null) continue;

                GUILayout.BeginHorizontal();

                string newName = GUILayout.TextField(preset.Name ?? "", GUILayout.Width(160));
                if (newName != preset.Name)
                {
                    preset.Name = string.IsNullOrWhiteSpace(newName) ? preset.Name : newName.Trim();
                    SavePresets();
                }

                if (GUILayout.Button(GetText("PresetApply"), GUILayout.Width(70)))
                {
                    ApplyPreset(preset);
                }

                if (GUILayout.Button(GetText("PresetOverwrite"), GUILayout.Width(90)))
                {
                    CaptureCurrentToPreset(preset);
                    SavePresets();
                }

                if (GUILayout.Button(GetText("PresetDelete"), GUILayout.Width(40)))
                {
                    removeIndex = i;
                }

                GUILayout.EndHorizontal();
            }

            if (removeIndex >= 0 && removeIndex < _presets.Count)
            {
                _presets.RemoveAt(removeIndex);
                SavePresets();
            }

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.Label(GetText("NewPresetName"), GUILayout.Width(90));
            _newPresetName = GUILayout.TextField(_newPresetName ?? "", GUILayout.Width(160));
            if (GUILayout.Button(GetText("NewPresetSave"), GUILayout.Width(140)))
            {
                SaveCurrentAsNewPreset();
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }
        
        private string GetCategoryForBlendshape(string key, SkinnedMeshRenderer renderer)
        {
            string k = key ?? string.Empty;
            k = k.ToLowerInvariant();
            string rname = (renderer != null && renderer.name != null) ? renderer.name.ToLowerInvariant() : string.Empty;

            if (k.Contains("eye") || k.Contains("blink") || k.Contains("wink") ||
                k.Contains("namida") || rname.Contains("eye"))
                return "眼睛";

            if (k.Contains("kuti") || k.Contains("mouth") || k.Contains("lip") ||
                k.Contains("vo_") || k.Contains("tooth") ||
                k.Contains("canine") || k.Contains("tang") || k.Contains("tongue"))
                return "嘴巴";

            if (k.Contains("brow") || k.Contains("mayu") || k.Contains("mayuge"))
                return "眉毛";

            if (rname.Contains("face") || rname.Contains("head"))
                return "脸部其它";

            return "身体/其它";
        }

        private string GetCategoryDisplayName(string catKey)
        {
            switch (catKey)
            {
                case "眼睛":
                    return GetText("CatEyes");
                case "嘴巴":
                    return GetText("CatMouth");
                case "眉毛":
                    return GetText("CatBrows");
                case "脸部其它":
                    return GetText("CatFaceOther");
                case "身体/其它":
                default:
                    return GetText("CatBodyOther");
            }
        }

        private void ApplyOverrideState()
        {
            if (_targetCharacter == null) return;

            if (_overrideFaceControllers)
                DisableFaceControllers(_targetCharacter);
            else
                EnableFaceControllers();
        }

        private void DisableFaceControllers(GameObject root)
        {
            EnableFaceControllers(); // 清理之前的
            if (root == null) return;

            var comps = root.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var comp in comps)
            {
                if (comp == null) continue;
                string typeName;
                try
                {
                    var t = comp.GetIl2CppType();
                    if (t == null) continue;
                    typeName = t.Name ?? string.Empty;
                }
                catch
                {
                    continue;
                }

                string lower = typeName.ToLowerInvariant();

                bool isFaceController =
                    lower.Contains("faceblendshape") ||
                    lower.Contains("ulipsync") ||
                    lower.Contains("lipsync") ||
                    lower.Contains("facecontroller") ||
                    lower.Contains("eyelook") ||
                    lower.Contains("lookat") ||
                    lower.Contains("blink");

                if (isFaceController && comp.enabled)
                {
                    comp.enabled = false;
                    _disabledFaceControllers.Add(comp);
                }
            }
        }

        private void EnableFaceControllers()
        {
            if (_disabledFaceControllers.Count == 0) return;
            foreach (var comp in _disabledFaceControllers)
            {
                if (comp == null) continue;
                try { comp.enabled = true; } catch { }
            }
            _disabledFaceControllers.Clear();
        }

        private string GetCoreName(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            int idx = key.LastIndexOf('.');
            if (idx >= 0 && idx < key.Length - 1)
                return key.Substring(idx + 1);
            return key;
        }

        private bool IsEyeFamilyKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            // 只同步 face.*, eyelash.*, eyelid.* 这三类
            string k = key.ToLowerInvariant();
            return k.Contains("face.") || k.Contains("eyelash.") || k.Contains("eyelid.");
        }

        private void SyncCoreGroup(string sourceKey, float value)
        {
            if (string.IsNullOrEmpty(sourceKey)) return;

            string core = GetCoreName(sourceKey);
            if (string.IsNullOrEmpty(core)) return;

            List<string> list;
            if (!_coreNameMap.TryGetValue(core, out list) || list == null) return;

            foreach (var otherKey in list)
            {
                if (otherKey == sourceKey) continue;

                if (!_blendshapeMap.TryGetValue(otherKey, out var info) || info.renderer == null)
                    continue;

                // 只同步 face./eyelash./eyelid. 开头的键，避免影响其他部位
                if (!IsEyeFamilyKey(otherKey)) continue;

                float current = info.renderer.GetBlendShapeWeight(info.index);
                if (Mathf.Abs(current - value) > 0.001f)
                {
                    info.renderer.SetBlendShapeWeight(info.index, value);
                    _valueInputMap[otherKey] = value.ToString("F1");
                }
            }
        }

        private string GetPresetFilePath()
        {
            try
            {
                return Path.Combine(Paths.ConfigPath, "SimpleBlendshapePresets.txt");
            }
            catch
            {
                return "SimpleBlendshapePresets.txt";
            }
        }

        private void LoadPresets()
        {
            _presets.Clear();

            string path = GetPresetFilePath();
            if (!File.Exists(path)) return;

            try
            {
                var lines = File.ReadAllLines(path);
                BlendshapePreset current = null;

                foreach (var raw in lines)
                {
                    string line = raw.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    if (line.StartsWith("Preset:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (current != null && !string.IsNullOrEmpty(current.Name))
                            _presets.Add(current);

                        current = new BlendshapePreset
                        {
                            Name = line.Substring("Preset:".Length).Trim()
                        };
                    }
                    else if (current != null)
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0 || eq >= line.Length - 1) continue;
                        string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string valStr = line.Substring(eq + 1).Trim();

                        float val;
                        if (float.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
                        {
                            current.Values[key] = val;
                        }
                    }
                }

                if (current != null && !string.IsNullOrEmpty(current.Name))
                    _presets.Add(current);
            }
            catch (Exception e)
            {
                Debug.LogError("[SimpleBlendshapePlugin] Failed to load presets: " + e);
            }
        }

        private void SavePresets()
        {
            string path = GetPresetFilePath();
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var outLines = new List<string>();

                foreach (var p in _presets)
                {
                    if (p == null || string.IsNullOrEmpty(p.Name)) continue;

                    outLines.Add("Preset:" + p.Name);
                    foreach (var kvp in p.Values)
                    {
                        outLines.Add(kvp.Key + "=" + kvp.Value.ToString(CultureInfo.InvariantCulture));
                    }
                    outLines.Add(string.Empty);
                }

                File.WriteAllLines(path, outLines.ToArray());
            }
            catch (Exception e)
            {
                Debug.LogError("[SimpleBlendshapePlugin] Failed to save presets: " + e);
            }
        }

        private void CaptureCurrentToPreset(BlendshapePreset preset)
        {
            if (preset == null) return;

            preset.Values.Clear();
            foreach (var kvp in _blendshapeMap)
            {
                var info = kvp.Value;
                if (info.renderer == null) continue;
                float v = info.renderer.GetBlendShapeWeight(info.index);
                preset.Values[kvp.Key] = v;
            }
        }

        private void ApplyPreset(BlendshapePreset preset)
        {
            if (preset == null) return;

            foreach (var kvp in preset.Values)
            {
                string key = kvp.Key;
                float value = kvp.Value;

                if (_blendshapeMap.TryGetValue(key, out var info) && info.renderer != null)
                {
                    info.renderer.SetBlendShapeWeight(info.index, value);
                    _valueInputMap[key] = value.ToString("F1");
                }
            }
        }

        private void SaveCurrentAsNewPreset()
        {
            string name = string.IsNullOrWhiteSpace(_newPresetName)
                ? GetText("PresetDefaultPrefix") + _presetNameCounter++
                : _newPresetName.Trim();

            var preset = new BlendshapePreset { Name = name };
            CaptureCurrentToPreset(preset);
            _presets.Add(preset);
            SavePresets();
            _newPresetName = "";
        }

        private string GetText(string key)
        {
            var lang = CurrentLanguage;

            switch (key)
            {
                case "WindowTitle":
                    return lang == Language.English
                        ? "Blendshape Adjust"
                        : lang == Language.Japanese ? "Blendshape 調整" : "Blendshape 调整";

                case "BtnClose":
                    return lang == Language.English ? "Close" : lang == Language.Japanese ? "閉じる" : "关闭";
                case "BtnRefresh":
                    return lang == Language.English ? "Refresh" : lang == Language.Japanese ? "更新" : "刷新";

                case "NoCharacter":
                    return lang == Language.English
                        ? "No character with SkinnedMeshRenderer found."
                        : lang == Language.Japanese
                            ? "SkinnedMeshRenderer を持つキャラが見つかりません。"
                            : "未找到有 SkinnedMeshRenderer 的角色。";

                case "TargetCharacterPrefix":
                    return lang == Language.English
                        ? "Target character: "
                        : lang == Language.Japanese ? "対象キャラ: " : "目标角色: ";

                case "ToggleOverride":
                    return lang == Language.English
                        ? "Take over facial control (disable built-in face/eye/lip scripts)"
                        : lang == Language.Japanese
                            ? "表情制御を乗っ取り（ゲーム標準の顔・口・視線スクリプトを無効化）"
                            : "接管表情控制（禁用游戏自带表情/口型脚本）";

                case "ToggleSync":
                    return lang == Language.English
                        ? "Sync eye & eyelash (same core blendshape name)"
                        : lang == Language.Japanese
                            ? "目とまつ毛を同期（同じコア名のブレンドシェイプ）"
                            : "同步眼睛与眼睫毛（同名核心形态键一起变化）";

                case "NoBlendshape":
                    return lang == Language.English
                        ? "Current character has no BlendShapes."
                        : lang == Language.Japanese
                            ? "現在のキャラにはブレンドシェイプがありません。"
                            : "当前角色没有 BlendShape。";

                case "PresetsHeader":
                    return lang == Language.English
                        ? "Blendshape presets:"
                        : lang == Language.Japanese
                            ? "ブレンドシェイププリセット："
                            : "形态键预设：";

                case "PresetApply":
                    return lang == Language.English ? "Apply" : lang == Language.Japanese ? "適用" : "应用";
                case "PresetOverwrite":
                    return lang == Language.English ? "Overwrite" : lang == Language.Japanese ? "上書き" : "覆写当前";
                case "PresetDelete":
                    return lang == Language.English ? "Del" : lang == Language.Japanese ? "削" : "删";

                case "NewPresetName":
                    return lang == Language.English
                        ? "New preset:"
                        : lang == Language.Japanese ? "新規プリセット:" : "新预设名:";
                case "NewPresetSave":
                    return lang == Language.English
                        ? "Save current as new"
                        : lang == Language.Japanese ? "現在を新規として保存" : "保存当前为新";

                case "PresetDefaultPrefix":
                    return lang == Language.English
                        ? "Preset"
                        : lang == Language.Japanese ? "プリセット" : "预设";

                case "CatEyes":
                    return lang == Language.English ? "Eyes" : lang == Language.Japanese ? "目" : "眼睛";
                case "CatMouth":
                    return lang == Language.English ? "Mouth" : lang == Language.Japanese ? "口" : "嘴巴";
                case "CatBrows":
                    return lang == Language.English ? "Brows" : lang == Language.Japanese ? "眉" : "眉毛";
                case "CatFaceOther":
                    return lang == Language.English ? "Face (other)" : lang == Language.Japanese ? "顔（その他）" : "脸部其它";
                case "CatBodyOther":
                    return lang == Language.English ? "Body / Other" : lang == Language.Japanese ? "体 / その他" : "身体/其它";
            }

            // fallback
            return key;
        }
    }

}

