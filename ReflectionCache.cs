using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using MelonLoader;

namespace PreyEyes2
{
    /// <summary>Color struct matching IL2CPP UnityEngine.Color layout. Used for native set_color calls.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct UnityColor
    {
        public float r, g, b, a;
        public UnityColor(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    /// <summary>
    /// Single source of truth for all Unity type and method resolution via reflection.
    /// Initialized once at mod startup. No other file should do assembly scanning.
    /// </summary>
    internal static class ReflectionCache
    {
        // ── Logger ──
        private static MelonLogger.Instance? _log;

        // ── Unity Types ──
        internal static Type? T_GameObject;
        internal static Type? T_Canvas;
        internal static Type? T_CanvasScaler;
        internal static Type? T_RectTransform;
        internal static Type? T_Transform;
        internal static Type? T_Image;
        internal static Type? T_RawImage;
        internal static Type? T_Graphic;
        internal static Type? T_Color;
        internal static Type? T_Vector2;
        internal static Type? T_Vector3;
        internal static Type? T_Rect;
        internal static Type? T_Object; // UnityEngine.Object
        internal static Type? T_Animator;
        internal static Type? T_Texture2D;
        internal static Type? T_Sprite;
        internal static Type? T_Screen;
        internal static Type? T_TextComponent; // TMP or legacy Text
        internal static Type? T_TMP_Settings;
        internal static Type? T_ImageConversion;
        internal static Type? T_Font;
        internal static Type? T_Resources;
        internal static Type? T_DatUnitWork;
        internal static Type? T_Outline; // UnityEngine.UI.Outline — adds border to Graphic elements

        internal static bool UseTMP;

        // ── Cached Constructors ──
        internal static ConstructorInfo? Ctor_GameObject_String;
        internal static ConstructorInfo? Ctor_Color4f;
        internal static ConstructorInfo? Ctor_Vector2;
        internal static ConstructorInfo? Ctor_Vector3;
        internal static ConstructorInfo? Ctor_Rect;
        internal static ConstructorInfo? Ctor_Texture2D;
        internal static ConstructorInfo? Ctor_DatUnitWork_IntPtr;

        // ── Cached Methods ──
        internal static MethodInfo? M_AddComponent; // open generic
        internal static MethodInfo? M_GetComponent; // open generic
        internal static MethodInfo? M_GetComponentsInChildren; // open generic
        internal static MethodInfo? M_SetActive;
        internal static MethodInfo? M_Transform_SetParent;
        internal static MethodInfo? M_DontDestroyOnLoad;
        internal static MethodInfo? M_SetText; // text property setter
        internal static MethodInfo? M_Sprite_Create;

        // ── Cached Properties ──
        internal static PropertyInfo? P_GO_Transform;
        internal static PropertyInfo? P_Canvas_RenderMode;
        internal static PropertyInfo? P_Canvas_SortingOrder;
        internal static PropertyInfo? P_RT_AnchoredPosition;
        internal static PropertyInfo? P_RT_AnchorMin;
        internal static PropertyInfo? P_RT_AnchorMax;
        internal static PropertyInfo? P_RT_Pivot;
        internal static PropertyInfo? P_RT_SizeDelta;
        internal static PropertyInfo? P_Transform_Position;
        internal static PropertyInfo? P_Transform_LocalPosition;
        internal static PropertyInfo? P_Transform_LocalScale;
        internal static MethodInfo? M_Transform_SetSiblingIndex;
        internal static PropertyInfo? P_Color_R;
        internal static PropertyInfo? P_Color_G;
        internal static PropertyInfo? P_Color_B;
        internal static PropertyInfo? P_Color_A;
        internal static PropertyInfo? P_Graphic_Color;
        internal static PropertyInfo? P_Image_Color;
        internal static PropertyInfo? P_Image_Sprite;
        internal static PropertyInfo? P_RawImage_Texture;
        internal static PropertyInfo? P_Animator_Enabled;
        internal static PropertyInfo? P_Screen_Width;
        internal static PropertyInfo? P_Screen_Height;

        // Camera for world-to-screen conversion
        internal static Type? T_Camera;
        internal static PropertyInfo? P_Camera_Main;
        internal static MethodInfo? M_WorldToScreenPoint;
        internal static PropertyInfo? P_Text_FontSize;
        internal static PropertyInfo? P_Text_Alignment;
        internal static PropertyInfo? P_Text_RichText;
        internal static PropertyInfo? P_Text_WordWrap;
        internal static PropertyInfo? P_Text_Font;
        internal static PropertyInfo? P_Text_Color;
        internal static PropertyInfo? P_TMP_DefaultFontAsset;
        internal static PropertyInfo? P_Texture2D_Width;
        internal static PropertyInfo? P_Texture2D_Height;
        internal static PropertyInfo? P_Unit_Hp;
        internal static PropertyInfo? P_Unit_MaxHp;

        // TextureFormat enum type (for Texture2D construction)
        private static Type? _textureFormatType;

        // ── Native Function Delegates ──
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr d_GetUnitWork(int formIndex, IntPtr methodInfo);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int d_GetUnitID(IntPtr unitWork, IntPtr methodInfo);

        // Graphic.set_color — MUST use ref struct calling convention (IL2CPP requirement)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void d_SetColorRef(IntPtr thisPtr, ref UnityColor color, IntPtr methodInfo);

        // datGetAisyo — direct affinity lookup from unit work (no skill needed)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate uint d_DatGetAisyo(IntPtr unitWork, int attr, IntPtr methodInfo);

        // cmbGetStatTarget — returns datUnitWork_t for demon viewed in Cathedral
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr d_CmbGetStatTarget(IntPtr methodInfo);

        internal static d_GetUnitWork? N_GetUnitWork;
        internal static IntPtr Mip_GetUnitWork;
        internal static d_GetUnitID? N_GetUnitID;
        internal static IntPtr Mip_GetUnitID;
        internal static d_DatGetAisyo? N_DatGetAisyo;
        internal static IntPtr Mip_DatGetAisyo;
        internal static d_CmbGetStatTarget? N_CmbGetStatTarget;
        internal static IntPtr Mip_CmbGetStatTarget;

        // Native set_color function pointer + methodInfo
        internal static d_SetColorRef? N_SetColor;
        internal static IntPtr Mip_SetColor;

        // frGetNameString via MethodInfo (managed call)
        internal static MethodInfo? M_frGetNameString;

        // Game methods that return Unity types (must go through reflection to avoid CoreModule ref)
        internal static MethodInfo? M_GetTargetMark2D; // nbMainProcess.GetTargetMark2D(int) → GameObject

        // ── Init ──

        internal static bool Init(MelonLogger.Instance log)
        {
            _log = log;
            bool ok = true;

            try
            {
                ForceLoadModules();
                ResolveTypes();
                ResolveConstructors();
                ResolveMethods();
                ResolveProperties();
                ResolveNatives();

                log.Msg($"ReflectionCache: Init complete. " +
                    $"GO={S(T_GameObject)} Canvas={S(T_Canvas)} Image={S(T_Image)} RawImage={S(T_RawImage)} " +
                    $"Color={S(T_Color)} RT={S(T_RectTransform)} Text={S(T_TextComponent)}({(UseTMP ? "TMP" : "Legacy")}) " +
                    $"Animator={S(T_Animator)} Sprite={S(T_Sprite)}");
                log.Msg($"ReflectionCache: Natives: GetUnitWork={S(N_GetUnitWork)} GetUnitID={S(N_GetUnitID)} datGetAisyo={S(N_DatGetAisyo)} frGetNameString={S(M_frGetNameString)} set_color={S(N_SetColor)}");
            }
            catch (Exception ex)
            {
                log.Error($"ReflectionCache.Init: {ex}");
                ok = false;
            }

            return ok;
        }

        private static string S(object? o) => o != null ? "OK" : "MISS";

        // ── Force-load DLL modules ──

        private static void ForceLoadModules()
        {
            string? asmDir = Path.GetDirectoryName(typeof(Il2Cpp.nbCalc).Assembly.Location);
            if (asmDir == null) return;

            string[] modules = {
                "UnityEngine.UIModule.dll",           // Canvas
                "UnityEngine.UI.dll",                 // Image, Graphic, CanvasScaler, Outline
                "UnityEngine.AnimationModule.dll",    // Animator
                "UnityEngine.TextRenderingModule.dll",
                "UnityEngine.ImageConversionModule.dll",
            };

            foreach (var mod in modules)
            {
                try { Assembly.LoadFrom(Path.Combine(asmDir, mod)); }
                catch { _log?.Warning($"ReflectionCache: Could not load {mod}"); }
            }
        }

        // ── Type Resolution ──

        private static void ResolveTypes()
        {
            // Scan all assemblies for Unity types, filtering out MelonLoader internals
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        string? ns = t.Namespace;
                        if (ns == null) continue;
                        if (ns.Contains("Melon")) continue;

                        switch (t.Name)
                        {
                            case "GameObject" when ns.Contains("UnityEngine") && T_GameObject == null:
                                T_GameObject = t; break;
                            case "Canvas" when ns.Contains("UnityEngine") && T_Canvas == null:
                                T_Canvas = t; break;
                            case "CanvasScaler" when ns.Contains("UI") && T_CanvasScaler == null:
                                T_CanvasScaler = t; break;
                            case "RectTransform" when T_RectTransform == null:
                                T_RectTransform = t; break;
                            case "Transform" when ns.Contains("UnityEngine") && T_Transform == null:
                                T_Transform = t; break;
                            case "Image" when ns.Contains("UI") && T_Image == null:
                                T_Image = t; break;
                            case "RawImage" when ns.Contains("UI") && T_RawImage == null:
                                T_RawImage = t; break;
                            case "Graphic" when ns.Contains("UI") && T_Graphic == null:
                                T_Graphic = t; break;
                            case "Color" when ns.Contains("UnityEngine") && T_Color == null:
                                T_Color = t; break;
                            case "Vector2" when ns.Contains("UnityEngine") && T_Vector2 == null:
                                T_Vector2 = t; break;
                            case "Vector3" when ns.Contains("UnityEngine") && T_Vector3 == null:
                                T_Vector3 = t; break;
                            case "Rect" when ns.Contains("UnityEngine") && T_Rect == null:
                                T_Rect = t; break;
                            case "Object" when ns == "UnityEngine" && T_Object == null:
                                T_Object = t; break;
                            case "Animator" when ns.Contains("UnityEngine") && T_Animator == null:
                                T_Animator = t; break;
                            case "Texture2D" when ns.Contains("UnityEngine") && T_Texture2D == null:
                                T_Texture2D = t; break;
                            case "Sprite" when ns.Contains("UnityEngine") && T_Sprite == null:
                                T_Sprite = t; break;
                            case "Screen" when ns.Contains("UnityEngine") && T_Screen == null:
                                T_Screen = t; break;
                            case "Camera" when ns.Contains("UnityEngine") && T_Camera == null:
                                T_Camera = t; break;
                            case "TextMeshProUGUI":
                                T_TextComponent ??= t; break;
                            case "TMP_Settings" when T_TMP_Settings == null:
                                T_TMP_Settings = t; break;
                            case "ImageConversion" when ns.Contains("UnityEngine") && T_ImageConversion == null:
                                T_ImageConversion = t; break;
                            case "Font" when ns.Contains("UnityEngine") && T_Font == null:
                                T_Font = t; break;
                            case "Resources" when ns.Contains("UnityEngine") && T_Resources == null:
                                T_Resources = t; break;
                            case "Outline" when ns.Contains("UI") && T_Outline == null:
                                T_Outline = t; break;
                            case "datUnitWork_t" when T_DatUnitWork == null:
                                T_DatUnitWork = t; break;
                        }
                    }
                }
                catch { }
            }

            // Fallback: if TMP not found, look for legacy UI.Text
            if (T_TextComponent == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        foreach (var t in asm.GetTypes())
                            if (t.Name == "Text" && t.Namespace != null &&
                                t.Namespace.Contains("UI") && !t.Namespace.Contains("Melon"))
                            { T_TextComponent = t; goto textDone; }
                    }
                    catch { }
                }
                textDone:;
            }

            UseTMP = T_TextComponent?.Name == "TextMeshProUGUI";
        }

        // ── Constructor Resolution ──

        private static void ResolveConstructors()
        {
            Ctor_GameObject_String = T_GameObject?.GetConstructor(new[] { typeof(string) });
            Ctor_Color4f = T_Color?.GetConstructor(new[] { typeof(float), typeof(float), typeof(float), typeof(float) });
            Ctor_Vector2 = T_Vector2?.GetConstructor(new[] { typeof(float), typeof(float) });
            Ctor_Vector3 = T_Vector3?.GetConstructor(new[] { typeof(float), typeof(float), typeof(float) });
            Ctor_Rect = T_Rect?.GetConstructor(new[] { typeof(float), typeof(float), typeof(float), typeof(float) });

            // Texture2D — no (int,int) ctor in IL2CPP. Use (int, int, TextureFormat, bool).
            // TextureFormat.RGBA32 = 4
            if (T_Texture2D != null)
            {
                foreach (var c in T_Texture2D.GetConstructors())
                {
                    var ps = c.GetParameters();
                    // Match: (Int32, Int32, TextureFormat, Boolean) — 4 params
                    if (ps.Length == 4 && ps[0].ParameterType == typeof(int) && ps[1].ParameterType == typeof(int)
                        && ps[3].ParameterType == typeof(bool))
                    {
                        Ctor_Texture2D = c;
                        // Cache the TextureFormat type for constructing the enum value
                        _textureFormatType = ps[2].ParameterType;
                        _log?.Msg($"ReflectionCache: Texture2D 4-param ctor found, TextureFormat type={_textureFormatType.Name}");
                        break;
                    }
                }
            }

            Ctor_DatUnitWork_IntPtr = T_DatUnitWork?.GetConstructor(new[] { typeof(IntPtr) });
        }

        // ── Method Resolution ──

        private static void ResolveMethods()
        {
            if (T_GameObject != null)
            {
                M_AddComponent = T_GameObject.GetMethod("AddComponent", Type.EmptyTypes);
                M_GetComponent = T_GameObject.GetMethod("GetComponent", Type.EmptyTypes);
                M_GetComponentsInChildren = T_GameObject.GetMethod("GetComponentsInChildren", Type.EmptyTypes);
                M_SetActive = T_GameObject.GetMethod("SetActive", new[] { typeof(bool) });
            }

            if (T_Transform != null)
            {
                // SetParent(Transform parent, bool worldPositionStays)
                // Try finding the two-parameter overload
                foreach (var m in T_Transform.GetMethods())
                {
                    if (m.Name != "SetParent") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 2 && ps[1].ParameterType == typeof(bool))
                    { M_Transform_SetParent = m; break; }
                }
            }

            // DontDestroyOnLoad
            if (T_Object != null)
            {
                foreach (var m in T_Object.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "DontDestroyOnLoad")
                    { M_DontDestroyOnLoad = m; break; }
            }

            // Text setter
            M_SetText = T_TextComponent?.GetProperty("text")?.GetSetMethod();

            // Sprite.Create
            if (T_Sprite != null)
            {
                foreach (var m in T_Sprite.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "Create") continue;
                    var ps = m.GetParameters();
                    // Looking for Create(Texture2D, Rect, Vector2)
                    if (ps.Length == 3)
                    { M_Sprite_Create = m; break; }
                }
            }

            // GetTargetMark2D — returns GameObject, so must be called via reflection
            // to avoid needing CoreModule reference
            try
            {
                M_GetTargetMark2D = typeof(Il2Cpp.nbMainProcess).GetMethod(
                    "GetTargetMark2D",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(int) }, null);
            }
            catch { }

            // frGetNameString — managed MethodInfo search
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        var m = t.GetMethod("frGetNameString", BindingFlags.Public | BindingFlags.Static);
                        if (m != null) { M_frGetNameString = m; goto frDone; }
                    }
                }
                catch { }
            }
            frDone:;
        }

        // ── Property Resolution ──

        private static void ResolveProperties()
        {
            var bf = BindingFlags.Public | BindingFlags.Instance;
            var bfs = BindingFlags.Public | BindingFlags.Static;

            P_GO_Transform = T_GameObject?.GetProperty("transform", bf);
            P_Canvas_RenderMode = T_Canvas?.GetProperty("renderMode", bf);
            P_Canvas_SortingOrder = T_Canvas?.GetProperty("sortingOrder", bf);

            P_RT_AnchoredPosition = T_RectTransform?.GetProperty("anchoredPosition", bf);
            P_RT_AnchorMin = T_RectTransform?.GetProperty("anchorMin", bf);
            P_RT_AnchorMax = T_RectTransform?.GetProperty("anchorMax", bf);
            P_RT_Pivot = T_RectTransform?.GetProperty("pivot", bf);
            P_RT_SizeDelta = T_RectTransform?.GetProperty("sizeDelta", bf);

            P_Transform_Position = T_Transform?.GetProperty("position", bf);
            P_Transform_LocalPosition = T_Transform?.GetProperty("localPosition", bf);
            P_Transform_LocalScale = T_Transform?.GetProperty("localScale", bf);

            if (T_Transform != null)
            {
                M_Transform_SetSiblingIndex = T_Transform.GetMethod("SetSiblingIndex",
                    new[] { typeof(int) });
            }

            // Color fields — IL2CPP Color may expose r/g/b/a as properties or fields
            P_Color_R = T_Color?.GetProperty("r", bf);
            P_Color_G = T_Color?.GetProperty("g", bf);
            P_Color_B = T_Color?.GetProperty("b", bf);
            P_Color_A = T_Color?.GetProperty("a", bf);

            P_Graphic_Color = T_Graphic?.GetProperty("color", bf);
            P_Image_Color = T_Image?.GetProperty("color", bf);
            P_Image_Sprite = T_Image?.GetProperty("sprite", bf);
            P_RawImage_Texture = T_RawImage?.GetProperty("texture", bf);
            P_Animator_Enabled = T_Animator?.GetProperty("enabled", bf);

            P_Screen_Width = T_Screen?.GetProperty("width", bfs);
            P_Screen_Height = T_Screen?.GetProperty("height", bfs);

            P_Camera_Main = T_Camera?.GetProperty("main", bfs);
            if (T_Camera != null && T_Vector3 != null)
            {
                foreach (var m in T_Camera.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "WorldToScreenPoint" && m.GetParameters().Length == 1)
                    { M_WorldToScreenPoint = m; break; }
                }
            }

            // Text properties (work for both TMP and legacy)
            if (T_TextComponent != null)
            {
                P_Text_FontSize = T_TextComponent.GetProperty("fontSize", bf);
                P_Text_Alignment = T_TextComponent.GetProperty("alignment", bf);
                P_Text_Color = T_TextComponent.GetProperty("color", bf);
                P_Text_Font = T_TextComponent.GetProperty("font", bf);

                if (UseTMP)
                {
                    P_Text_RichText = T_TextComponent.GetProperty("richText", bf);
                    P_Text_WordWrap = T_TextComponent.GetProperty("enableWordWrapping", bf);
                }
                else
                {
                    P_Text_RichText = T_TextComponent.GetProperty("supportRichText", bf);
                    // legacy Text doesn't have enableWordWrapping
                }
            }

            P_TMP_DefaultFontAsset = T_TMP_Settings?.GetProperty("defaultFontAsset", bfs);
            P_Texture2D_Width = T_Texture2D?.GetProperty("width", bf);
            P_Texture2D_Height = T_Texture2D?.GetProperty("height", bf);
            P_Unit_Hp = T_DatUnitWork?.GetProperty("hp", bf);
            P_Unit_MaxHp = T_DatUnitWork?.GetProperty("maxhp", bf);
        }

        // ── Native Function Resolution ──

        private static void ResolveNatives()
        {
            // General native scan for game functions
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                        foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                        {
                            string n = f.Name;
                            if (!n.Contains("NativeMethodInfoPtr")) continue;

                            if (N_GetUnitWork == null && n.Contains("nbGetUnitWorkFromFormindex"))
                            {
                                Mip_GetUnitWork = (IntPtr)f.GetValue(null)!;
                                IntPtr fp = Marshal.ReadIntPtr(Mip_GetUnitWork);
                                if (fp != IntPtr.Zero)
                                    N_GetUnitWork = Marshal.GetDelegateForFunctionPointer<d_GetUnitWork>(fp);
                            }

                            if (N_GetUnitID == null && n.Contains("nbGetUnitID") && n.Contains("datUnitWork"))
                            {
                                Mip_GetUnitID = (IntPtr)f.GetValue(null)!;
                                IntPtr fp = Marshal.ReadIntPtr(Mip_GetUnitID);
                                if (fp != IntPtr.Zero)
                                    N_GetUnitID = Marshal.GetDelegateForFunctionPointer<d_GetUnitID>(fp);
                            }

                            // cmbGetStatTarget — Cathedral demon viewer
                            if (N_CmbGetStatTarget == null && n.Contains("cmbGetStatTarget"))
                            {
                                Mip_CmbGetStatTarget = (IntPtr)f.GetValue(null)!;
                                IntPtr fp = Marshal.ReadIntPtr(Mip_CmbGetStatTarget);
                                if (fp != IntPtr.Zero)
                                    N_CmbGetStatTarget = Marshal.GetDelegateForFunctionPointer<d_CmbGetStatTarget>(fp);
                            }

                            // datGetAisyo — direct affinity lookup (no skill context needed)
                            if (N_DatGetAisyo == null && n.Contains("datGetAisyo") && n.Contains("datUnitWork"))
                            {
                                Mip_DatGetAisyo = (IntPtr)f.GetValue(null)!;
                                IntPtr fp = Marshal.ReadIntPtr(Mip_DatGetAisyo);
                                if (fp != IntPtr.Zero)
                                    N_DatGetAisyo = Marshal.GetDelegateForFunctionPointer<d_DatGetAisyo>(fp);
                            }
                        }
                }
                catch { }
            }

            // Graphic.set_color — resolve ONLY from the Graphic type we already found.
            // SpriteRenderer.set_color and TMP_Text.set_color both CRASH on Image objects.
            if (T_Graphic != null)
            {
                foreach (var f in T_Graphic.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (f.Name.Contains("NativeMethodInfoPtr") && f.Name.Contains("set_color"))
                    {
                        Mip_SetColor = (IntPtr)f.GetValue(null)!;
                        IntPtr fp = Marshal.ReadIntPtr(Mip_SetColor);
                        if (fp != IntPtr.Zero)
                        {
                            N_SetColor = Marshal.GetDelegateForFunctionPointer<d_SetColorRef>(fp);
                            _log?.Msg($"ReflectionCache: set_color resolved from Graphic.{f.Name}");
                            break;
                        }
                    }
                }
            }

            if (N_SetColor == null)
                _log?.Warning("ReflectionCache: FAILED to resolve Graphic.set_color!");
        }

        // ═══════════════════════════════════════════════════
        //  UTILITY METHODS — convenience wrappers for other files
        // ═══════════════════════════════════════════════════

        /// <summary>Read RGBA from a Graphic/Image component's color property.</summary>
        internal static bool ReadColor(object component, out float r, out float g, out float b, out float a)
        {
            r = g = b = a = 0f;
            try
            {
                // Try Image.color first, fallback to Graphic.color
                var prop = P_Image_Color ?? P_Graphic_Color;
                if (prop == null) return false;

                object? color = prop.GetValue(component);
                if (color == null) return false;

                // Read r/g/b/a — try properties first, then fields
                r = ReadFloat(color, "r");
                g = ReadFloat(color, "g");
                b = ReadFloat(color, "b");
                a = ReadFloat(color, "a");
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Write RGBA to a Graphic/Image component's color via NATIVE set_color call.
        /// PropertyInfo.SetValue does NOT work for IL2CPP Color structs — must use
        /// the native function pointer with ref struct calling convention.
        /// Falls back to PropertyInfo if native method isn't resolved.
        /// </summary>
        internal static bool WriteColor(object component, float r, float g, float b, float a)
        {
            try
            {
                // Primary: native set_color with ref struct (proven to work in v1)
                if (N_SetColor != null && Mip_SetColor != IntPtr.Zero)
                {
                    IntPtr ptr = GetIl2CppPointer(component);
                    if (ptr != IntPtr.Zero)
                    {
                        var color = new UnityColor(r, g, b, a);
                        N_SetColor(ptr, ref color, Mip_SetColor);
                        return true;
                    }
                }

                // Fallback: PropertyInfo (may not actually write through on game objects)
                var prop = SelectColorProperty(component);
                if (prop == null || Ctor_Color4f == null) return false;
                object colorObj = Ctor_Color4f.Invoke(new object[] { r, g, b, a });
                prop.SetValue(component, colorObj);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Get the IL2CPP native pointer from an Il2CppObjectBase-derived object.
        /// IL2CPP interop objects have a .Pointer property that gives the native IntPtr.
        /// </summary>
        internal static IntPtr GetIl2CppPointerPublic(object obj) => GetIl2CppPointer(obj);
        private static IntPtr GetIl2CppPointer(object obj)
        {
            try
            {
                // Walk up the type hierarchy to find the Pointer property
                var type = obj.GetType();
                while (type != null)
                {
                    var pp = type.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
                    if (pp != null && pp.PropertyType == typeof(IntPtr))
                        return (IntPtr)pp.GetValue(obj)!;
                    type = type.BaseType;
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        /// <summary>Read a float from an object's property or field by name.</summary>
        private static float ReadFloat(object obj, string name)
        {
            var type = obj.GetType();
            // Try property
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null) return (float)prop.GetValue(obj)!;
            // Try field
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) return (float)field.GetValue(obj)!;
            return 0f;
        }

        private static int ReadInt(object obj, PropertyInfo? prop, string name)
        {
            try
            {
                if (prop != null)
                    return Convert.ToInt32(prop.GetValue(obj));

                var type = obj.GetType();
                var namedProp = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (namedProp != null)
                    return Convert.ToInt32(namedProp.GetValue(obj));

                var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null)
                    return Convert.ToInt32(field.GetValue(obj));
            }
            catch { }

            return -1;
        }

        private static PropertyInfo? SelectColorProperty(object component)
        {
            try
            {
                Type componentType = component.GetType();
                if (T_Image != null && T_Image.IsAssignableFrom(componentType))
                    return P_Image_Color ?? P_Graphic_Color;
            }
            catch { }

            return P_Graphic_Color ?? P_Image_Color;
        }

        /// <summary>Create a child GameObject under a parent.</summary>
        internal static object? MakeChild(object parentGO, string name)
        {
            if (Ctor_GameObject_String == null || P_GO_Transform == null || M_Transform_SetParent == null)
                return null;
            try
            {
                var child = Ctor_GameObject_String.Invoke(new object[] { name });
                var childTr = P_GO_Transform.GetValue(child);
                var parentTr = P_GO_Transform.GetValue(parentGO);
                if (childTr != null && parentTr != null)
                    M_Transform_SetParent.Invoke(childTr, new[] { parentTr, (object)false });
                return child;
            }
            catch { return null; }
        }

        /// <summary>Configure a RectTransform with full anchor/pivot/size/position.</summary>
        internal static void SetRect(object go, float w, float h,
            float anchorX, float anchorY, float pivotX, float pivotY,
            float posX, float posY)
        {
            if (T_RectTransform == null || M_GetComponent == null || Ctor_Vector2 == null) return;
            try
            {
                var rt = M_GetComponent.MakeGenericMethod(T_RectTransform).Invoke(go, null);
                if (rt == null) return;

                P_RT_AnchorMin?.SetValue(rt, Ctor_Vector2.Invoke(new object[] { anchorX, anchorY }));
                P_RT_AnchorMax?.SetValue(rt, Ctor_Vector2.Invoke(new object[] { anchorX, anchorY }));
                P_RT_Pivot?.SetValue(rt, Ctor_Vector2.Invoke(new object[] { pivotX, pivotY }));
                P_RT_SizeDelta?.SetValue(rt, Ctor_Vector2.Invoke(new object[] { w, h }));
                P_RT_AnchoredPosition?.SetValue(rt, Ctor_Vector2.Invoke(new object[] { posX, posY }));
            }
            catch { }
        }

        /// <summary>Set a GameObject active or inactive.</summary>
        internal static void SetActive(object? go, bool active)
        {
            if (go == null || M_SetActive == null) return;
            try { M_SetActive.Invoke(go, new object[] { active }); } catch { }
        }

        /// <summary>Get a component of a specific type from a GameObject.</summary>
        internal static object? GetComponent(object go, Type? componentType)
        {
            if (componentType == null || M_GetComponent == null) return null;
            try { return M_GetComponent.MakeGenericMethod(componentType).Invoke(go, null); }
            catch { return null; }
        }

        /// <summary>Get all components of a specific type from a GO and its children.</summary>
        internal static object? GetComponentsInChildren(object go, Type? componentType)
        {
            if (componentType == null || M_GetComponentsInChildren == null) return null;
            try { return M_GetComponentsInChildren.MakeGenericMethod(componentType).Invoke(go, null); }
            catch { return null; }
        }

        /// <summary>Add a component to a GameObject.</summary>
        internal static object? AddComponent(object go, Type? componentType)
        {
            if (componentType == null || M_AddComponent == null) return null;
            try { return M_AddComponent.MakeGenericMethod(componentType).Invoke(go, null); }
            catch { return null; }
        }

        /// <summary>Get the demon species ID for a formation index.</summary>
        internal static int GetDemonId(int curidx)
        {
            if (N_GetUnitWork == null || N_GetUnitID == null) return -1;
            try
            {
                IntPtr uw = N_GetUnitWork(curidx, Mip_GetUnitWork);
                if (uw == IntPtr.Zero) return -1;
                return N_GetUnitID(uw, Mip_GetUnitID);
            }
            catch { return -1; }
        }

        internal static bool TryGetUnitVitals(int curidx, out int demonId, out int hp, out int maxHp)
        {
            demonId = -1;
            hp = -1;
            maxHp = -1;

            if (N_GetUnitWork == null)
                return false;

            try
            {
                IntPtr uw = N_GetUnitWork(curidx, Mip_GetUnitWork);
                if (uw == IntPtr.Zero)
                    return false;

                if (N_GetUnitID != null)
                    demonId = N_GetUnitID(uw, Mip_GetUnitID);

                if (Ctor_DatUnitWork_IntPtr == null)
                    return demonId > 0;

                object? unit = Ctor_DatUnitWork_IntPtr.Invoke(new object[] { uw });
                if (unit == null)
                    return demonId > 0;

                hp = ReadInt(unit, P_Unit_Hp, "hp");
                maxHp = ReadInt(unit, P_Unit_MaxHp, "maxhp");
                return true;
            }
            catch
            {
                return demonId > 0;
            }
        }

        /// <summary>Get the reticle GameObject for a formation index (via reflection to avoid CoreModule ref).</summary>
        internal static object? GetTargetMark2D(int curidx)
        {
            if (M_GetTargetMark2D == null) return null;
            try { return M_GetTargetMark2D.Invoke(null, new object[] { curidx }); }
            catch { return null; }
        }

        /// <summary>Create a new Texture2D(width, height, RGBA32, false) via reflection.</summary>
        internal static object? CreateTexture2D(int width, int height)
        {
            if (Ctor_Texture2D == null || _textureFormatType == null) return null;
            try
            {
                // TextureFormat.RGBA32 = 4
                object texFormat = Enum.ToObject(_textureFormatType, 4);
                return Ctor_Texture2D.Invoke(new object[] { width, height, texFormat, false });
            }
            catch { return null; }
        }

        /// <summary>Get the player character's name (for save file keying).</summary>
        internal static string GetPlayerName()
        {
            if (M_frGetNameString == null) return "Unknown";
            try
            {
                var result = M_frGetNameString.Invoke(null, new object[] { (sbyte)0 });
                return result?.ToString() ?? "Unknown";
            }
            catch { return "Unknown"; }
        }

        /// <summary>Get screen dimensions.</summary>
        internal static (int width, int height) GetScreenSize()
        {
            try
            {
                int w = (int)(P_Screen_Width?.GetValue(null) ?? 1920);
                int h = (int)(P_Screen_Height?.GetValue(null) ?? 1080);
                return (w, h);
            }
            catch { return (1920, 1080); }
        }

        /// <summary>Convert a world-space position to screen-space via Camera.main.</summary>
        internal static (float x, float y) WorldToScreen(float wx, float wy, float wz)
        {
            try
            {
                if (P_Camera_Main == null || M_WorldToScreenPoint == null || Ctor_Vector3 == null) return (0, 0);
                var cam = P_Camera_Main.GetValue(null);
                if (cam == null) return (0, 0);
                var worldPos = Ctor_Vector3.Invoke(new object[] { wx, wy, wz });
                var screenPos = M_WorldToScreenPoint.Invoke(cam, new[] { worldPos });
                if (screenPos == null) return (0, 0);
                float sx = ReadFloat(screenPos, "x");
                float sy = ReadFloat(screenPos, "y");
                return (sx, sy);
            }
            catch { return (0, 0); }
        }

        /// <summary>Get a UI element's canvas position via RectTransform.anchoredPosition.</summary>
        internal static (float canvasX, float canvasY) GetCanvasPosition(object go)
        {
            try
            {
                if (T_RectTransform == null || P_RT_AnchoredPosition == null) return (0, 0);
                var getComp = go.GetType().GetMethod("GetComponent", Type.EmptyTypes);
                if (getComp == null) return (0, 0);
                var rt = getComp.MakeGenericMethod(T_RectTransform).Invoke(go, null);
                if (rt == null) return (0, 0);

                var anchoredPos = P_RT_AnchoredPosition.GetValue(rt);
                if (anchoredPos == null) return (0, 0);

                // anchoredPosition maps directly to our canvas coords (both center-anchored)
                float ax = ReadFloat(anchoredPos, "x");
                float ay = ReadFloat(anchoredPos, "y");
                return (ax, ay);
            }
            catch { return (0, 0); }
        }

        /// <summary>Get full world position (x,y,z) from a GO's transform.</summary>
        internal static (float x, float y, float z) GetWorldPosition(object go)
        {
            try
            {
                if (P_GO_Transform == null || P_Transform_Position == null) return (0, 0, 0);
                var tr = P_GO_Transform.GetValue(go);
                if (tr == null) return (0, 0, 0);
                var pos = P_Transform_Position.GetValue(tr);
                if (pos == null) return (0, 0, 0);
                return (ReadFloat(pos, "x"), ReadFloat(pos, "y"), ReadFloat(pos, "z"));
            }
            catch { return (0, 0, 0); }
        }

        /// <summary>Read a world/screen position from a Transform or RectTransform.</summary>
        internal static (float x, float y) GetPosition(object go)
        {
            try
            {
                if (P_GO_Transform == null) return (0, 0);
                var tr = P_GO_Transform.GetValue(go);
                if (tr == null) return (0, 0);

                var pos = P_Transform_Position?.GetValue(tr);
                if (pos == null) return (0, 0);

                float x = ReadFloat(pos, "x");
                float y = ReadFloat(pos, "y");
                return (x, y);
            }
            catch { return (0, 0); }
        }

        /// <summary>Apply DontDestroyOnLoad to a GameObject.</summary>
        internal static void DontDestroyOnLoad(object go)
        {
            if (M_DontDestroyOnLoad == null) return;
            try { M_DontDestroyOnLoad.Invoke(null, new[] { go }); } catch { }
        }

        /// <summary>Set the text on a Text/TMP component.</summary>
        internal static void SetText(object? textComponent, string text)
        {
            if (textComponent == null || M_SetText == null) return;
            try { M_SetText.Invoke(textComponent, new object[] { text }); } catch { }
        }

        /// <summary>Set Animator.enabled on a component.</summary>
        internal static void SetAnimatorEnabled(object? animator, bool enabled)
        {
            if (animator == null || P_Animator_Enabled == null) return;
            try { P_Animator_Enabled.SetValue(animator, enabled); } catch { }
        }
    }
}
