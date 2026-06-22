using System;
using System.IO;
using System.Reflection;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;

namespace PreyEyes2
{
    /// <summary>
    /// Renders an affinity board above the targeted enemy.
    /// Two visual styles:
    ///   SMT3 (default): Game's native banner bar backdrop, individual element circles, color-tinted result tiles.
    ///   SMTV (legacy):  Element strip banner, separate backdrop texture, result tiles with their own backdrop.
    /// Style selected by presence of Mods/PreyEyes2_smtvstyle file.
    /// </summary>
    internal static class AffinityBoard
    {
        private static MelonLogger.Instance? _log;
        private static string _modsDir = "";
        private static bool _diagnosticMode = false;
        private static bool _useSmt3Style = true;

        // ── Canvas ──
        private static bool _initialized = false;
        private static object? _canvasGO;

        // Pre-loaded sprites (shared across all boards)
        private static readonly object?[] _resultSprites = new object?[7];
        private static readonly object?[] _elemSprites = new object?[NUM_ELEMENTS];

        // ── Per-enemy board pool (curidx 0-11) ──
        private const int MAX_SLOTS = 12;

        private struct BoardInstance
        {
            public object? boardGO;
            public object? backdropGO;
            public object?[] elemGOs;
            public object?[] elemImages;
            public object?[] tileGOs;
            public object?[] tileImages;
            // Ailment row (above element board)
            public object? ailmentRowGO;
            public object? ailmentBackdropGO;
            public object?[] ailmentIconGOs;
            public object?[] ailmentIconImages;
            public object?[] ailmentTileGOs;
            public object?[] ailmentTileImages;
            public bool created;
            public bool visible;
        }

        private static BoardInstance[] _boards = new BoardInstance[MAX_SLOTS];

        // ── State ──
        private static int _posLogCount = 0;
        private static readonly bool[] _drawnThisFrame = new bool[MAX_SLOTS];

        // ── Layout constants ──
        private const int NUM_ELEMENTS = 7;
        private const int NUM_AILMENTS = 3;

        // Ailment attr mapping: display index → game attr
        // attr 8 = Curse, attr 9 = Nerve, attr 10 = Mind
        private static readonly int[] AILMENT_ATTRS = { 8, 9, 10 };
        private static readonly string[] AILMENT_ICON_NAMES = { "curse", "nerve", "mind" };
        private static readonly object?[] _ailmentSprites = new object?[NUM_AILMENTS];

        // Ailment row layout
        private const float AILMENT_ICON_SIZE = 22f;
        private const float AILMENT_GROUP_WIDTH = 48f;   // icon(22) + tile(22) + 4px gap
        private const float AILMENT_GROUP_GAP = 6f;
        private const float AILMENT_ROW_Y_OFFSET = 46f;  // above element board center
        private const float AILMENT_BACKDROP_HEIGHT = 34f;
        private const float AILMENT_BACKDROP_PADDING = 12f;

        // SMT3 style layout — FIXED position (above demon name, fallback)
        private const float SMT3_FIXED_WIDTH = 450f;
        private const float SMT3_FIXED_HEIGHT = 82f;
        private const float SMT3_FIXED_Y = 355f;

        // SMT3 style layout — snug, mathematically precise
        private const float SMT3_BOARD_WIDTH = 196f;     // 7 slots × 28px (26px icon + 2px gap)
        private const float SMT3_BACKDROP_WIDTH = 236f;   // content + 20px padding each side
        private const float SMT3_BACKDROP_HEIGHT = 74f;
        private const float SMT3_ELEM_SIZE = 26f;
        private const float SMT3_ELEM_Y_OFFSET = 14f;
        private const float SMT3_RESULT_Y_OFFSET = -14f;
        private const float BOARD_Y_BELOW_RETICLE = -80f;

        // SMTV style layout (legacy)
        private const float SMTV_BANNER_HEIGHT = 32f;
        private const float SMTV_BANNER_ASPECT = 1297f / 114f;
        private const float SMTV_TILE_SIZE = 29f;
        private const float SMTV_BANNER_Y = 385f;
        private const float SMTV_TILE_Y = 353f;

        // Shared: icon positions (measured from banner)
        private static readonly float[] ICON_CENTERS_NORM = {
            0.0703f, 0.2147f, 0.3587f, 0.5031f, 0.6463f, 0.7892f, 0.9319f
        };

        // Result tile color tints (for SMT3 style — white tiles tinted by affinity)
        private static readonly float[][] RESULT_TINTS = {
            new[] { 0.3f, 1.0f, 0.3f, 1f },   // Weak — green
            new[] { 1.0f, 0.9f, 0.2f, 1f },   // Resist — yellow
            new[] { 1.0f, 0.3f, 0.3f, 1f },   // Null — red
            new[] { 1.0f, 0.3f, 0.3f, 1f },   // Reflect — red
            new[] { 1.0f, 0.3f, 0.3f, 1f },   // Drain — red
            new[] { 1.0f, 1.0f, 1.0f, 1f },   // Normal — full white
            new[] { 1.0f, 1.0f, 1.0f, 1f },   // Unknown — full white
        };

        // ── Cached reflection for ImageConversion ──
        private static MethodInfo? _loadImageMethod;

        // ═══════════════════════════════════════════════════
        //  INIT
        // ═══════════════════════════════════════════════════

        internal static void Init(MelonLogger.Instance log, string modsDir, bool diagnosticMode = false)
        {
            _log = log;
            _modsDir = modsDir;
            _diagnosticMode = diagnosticMode;
            // Check Hyperconfig for UI style, fallback to file-based toggle
            try
            {
                var hcType = Type.GetType("Hyperconfig.HyperconfigStore, Hyperconfig");
                if (hcType != null)
                {
                    var getStr = hcType.GetMethod("GetString", new[] { typeof(string), typeof(string) });
                    if (getStr != null)
                    {
                        string style = (string)(getStr.Invoke(null, new object[] { "PreyEyes.UIStyle", "SMT3" }) ?? "SMT3");
                        _useSmt3Style = !style.Equals("SMTV", StringComparison.OrdinalIgnoreCase);
                        log.Msg($"AffinityBoard: UIStyle from Hyperconfig = {style}");
                    }
                    else _useSmt3Style = !File.Exists(Path.Combine(modsDir, "PreyEyes2_smtvstyle"));
                }
                else _useSmt3Style = !File.Exists(Path.Combine(modsDir, "PreyEyes2_smtvstyle"));
            }
            catch { _useSmt3Style = !File.Exists(Path.Combine(modsDir, "PreyEyes2_smtvstyle")); }

            if (ReflectionCache.T_ImageConversion != null)
            {
                foreach (var m in ReflectionCache.T_ImageConversion.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "LoadImage" && m.GetParameters().Length == 2)
                    { _loadImageMethod = m; break; }
                }
            }

            log.Msg($"AffinityBoard: style={(_useSmt3Style ? "SMT3" : "SMTV")}");
        }

        private static bool EnsureInitialized()
        {
            if (_initialized) return _canvasGO != null;
            _initialized = true;

            try
            {
                LoadAllSprites();
                CreateCanvas();
                // Board creation is lazy per-slot (called from OnTargetDrawn)

                int spriteCount = 0;
                for (int i = 0; i < _resultSprites.Length; i++) if (_resultSprites[i] != null) spriteCount++;
                _log?.Msg($"AffinityBoard: Created. Sprites={spriteCount}/7");
                return _canvasGO != null;
            }
            catch (Exception ex)
            {
                _log?.Error($"AffinityBoard.Init: {ex}");
                return false;
            }
        }

        // ═══════════════════════════════════════════════════
        //  SPRITE LOADING
        // ═══════════════════════════════════════════════════

        internal static object? LoadSpritePublic(string path) => LoadSprite(path);
        private static object? LoadSprite(string path)
        {
            if (!File.Exists(path)) return null;
            if (_loadImageMethod == null) return null;

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                var il2cppBytes = new Il2CppStructArray<byte>(bytes.Length);
                for (int i = 0; i < bytes.Length; i++)
                    il2cppBytes[i] = bytes[i];

                var texture = ReflectionCache.CreateTexture2D(2, 2);
                if (texture == null) return null;

                _loadImageMethod.Invoke(null, new object[] { texture, il2cppBytes });

                // Set filterMode to Bilinear for smooth scaling
                try
                {
                    var filterProp = texture.GetType().GetProperty("filterMode");
                    if (filterProp != null)
                        filterProp.SetValue(texture, Enum.ToObject(filterProp.PropertyType, 1));
                }
                catch { }

                int texW = (int)(ReflectionCache.P_Texture2D_Width?.GetValue(texture) ?? 64);
                int texH = (int)(ReflectionCache.P_Texture2D_Height?.GetValue(texture) ?? 64);

                if (ReflectionCache.M_Sprite_Create != null &&
                    ReflectionCache.Ctor_Rect != null &&
                    ReflectionCache.Ctor_Vector2 != null)
                {
                    var rect = ReflectionCache.Ctor_Rect.Invoke(
                        new object[] { 0f, 0f, (float)texW, (float)texH });
                    var pivot = ReflectionCache.Ctor_Vector2.Invoke(
                        new object[] { 0.5f, 0.5f });
                    return ReflectionCache.M_Sprite_Create.Invoke(null,
                        new object[] { texture, rect, pivot });
                }
            }
            catch (Exception ex)
            {
                _log?.Warning($"AffinityBoard: Failed to load {Path.GetFileName(path)}: {ex.Message}");
            }
            return null;
        }

        private static void LoadAllSprites()
        {
            // Load pre-tinted result icons from Mods/icons/smt3/results/
            // These are baked with affinity colors (green weak, red null, etc.)
            // Fallback to original opaque icons if tinted versions not found
            string resultDir = Path.Combine(_modsDir, "icons", "smt3", "results");
            string fallbackDir = _useSmt3Style
                ? Path.Combine(Path.GetDirectoryName(_modsDir) ?? ".", "ui models", "affinity result icons")
                : Path.Combine(Path.GetDirectoryName(_modsDir) ?? ".", "ui models", "affinity result icons", "transparent");

            string[] resultNames = { "weak", "resist", "null", "reflect", "drain", "normal", "unknown" };

            for (int i = 0; i < resultNames.Length; i++)
            {
                // Try pre-tinted first, fall back to originals
                string path = Path.Combine(resultDir, resultNames[i] + ".png");
                _resultSprites[i] = LoadSprite(path);
                if (_resultSprites[i] == null)
                {
                    path = Path.Combine(fallbackDir, resultNames[i] + ".png");
                    _resultSprites[i] = LoadSprite(path);
                }
                if (_resultSprites[i] != null)
                    _log?.Msg($"AffinityBoard: Loaded {resultNames[i]}.png");
            }

            // Load individual element icons (SMT3 style)
            if (_useSmt3Style)
            {
                // Display order: phys, fire, ice, elec, force, DARK, LIGHT
                // Game attrs 5=light, 6=dark, but display swaps them
                string[] elemNames = { "phys", "fire", "ice", "elec", "force", "dark", "light" };
                string elemDir = Path.Combine(_modsDir, "icons", "smt3");
                for (int i = 0; i < elemNames.Length; i++)
                {
                    _elemSprites[i] = LoadSprite(Path.Combine(elemDir, elemNames[i] + ".png"));
                }

                // Load ailment category icons
                string ailDir = Path.Combine(_modsDir, "icons", "smt3", "ailments");
                string statusDir = Path.Combine(Path.GetDirectoryName(_modsDir) ?? ".", "ui models", "status icons");
                for (int i = 0; i < NUM_AILMENTS; i++)
                {
                    // Prefer status icons dir (higher quality), fall back to ailments dir
                    string statusPath = Path.Combine(statusDir, AILMENT_ICON_NAMES[i] + ".png");
                    _ailmentSprites[i] = LoadSprite(statusPath);
                    if (_ailmentSprites[i] != null)
                    {
                        _log?.Msg($"AffinityBoard: Loaded ailment icon {AILMENT_ICON_NAMES[i]}.png from status icons");
                        continue;
                    }
                    _ailmentSprites[i] = LoadSprite(Path.Combine(ailDir, AILMENT_ICON_NAMES[i] + ".png"));
                    if (_ailmentSprites[i] != null)
                        _log?.Msg($"AffinityBoard: Loaded ailment icon {AILMENT_ICON_NAMES[i]}.png from ailments");
                }
            }
        }

        private static object? GetSpriteForResult(AffinityResult result) => result switch
        {
            AffinityResult.Weak => _resultSprites[0],
            AffinityResult.Resist => _resultSprites[1],
            AffinityResult.Null => _resultSprites[2],
            AffinityResult.Reflect => _resultSprites[3],
            AffinityResult.Drain => _resultSprites[4],
            AffinityResult.Normal => _resultSprites[5],
            AffinityResult.Unknown => _resultSprites[6],
            _ => _resultSprites[5]
        };

        private static int ResultIndex(AffinityResult r) => r switch
        {
            AffinityResult.Weak => 0, AffinityResult.Resist => 1,
            AffinityResult.Null => 2, AffinityResult.Reflect => 3,
            AffinityResult.Drain => 4, AffinityResult.Normal => 5,
            AffinityResult.Unknown => 6, _ => 5
        };

        // ═══════════════════════════════════════════════════
        //  CANVAS
        // ═══════════════════════════════════════════════════

        private static void CreateCanvas()
        {
            if (ReflectionCache.Ctor_GameObject_String == null || ReflectionCache.T_Canvas == null) return;

            _canvasGO = ReflectionCache.Ctor_GameObject_String.Invoke(new object[] { "PE2BoardCanvas" });
            var canvas = ReflectionCache.AddComponent(_canvasGO, ReflectionCache.T_Canvas);
            if (canvas == null) return;

            ReflectionCache.P_Canvas_RenderMode?.SetValue(canvas,
                Enum.ToObject(ReflectionCache.P_Canvas_RenderMode.PropertyType, 0));
            ReflectionCache.P_Canvas_SortingOrder?.SetValue(canvas, 9980);

            if (ReflectionCache.T_CanvasScaler != null)
            {
                var scaler = ReflectionCache.AddComponent(_canvasGO, ReflectionCache.T_CanvasScaler);
                if (scaler != null)
                {
                    try
                    {
                        scaler.GetType().GetProperty("uiScaleMode")?.SetValue(scaler,
                            Enum.ToObject(scaler.GetType().GetProperty("uiScaleMode")!.PropertyType, 1));
                        var refRes = scaler.GetType().GetProperty("referenceResolution");
                        if (refRes != null && ReflectionCache.Ctor_Vector2 != null)
                            refRes.SetValue(scaler, ReflectionCache.Ctor_Vector2.Invoke(new object[] { 1920f, 1080f }));
                        scaler.GetType().GetProperty("matchWidthOrHeight")?.SetValue(scaler, 0.5f);
                    }
                    catch { }
                }
            }

            ReflectionCache.DontDestroyOnLoad(_canvasGO);
        }

        // ═══════════════════════════════════════════════════
        //  SMT3 STYLE BOARD
        // ═══════════════════════════════════════════════════

        private static object? _backdropSprite; // cached, shared across all boards

        /// <summary>Create a board instance for a specific curidx slot.</summary>
        private static void CreateBoardForSlot(int slot)
        {
            if (_canvasGO == null || slot < 0 || slot >= MAX_SLOTS) return;
            if (_boards[slot].created) return;

            ref var b = ref _boards[slot];
            b.elemGOs = new object?[NUM_ELEMENTS];
            b.elemImages = new object?[NUM_ELEMENTS];
            b.tileGOs = new object?[NUM_ELEMENTS];
            b.tileImages = new object?[NUM_ELEMENTS];

            b.boardGO = ReflectionCache.MakeChild(_canvasGO, $"PE2Board{slot}");
            if (b.boardGO == null) return;

            // Backdrop
            if (_backdropSprite == null)
                _backdropSprite = LoadSprite(Path.Combine(_modsDir, "icons", "smt3", "anchored_board_banner.png"));

            b.backdropGO = ReflectionCache.MakeChild(b.boardGO, $"PE2Bd{slot}");
            if (b.backdropGO != null && ReflectionCache.T_Image != null)
            {
                var img = ReflectionCache.AddComponent(b.backdropGO, ReflectionCache.T_Image);
                if (_backdropSprite != null && img != null)
                {
                    ReflectionCache.P_Image_Sprite?.SetValue(img, _backdropSprite);
                    img.GetType().GetProperty("type")?.SetValue(img, Enum.ToObject(img.GetType().GetProperty("type")!.PropertyType, 0));
                }
                ReflectionCache.SetRect(b.backdropGO, SMT3_BACKDROP_WIDTH, SMT3_BACKDROP_HEIGHT,
                    0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f);
            }

            float spacing = SMT3_BOARD_WIDTH / NUM_ELEMENTS;
            float startX = -SMT3_BOARD_WIDTH / 2f + spacing / 2f;

            for (int i = 0; i < NUM_ELEMENTS; i++)
            {
                float x = (float)Math.Round(startX + i * spacing);

                var elemGO = ReflectionCache.MakeChild(b.boardGO, $"PE2E{slot}_{i}");
                b.elemGOs[i] = elemGO;
                if (elemGO != null && ReflectionCache.T_Image != null)
                {
                    var img = ReflectionCache.AddComponent(elemGO, ReflectionCache.T_Image);
                    b.elemImages[i] = img;
                    if (img != null && _elemSprites[i] != null)
                    {
                        ReflectionCache.P_Image_Sprite?.SetValue(img, _elemSprites[i]);
                        img.GetType().GetProperty("type")?.SetValue(img, Enum.ToObject(img.GetType().GetProperty("type")!.PropertyType, 0));
                        img.GetType().GetProperty("preserveAspect")?.SetValue(img, true);
                    }
                    ReflectionCache.SetRect(elemGO, SMT3_ELEM_SIZE, SMT3_ELEM_SIZE,
                        0.5f, 0.5f, 0.5f, 0.5f, x, SMT3_ELEM_Y_OFFSET);
                }

                var tileGO = ReflectionCache.MakeChild(b.boardGO, $"PE2T{slot}_{i}");
                b.tileGOs[i] = tileGO;
                if (tileGO != null && ReflectionCache.T_Image != null)
                {
                    var img = ReflectionCache.AddComponent(tileGO, ReflectionCache.T_Image);
                    b.tileImages[i] = img;
                    if (img != null)
                    {
                        img.GetType().GetProperty("preserveAspect")?.SetValue(img, true);
                        img.GetType().GetProperty("type")?.SetValue(img,
                            Enum.ToObject(img.GetType().GetProperty("type")!.PropertyType, 0)); // Simple
                    }
                    ReflectionCache.SetRect(tileGO, SMT3_ELEM_SIZE, SMT3_ELEM_SIZE,
                        0.5f, 0.5f, 0.5f, 0.5f, x, SMT3_RESULT_Y_OFFSET);
                }
            }

            // ── Ailment row (above element board) ──
            b.ailmentRowGO = ReflectionCache.MakeChild(b.boardGO, $"PE2Ail{slot}");
            // Force center anchors so children use same coordinate system as element board
            if (b.ailmentRowGO != null)
                ReflectionCache.SetRect(b.ailmentRowGO, 0f, 0f, 0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f);
            b.ailmentIconGOs = new object?[NUM_AILMENTS];
            b.ailmentIconImages = new object?[NUM_AILMENTS];
            b.ailmentTileGOs = new object?[NUM_AILMENTS];
            b.ailmentTileImages = new object?[NUM_AILMENTS];

            if (b.ailmentRowGO == null) goto skipAilments;

            // Ailment backdrop
            b.ailmentBackdropGO = ReflectionCache.MakeChild(b.ailmentRowGO, $"PE2AilBd{slot}");
            if (b.ailmentBackdropGO != null && ReflectionCache.T_Image != null)
            {
                var img = ReflectionCache.AddComponent(b.ailmentBackdropGO, ReflectionCache.T_Image);
                if (_backdropSprite != null && img != null)
                {
                    ReflectionCache.P_Image_Sprite?.SetValue(img, _backdropSprite);
                    img.GetType().GetProperty("type")?.SetValue(img, Enum.ToObject(img.GetType().GetProperty("type")!.PropertyType, 0));
                }
                // Size set dynamically in UpdateAilmentRow based on visible count
                ReflectionCache.SetRect(b.ailmentBackdropGO, 0f, AILMENT_BACKDROP_HEIGHT,
                    0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f);
            }

            for (int i = 0; i < NUM_AILMENTS; i++)
            {
                // Ailment category icon
                var aiGO = ReflectionCache.MakeChild(b.ailmentRowGO, $"PE2AI{slot}_{i}");
                b.ailmentIconGOs[i] = aiGO;
                if (aiGO != null && ReflectionCache.T_Image != null)
                {
                    var img = ReflectionCache.AddComponent(aiGO, ReflectionCache.T_Image);
                    b.ailmentIconImages[i] = img;
                    if (img != null && _ailmentSprites[i] != null)
                    {
                        ReflectionCache.P_Image_Sprite?.SetValue(img, _ailmentSprites[i]);
                        img.GetType().GetProperty("type")?.SetValue(img, Enum.ToObject(img.GetType().GetProperty("type")!.PropertyType, 0));
                        img.GetType().GetProperty("preserveAspect")?.SetValue(img, true);
                    }
                    ReflectionCache.SetRect(aiGO, AILMENT_ICON_SIZE, AILMENT_ICON_SIZE,
                        0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f);
                }

                // Ailment result tile
                var atGO = ReflectionCache.MakeChild(b.ailmentRowGO, $"PE2AT{slot}_{i}");
                b.ailmentTileGOs[i] = atGO;
                if (atGO != null && ReflectionCache.T_Image != null)
                {
                    var img = ReflectionCache.AddComponent(atGO, ReflectionCache.T_Image);
                    b.ailmentTileImages[i] = img;
                    if (img != null)
                    {
                        img.GetType().GetProperty("preserveAspect")?.SetValue(img, true);
                        img.GetType().GetProperty("type")?.SetValue(img,
                            Enum.ToObject(img.GetType().GetProperty("type")!.PropertyType, 0));
                    }
                    ReflectionCache.SetRect(atGO, AILMENT_ICON_SIZE, AILMENT_ICON_SIZE,
                        0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f);
                }
            }

            // Start ailment row hidden (shown only when non-Normal ailments exist)
            ReflectionCache.SetActive(b.ailmentRowGO, false);

            skipAilments:
            b.created = true;
            b.visible = false;
            ReflectionCache.SetActive(b.boardGO, false);
        }

        // Legacy single-board creation removed — pool handles it

        // SMTV style removed — assets preserved in Mods/icons/ for future use

        // ═══════════════════════════════════════════════════
        //  TILE UPDATE
        // ═══════════════════════════════════════════════════

        private static void SetTile(int boardSlot, int elemIdx, AffinityResult affinity)
        {
            if (boardSlot < 0 || boardSlot >= MAX_SLOTS || !_boards[boardSlot].created) return;
            if (elemIdx < 0 || elemIdx >= NUM_ELEMENTS) return;
            var tileImg = _boards[boardSlot].tileImages?[elemIdx];
            if (tileImg == null) return;

            var sprite = GetSpriteForResult(affinity);
            if (sprite != null)
            {
                try
                {
                    // Set Simple FIRST, then sprite
                    var typeProp = tileImg.GetType().GetProperty("type");
                    if (typeProp != null)
                        typeProp.SetValue(tileImg, Enum.ToObject(typeProp.PropertyType, 0));

                    ReflectionCache.P_Image_Sprite?.SetValue(tileImg, sprite);

                    // Force Simple again AFTER sprite (some sprites reset the type)
                    if (typeProp != null)
                        typeProp.SetValue(tileImg, Enum.ToObject(typeProp.PropertyType, 0));
                }
                catch (Exception ex)
                {
                    if (_posLogCount < 3) _log?.Warning($"PE2 SetTile: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            // No tinting — original icon colors look better with their decorative frames
            WriteComponentColor(tileImg, 1f, 1f, 1f, 1f);
        }

        // Display-to-game attr mapping:
        // Display: phys(0), fire(1), ice(2), elec(3), force(4), dark(5), light(6)
        // Game:    attr0,   attr1,   attr2,  attr3,   attr4,    attr7,   attr6
        private static readonly int[] DISPLAY_TO_ATTR = { 0, 1, 2, 3, 4, 7, 6 };

        private static void UpdateBoardForEnemy(int boardSlot, int curidx)
        {
            for (int displayIdx = 0; displayIdx < NUM_ELEMENTS; displayIdx++)
            {
                int gameAttr = DISPLAY_TO_ATTR[displayIdx];
                AffinityResult result = AffinityResolver.QueryAffinityForAttr(curidx, gameAttr);
                SetTile(boardSlot, displayIdx, result);
            }
        }

        // ═══════════════════════════════════════════════════
        //  AILMENT ROW UPDATE
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Update and position the ailment row for a target.
        /// Groups ailments by result type: [CurseIcon][MindIcon][NULL tile]
        /// Uses absolute canvas coordinates (same as element board children).
        /// ailmentRowGO stays at (0,0) — it's just a show/hide container.
        /// </summary>
        private static bool UpdateAilmentRow(int boardSlot, int curidx, bool isCathedral,
            IntPtr cathedralUnitWork, float boardCenterX, float boardCenterY)
        {
            if (boardSlot < 0 || boardSlot >= MAX_SLOTS || !_boards[boardSlot].created) return false;
            ref var b = ref _boards[boardSlot];
            if (b.ailmentRowGO == null) return false;

            // Query all 3 ailment attrs
            var results = new AffinityResult[NUM_AILMENTS];
            int nonNormalCount = 0;

            // Get demon ID for knowledge gate (combat only)
            int demonId = -1;
            if (!isCathedral && curidx >= 0)
                demonId = ReflectionCache.GetDemonId(curidx);

            for (int i = 0; i < NUM_AILMENTS; i++)
            {
                int gameAttr = AILMENT_ATTRS[i];
                AffinityResult result = AffinityResult.Normal;

                // Knowledge gate: Cathedral always known, combat requires discovery
                // Unknown ailments are HIDDEN (not shown), unlike element unknowns
                if (!isCathedral && !KnowledgeStore.IsElementKnown(demonId, gameAttr))
                {
                    results[i] = AffinityResult.Normal; // treated as Normal = hidden
                    continue;
                }

                if (isCathedral && cathedralUnitWork != IntPtr.Zero && ReflectionCache.N_DatGetAisyo != null)
                {
                    uint aisyo = ReflectionCache.N_DatGetAisyo(cathedralUnitWork, gameAttr, ReflectionCache.Mip_DatGetAisyo);
                    result = AffinityResolver.DecodeAisyo(aisyo);
                }
                else if (!isCathedral && ReflectionCache.N_DatGetAisyo != null && ReflectionCache.N_GetUnitWork != null)
                {
                    IntPtr uw = ReflectionCache.N_GetUnitWork(curidx, ReflectionCache.Mip_GetUnitWork);
                    if (uw != IntPtr.Zero)
                    {
                        uint aisyo = ReflectionCache.N_DatGetAisyo(uw, gameAttr, ReflectionCache.Mip_DatGetAisyo);
                        result = AffinityResolver.DecodeAisyo(aisyo);
                    }
                }

                results[i] = result;
                if (result != AffinityResult.Normal) nonNormalCount++;
            }

            if (nonNormalCount == 0)
            {
                ReflectionCache.SetActive(b.ailmentRowGO, false);
                return false;
            }

            // ── Group ailments by result type ──
            // Each group: list of ailment indices sharing the same result, then one tile
            // Max 3 groups (one per unique non-Normal result)
            var groupResults = new AffinityResult[NUM_AILMENTS]; // result per group
            var groupMembers = new int[NUM_AILMENTS][]; // ailment indices per group
            int groupCount = 0;

            for (int i = 0; i < NUM_AILMENTS; i++)
            {
                if (results[i] == AffinityResult.Normal) continue;
                // Check if this result already has a group
                int existingGroup = -1;
                for (int g = 0; g < groupCount; g++)
                    if (groupResults[g] == results[i]) { existingGroup = g; break; }

                if (existingGroup >= 0)
                {
                    // Add to existing group
                    var old = groupMembers[existingGroup];
                    var expanded = new int[old.Length + 1];
                    Array.Copy(old, expanded, old.Length);
                    expanded[old.Length] = i;
                    groupMembers[existingGroup] = expanded;
                }
                else
                {
                    groupResults[groupCount] = results[i];
                    groupMembers[groupCount] = new[] { i };
                    groupCount++;
                }
            }

            // ── Absolute canvas positioning, left-aligned above element board ──
            // Ailment center: just above element backdrop top, flush
            float ailmentY = boardCenterY + SMT3_BACKDROP_HEIGHT / 2f + 2f;
            float leftEdge = boardCenterX - SMT3_BOARD_WIDTH / 2f;
            float x = leftEdge;
            int tilePoolIdx = 0;

            // Hide all ailment elements first
            for (int i = 0; i < NUM_AILMENTS; i++)
            {
                if (b.ailmentIconGOs?[i] != null) ReflectionCache.SetActive(b.ailmentIconGOs[i], false);
                if (b.ailmentTileGOs?[i] != null) ReflectionCache.SetActive(b.ailmentTileGOs[i], false);
            }

            for (int g = 0; g < groupCount; g++)
            {
                if (g > 0) x += AILMENT_GROUP_GAP;

                // Place ailment category icons for this group
                for (int m = 0; m < groupMembers[g].Length; m++)
                {
                    int ailIdx = groupMembers[g][m];
                    if (b.ailmentIconGOs?[ailIdx] != null)
                    {
                        ReflectionCache.SetActive(b.ailmentIconGOs[ailIdx], true);
                        ReflectionCache.SetRect(b.ailmentIconGOs[ailIdx]!, AILMENT_ICON_SIZE, AILMENT_ICON_SIZE,
                            0.5f, 0.5f, 0.5f, 0.5f, x + AILMENT_ICON_SIZE / 2f, ailmentY);
                        x += AILMENT_ICON_SIZE + 2f;
                    }
                }

                // Place shared result tile (reuse from tile pool)
                if (tilePoolIdx < NUM_AILMENTS && b.ailmentTileGOs?[tilePoolIdx] != null)
                {
                    ReflectionCache.SetActive(b.ailmentTileGOs[tilePoolIdx], true);
                    ReflectionCache.SetRect(b.ailmentTileGOs[tilePoolIdx]!, AILMENT_ICON_SIZE, AILMENT_ICON_SIZE,
                        0.5f, 0.5f, 0.5f, 0.5f, x + AILMENT_ICON_SIZE / 2f, ailmentY);

                    var tileImg = b.ailmentTileImages?[tilePoolIdx];
                    if (tileImg != null)
                    {
                        var sprite = GetSpriteForResult(groupResults[g]);
                        if (sprite != null)
                        {
                            var typeProp = tileImg.GetType().GetProperty("type");
                            if (typeProp != null)
                                typeProp.SetValue(tileImg, Enum.ToObject(typeProp.PropertyType, 0));
                            ReflectionCache.P_Image_Sprite?.SetValue(tileImg, sprite);
                            if (typeProp != null)
                                typeProp.SetValue(tileImg, Enum.ToObject(typeProp.PropertyType, 0));
                        }

                        WriteComponentColor(tileImg, 1f, 1f, 1f, 1f);
                    }
                    x += AILMENT_ICON_SIZE;
                    tilePoolIdx++;
                }
            }

            // Size and position backdrop
            float totalWidth = x - leftEdge;
            float backdropCenterX = leftEdge + totalWidth / 2f;
            if (b.ailmentBackdropGO != null)
            {
                ReflectionCache.SetRect(b.ailmentBackdropGO, totalWidth + AILMENT_BACKDROP_PADDING * 2,
                    AILMENT_BACKDROP_HEIGHT, 0.5f, 0.5f, 0.5f, 0.5f, backdropCenterX, ailmentY);
            }

            ReflectionCache.SetActive(b.ailmentRowGO, true);
            return true;
        }

        // ═══════════════════════════════════════════════════
        //  SHOW / HIDE
        // ═══════════════════════════════════════════════════

        private static void ShowBoard(int slot)
        {
            if (slot < 0 || slot >= MAX_SLOTS || !_boards[slot].created) return;
            if (!_boards[slot].visible) { _boards[slot].visible = true; ReflectionCache.SetActive(_boards[slot].boardGO, true); }
        }

        private static void HideBoard(int slot)
        {
            if (slot < 0 || slot >= MAX_SLOTS || !_boards[slot].created) return;
            if (_boards[slot].visible) { _boards[slot].visible = false; ReflectionCache.SetActive(_boards[slot].boardGO, false); }
        }

        private static void HideAllBoards()
        {
            for (int i = 0; i < MAX_SLOTS; i++) HideBoard(i);
        }

        // ═══════════════════════════════════════════════════
        //  PUBLIC API
        // ═══════════════════════════════════════════════════

        internal static void OnTargetDrawn(int curidx, int targetCount, object? reticleGO)
        {
            if (!EnsureInitialized()) return;
            if (targetCount > 1) { HideAllBoards(); return; } // hide during AoE

            // Use slot 0 as the single board
            if (_useSmt3Style && !_boards[0].created)
                CreateBoardForSlot(0);

            UpdateBoardForEnemy(0, curidx);
            UpdateAilmentRow(0, curidx, false, IntPtr.Zero, 0f, SMT3_FIXED_Y);
            PositionBoard(0, 0f, SMT3_FIXED_Y);
            ShowBoard(0);
        }

        /// <summary>Move a board to a canvas position.</summary>
        private static void PositionBoard(int slot, float centerX, float centerY)
        {
            if (slot < 0 || slot >= MAX_SLOTS || !_boards[slot].created) return;
            ref var b = ref _boards[slot];

            if (b.backdropGO != null)
            {
                ReflectionCache.SetRect(b.backdropGO, SMT3_BACKDROP_WIDTH, SMT3_BACKDROP_HEIGHT,
                    0.5f, 0.5f, 0.5f, 0.5f, centerX, centerY);
            }

            float spacing = SMT3_BOARD_WIDTH / NUM_ELEMENTS; // 196/7 = 28px per slot
            float startX = centerX - SMT3_BOARD_WIDTH / 2f + spacing / 2f;

            for (int i = 0; i < NUM_ELEMENTS; i++)
            {
                float x = (float)Math.Round(startX + i * spacing);
                if (b.elemGOs?[i] != null)
                    ReflectionCache.SetRect(b.elemGOs[i]!, SMT3_ELEM_SIZE, SMT3_ELEM_SIZE,
                        0.5f, 0.5f, 0.5f, 0.5f, x, centerY + SMT3_ELEM_Y_OFFSET);
                if (b.tileGOs?[i] != null)
                    ReflectionCache.SetRect(b.tileGOs[i]!, SMT3_ELEM_SIZE, SMT3_ELEM_SIZE,
                        0.5f, 0.5f, 0.5f, 0.5f, x, centerY + SMT3_RESULT_Y_OFFSET);
            }
        }

        internal static void OnNoTargets() { HideAllBoards(); }

        // ═══════════════════════════════════════════════════
        //  CATHEDRAL OF SHADOWS
        // ═══════════════════════════════════════════════════

        private const float CATHEDRAL_BOARD_X = -149f;
        private const float CATHEDRAL_BOARD_Y = 170f;
        private const int CATHEDRAL_SLOT = 1;
        private static int _lastCathedralDemonId = -1;

        /// <summary>Show the affinity board for a demon in the Cathedral (fusion/compendium).</summary>
        internal static void OnCathedralTarget(IntPtr unitWork)
        {
            if (!EnsureInitialized()) return;
            if (unitWork == IntPtr.Zero) { HideBoard(CATHEDRAL_SLOT); return; }

            // Get demon ID — only show if valid (not lobby, actually viewing a demon)
            int demonId = -1;
            if (ReflectionCache.N_GetUnitID != null)
                demonId = ReflectionCache.N_GetUnitID(unitWork, ReflectionCache.Mip_GetUnitID);
            if (demonId <= 0) { HideBoard(CATHEDRAL_SLOT); return; }

            if (!_boards[CATHEDRAL_SLOT].created)
                CreateBoardForSlot(CATHEDRAL_SLOT);

            // Query all 7 affinities directly (always fully known in Cathedral)
            int spriteCount = 0;
            for (int i = 0; i < _resultSprites.Length; i++) if (_resultSprites[i] != null) spriteCount++;
            if (_diagnosticMode && _posLogCount < 3) { _posLogCount++; _log?.Msg($"PE2 CATHEDRAL: demonId={demonId} sprites={spriteCount}/7 board={_boards[CATHEDRAL_SLOT].created}"); }

            // Log raw aisyo per unique demon (once per ID)
            if (_diagnosticMode && demonId != _lastCathedralDemonId)
            {
                _lastCathedralDemonId = demonId;
                // Log attrs 0-9 to find Light/Dark
                string diag = $"PE2 CATHEDRAL AISYO demonId={demonId}: ";
                string[] attrNames = { "phys", "fire", "ice", "elec", "force", "a5", "light", "dark", "a8", "a9", "a10", "a11", "a12", "a13", "a14", "a15" };
                for (int a = 0; a < 16; a++)
                {
                    try
                    {
                        uint raw = ReflectionCache.N_DatGetAisyo!(unitWork, a, ReflectionCache.Mip_DatGetAisyo);
                        diag += $"{attrNames[a]}=0x{raw:X}({AffinityResolver.DecodeAisyo(raw)}) ";
                    }
                    catch { diag += $"{attrNames[a]}=ERR "; }
                }
                _log?.Msg(diag);
            }

            for (int displayIdx = 0; displayIdx < NUM_ELEMENTS; displayIdx++)
            {
                int gameAttr = DISPLAY_TO_ATTR[displayIdx];
                AffinityResult result = AffinityResult.Normal;
                if (ReflectionCache.N_DatGetAisyo != null)
                {
                    uint aisyo = ReflectionCache.N_DatGetAisyo(unitWork, gameAttr, ReflectionCache.Mip_DatGetAisyo);
                    result = AffinityResolver.DecodeAisyo(aisyo);
                }
                SetTile(CATHEDRAL_SLOT, displayIdx, result);
            }

            // Update ailment row for Cathedral
            float catBoardX = CATHEDRAL_BOARD_X + SMT3_BOARD_WIDTH / 2f;
            UpdateAilmentRow(CATHEDRAL_SLOT, -1, true, unitWork, catBoardX, CATHEDRAL_BOARD_Y);
            PositionBoard(CATHEDRAL_SLOT, catBoardX, CATHEDRAL_BOARD_Y);
            ShowBoard(CATHEDRAL_SLOT);
        }

        internal static void OnCathedralClosed()
        {
            HideBoard(CATHEDRAL_SLOT);
            _lastCathedralDemonId = -1;
        }

        /// <summary>Reset board state between battles.</summary>
        internal static void ResetForNewBattle()
        {
            HideAllBoards();
            _initialized = false;
            _canvasGO = null;
            _backdropSprite = null;
            _boards = new BoardInstance[MAX_SLOTS];
            Array.Clear(_resultSprites, 0, _resultSprites.Length);
            Array.Clear(_elemSprites, 0, _elemSprites.Length);
            Array.Clear(_ailmentSprites, 0, _ailmentSprites.Length);
            Array.Clear(_drawnThisFrame, 0, MAX_SLOTS);
            _posLogCount = 0;
        }

        private static void WriteComponentColor(object component, float r, float g, float b, float a)
        {
            try
            {
                var cp = component.GetType().GetProperty("color");
                if (cp == null || ReflectionCache.Ctor_Color4f == null) return;
                cp.SetValue(component, ReflectionCache.Ctor_Color4f.Invoke(new object[] { r, g, b, a }));
            }
            catch { }
        }
    }
}
