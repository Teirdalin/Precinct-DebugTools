using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using MelonLoader.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.SceneManagement;

[assembly: MelonInfo(typeof(DebugToolsMod), "Debug Tools", "1.0.0", "Teirdalin")]
[assembly: MelonGame("Fallen Tree Games Ltd", "The Precinct")]

public sealed class DebugToolsMod : MelonMod
{
    private HarmonyLib.Harmony _harmony;
    public override void OnInitializeMelon()
    {
        try
        {
            _harmony = new HarmonyLib.Harmony("com.teirdalin.debugtools");
            _harmony.PatchAll(typeof(DebugToolsMod).Assembly);
            DebugTools.Attach();
            DebugTools.CameraGrabbed += cam => { if (cam) MelonLogger.Msg($"[Grab] Camera: {cam.name}"); };
            DebugTools.TargetInspected += go => { if (go) MelonLogger.Msg($"[Inspect] {go.name}"); };
            MelonLogger.Msg("[Init] Debug Tools ready. Press F12.");
        }
        catch (Exception ex) { MelonLogger.Error("[Init EX] " + ex); }
    }
    public override void OnDeinitializeMelon()
    {
        try { DebugTools.Detach(); _harmony?.UnpatchSelf(); } catch { }
        MelonLogger.Msg("[Deinit] Debug Tools unloaded.");
    }
}

public static class DebugTools
{
    private const string GO_NAME = "[SCM DebugTools Overlay]";
    private static DebugOverlay _overlay;
    internal static bool BlockAllInput { get; private set; }
    public static event Action<Camera> CameraGrabbed;
    public static event Action<GameObject> TargetInspected;
    internal static void InvokeGrab(Camera c) => CameraGrabbed?.Invoke(c);
    internal static void InvokeInspect(GameObject go) => TargetInspected?.Invoke(go);
    internal static void SetBlockInput(bool block) => BlockAllInput = block;

    public static void Attach()
    {
        try
        {
            if (!ClassInjector.IsTypeRegisteredInIl2Cpp(typeof(DebugOverlay)))
                ClassInjector.RegisterTypeInIl2Cpp<DebugOverlay>();
            var existing = GameObject.Find(GO_NAME);
            if (existing == null)
            {
                existing = new GameObject(GO_NAME) { hideFlags = HideFlags.DontSave };
                UnityEngine.Object.DontDestroyOnLoad(existing);
            }
            _overlay = existing.GetComponent<DebugOverlay>();
            if (_overlay == null)
                _overlay = existing.AddComponent<DebugOverlay>();
            MelonLogger.Msg("[DebugTools] Overlay attached");
        }
        catch (Exception e) { MelonLogger.Error("[DebugTools.Attach EX] " + e); }
    }

    public static void Detach()
    {
        try
        {
            var existing = GameObject.Find(GO_NAME);
            if (existing != null) UnityEngine.Object.Destroy(existing);
            _overlay = null;
            MelonLogger.Msg("[DebugTools] Overlay detached");
        }
        catch (Exception e) { MelonLogger.Error("[DebugTools.Detach EX] " + e); }
    }

    public sealed class DebugOverlay : MonoBehaviour
    {
        public DebugOverlay(IntPtr ptr) : base(ptr) { }
        public DebugOverlay() : base(ClassInjector.DerivedConstructorPointer<DebugOverlay>()) { ClassInjector.DerivedConstructorBody(this); }

        private bool _visible;
        private Rect _rect = new Rect(80, 80, 960, 720);
        private Vector2 _scroll;
        private enum RootPane { Home, Grabber, Weather }
        private RootPane _root = RootPane.Home;
        private enum GrabberPane { Menu, Cameras, ObjectSearch, Nearby, ObjectActions, ComponentList, ComponentEditor }
        private GrabberPane _grabberPane = GrabberPane.Menu;

        private CursorLockMode _prevLock;
        private bool _prevCursorVisible;
        private float _prevTimeScale = 1f;

        private Camera[] _cams = Array.Empty<Camera>();
        private Transform[] _matches = Array.Empty<Transform>();
        private string _query = "";

        private Transform[] _near = Array.Empty<Transform>();
        private float[] _nearDist = Array.Empty<float>();
        private float _nearRadius = 16f;
        private int _nearLimit = 128;
        private bool _playerBodyOnly;

        private bool _dragging;
        private Vector2 _dragOffset;

        private GameObject _selectedGO;
        private string _selectedPath = "";
        private Component _selectedComponent;

        private readonly Dictionary<string, string> _buffers = new Dictionary<string, string>(1024);
        private class ChangeRecord
        {
            public Component Component;
            public bool IsField;
            public IntPtr Member;
            public bool IsStatic;
            public FieldKind Kind;
            public string OriginalValue;
        }
        private class FreezeRecord
        {
            public Component Component;
            public bool IsField;
            public IntPtr Member;
            public bool IsStatic;
            public FieldKind Kind;
            public string Value;
        }

        private readonly Dictionary<string, ChangeRecord> _changes = new Dictionary<string, ChangeRecord>(128);
        private readonly Dictionary<string, FreezeRecord> _frozen = new Dictionary<string, FreezeRecord>(128);

        private static readonly HashSet<string> BODY_PARTS =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CC_Base_Head","CC_Base_NeckTwist01","InteractionPoint","CC_Base_L_Clavicle","CC_Base_R_Clavicle",
            "CC_Base_L_Upperarm","CC_Base_R_Upperarm","AudioListener","CC_Base_Spine02","CC_Base_L_Forearm",
            "CC_Base_Spine01","CC_Base_Waist","CC_Base_R_Forearm","CC_Base_L_Thumb1","CC_Base_Pelvis",
            "CC_Base_Hip","CC_Base_L_Thigh","LeftEquipPoint","CC_Base_L_Hand","CC_Base_L_Thumb2",
            "hitBox","CC_Base_L_Thumb3","CC_Base_R_Thigh","CC_Base_L_Mid1","CC_Base_L_Mid2","CC_Base_L_Mid3",
            "CC_Base_R_Thumb1","DefaultEquipPoint","CC_Base_R_Hand","CC_Base_R_Thumb2","CC_Base_R_Thumb3",
            "CC_Base_R_Mid1","CC_Base_R_Mid2","CC_Base_R_Mid3","CC_Base_L_Calf","CC_Base_R_Calf",
            "Restrain Offset","ButtonPushTransform","FakeShadow_Character(Clone)","CC_Base_L_Foot","CC_Base_R_Foot",
            "RL_BoneRoot","CC_Skin","CC_Clothes","POL_PoliceMale_Player","Models","sourceDefault",
            "footStepSource","Player(Clone)","AlwaysEnabled","PlayerEffects","PlayerAimAtTransform","PlayerGroup"
        };

        private void Awake()
        {
            useGUILayout = true;
            ScanCameras();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F12))
                SetMenuVisible(!_visible);
            if (_visible)
            {
                if (Input.GetKeyDown(KeyCode.Escape))
                    SetMenuVisible(false);
                // (removed) Input.ResetInputAxes(); // avoid nuking input every frame

            }
        }

        private void LateUpdate()
        {
            if (_visible)
                Time.timeScale = 0f;
            ApplyFrozenValues();
        }

        private void SetMenuVisible(bool vis)
        {
            if (_visible == vis) return;
            _visible = vis;
            if (_visible)
            {
                _prevLock = Cursor.lockState;
                _prevCursorVisible = Cursor.visible;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                DebugTools.SetBlockInput(true);
                Input.ResetInputAxes();
                _prevTimeScale = Time.timeScale;
                Time.timeScale = 0f;
            }
            else
            {                Time.timeScale = _prevTimeScale;

                DebugTools.SetBlockInput(false);
                Cursor.lockState = _prevLock;
                Cursor.visible = _prevCursorVisible;
            }
        }

        private void OnGUI()
        {
            if (!_visible) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            var prev = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.35f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = prev;

            GUILayout.BeginArea(_rect, "Debug Tools 1.0.0 — F12", GUI.skin.window);
            DrawDragHandle();
            GUILayout.Space(6);

            switch (_root)
            {
                case RootPane.Home:
                    GUILayout.Label("Select a tool:");
                    GUILayout.Space(8);
                    if (GUILayout.Button("Grabber", GUILayout.Height(40))) { _grabberPane = GrabberPane.Menu; _root = RootPane.Grabber; }
                    GUILayout.Space(6);
                    if (GUILayout.Button("Weather Manager", GUILayout.Height(40))) _root = RootPane.Weather;
                    break;
                case RootPane.Grabber:
                    DrawGrabber();
                    break;
                case RootPane.Weather:
                    DrawWeatherManager();
                    break;
            }

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (_root != RootPane.Home)
            {
                if (GUILayout.Button("‹ Back", GUILayout.Width(90))) _root = RootPane.Home;
            }
            GUILayout.FlexibleSpace();
            if (_changes.Count > 0 && GUILayout.Button("Revert Changes", GUILayout.Width(140)))
                RevertAllChanges();
            if (GUILayout.Button("Close (Esc)", GUILayout.Width(120))) SetMenuVisible(false);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void DrawDragHandle()
        {
            var e = Event.current;
            var bar = new Rect(0, 0, _rect.width, 22);
            var prev = GUI.color;
            GUI.color = new Color(1, 1, 1, 0.1f);
            GUI.DrawTexture(new Rect(8, 22, _rect.width - 16, 1), Texture2D.whiteTexture);
            GUI.color = prev;
            if (e == null) return;
            if (e.type == EventType.MouseDown && bar.Contains(e.mousePosition)) { _dragging = true; _dragOffset = e.mousePosition; e.Use(); }
            else if (e.type == EventType.MouseDrag && _dragging) { var d = (Vector2)e.mousePosition - _dragOffset; _rect.position += d; e.Use(); }
            else if (e.type == EventType.MouseUp) { _dragging = false; }
        }

        private void DrawGrabber()
        {
            if (_grabberPane == GrabberPane.Menu)
            {
                GUILayout.Label("Grabber:");
                GUILayout.Space(8);
                if (GUILayout.Button("List Cameras", GUILayout.Height(32))) { ScanCameras(); _grabberPane = GrabberPane.Cameras; }
                GUILayout.Space(6);
                if (GUILayout.Button("Search Objects", GUILayout.Height(32))) { _matches = Array.Empty<Transform>(); _query = ""; _grabberPane = GrabberPane.ObjectSearch; }
                GUILayout.Space(6);
                if (GUILayout.Button("Nearby (Player(Clone))", GUILayout.Height(32))) { RefreshNearby(); _grabberPane = GrabberPane.Nearby; }
                return;
            }

            if (_grabberPane == GrabberPane.Cameras)
            {
                GUILayout.Label("Cameras (click for actions; Shift+Click = Grab+Print)");
                GUILayout.Space(6);
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
                foreach (var c in _cams)
                {
                    if (c == null) continue;
                    var label = c.name + (c.isActiveAndEnabled ? "" : "  (disabled)");
                    if (GUILayout.Button(label, GUILayout.Height(24)))
                    {
                        bool grab = IsShiftHeld();
                        if (grab) TryInspectGO(c.gameObject, c);
                        else SelectObject(c.gameObject);
                    }
                }
                GUILayout.EndScrollView();
                GUILayout.Space(6);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Rescan", GUILayout.Width(100))) ScanCameras();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("‹ Back", GUILayout.Width(90))) _grabberPane = GrabberPane.Menu;
                GUILayout.EndHorizontal();
                return;
            }

            if (_grabberPane == GrabberPane.ObjectSearch)
            {
                GUILayout.Label("Search by name. Click result for actions (Shift+Click to Grab if it has Camera).");
                GUILayout.Space(6);
                GUILayout.BeginHorizontal();
                _query = GUILayout.TextField(_query ?? "", GUILayout.ExpandWidth(true));
                if (GUILayout.Button("Find", GUILayout.Width(90))) DoSearch();
                GUILayout.EndHorizontal();
                var e = Event.current;
                if (e != null && e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)) { DoSearch(); e.Use(); }
                GUILayout.Space(6);
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
                foreach (var t in _matches)
                {
                    if (t == null) continue;
                    var path = BuildPath(t);
                    if (GUILayout.Button(path, GUILayout.Height(22)))
                    {
                        var cam = t.GetComponent<Camera>();
                        bool grab = IsShiftHeld() && (cam != null);
                        if (grab) TryInspectGO(t.gameObject, cam);
                        else SelectObject(t.gameObject);
                    }
                }
                GUILayout.EndScrollView();
                GUILayout.Space(6);
                if (GUILayout.Button("‹ Back", GUILayout.Width(90))) _grabberPane = GrabberPane.Menu;
                return;
            }

            if (_grabberPane == GrabberPane.Nearby)
            {
                GUILayout.Label("Objects near Player(Clone) (fallback: Main Camera). Click for actions (Shift+Click = Grab+Print).");
                GUILayout.Label(_playerBodyOnly ? "Filter: ONLY body parts" : "Filter: HIDE body parts");
                GUILayout.Space(6);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Radius:", GUILayout.Width(60));
                float.TryParse(GUILayout.TextField(_nearRadius.ToString("F1", CultureInfo.InvariantCulture), GUILayout.Width(60)), NumberStyles.Float, CultureInfo.InvariantCulture, out _nearRadius);
                _nearRadius = Mathf.Clamp(_nearRadius, 1f, 200f);
                GUILayout.Space(12);
                GUILayout.Label("Limit:", GUILayout.Width(50));
                int.TryParse(GUILayout.TextField(_nearLimit.ToString(CultureInfo.InvariantCulture), GUILayout.Width(60)), out _nearLimit);
                _nearLimit = Mathf.Clamp(_nearLimit, 1, 1024);
                GUILayout.Space(12);
                _playerBodyOnly = GUILayout.Toggle(_playerBodyOnly, "Player Body", GUILayout.Width(110));
                GUILayout.Space(12);
                if (GUILayout.Button("Refresh", GUILayout.Width(90))) RefreshNearby();
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(6);
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
                for (int i = 0; i < _near.Length; i++)
                {
                    var t = _near[i];
                    if (t == null) continue;
                    var label = $"{_nearDist[i]:F2} — {BuildPath(t)}";
                    if (GUILayout.Button(label, GUILayout.Height(22)))
                    {
                        var cam = t.GetComponent<Camera>();
                        bool grab = IsShiftHeld() && (cam != null);
                        if (grab) TryInspectGO(t.gameObject, cam);
                        else SelectObject(t.gameObject);
                    }
                }
                GUILayout.EndScrollView();
                GUILayout.Space(6);
                if (GUILayout.Button("‹ Back", GUILayout.Width(90))) _grabberPane = GrabberPane.Menu;
                return;
            }

            if (_grabberPane == GrabberPane.ObjectActions)
            {
                GUILayout.Label($"Target: {_selectedPath}");
                GUILayout.Space(8);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Print Info", GUILayout.Height(32), GUILayout.Width(160))) DumpComponentsToLogAndFile(_selectedGO);
                GUILayout.Space(12);
                if (GUILayout.Button("Modify", GUILayout.Height(32), GUILayout.Width(160))) _grabberPane = GrabberPane.ComponentList;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(12);
                if (GUILayout.Button("‹ Back", GUILayout.Width(90))) _grabberPane = GrabberPane.Menu;
                return;
            }

            if (_grabberPane == GrabberPane.ComponentList)
            {
                if (_selectedGO == null) { _grabberPane = GrabberPane.Menu; return; }
                GUILayout.Label($"Components on {_selectedGO.name}:");
                GUILayout.Space(6);
                var comps = _selectedGO.GetComponents<Component>();
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    string typeName = GetSafeTypeName(c);
                    if (GUILayout.Button(typeName, GUILayout.Height(22))) { _selectedComponent = c; _grabberPane = GrabberPane.ComponentEditor; }
                }
                GUILayout.EndScrollView();
                GUILayout.Space(6);
                if (GUILayout.Button("‹ Back", GUILayout.Width(90))) _grabberPane = GrabberPane.ObjectActions;
                return;
            }

            if (_grabberPane == GrabberPane.ComponentEditor)
            {
                if (_selectedComponent == null) { _grabberPane = GrabberPane.ComponentList; return; }
                string typeName = GetSafeTypeName(_selectedComponent);
                GUILayout.Label(typeName);
                GUILayout.Space(6);

                var obj = (Il2CppObjectBase)(object)_selectedComponent;
                IntPtr klass = IL2CPP.il2cpp_object_get_class(obj.Pointer);

                GUILayout.Label("Fields");
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
                IntPtr fIter = IntPtr.Zero;
                IntPtr field;
                while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref fIter)) != IntPtr.Zero)
                {
                    var attr = (FieldAttributes)IL2CPP.il2cpp_field_get_flags(field);
                    if ((attr & FieldAttributes.Public) == 0) continue;
                    string fieldName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_field_get_name(field));
                    bool isStatic = (attr & FieldAttributes.Static) != 0;

                    IntPtr ftype = IL2CPP.il2cpp_field_get_type(field);
                    string typeDisplay = TypeFullNameFromIl2CppType(ftype);
                    var kind = KindFromIl2CppType(ftype);

                    string curVal = SafeGetFieldValueStringTyped(obj.Pointer, field, isStatic, ftype);

                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"{fieldName} : {typeDisplay}", GUILayout.Width(420));
                    GUILayout.Label(curVal ?? "<unknown>", GUILayout.MinWidth(140));

                    string key = "F|" + _selectedComponent.GetInstanceID().ToString() + "|" + fieldName;
                    if (!_buffers.TryGetValue(key, out var buf)) buf = curVal ?? "";

                    if (kind == FieldKind.Bool)
                    {
                        bool parsed = curVal != null && (curVal.Equals("true", StringComparison.OrdinalIgnoreCase) || curVal.Equals("True"));
                        bool newVal = GUILayout.Toggle(parsed, "value", GUILayout.Width(80));
                        if (newVal != parsed)
                        {
                            if (!_changes.ContainsKey(key))
                                _changes[key] = new ChangeRecord { Component = _selectedComponent, IsField = true, Member = field, IsStatic = isStatic, Kind = FieldKind.Bool, OriginalValue = curVal ?? "false" };
                            if (ApplyBoolField(obj.Pointer, field, isStatic, newVal))
                            {
                                _buffers[key] = newVal ? "true" : "false";
                                MelonLogger.Msg($"[DebugTools] Set {typeName}.{fieldName} = {_buffers[key]} (was {curVal})");
                            }
                        }
                        bool frozen = _frozen.ContainsKey(key);
                        bool newFrozen = GUILayout.Toggle(frozen, "Freeze", GUILayout.Width(70));
                        if (newFrozen != frozen)
                        {
                            if (newFrozen)
                                _frozen[key] = new FreezeRecord { Component = _selectedComponent, IsField = true, Member = field, IsStatic = isStatic, Kind = FieldKind.Bool, Value = _buffers[key] };
                            else
                                _frozen.Remove(key);
                        }
                    }
                    else if (kind == FieldKind.Int || kind == FieldKind.Float || kind == FieldKind.String)
                    {
                        string newBuf = GUILayout.TextField(buf, GUILayout.MinWidth(200));
                        if (!ReferenceEquals(newBuf, buf)) { buf = newBuf; _buffers[key] = buf; }
                        if (GUILayout.Button("Set", GUILayout.Width(60)))
                        {
                            bool ok = false;
                            if (kind == FieldKind.Int) ok = ApplyIntField(obj.Pointer, field, isStatic, TryParseInt(buf, curVal));
                            else if (kind == FieldKind.Float) ok = ApplyFloatField(obj.Pointer, field, isStatic, TryParseFloat(buf, curVal));
                            else if (kind == FieldKind.String) ok = ApplyStringField(obj.Pointer, field, isStatic, buf ?? "");
                            if (ok)
                            {
                                if (!_changes.ContainsKey(key))
                                    _changes[key] = new ChangeRecord { Component = _selectedComponent, IsField = true, Member = field, IsStatic = isStatic, Kind = kind, OriginalValue = curVal ?? "" };
                                _buffers[key] = SafeGetFieldValueStringTyped(obj.Pointer, field, isStatic, ftype) ?? buf;
                                MelonLogger.Msg($"[DebugTools] Set {typeName}.{fieldName} = {_buffers[key]} (was {curVal})");
                            }
                        }
                        bool frozen = _frozen.ContainsKey(key);
                        bool newFrozen = GUILayout.Toggle(frozen, "Freeze", GUILayout.Width(70));
                        if (newFrozen != frozen)
                        {
                            if (newFrozen)
                                _frozen[key] = new FreezeRecord { Component = _selectedComponent, IsField = true, Member = field, IsStatic = isStatic, Kind = kind, Value = _buffers[key] };
                            else
                                _frozen.Remove(key);
                        }
                    }
                    else
                    {
                        GUILayout.Label("[unsupported]", GUILayout.Width(110));
                    }
                    GUILayout.EndHorizontal();
                }

                GUILayout.Space(10);
                GUILayout.Label("Properties");
                IntPtr pIter = IntPtr.Zero;
                IntPtr prop;
                while ((prop = IL2CPP.il2cpp_class_get_properties(klass, ref pIter)) != IntPtr.Zero)
                {
                    IntPtr getter = IL2CPP.il2cpp_property_get_get_method(prop);
                    IntPtr setter = IL2CPP.il2cpp_property_get_set_method(prop);
                    if (getter == IntPtr.Zero || setter == IntPtr.Zero) continue;
                    uint gflags = 0, sflags = 0;
                    var gmf = (MethodAttributes)IL2CPP.il2cpp_method_get_flags(getter, ref gflags);
                    var smf = (MethodAttributes)IL2CPP.il2cpp_method_get_flags(setter, ref sflags);
                    if ((gmf & MethodAttributes.Public) == 0 || (smf & MethodAttributes.Public) == 0) continue;
                    int pc = (int)IL2CPP.il2cpp_method_get_param_count(setter);
                    if (pc != 1) continue;
                    bool isStatic = (gmf & MethodAttributes.Static) != 0;

                    IntPtr ptype = IL2CPP.il2cpp_method_get_param(setter, 0);
                    string typeDisplay = TypeFullNameFromIl2CppType(ptype);
                    var kind = KindFromIl2CppType(ptype);

                    string propName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_property_get_name(prop));
                    string curVal = SafeGetPropertyValueStringTyped(obj.Pointer, getter, isStatic, IL2CPP.il2cpp_method_get_return_type(getter));

                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"{propName} : {typeDisplay}", GUILayout.Width(420));
                    GUILayout.Label(curVal ?? "<unknown>", GUILayout.MinWidth(140));

                    string key = "P|" + _selectedComponent.GetInstanceID().ToString() + "|" + propName;
                    if (!_buffers.TryGetValue(key, out var buf)) buf = curVal ?? "";

                    if (kind == FieldKind.Bool)
                    {
                        bool parsed = curVal != null && (curVal.Equals("true", StringComparison.OrdinalIgnoreCase) || curVal.Equals("True"));
                        bool newVal = GUILayout.Toggle(parsed, "value", GUILayout.Width(80));
                        if (newVal != parsed)
                        {
                            if (!_changes.ContainsKey(key))
                                _changes[key] = new ChangeRecord { Component = _selectedComponent, IsField = false, Member = setter, IsStatic = isStatic, Kind = FieldKind.Bool, OriginalValue = curVal ?? "false" };
                            if (ApplyBoolProperty(obj.Pointer, setter, isStatic, newVal))
                            {
                                _buffers[key] = newVal ? "true" : "false";
                                MelonLogger.Msg($"[DebugTools] Set {typeName}.{propName} = {_buffers[key]} (was {curVal})");
                            }
                        }
                        bool frozen = _frozen.ContainsKey(key);
                        bool newFrozen = GUILayout.Toggle(frozen, "Freeze", GUILayout.Width(70));
                        if (newFrozen != frozen)
                        {
                            if (newFrozen)
                                _frozen[key] = new FreezeRecord { Component = _selectedComponent, IsField = false, Member = setter, IsStatic = isStatic, Kind = FieldKind.Bool, Value = _buffers[key] };
                            else
                                _frozen.Remove(key);
                        }
                    }

                    else if (kind == FieldKind.Int || kind == FieldKind.Float || kind == FieldKind.String)
                    {
                        string newBuf = GUILayout.TextField(buf, GUILayout.MinWidth(200));
                        if (!ReferenceEquals(newBuf, buf)) { buf = newBuf; _buffers[key] = buf; }
                        if (GUILayout.Button("Set", GUILayout.Width(60)))
                        {
                            bool ok = false;
                            if (kind == FieldKind.Int)   ok = ApplyIntProperty(obj.Pointer, setter, isStatic, TryParseInt(buf, curVal));
                            else if (kind == FieldKind.Float)  ok = ApplyFloatProperty(obj.Pointer, setter, isStatic, TryParseFloat(buf, curVal));
                            else if (kind == FieldKind.String) ok = ApplyStringProperty(obj.Pointer, setter, isStatic, buf ?? "");
                            if (ok)
                            {
                                if (!_changes.ContainsKey(key))
                                    _changes[key] = new ChangeRecord { Component = _selectedComponent, IsField = false, Member = setter, IsStatic = isStatic, Kind = kind, OriginalValue = curVal ?? "" };
                                _buffers[key] = SafeGetPropertyValueStringTyped(obj.Pointer, getter, isStatic, IL2CPP.il2cpp_method_get_return_type(getter)) ?? buf;
                                MelonLogger.Msg($"[DebugTools] Set {typeName}.{propName} = {_buffers[key]} (was {curVal})");
                            }
                        }
                        bool frozen = _frozen.ContainsKey(key);
                        bool newFrozen = GUILayout.Toggle(frozen, "Freeze", GUILayout.Width(70));
                        if (newFrozen != frozen)
                        {
                            if (newFrozen)
                                _frozen[key] = new FreezeRecord { Component = _selectedComponent, IsField = false, Member = setter, IsStatic = isStatic, Kind = kind, Value = _buffers[key] };
                            else
                                _frozen.Remove(key);
                        }
                    }
                    else
                    {
                        GUILayout.Label("[unsupported]", GUILayout.Width(110));
                    }
                    
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();

                GUILayout.Space(6);
                if (GUILayout.Button("‹ Back", GUILayout.Width(90))) _grabberPane = GrabberPane.ComponentList;
                return;
            }
        }

        private void DrawWeatherManager()
        {
            GUILayout.Label("Weather Manager:");
            GUILayout.Space(8);
            if (GUILayout.Button("Print Weather Definitions", GUILayout.Height(32)))
                WeatherManager.PrintWeatherDefs();
        }

        private void SelectObject(GameObject go)
        {
            _selectedGO = go;
            _selectedPath = BuildPath(go != null ? go.transform : null);
            _grabberPane = GrabberPane.ObjectActions;
        }

        private void RevertAllChanges()
        {
            foreach (var kv in _changes.ToArray())
            {
                var rec = kv.Value;
                if (rec.Component == null) { _changes.Remove(kv.Key); continue; }
                var obj = (Il2CppObjectBase)(object)rec.Component;
                bool ok = false;
                if (rec.Kind == FieldKind.Int)
                {
                    int v = TryParseInt(rec.OriginalValue, rec.OriginalValue);
                    ok = rec.IsField ? ApplyIntField(obj.Pointer, rec.Member, rec.IsStatic, v)
                                      : ApplyIntProperty(obj.Pointer, rec.Member, rec.IsStatic, v);
                }
                else if (rec.Kind == FieldKind.Float)
                {
                    float v = TryParseFloat(rec.OriginalValue, rec.OriginalValue);
                    ok = rec.IsField ? ApplyFloatField(obj.Pointer, rec.Member, rec.IsStatic, v)
                                      : ApplyFloatProperty(obj.Pointer, rec.Member, rec.IsStatic, v);
                }
                else if (rec.Kind == FieldKind.Bool)
                {
                    bool v = rec.OriginalValue.Equals("true", StringComparison.OrdinalIgnoreCase);
                    ok = rec.IsField ? ApplyBoolField(obj.Pointer, rec.Member, rec.IsStatic, v)
                                      : ApplyBoolProperty(obj.Pointer, rec.Member, rec.IsStatic, v);
                }
                else if (rec.Kind == FieldKind.String)
                {
                    ok = rec.IsField ? ApplyStringField(obj.Pointer, rec.Member, rec.IsStatic, rec.OriginalValue)
                                      : ApplyStringProperty(obj.Pointer, rec.Member, rec.IsStatic, rec.OriginalValue);
                }
                if (ok)
                    MelonLogger.Msg("[DebugTools] Reverted " + kv.Key);
            }
            _changes.Clear();
            _frozen.Clear();
        }

        private void ApplyFrozenValues()
        {
            foreach (var kv in _frozen.ToArray())
            {
                var fr = kv.Value;
                if (fr.Component == null) { _frozen.Remove(kv.Key); continue; }
                var obj = (Il2CppObjectBase)(object)fr.Component;
                if (fr.Kind == FieldKind.Int)
                {
                    int v = TryParseInt(fr.Value, fr.Value);
                    if (fr.IsField) ApplyIntField(obj.Pointer, fr.Member, fr.IsStatic, v);
                    else ApplyIntProperty(obj.Pointer, fr.Member, fr.IsStatic, v);
                }
                else if (fr.Kind == FieldKind.Float)
                {
                    float v = TryParseFloat(fr.Value, fr.Value);
                    if (fr.IsField) ApplyFloatField(obj.Pointer, fr.Member, fr.IsStatic, v);
                    else ApplyFloatProperty(obj.Pointer, fr.Member, fr.IsStatic, v);
                }
                else if (fr.Kind == FieldKind.Bool)
                {
                    bool v = fr.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    if (fr.IsField) ApplyBoolField(obj.Pointer, fr.Member, fr.IsStatic, v);
                    else ApplyBoolProperty(obj.Pointer, fr.Member, fr.IsStatic, v);
                }
                else if (fr.Kind == FieldKind.String)
                {
                    if (fr.IsField) ApplyStringField(obj.Pointer, fr.Member, fr.IsStatic, fr.Value);
                    else ApplyStringProperty(obj.Pointer, fr.Member, fr.IsStatic, fr.Value);
                }
            }
        }

        private void RefreshNearby()
        {
            var player = GameObject.Find("Player(Clone)");
            Vector3 origin = player != null ? player.transform.position : (Camera.main != null ? Camera.main.transform.position : Vector3.zero);
            float r2 = _nearRadius * _nearRadius;
            var list = new List<Transform>(256);
            var dists = new List<float>(256);
            Transform[] all;
            try { all = GameObject.FindObjectsOfType<Transform>(true); } catch { all = Array.Empty<Transform>(); }
            for (int i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                if (!t.gameObject.scene.IsValid()) continue;
                var p = t.position;
                var dx = p.x - origin.x; var dy = p.y - origin.y; var dz = p.z - origin.z;
                float dist2 = dx * dx + dy * dy + dz * dz;
                if (dist2 > r2) continue;
                bool isBody = BODY_PARTS.Contains(t.name);
                if (_playerBodyOnly ? !isBody : isBody) continue;
                list.Add(t);
                dists.Add(Mathf.Sqrt(dist2));
            }
            var idx = Enumerable.Range(0, list.Count).OrderBy(i => dists[i]).Take(_nearLimit).ToArray();
            _near = idx.Select(i => list[i]).ToArray();
            _nearDist = idx.Select(i => dists[i]).ToArray();
        }

        private bool IsShiftHeld()
        {
            var e = Event.current;
            return e != null && (e.shift || (e.modifiers & EventModifiers.Shift) != 0);
        }

        private void ScanCameras()
        {
            try
            {
                _cams = GameObject.FindObjectsOfType<Camera>(true)
                        .Where(c => c != null && c.gameObject.scene.IsValid())
                        .OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
            }
            catch { _cams = Camera.allCameras ?? Array.Empty<Camera>(); }
        }

        private void DoSearch()
        {
            var q = (_query ?? "").Trim();
            if (q.Length == 0) { _matches = Array.Empty<Transform>(); return; }
            try
            {
                var all = GameObject.FindObjectsOfType<Transform>(true);
                _matches = all
                    .Where(t => t != null && t.gameObject.scene.IsValid() && !string.IsNullOrEmpty(t.name) && t.name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(t => t.name, StringComparer.OrdinalIgnoreCase)
                    .Take(512)
                    .ToArray();
            }
            catch (Exception ex) { _matches = Array.Empty<Transform>(); MelonLogger.Error("[Finder] Search EX: " + ex.Message); }
        }

        private static string BuildPath(Transform t)
        {
            if (t == null) return "<null>";
            string path = t.name;
            var p = t.parent;
            int guard = 0;
            while (p != null && guard++ < 1024) { path = p.name + "/" + path; p = p.parent; }
            return path;
        }

        private void TryInspectGO(GameObject go, Camera grabCamera)
        {
            try
            {
                if (go == null) { MelonLogger.Msg("[Finder] Target null."); return; }
                DumpComponentsToLogAndFile(go);
                DebugTools.InvokeInspect(go);
                MelonLogger.Msg("[Finder] Completed: " + go.name);
                if (grabCamera != null)
                {
                    DebugTools.InvokeGrab(grabCamera);
                    MelonLogger.Msg("[Finder] Grabbed camera: " + grabCamera.name);
                }
            }
            catch (Exception e) { MelonLogger.Error("[Finder] Failed on " + (go != null ? go.name : "<null>") + ": " + e.Message); }
        }

        public static unsafe void DumpComponentsToLogAndFile(GameObject target)
        {
            string exportDir = EnsureLogDir();
            if (target == null) { MelonLogger.Msg("[Finder] Target is null."); return; }
            var sceneName = SceneManager.GetActiveScene().name ?? "UnknownScene";
            var objName = target.name ?? "Object";
            string sceneSafe = Sanitize(sceneName);
            string objSafe = Sanitize(objName);
            if (objSafe.Length > 80) objSafe = objSafe.Substring(0, 80);
            var nowStamp = DateTime.Now.ToString("HHmmssff", CultureInfo.InvariantCulture);

            var fileName = $"{sceneSafe} - {objSafe} Components - {nowStamp}.log";
            if (fileName.Length > 120) fileName = fileName.Substring(0, 120);
            var filePath = Path.Combine(exportDir, fileName);

            var lines = new List<string>(2048);
            void W(string s) { lines.Add(s); MelonLogger.Msg(s); }

            try
            {
                var comps = target.GetComponents<Component>();
                W($"--- Finder: Components on {objName} ({comps.Length}) ---");
                foreach (var c in comps)
                {
                    if (c == null) continue;
                    try
                    {
                        var ob = (Il2CppObjectBase)(object)c;
                        IntPtr klass = IL2CPP.il2cpp_object_get_class(ob.Pointer);
                        string name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
                        string ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass));
                        string typeName = string.IsNullOrEmpty(ns) ? name : (ns + "." + name);
                        var beh = c as Behaviour;
                        if (beh != null) typeName += " Enabled=" + beh.enabled;
                        W("Comp: " + typeName);

                        IntPtr fIter = IntPtr.Zero;
                        IntPtr field;
                        while ((field = IL2CPP.il2cpp_class_get_fields(klass, ref fIter)) != IntPtr.Zero)
                        {
                            var attr = (FieldAttributes)IL2CPP.il2cpp_field_get_flags(field);
                            if ((attr & FieldAttributes.Public) == 0) continue;
                            string fieldName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_field_get_name(field));
                            IntPtr ftype = IL2CPP.il2cpp_field_get_type(field);
                            try
                            {
                                IntPtr valPtr = ((attr & FieldAttributes.Static) != 0)
                                    ? IL2CPP.il2cpp_field_get_value_object(field, IntPtr.Zero)
                                    : IL2CPP.il2cpp_field_get_value_object(field, ob.Pointer);
                                string valStr = FormatValueByType(valPtr, ftype);
                                W("    Field " + fieldName + " = " + valStr);
                            }
                            catch (Exception e) { W("    Field " + fieldName + " = <err: " + e.Message + ">"); }
                        }

                        IntPtr pIter = IntPtr.Zero;
                        IntPtr prop;
                        while ((prop = IL2CPP.il2cpp_class_get_properties(klass, ref pIter)) != IntPtr.Zero)
                        {
                            IntPtr getter = IL2CPP.il2cpp_property_get_get_method(prop);
                            if (getter == IntPtr.Zero) continue;
                            uint iflags = 0;
                            var mflags = (MethodAttributes)IL2CPP.il2cpp_method_get_flags(getter, ref iflags);
                            if ((mflags & MethodAttributes.Public) == 0) continue;
                            bool isStatic = (mflags & MethodAttributes.Static) != 0;
                            string propName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_property_get_name(prop));
                            try
                            {
                                IntPtr exc = IntPtr.Zero;
                                IntPtr targetPtr = isStatic ? IntPtr.Zero : ob.Pointer;
                                IntPtr valPtr = IL2CPP.il2cpp_runtime_invoke(getter, targetPtr, null, ref exc);
                                string valStr = (exc != IntPtr.Zero)
                                    ? "<exception>"
                                    : FormatValueByType(valPtr, IL2CPP.il2cpp_method_get_return_type(getter));
                                W("    Prop " + propName + " = " + valStr);
                            }
                            catch (Exception e) { W("    Prop " + propName + " = <err: " + e.Message + ">"); }
                        }
                    }
                    catch (Exception e)
                    {
                        string tn = "<unknown>";
                        try { tn = c.GetType().FullName; } catch { }
                        W("[Finder] Failed component " + tn + ": " + e.Message);
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Error("[Dump EX] " + ex); }

            try
            {
                File.WriteAllLines(filePath, lines);
                MelonLogger.Msg($"[Export] Wrote -> {filePath}");
            }
            catch (Exception ex) { MelonLogger.Error("[Export EX] " + ex); }
        }

        private static string GetSafeTypeName(Component c)
        {
            try
            {
                var obj = (Il2CppObjectBase)(object)c;
                IntPtr klass = IL2CPP.il2cpp_object_get_class(obj.Pointer);
                string name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
                string ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass));
                return string.IsNullOrEmpty(ns) ? name : (ns + "." + name);
            }
            catch { return c.GetType().Name; }
        }

        private enum FieldKind { Bool, Int, Float, String, Other }

		private static FieldKind KindFromIl2CppType(IntPtr il2cppType)
		{
			var full = TypeFullNameFromIl2CppType(il2cppType);
			if (full == "System.Boolean") return FieldKind.Bool;
			if (full == "System.Int32")   return FieldKind.Int;
			if (full == "System.Single")  return FieldKind.Float;
			if (full == "System.String")  return FieldKind.String;
			return FieldKind.Other;
		}

		private static string TypeFullNameFromIl2CppType(IntPtr il2cppType)
		{
			try
			{
				IntPtr klass = IL2CPP.il2cpp_class_from_il2cpp_type(il2cppType);
				string name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
				string ns   = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass));
				return string.IsNullOrEmpty(ns) ? name : (ns + "." + name);
			}
			catch { return "Unknown"; }
		}

		private static unsafe string SafeGetFieldValueStringTyped(IntPtr objPtr, IntPtr field, bool isStatic, IntPtr il2cppType)
		{
			try
			{
				IntPtr boxed = isStatic
					? IL2CPP.il2cpp_field_get_value_object(field, IntPtr.Zero)
					: IL2CPP.il2cpp_field_get_value_object(field, objPtr);
				return FormatValueByType(boxed, il2cppType);
			}
			catch { return null; }
		}

		private static unsafe string SafeGetPropertyValueStringTyped(IntPtr objPtr, IntPtr getter, bool isStatic, IntPtr retType)
		{
			try
			{
				IntPtr exc = IntPtr.Zero;
				IntPtr targetPtr = isStatic ? IntPtr.Zero : objPtr;
				IntPtr boxed = IL2CPP.il2cpp_runtime_invoke(getter, targetPtr, null, ref exc);
				if (exc != IntPtr.Zero) return "<exception>";
				return FormatValueByType(boxed, retType);
			}
			catch { return null; }
		}

		private static unsafe string FormatValueByType(IntPtr boxedObj, IntPtr il2cppType)
		{
			if (boxedObj == IntPtr.Zero) return "null";

			// Detect by fully-qualified type name instead of Il2CppTypeEnum
			var full = TypeFullNameFromIl2CppType(il2cppType);

			if (full == "System.Boolean")
			{
				byte* p = (byte*)IL2CPP.il2cpp_object_unbox(boxedObj);
				return (p != null && *p != 0) ? "true" : "false";
			}
			if (full == "System.Int32")
			{
				int* p = (int*)IL2CPP.il2cpp_object_unbox(boxedObj);
				return p != null ? (*p).ToString(CultureInfo.InvariantCulture) : "0";
			}
			if (full == "System.Single")
			{
				float* p = (float*)IL2CPP.il2cpp_object_unbox(boxedObj);
				return p != null ? (*p).ToString("R", CultureInfo.InvariantCulture) : "0";
			}
			if (full == "System.String")
			{
				return IL2CPP.Il2CppStringToManaged(boxedObj) ?? "null";
			}

			// Fallback to ToString() for everything else
			return Il2CppObjectToStringSafe(boxedObj);
		}

		private static unsafe string Il2CppObjectToStringSafe(IntPtr objPtr)
		{
			if (objPtr == IntPtr.Zero) return "null";
			try
			{
				IntPtr klass = IL2CPP.il2cpp_object_get_class(objPtr);
				IntPtr toString = IL2CPP.il2cpp_class_get_method_from_name(klass, "ToString", 0);
				if (toString == IntPtr.Zero) return "<unknown>";
				IntPtr exc = IntPtr.Zero;
				IntPtr strPtr = IL2CPP.il2cpp_runtime_invoke(toString, objPtr, null, ref exc);
				if (exc != IntPtr.Zero) return "<exception>";
				return IL2CPP.Il2CppStringToManaged(strPtr);
			}
			catch { return "<unknown>"; }
		}


        private static int TryParseInt(string buf, string fallbackStr)
        {
            if (int.TryParse(buf, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            if (int.TryParse(fallbackStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        private static float TryParseFloat(string buf, string fallbackStr)
        {
            if (float.TryParse(buf, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            if (float.TryParse(fallbackStr, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return 0f;
        }

        private static unsafe bool ApplyBoolField(IntPtr objPtr, IntPtr field, bool isStatic, bool value)
        {
            try
            {
                byte v = value ? (byte)1 : (byte)0;
                void* mem = &v;
                if (isStatic) IL2CPP.il2cpp_field_static_set_value(field, mem);
                else IL2CPP.il2cpp_field_set_value(objPtr, field, mem);
                return true;
            }
            catch (Exception e) { MelonLogger.Error("[Set Bool Field] " + e.Message); return false; }
        }

        private static unsafe bool ApplyIntField(IntPtr objPtr, IntPtr field, bool isStatic, int value)
        {
            try
            {
                void* mem = &value;
                if (isStatic) IL2CPP.il2cpp_field_static_set_value(field, mem);
                else IL2CPP.il2cpp_field_set_value(objPtr, field, mem);
                return true;
            }
            catch (Exception e) { MelonLogger.Error("[Set Int Field] " + e.Message); return false; }
        }

        private static unsafe bool ApplyFloatField(IntPtr objPtr, IntPtr field, bool isStatic, float value)
        {
            try
            {
                void* mem = &value;
                if (isStatic) IL2CPP.il2cpp_field_static_set_value(field, mem);
                else IL2CPP.il2cpp_field_set_value(objPtr, field, mem);
                return true;
            }
            catch (Exception e) { MelonLogger.Error("[Set Float Field] " + e.Message); return false; }
        }

        private static unsafe bool ApplyStringField(IntPtr objPtr, IntPtr field, bool isStatic, string value)
        {
            try
            {
                IntPtr s = IL2CPP.ManagedStringToIl2Cpp(value ?? "");
                IntPtr* sp = stackalloc IntPtr[1]; sp[0] = s;
                void* mem = sp;
                if (isStatic) IL2CPP.il2cpp_field_static_set_value(field, mem);
                else IL2CPP.il2cpp_field_set_value(objPtr, field, mem);
                return true;
            }
            catch (Exception e) { MelonLogger.Error("[Set String Field] " + e.Message); return false; }
        }

        private static unsafe bool ApplyBoolProperty(IntPtr objPtr, IntPtr setter, bool isStatic, bool value)
        {
            try
            {
                byte v = value ? (byte)1 : (byte)0;
                void** args = stackalloc void*[1];
                args[0] = &v;
                IntPtr exc = IntPtr.Zero;
                IntPtr targetPtr = isStatic ? IntPtr.Zero : objPtr;
                IL2CPP.il2cpp_runtime_invoke(setter, targetPtr, args, ref exc);
                return exc == IntPtr.Zero;
            }
            catch (Exception e) { MelonLogger.Error("[Set Bool Prop] " + e.Message); return false; }
        }

        private static unsafe bool ApplyIntProperty(IntPtr objPtr, IntPtr setter, bool isStatic, int value)
        {
            try
            {
                void** args = stackalloc void*[1];
                args[0] = &value;
                IntPtr exc = IntPtr.Zero;
                IntPtr targetPtr = isStatic ? IntPtr.Zero : objPtr;
                IL2CPP.il2cpp_runtime_invoke(setter, targetPtr, args, ref exc);
                return exc == IntPtr.Zero;
            }
            catch (Exception e) { MelonLogger.Error("[Set Int Prop] " + e.Message); return false; }
        }

        private static unsafe bool ApplyFloatProperty(IntPtr objPtr, IntPtr setter, bool isStatic, float value)
        {
            try
            {
                void** args = stackalloc void*[1];
                args[0] = &value;
                IntPtr exc = IntPtr.Zero;
                IntPtr targetPtr = isStatic ? IntPtr.Zero : objPtr;
                IL2CPP.il2cpp_runtime_invoke(setter, targetPtr, args, ref exc);
                return exc == IntPtr.Zero;
            }
            catch (Exception e) { MelonLogger.Error("[Set Float Prop] " + e.Message); return false; }
        }

        private static unsafe bool ApplyStringProperty(IntPtr objPtr, IntPtr setter, bool isStatic, string value)
        {
            try
            {
                IntPtr s = IL2CPP.ManagedStringToIl2Cpp(value ?? "");
                void** args = stackalloc void*[1];
                args[0] = (void*)s; // pass object pointer, not pointer-to-pointer
                IntPtr exc = IntPtr.Zero;
                IntPtr targetPtr = isStatic ? IntPtr.Zero : objPtr;
                IL2CPP.il2cpp_runtime_invoke(setter, targetPtr, args, ref exc);
                return exc == IntPtr.Zero;
            }
            catch (Exception e) { MelonLogger.Error("[Set String Prop] " + e.Message); return false; }
        }

        private static unsafe string Il2CppObjectToString(IntPtr objPtr)
        {
            if (objPtr == IntPtr.Zero) return "null";
            IntPtr klass = IL2CPP.il2cpp_object_get_class(objPtr);
            IntPtr toString = IL2CPP.il2cpp_class_get_method_from_name(klass, "ToString", 0);
            if (toString == IntPtr.Zero) return "<unknown>";
            IntPtr exc = IntPtr.Zero;
            IntPtr strPtr = IL2CPP.il2cpp_runtime_invoke(toString, objPtr, null, ref exc);
            if (exc != IntPtr.Zero) return "<exception>";
            return IL2CPP.Il2CppStringToManaged(strPtr);
        }

        private static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Unnamed";
            foreach (var ch in Path.GetInvalidFileNameChars()) raw = raw.Replace(ch, '_');
            return raw.Trim();
        }

        private static string EnsureLogDir()
        {
            string[] candidates =
            {
                Path.Combine(MelonEnvironment.ModsDirectory, "DebugTools Logs"),
                Path.Combine(MelonEnvironment.UserDataDirectory, "DebugTools Logs"),
                Directory.GetCurrentDirectory()
            };
            foreach (var dir in candidates)
            {
                try { Directory.CreateDirectory(dir); return dir; }
                catch { }
            }
            return Directory.GetCurrentDirectory();
        }
    }
}

[HarmonyLib.HarmonyPatch]
static class InputBlockPatches
{
    private static bool AllowKey(KeyCode k) => k == KeyCode.F12 || k == KeyCode.Escape;

    [HarmonyLib.HarmonyPatch(typeof(Input), nameof(Input.GetKey), new Type[] { typeof(KeyCode) })]
    [HarmonyPrefix] static bool GetKey_KeyCode(ref bool __result, KeyCode __0) { if (!DebugTools.BlockAllInput || AllowKey(__0)) return true; __result = false; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), nameof(Input.GetKeyDown), new Type[] { typeof(KeyCode) })]
    [HarmonyPrefix] static bool GetKeyDown_KeyCode(ref bool __result, KeyCode __0) { if (!DebugTools.BlockAllInput || AllowKey(__0)) return true; __result = false; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), nameof(Input.GetKeyUp), new Type[] { typeof(KeyCode) })]
    [HarmonyPrefix] static bool GetKeyUp_KeyCode(ref bool __result, KeyCode __0) { if (!DebugTools.BlockAllInput || AllowKey(__0)) return true; __result = false; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), nameof(Input.GetKey), new Type[] { typeof(string) })]
    [HarmonyPrefix] static bool GetKey_String(ref bool __result) { if (!DebugTools.BlockAllInput) return true; __result = false; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), nameof(Input.GetKeyDown), new Type[] { typeof(string) })]
    [HarmonyPrefix] static bool GetKeyDown_String(ref bool __result) { if (!DebugTools.BlockAllInput) return true; __result = false; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), nameof(Input.GetKeyUp), new Type[] { typeof(string) })]
    [HarmonyPrefix] static bool GetKeyUp_String(ref bool __result) { if (!DebugTools.BlockAllInput) return true; __result = false; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), nameof(Input.GetButton), new Type[] { typeof(string) })]
    [HarmonyPrefix] static bool GetButton(ref bool __result) { if (!DebugTools.BlockAllInput) return true; __result = false; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), nameof(Input.GetButtonDown), new Type[] { typeof(string) })]
    [HarmonyPrefix] static bool GetButtonDown(ref bool __result) { if (!DebugTools.BlockAllInput) return true; __result = false; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), nameof(Input.GetButtonUp), new Type[] { typeof(string) })]
    [HarmonyPrefix] static bool GetButtonUp(ref bool __result) { if (!DebugTools.BlockAllInput) return true; __result = false; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), nameof(Input.GetAxis), new Type[] { typeof(string) })]
    [HarmonyPrefix] static bool GetAxis(ref float __result) { if (!DebugTools.BlockAllInput) return true; __result = 0f; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), nameof(Input.GetAxisRaw), new Type[] { typeof(string) })]
    [HarmonyPrefix] static bool GetAxisRaw(ref float __result) { if (!DebugTools.BlockAllInput) return true; __result = 0f; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), nameof(Input.GetMouseButton), new Type[] { typeof(int) })]
    [HarmonyPrefix] static bool GetMouseButton(ref bool __result) { if (!DebugTools.BlockAllInput) return true; __result = false; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), nameof(Input.GetMouseButtonDown), new Type[] { typeof(int) })]
    [HarmonyPrefix] static bool GetMouseButtonDown(ref bool __result) { if (!DebugTools.BlockAllInput) return true; __result = false; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), nameof(Input.GetMouseButtonUp), new Type[] { typeof(int) })]
    [HarmonyPrefix] static bool GetMouseButtonUp(ref bool __result) { if (!DebugTools.BlockAllInput) return true; __result = false; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), "get_anyKey")]
    [HarmonyPrefix] static bool get_anyKey(ref bool __result) { if (!DebugTools.BlockAllInput) return true; __result = false; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), "get_anyKeyDown")]
    [HarmonyPrefix] static bool get_anyKeyDown(ref bool __result) { if (!DebugTools.BlockAllInput) return true; __result = false; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), "get_mouseScrollDelta")]
    [HarmonyPrefix] static bool get_mouseScrollDelta(ref Vector2 __result) { if (!DebugTools.BlockAllInput) return true; __result = Vector2.zero; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), "get_touchCount")]
    [HarmonyPrefix] static bool get_touchCount(ref int __result) { if (!DebugTools.BlockAllInput) return true; __result = 0; return false; }

    [HarmonyLib.HarmonyPatch(typeof(Input), nameof(Input.GetTouch), new Type[] { typeof(int) })]
    [HarmonyPrefix] static bool GetTouch(ref Touch __result) { if (!DebugTools.BlockAllInput) return true; __result = default; return false; }
}
