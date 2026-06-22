using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2Cpp;
using Il2Cpplibsdf_H;
using MelonLoader;

namespace PreyEyes2
{
    /// <summary>
    /// BuffDisplay — integrated buff/debuff overlay. Press START in battle to toggle.
    /// Ported from BuffView v12.
    /// </summary>
    internal static class BuffDisplay
    {
        private static MelonLogger.Instance? _log;
        private static bool _inBattle, _startWasDown, _overlayVisible, _triedCreate, _diagnosed;
        private static object? _canvasGO;
        private static MethodInfo? _goSetActive;

        private const int NUM_COLS = 5;
        private static object?[] _colText = new object[NUM_COLS];
        private static MethodInfo?[] _colSetText = new MethodInfo[NUM_COLS];
        private static object? _titleText, _footerText;
        private static MethodInfo? _titleSetText, _footerSetText;

        private const int MAX_FORMS = 12;
        private const int MAX_HOJO = 8;
        private static int[,] _hojoValues = new int[MAX_FORMS, MAX_HOJO];
        private static bool[] _formAlive = new bool[MAX_FORMS];
        private static string[] _formName = new string[MAX_FORMS];
        private static int _formCount;

        private static readonly int[] HojoIndex = { 4, 7, 6, 5 };
        private static readonly string[] ColHeader = { "", "ATK", "DEF", "HIT", "MAG" };
        private static readonly string[] ColSub = { "", "Taru", "Raku", "Suku", "Maka" };

        private const float BOX_W = 400, BOX_H = 310;
        private const float MARGIN = 14;
        private const float NAME_COL_W = 140;
        private const float BUFF_COL_W = 55;
        private const float HEADER_H = 38;
        private const int FONT_SIZE = 15;

        private static Type? _goType, _rtType, _imgType, _tmpType;
        private static ConstructorInfo? _goCtor;
        private static MethodInfo? _addComp, _getComp;
        private static PropertyInfo? _transProp;
        private static object? _parentTr;
        private static object? _stolenFont;
        private static object? _stolenMaterial; // battle font material with outline support

        internal static void Init(MelonLogger.Instance log)
        {
            _log = log;
        }

        internal static void OnUpdate(bool inBattle)
        {
            try
            {
                _inBattle = inBattle;
                if (!_inBattle) { if (_overlayVisible) Hide(); _overlayVisible = false; _diagnosed = false; return; }

                bool st = dds3PadManager.DDS3_PADCHECK_TRIG(SDF_PADMAP.SDF_PADMAP_START, 0);
                if (st && !_startWasDown)
                {
                    _overlayVisible = !_overlayVisible;
                    if (_overlayVisible) { RefreshHojo(); DumpHojo(); Show(); } else Hide();
                }
                _startWasDown = st;

                if (_overlayVisible) { RefreshHojo(); UpdateColumns(); }
            }
            catch { }
        }

        private static void Show()
        {
            if (!_triedCreate) { _triedCreate = true; CreateUI(); }
            try { _goSetActive?.Invoke(_canvasGO, new object[] { true }); } catch { }
        }
        private static void Hide()
        {
            try { _goSetActive?.Invoke(_canvasGO, new object[] { false }); } catch { }
        }

        private static Type? Find(string fullName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = a.GetType(fullName, false);
                if (t != null && !(t.FullName ?? "").Contains("Melon")) return t;
            }
            var sn = fullName.Substring(fullName.LastIndexOf('.') + 1);
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in a.GetTypes())
                        if (t.Name == sn && t.Namespace != null
                            && (t.Namespace.Contains("UnityEngine") || t.Namespace.Contains("TMPro"))
                            && !t.Namespace.Contains("Melon"))
                            return t;
                }
                catch { }
            }
            return null;
        }

        private static void CreateUI()
        {
            _log?.Msg("=== BuffView v12 UI ===");

            try
            {
                var asmDir = System.IO.Path.GetDirectoryName(typeof(nbCalc).Assembly.Location)!;
                Assembly.LoadFrom(System.IO.Path.Combine(asmDir, "UnityEngine.TextRenderingModule.dll"));
            }
            catch { }

            _goType = Find("UnityEngine.GameObject");
            var canvasT = Find("UnityEngine.Canvas");
            _rtType = Find("UnityEngine.RectTransform");
            _imgType = Find("UnityEngine.UI.Image");
            _tmpType = Find("TMPro.TextMeshProUGUI");

            Type? textType = _tmpType ?? Find("UnityEngine.UI.Text");
            bool useTMP = _tmpType != null;

            if (_goType == null || canvasT == null || textType == null) { _log?.Error("Missing types"); return; }

            _goCtor = _goType.GetConstructor(new[] { typeof(string) })!;
            _addComp = _goType.GetMethod("AddComponent", Type.EmptyTypes)!;
            _getComp = _goType.GetMethod("GetComponent", Type.EmptyTypes)!;
            _transProp = _goType.GetProperty("transform")!;
            _goSetActive = _goType.GetMethod("SetActive", new[] { typeof(bool) });

            // Canvas
            _canvasGO = _goCtor.Invoke(new object[] { "BuffViewCanvas" });
            var canvas = _addComp.MakeGenericMethod(canvasT).Invoke(_canvasGO, null);
            canvasT.GetProperty("renderMode")!.SetValue(canvas, Enum.ToObject(canvasT.GetProperty("renderMode")!.PropertyType, 0));
            canvasT.GetProperty("sortingOrder")?.SetValue(canvas, 9999);

            // DDOL
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var oT = a.GetType("UnityEngine.Object", false); if (oT == null) continue;
                    foreach (var m in oT.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        if (m.Name == "DontDestroyOnLoad") { m.Invoke(null, new[] { _canvasGO }); goto done; }
                }
                done:;
            }
            catch { }

            _parentTr = _transProp.GetValue(_canvasGO);

            // Steal font AND material from battle TMP (for outline support)
            if (useTMP) StealBattleFont(textType);

            // If we got LiberationSans (TMP default), that's NOT the game font.
            // Try harder: search for TMP components on known battle UI GameObjects
            if (_stolenFont != null)
            {
                var fnProp = _stolenFont.GetType().GetProperty("name");
                string fn = fnProp?.GetValue(_stolenFont)?.ToString() ?? "";
                if (fn.Contains("Liberation"))
                {
                    _log?.Msg("  Got default font, searching for game font...");
                    TryStealFromSceneObjects(textType);
                }
            }

            // --- Background: semi-transparent dark, like battle command panel ---
            if (_imgType != null)
            {
                var bgGO = MakeChild("BG");
                _addComp.MakeGenericMethod(_imgType).Invoke(bgGO, null);
                var bgImg = _getComp.MakeGenericMethod(_imgType).Invoke(bgGO, null);
                SetColor(bgImg, 0.0f, 0.0f, 0.05f, 0.75f);
                SetRect(bgGO, BOX_W, BOX_H, 0, 0);

                // Thin top/bottom border lines (subtle blue, like battle UI borders)
                MakeLine("TopLine", BOX_W, 1.5f, 0, BOX_H / 2, 0.3f, 0.35f, 0.6f, 0.7f);
                MakeLine("BotLine", BOX_W, 1.5f, 0, -BOX_H / 2, 0.3f, 0.35f, 0.6f, 0.7f);

                // Slight header separator
                float sepY = BOX_H / 2 - 28;
                MakeLine("Sep", BOX_W - 20, 1f, 0, sepY, 0.2f, 0.2f, 0.4f, 0.4f);
            }

            // --- Text elements ---
            float colStartX = -BOX_W / 2 + MARGIN;
            float headerY = BOX_H / 2 - MARGIN - 30;
            float dataY = headerY - HEADER_H;
            float colH = BOX_H - 90;

            // Title
            _titleText = MakeTextObj(textType, useTMP, "Title", -BOX_W/2 + MARGIN, BOX_H/2 - 6, BOX_W - MARGIN*2, 22, FONT_SIZE + 1, 0);
            _titleSetText = textType.GetProperty("text")?.GetSetMethod();

            // Column headers
            for (int c = 0; c < 4; c++)
            {
                float x = colStartX + NAME_COL_W + c * BUFF_COL_W;
                var hdr = MakeTextObj(textType, useTMP, $"Hdr{c}", x, headerY, BUFF_COL_W, HEADER_H, FONT_SIZE - 1, 1);
                var st2 = textType.GetProperty("text")?.GetSetMethod();
                st2?.Invoke(hdr, new object[] { $"<color=#AABBDD>{ColHeader[c + 1]}</color>\n<color=#7788AA><size={FONT_SIZE - 3}>{ColSub[c + 1]}</size></color>" });
            }

            // Data columns
            _colText[0] = MakeTextObj(textType, useTMP, "Names", colStartX, dataY, NAME_COL_W, colH, FONT_SIZE, 0);
            _colSetText[0] = textType.GetProperty("text")?.GetSetMethod();

            for (int c = 1; c < NUM_COLS; c++)
            {
                float x = colStartX + NAME_COL_W + (c - 1) * BUFF_COL_W;
                _colText[c] = MakeTextObj(textType, useTMP, $"Col{c}", x, dataY, BUFF_COL_W, colH, FONT_SIZE, 1);
                _colSetText[c] = textType.GetProperty("text")?.GetSetMethod();
            }

            // Footer
            _footerText = MakeTextObj(textType, useTMP, "Footer", colStartX, -BOX_H/2 + MARGIN + 20, BOX_W - MARGIN*2, 18, FONT_SIZE - 3, 0);
            _footerSetText = textType.GetProperty("text")?.GetSetMethod();
            _footerSetText?.Invoke(_footerText, new object[] { "<color=#667799>Press START to close</color>" });

            _log?.Msg("=== UI Done ===");
        }

        private static void StealBattleFont(Type textType)
        {
            _log?.Msg("  Stealing battle font...");
            try
            {
                // Use GameObject.Find to locate battle UI text, or iterate children
                // The battle HP/skill text should be active during combat
                // Try to get TMP components from the battle panel hierarchy

                // Search approach: get all TMP components via the scene
                // Since FindObjectsOfType failed before, try GameObject.Find for known objects
                var findMethod = _goType!.GetMethod("Find", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                _log?.Msg($"    GameObject.Find: {findMethod != null}");

                // Try common battle UI paths
                string[] searchNames = { "BattleUICanvas", "Canvas", "BtlCanvas", "UICanvas" };
                object? foundGO = null;
                foreach (var name in searchNames)
                {
                    if (findMethod != null)
                    {
                        foundGO = findMethod.Invoke(null, new object[] { name });
                        if (foundGO != null) { _log?.Msg($"    Found GO: {name}"); break; }
                    }
                }

                // Alternative: search by finding TMP components via the textType itself
                // Use GetComponentsInChildren on the root canvas objects
                // Actually, let's just try to find ANY TMP text in the scene using a different approach
                // Get all root GameObjects from the scene

                if (_tmpType != null)
                {
                    // Try: iterate all transforms to find TMP components
                    // Simpler: just create our own TMP, it should inherit the default TMP settings
                    // which include the font asset from TMP Settings

                    // Try TMP_Settings.defaultFontAsset
                    Type? tmpSettings = null;
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        tmpSettings ??= a.GetType("TMPro.TMP_Settings", false);
                        tmpSettings ??= a.GetType("Il2CppTMPro.TMP_Settings", false);
                        // Brute force
                        if (tmpSettings == null)
                        {
                            try
                            {
                                foreach (var t in a.GetTypes())
                                    if (t.Name == "TMP_Settings") { tmpSettings = t; break; }
                            }
                            catch { }
                        }
                    }
                    _log?.Msg($"    TMP_Settings: {tmpSettings?.FullName ?? "null"}");

                    if (tmpSettings != null)
                    {
                        var dfaProp = tmpSettings.GetProperty("defaultFontAsset", BindingFlags.Public | BindingFlags.Static);
                        if (dfaProp != null)
                        {
                            _stolenFont = dfaProp.GetValue(null);
                            _log?.Msg($"    TMP_Settings.defaultFontAsset: {_stolenFont != null}");
                        }

                        // Also check if there's a default material
                        if (_stolenFont != null)
                        {
                            var matProp = _stolenFont.GetType().GetProperty("material");
                            _stolenMaterial = matProp?.GetValue(_stolenFont);
                            _log?.Msg($"    Default material: {_stolenMaterial != null}");

                            // Log font name
                            var nameProp = _stolenFont.GetType().GetProperty("name");
                            _log?.Msg($"    Font name: {nameProp?.GetValue(_stolenFont)}");
                        }
                    }
                    // Fallback: look for TMP_FontAsset with a search
                    if (_stolenFont == null)
                    {
                        _log?.Msg("    Trying TMP_FontAsset search...");
                        Type? faType = null;
                        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            try
                            {
                                foreach (var t in a.GetTypes())
                                    if (t.Name == "TMP_FontAsset") { faType = t; break; }
                            }
                            catch { }
                            if (faType != null) break;
                        }
                        _log?.Msg($"    TMP_FontAsset type: {faType?.FullName ?? "null"}");

                        // List all its static properties
                        if (faType != null)
                        {
                            foreach (var p in faType.GetProperties(BindingFlags.Public | BindingFlags.Static))
                                _log?.Msg($"      static prop: {p.Name} ({p.PropertyType.Name})");
                            foreach (var m in faType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                            {
                                if (m.Name.Contains("Create") || m.Name.Contains("Default") || m.Name.Contains("Load"))
                                    _log?.Msg($"      static method: {m.Name}({string.Join(", ", Array.ConvertAll(m.GetParameters(), pp => pp.ParameterType.Name))})");
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { _log?.Warning($"  Font steal: {ex.InnerException?.Message ?? ex.Message}"); }
        }

        private static void TryStealFromSceneObjects(Type textType)
        {
            try
            {
                var findMethod = _goType!.GetMethod("Find", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (findMethod == null) { _log?.Msg("    GameObject.Find not available"); return; }

                // Try known/likely battle UI root names
                string[] candidates = {
                    "Canvas", "UICanvas", "BattleCanvas", "BtlCanvas",
                    "CampCanvas", "MainCanvas", "GameCanvas",
                    "UI", "BattleUI", "Root", "UIRoot"
                };

                foreach (var name in candidates)
                {
                    var go = findMethod.Invoke(null, new object[] { name });
                    if (go == null) continue;
                    _log?.Msg($"    Found GO '{name}', searching children for TMP...");

                    // GetComponentsInChildren<TextMeshProUGUI>(true) — include inactive
                    var gcic = _goType.GetMethod("GetComponentsInChildren", new[] { typeof(bool) });
                    if (gcic == null) continue;
                    var gcicTmp = gcic.MakeGenericMethod(_tmpType!);
                    var results = gcicTmp.Invoke(go, new object[] { true });
                    if (results == null) continue;

                    var countP = results.GetType().GetProperty("Count") ?? results.GetType().GetProperty("Length");
                    int count = countP != null ? (int)countP.GetValue(results)! : 0;
                    _log?.Msg($"    Found {count} TMP children");

                    if (count > 0)
                    {
                        var indexer = results.GetType().GetProperty("Item");
                        for (int i = 0; i < Math.Min(count, 5); i++)
                        {
                            var tmp = indexer?.GetValue(results, new object[] { i });
                            if (tmp == null) continue;

                            var fontProp = tmp.GetType().GetProperty("font");
                            var f = fontProp?.GetValue(tmp);
                            if (f == null) continue;

                            var fnP = f.GetType().GetProperty("name");
                            string fname = fnP?.GetValue(f)?.ToString() ?? "";
                            _log?.Msg($"    [{i}] font='{fname}'");

                            if (!fname.Contains("Liberation") && fname.Length > 0)
                            {
                                _stolenFont = f;
                                _stolenMaterial = tmp.GetType().GetProperty("fontSharedMaterial")?.GetValue(tmp);
                                _log?.Msg($"    STOLEN: '{fname}' material={_stolenMaterial != null}");
                                return;
                            }
                        }
                    }
                }

                // Nuclear option: search ALL game objects by iterating root scene objects
                // Use a different approach — find via the canvas hierarchy
                _log?.Msg("    Named search failed. Trying transform hierarchy...");

                // Get our own canvas's parent scene — find other canvases
                Type? canvasType2 = Find("UnityEngine.Canvas");
                if (canvasType2 != null)
                {
                    var gcic2 = _goType.GetMethod("GetComponentsInChildren", new[] { typeof(bool) });
                    // Can't search all scene without FindObjectsOfType...
                    // Try: search specific paths used by SMT3's UI
                    string[] paths = {
                        "dds3UIManager", "dds3Kernel", "GameController",
                        "nbPanel(Clone)", "cmpPanel(Clone)", "BattlePanel"
                    };
                    foreach (var path in paths)
                    {
                        var go = findMethod.Invoke(null, new object[] { path });
                        if (go == null) continue;
                        _log?.Msg($"    Found '{path}'!");

                        var gcicTmp2 = _goType.GetMethod("GetComponentsInChildren", new[] { typeof(bool) })?.MakeGenericMethod(_tmpType!);
                        var results2 = gcicTmp2?.Invoke(go, new object[] { true });
                        var countP2 = results2?.GetType().GetProperty("Count") ?? results2?.GetType().GetProperty("Length");
                        int count2 = countP2 != null ? (int)countP2.GetValue(results2)! : 0;
                        if (count2 > 0)
                        {
                            var indexer2 = results2!.GetType().GetProperty("Item");
                            var tmp = indexer2?.GetValue(results2, new object[] { 0 });
                            var f = tmp?.GetType().GetProperty("font")?.GetValue(tmp);
                            if (f != null)
                            {
                                var fname = f.GetType().GetProperty("name")?.GetValue(f)?.ToString() ?? "";
                                if (!fname.Contains("Liberation"))
                                {
                                    _stolenFont = f;
                                    _stolenMaterial = tmp?.GetType().GetProperty("fontSharedMaterial")?.GetValue(tmp);
                                    _log?.Msg($"    STOLEN from '{path}': '{fname}'");
                                    return;
                                }
                            }
                        }
                    }
                }

                _log?.Msg("    Could not find game font — using LiberationSans");
            }
            catch (Exception ex) { _log?.Warning($"    Scene search: {ex.InnerException?.Message ?? ex.Message}"); }
        }

        private static void ConfigureOutline(object tmpText)
        {
            if (_tmpType == null) return;
            try
            {
                // TMP outline requires:
                // 1. fontMaterial (creates per-instance copy) with OUTLINE_ON keyword
                // 2. Shader properties _OutlineWidth, _OutlineColor, _OutlineSoftness
                // 3. OR use the TMP component's outlineColor/outlineWidth properties

                // First: get fontMaterial (this creates a material instance we can modify)
                var matProp = tmpText.GetType().GetProperty("fontMaterial");
                var mat = matProp?.GetValue(tmpText);

                if (mat == null)
                {
                    // Try fontSharedMaterial
                    matProp = tmpText.GetType().GetProperty("fontSharedMaterial");
                    mat = matProp?.GetValue(tmpText);
                }

                if (mat != null)
                {
                    // Enable outline keyword
                    var enableKeyword = mat.GetType().GetMethod("EnableKeyword", new[] { typeof(string) });
                    enableKeyword?.Invoke(mat, new object[] { "OUTLINE_ON" });

                    // Set shader properties
                    var setFloat = mat.GetType().GetMethod("SetFloat", new[] { typeof(string), typeof(float) });
                    if (setFloat != null)
                    {
                        setFloat.Invoke(mat, new object[] { "_OutlineWidth", 0.25f });
                        setFloat.Invoke(mat, new object[] { "_OutlineSoftness", 0.1f });
                    }

                    // Set outline color via shader — need SetColor(string, Color)
                    // Find the right SetColor overload
                    foreach (var m in mat.GetType().GetMethods())
                    {
                        if (m.Name != "SetColor") continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 2 && ps[0].ParameterType == typeof(string))
                        {
                            // Create a Color for dark blue outline
                            var colorType = ps[1].ParameterType;
                            var cCtor = colorType.GetConstructor(new[] { typeof(float), typeof(float), typeof(float), typeof(float) });
                            if (cCtor != null)
                            {
                                var outlineCol = cCtor.Invoke(new object[] { 0.05f, 0.08f, 0.2f, 1f });
                                m.Invoke(mat, new[] { (object)"_OutlineColor", outlineCol });
                                _log?.Msg("    Outline color set on material");
                            }
                            break;
                        }
                    }

                    // Re-assign to force update
                    tmpText.GetType().GetProperty("fontMaterial")?.SetValue(tmpText, mat);
                }

                // Also set TMP component-level properties
                var outlineColorProp = _tmpType.GetProperty("outlineColor");
                if (outlineColorProp != null)
                {
                    var c32Type = outlineColorProp.PropertyType;
                    var c32Ctor = c32Type.GetConstructor(new[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte) });
                    if (c32Ctor != null)
                        outlineColorProp.SetValue(tmpText, c32Ctor.Invoke(new object[] { (byte)15, (byte)20, (byte)50, (byte)255 }));
                }

                _tmpType.GetProperty("outlineWidth")?.SetValue(tmpText, 0.25f);

                // Face color — bright white
                var faceColorProp = _tmpType.GetProperty("faceColor");
                if (faceColorProp != null)
                {
                    var c32Type = faceColorProp.PropertyType;
                    var c32Ctor = c32Type.GetConstructor(new[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte) });
                    if (c32Ctor != null)
                        faceColorProp.SetValue(tmpText, c32Ctor.Invoke(new object[] { (byte)245, (byte)245, (byte)255, (byte)255 }));
                }

                // Force mesh update
                var updateMesh = tmpText.GetType().GetMethod("UpdateMeshPadding");
                updateMesh?.Invoke(tmpText, null);
                var forceMesh = tmpText.GetType().GetMethod("ForceMeshUpdate", Type.EmptyTypes);
                forceMesh?.Invoke(tmpText, null);
            }
            catch (Exception ex)
            {
                _log?.Warning($"    Outline: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        private static object MakeChild(string name)
        {
            var go = _goCtor!.Invoke(new object[] { name });
            var ct = _transProp!.GetValue(go)!;
            ct.GetType().GetMethod("SetParent", new[] { _parentTr!.GetType(), typeof(bool) })
                ?.Invoke(ct, new[] { _parentTr, (object)false });
            return go;
        }

        private static void SetRect(object go, float w, float h, float x, float y)
        {
            if (_rtType == null) return;
            var rt = _getComp!.MakeGenericMethod(_rtType).Invoke(go, null); if (rt == null) return;
            var sd = rt.GetType().GetProperty("sizeDelta"); if (sd == null) return;
            var v2C = sd.PropertyType.GetConstructor(new[] { typeof(float), typeof(float) }); if (v2C == null) return;
            rt.GetType().GetProperty("anchorMin")?.SetValue(rt, v2C.Invoke(new object[] { 0.5f, 0.5f }));
            rt.GetType().GetProperty("anchorMax")?.SetValue(rt, v2C.Invoke(new object[] { 0.5f, 0.5f }));
            rt.GetType().GetProperty("pivot")?.SetValue(rt, v2C.Invoke(new object[] { 0.5f, 0.5f }));
            sd.SetValue(rt, v2C.Invoke(new object[] { w, h }));
            rt.GetType().GetProperty("anchoredPosition")?.SetValue(rt, v2C.Invoke(new object[] { x, y }));
        }

        private static void MakeLine(string name, float w, float h, float x, float y, float r, float g, float b, float a)
        {
            if (_imgType == null) return;
            var go = MakeChild(name);
            _addComp!.MakeGenericMethod(_imgType).Invoke(go, null);
            var img = _getComp!.MakeGenericMethod(_imgType).Invoke(go, null);
            SetColor(img, r, g, b, a);
            SetRect(go, w, h, x, y);
        }

        private static object? MakeTextObj(Type textType, bool useTMP, string name, float x, float y, float w, float h, int fontSize, int alignment)
        {
            var go = MakeChild(name);
            var text = _addComp!.MakeGenericMethod(textType).Invoke(go, null);
            if (text == null) return null;

            if (useTMP)
            {
                textType.GetProperty("fontSize")?.SetValue(text, (float)fontSize);
                textType.GetProperty("richText")?.SetValue(text, true);
                textType.GetProperty("enableWordWrapping")?.SetValue(text, false);
                textType.GetProperty("overflowMode")?.SetValue(text, 0);

                var alP = textType.GetProperty("alignment");
                if (alP != null)
                {
                    int tmpAlign = alignment switch { 0 => 257, 1 => 258, 4 => 514, 6 => 1025, _ => 257 };
                    alP.SetValue(text, Enum.ToObject(alP.PropertyType, tmpAlign));
                }

                if (_stolenFont != null)
                    textType.GetProperty("font")?.SetValue(text, _stolenFont);

                // Apply battle-style outline
                ConfigureOutline(text);
            }
            else
            {
                textType.GetProperty("fontSize")?.SetValue(text, fontSize);
                textType.GetProperty("supportRichText")?.SetValue(text, true);
                var alP = textType.GetProperty("alignment");
                if (alP != null) alP.SetValue(text, Enum.ToObject(alP.PropertyType, alignment));
                var hoP = textType.GetProperty("horizontalOverflow");
                if (hoP != null) hoP.SetValue(text, Enum.ToObject(hoP.PropertyType, 1));
                var voP = textType.GetProperty("verticalOverflow");
                if (voP != null) voP.SetValue(text, Enum.ToObject(voP.PropertyType, 1));
                SetColor(text, 0.95f, 0.95f, 1f, 1f);
                LoadArialFont(textType, text);
            }

            // Position
            if (_rtType != null)
            {
                var rt = _getComp!.MakeGenericMethod(_rtType).Invoke(go, null);
                if (rt != null)
                {
                    var sdP = rt.GetType().GetProperty("sizeDelta");
                    var v2C = sdP?.PropertyType.GetConstructor(new[] { typeof(float), typeof(float) });
                    if (v2C != null)
                    {
                        rt.GetType().GetProperty("anchorMin")?.SetValue(rt, v2C.Invoke(new object[] { 0.5f, 0.5f }));
                        rt.GetType().GetProperty("anchorMax")?.SetValue(rt, v2C.Invoke(new object[] { 0.5f, 0.5f }));
                        rt.GetType().GetProperty("pivot")?.SetValue(rt, v2C.Invoke(new object[] { 0f, 1f }));
                        sdP!.SetValue(rt, v2C.Invoke(new object[] { w, h }));
                        rt.GetType().GetProperty("anchoredPosition")?.SetValue(rt, v2C.Invoke(new object[] { x, y }));
                    }
                }
            }
            return text;
        }

        private static void SetColor(object? component, float r, float g, float b, float a)
        {
            if (component == null) return;
            try
            {
                var cp = component.GetType().GetProperty("color");
                var cc = cp?.PropertyType.GetConstructor(new[] { typeof(float), typeof(float), typeof(float), typeof(float) });
                cp?.SetValue(component, cc?.Invoke(new object[] { r, g, b, a }));
            }
            catch { }
        }

        private static bool _arialLoaded;
        private static object? _arialFont;
        private static void LoadArialFont(Type textType, object text)
        {
            if (_arialLoaded) { if (_arialFont != null) textType.GetProperty("font")?.SetValue(text, _arialFont); return; }
            _arialLoaded = true;
            try
            {
                var resT = Find("UnityEngine.Resources");
                var fontT = Find("UnityEngine.Font");
                if (resT == null || fontT == null) return;
                MethodInfo? gbr = null;
                foreach (var m in resT.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "GetBuiltinResource" && !m.IsGenericMethod && m.GetParameters().Length == 2) gbr = m;
                if (gbr == null) return;
                Type? il2cppTypeUtil = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    il2cppTypeUtil ??= a.GetType("Il2CppInterop.Runtime.Il2CppType", false);
                var fromM = il2cppTypeUtil?.GetMethod("From", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Type) }, null);
                var il2ft = fromM?.Invoke(null, new object[] { fontT });
                var result = gbr.Invoke(null, new[] { il2ft, (object)"Arial.ttf" });
                if (result != null)
                {
                    if (!fontT.IsAssignableFrom(result.GetType()))
                    {
                        var ptrP = result.GetType().GetProperty("Pointer");
                        var bt = result.GetType();
                        while (ptrP == null && bt != null) { ptrP = bt.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance); bt = bt.BaseType; }
                        if (ptrP != null)
                        {
                            var ptr = (IntPtr)ptrP.GetValue(result)!;
                            if (ptr != IntPtr.Zero) result = fontT.GetConstructor(new[] { typeof(IntPtr) })?.Invoke(new object[] { ptr });
                        }
                    }
                    _arialFont = result;
                    textType.GetProperty("font")?.SetValue(text, _arialFont);
                }
            }
            catch { }
        }

        private static void UpdateColumns()
        {
            if (_colText[0] == null) return;
            try
            {
                _titleSetText?.Invoke(_titleText, new object[] { "<b>BUFF / DEBUFF STATUS</b>" });

                string names = "";
                string[] buffCols = new string[4];
                for (int c = 0; c < 4; c++) buffCols[c] = "";

                for (int f = 0; f < 4; f++)
                {
                    if (!_formAlive[f]) continue;
                    string n = _formName[f].Length > 16 ? _formName[f].Substring(0, 16) : _formName[f];
                    names += $"<color=#CCEECC>{n}</color>\n";
                    for (int c = 0; c < 4; c++)
                        buffCols[c] += FormatVal(_hojoValues[f, HojoIndex[c]]) + "\n";
                }

                bool hasEnemy = false;
                for (int f = 4; f < _formCount; f++) if (_formAlive[f]) { hasEnemy = true; break; }
                if (hasEnemy) { names += "\n"; for (int c = 0; c < 4; c++) buffCols[c] += "\n"; }

                for (int f = 4; f < _formCount; f++)
                {
                    if (!_formAlive[f]) continue;
                    string n = _formName[f].Length > 16 ? _formName[f].Substring(0, 16) : _formName[f];
                    names += $"<color=#EECCCC>{n}</color>\n";
                    for (int c = 0; c < 4; c++)
                        buffCols[c] += FormatVal(_hojoValues[f, HojoIndex[c]]) + "\n";
                }

                _colSetText[0]?.Invoke(_colText[0], new object[] { names });
                for (int c = 0; c < 4; c++)
                    _colSetText[c + 1]?.Invoke(_colText[c + 1], new object[] { buffCols[c] });
            }
            catch { }
        }

        private static string FormatVal(int v)
        {
            if (v > 0) return $"<color=#88FFAA><b>+{v}</b></color>";
            if (v < 0) return $"<color=#FF8888><b>{v}</b></color>";
            return "<color=#445566>\u00B7</color>";
        }

        // --- Name resolution ---
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_GetUnitWork(int formIndex, IntPtr methodInfo);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int d_GetUnitID(IntPtr unitWork, IntPtr methodInfo);

        private static d_GetUnitWork? _nGetUnitWork;
        private static IntPtr _mipUnitWork;
        private static d_GetUnitID? _nGetUnitID;
        private static IntPtr _mipUnitID;
        private static MethodInfo? _getDevilName;
        private static bool _nameResolved;

        private static void ResolveNameMethods()
        {
            if (_nameResolved) return;
            _nameResolved = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (_nGetUnitWork != null && _nGetUnitID != null) break;
                    try
                    {
                        foreach (var t in asm.GetTypes())
                            foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                            {
                                if (_nGetUnitWork == null && f.Name.Contains("nbGetUnitWorkFromFormindex") && f.Name.Contains("NativeMethodInfoPtr"))
                                { _mipUnitWork = (IntPtr)f.GetValue(null)!; _nGetUnitWork = Marshal.GetDelegateForFunctionPointer<d_GetUnitWork>(Marshal.ReadIntPtr(_mipUnitWork)); }
                                if (_nGetUnitID == null && f.Name.Contains("nbGetUnitID") && f.Name.Contains("NativeMethodInfoPtr") && f.Name.Contains("datUnitWork"))
                                { _mipUnitID = (IntPtr)f.GetValue(null)!; _nGetUnitID = Marshal.GetDelegateForFunctionPointer<d_GetUnitID>(Marshal.ReadIntPtr(_mipUnitID)); }
                            }
                    }
                    catch { }
                }
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var ddn = asm.GetType("Il2Cpp.datDevilName", false);
                    if (ddn == null) continue;
                    _getDevilName = ddn.GetMethod("Get", new[] { typeof(int) }) ?? ddn.GetMethod("get_Item", new[] { typeof(int) });
                    break;
                }
            }
            catch { }
        }

        private static string GetFormName(int fi)
        {
            try
            {
                if (_nGetUnitWork == null) return fi < 4 ? $"Party {fi}" : $"Enemy {fi-4}";
                IntPtr uw = _nGetUnitWork(fi, _mipUnitWork);
                if (uw == IntPtr.Zero) return "";
                if (_nGetUnitID != null)
                {
                    int id = _nGetUnitID(uw, _mipUnitID);
                    if (id <= 0) return "";
                    if (_getDevilName != null)
                    { try { var r = _getDevilName.Invoke(null, new object[] { id }); if (r != null) return r.ToString()!; } catch { } }
                    return $"#{id}";
                }
                return fi < 4 ? $"Party {fi}" : $"Enemy {fi-4}";
            }
            catch { return ""; }
        }

        private static void DumpHojo()
        {
            _log?.Msg("=== HOJO DUMP ===");
            for (int f = 0; f < _formCount; f++)
            {
                if (!_formAlive[f]) continue;
                string v = ""; for (int h = 0; h < MAX_HOJO; h++) v += $" t{h}={_hojoValues[f, h]}";
                _log?.Msg($"  [{f}] {_formName[f]}:{v}");
            }
        }

        private static void RefreshHojo()
        {
            try
            {
                if (!_nameResolved) ResolveNameMethods();
                _formCount = 0;
                for (int form = 0; form < MAX_FORMS; form++)
                {
                    _formAlive[form] = false; _formName[form] = "";
                    try
                    {
                        bool hasData = false;
                        for (int h = 0; h < MAX_HOJO; h++)
                        { try { _hojoValues[form, h] = nbCalc.nbGetHojoCounter(form, h); hasData = true; } catch { _hojoValues[form, h] = 0; } }
                        if (!hasData) continue;
                        string name = GetFormName(form);
                        if (name == "" && form >= 4) continue;
                        if (name == "" && form < 4) name = form == 0 ? "Demi-fiend" : $"Ally {form}";
                        _formAlive[form] = true; _formName[form] = name; _formCount = form + 1;
                    }
                    catch { }
                }
                if (!_diagnosed && _formCount > 0) _diagnosed = true;
            }
            catch { }
        }
    }
}
