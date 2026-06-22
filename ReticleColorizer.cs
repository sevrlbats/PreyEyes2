using System;
using MelonLoader;

namespace PreyEyes2
{
    // ═══════════════════════════════════════════════════
    //  TINT STRATEGY INTERFACE
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Defines how to transform an Image's current RGBA color based on affinity.
    /// The source color is what the animator set this frame; the output is what we write back.
    /// </summary>
    internal interface ITintStrategy
    {
        void Apply(float srcR, float srcG, float srcB, float srcA,
                   AffinityResult affinity,
                   out float dstR, out float dstG, out float dstB, out float dstA);
    }

    // ═══════════════════════════════════════════════════
    //  HSV HUE-SHIFT STRATEGY (Primary)
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Reads the animator's current color, converts to HSV, replaces the hue based on
    /// affinity, and converts back. Preserves saturation, value, and alpha — so the
    /// animation's luminance dynamics and texture detail survive through the tint.
    /// </summary>
    internal class HsvHueShiftStrategy : ITintStrategy
    {
        // The sprite itself is pink — Image.color is always (1,1,1,1).
        // So we set ABSOLUTE tint colors. Since Image.color is multiplicative with
        // the sprite, these tints push the pink toward our target color.
        // Never zero a channel completely — that kills texture detail.
        //
        // These are tuned for a pinkish sprite. Adjust empirically.

        public void Apply(float srcR, float srcG, float srcB, float srcA,
                          AffinityResult affinity,
                          out float dstR, out float dstG, out float dstB, out float dstA)
        {
            dstA = srcA; // always preserve alpha (animator's fade/pulse)

            switch (affinity)
            {
                case AffinityResult.Weak:
                    // Warm green — on white sprite, pure tint works cleanly
                    dstR = 0.2f; dstG = 0.9f; dstB = 0.15f; return;

                case AffinityResult.Resist:
                    // Bright yellow
                    dstR = 1.0f; dstG = 0.9f; dstB = 0.0f; return;

                case AffinityResult.Null:
                case AffinityResult.Reflect:
                case AffinityResult.Drain:
                    // Rich red
                    dstR = 1.0f; dstG = 0.1f; dstB = 0.1f; return;

                case AffinityResult.Normal:
                    // Bright white — the desaturated sprite IS white, just let it through
                    dstR = 1.0f; dstG = 1.0f; dstB = 1.0f; return;

                case AffinityResult.Unknown:
                    // Pure white, same as Normal — the question mark overlay distinguishes it
                    dstR = 1.0f; dstG = 1.0f; dstB = 1.0f; return;

                default:
                    dstR = srcR; dstG = srcG; dstB = srcB; return;
            }
        }

        // ── Pure math RGB ↔ HSV ──

        internal static void RgbToHsv(float r, float g, float b,
                                       out float h, out float s, out float v)
        {
            float cmax = Math.Max(r, Math.Max(g, b));
            float cmin = Math.Min(r, Math.Min(g, b));
            float delta = cmax - cmin;

            v = cmax;
            s = (cmax == 0f) ? 0f : delta / cmax;

            if (delta == 0f)
            {
                h = 0f;
            }
            else if (cmax == r)
            {
                h = 60f * (((g - b) / delta) % 6f);
            }
            else if (cmax == g)
            {
                h = 60f * ((b - r) / delta + 2f);
            }
            else // cmax == b
            {
                h = 60f * ((r - g) / delta + 4f);
            }

            if (h < 0f) h += 360f;
        }

        internal static void HsvToRgb(float h, float s, float v,
                                       out float r, out float g, out float b)
        {
            float c = v * s;
            float x = c * (1f - Math.Abs((h / 60f) % 2f - 1f));
            float m = v - c;

            float r1, g1, b1;

            if (h < 60f)       { r1 = c; g1 = x; b1 = 0; }
            else if (h < 120f) { r1 = x; g1 = c; b1 = 0; }
            else if (h < 180f) { r1 = 0; g1 = c; b1 = x; }
            else if (h < 240f) { r1 = 0; g1 = x; b1 = c; }
            else if (h < 300f) { r1 = x; g1 = 0; b1 = c; }
            else               { r1 = c; g1 = 0; b1 = x; }

            r = r1 + m;
            g = g1 + m;
            b = b1 + m;
        }
    }

    // ═══════════════════════════════════════════════════
    //  LUMINANCE-PRESERVING STRATEGY (Fallback)
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Direct multiplicative tints that are less saturated than pure colors,
    /// preserving more brightness from the original. Simpler but less flexible.
    /// Activate by placing a file named "PreyEyes2_luminance" in the Mods folder.
    /// </summary>
    internal class LuminancePreservingStrategy : ITintStrategy
    {
        public void Apply(float srcR, float srcG, float srcB, float srcA,
                          AffinityResult affinity,
                          out float dstR, out float dstG, out float dstB, out float dstA)
        {
            dstA = srcA;

            float tr, tg, tb;
            switch (affinity)
            {
                case AffinityResult.Weak:
                    tr = 0.5f; tg = 1.0f; tb = 0.5f; break;
                case AffinityResult.Resist:
                    tr = 1.0f; tg = 0.9f; tb = 0.3f; break;
                case AffinityResult.Null:
                case AffinityResult.Reflect:
                case AffinityResult.Drain:
                    tr = 1.0f; tg = 0.4f; tb = 0.4f; break;
                case AffinityResult.Unknown:
                    tr = 0.30f; tg = 0.60f; tb = 0.90f; break;
                default: // Normal — pale blue
                    tr = 0.25f; tg = 0.65f; tb = 1.0f; break;
            }

            dstR = srcR * tr;
            dstG = srcG * tg;
            dstB = srcB * tb;
        }
    }

    // ═══════════════════════════════════════════════════
    //  RETICLE COLORIZER — State + Application
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Manages per-curidx color caching and reticle tint application.
    /// Called from the hook (after trampoline) and from OnUpdate (to fight animator).
    /// </summary>
    internal static class ReticleColorizer
    {
        internal static ITintStrategy ActiveStrategy = new HsvHueShiftStrategy();

        private static MelonLogger.Instance? _log;

        // Per-curidx caches (0-11)
        private const int MAX_SLOTS = 12;
        private static readonly float[] _cachedR = new float[MAX_SLOTS];
        private static readonly float[] _cachedG = new float[MAX_SLOTS];
        private static readonly float[] _cachedB = new float[MAX_SLOTS];
        private static readonly float[] _cachedA = new float[MAX_SLOTS];
        private static readonly bool[] _slotActive = new bool[MAX_SLOTS];

        // Cached Image component ARRAYS — reticle has 3 child Images, no Image on root.
        // We tint ALL of them for each curidx.
        private static readonly object?[]?[] _cachedImageArrays = new object?[]?[MAX_SLOTS];

        // Track whether we've disabled Animators on each reticle (once per curidx per battle)
        private static readonly bool[] _animatorsDisabled = new bool[MAX_SLOTS];

        // Stacked Outline components for border effect on default reticle
        // Two outlines: black outer (larger distance) + white inner (smaller distance)
        private static readonly object?[] _outlineBlack = new object?[MAX_SLOTS];
        private static readonly object?[] _outlineWhite = new object?[MAX_SLOTS];
        private static readonly bool[] _bordersCreated = new bool[MAX_SLOTS];

        // Diagnostic: log first N hook calls to trace the pipeline
        private static int _diagCount = 0;
        private const int MAX_DIAG = 15;
        private static bool _spriteInfoLogged = false;

        // White reticle sprite — loaded once from Mods/icons/reticle_white.png
        private static object? _whiteSprite;
        private static bool _whiteSpriteLoaded = false;
        private static string _modsDir = "";
        private static bool _diagnosticMode = false;

        // Center overlays for Unknown and specific affinity result icons
        private const float CenterOverlaySize = 100f;
        private const float ResultIconSize = 64f;
        private static readonly object?[] _questionGOs = new object?[MAX_SLOTS];
        private static readonly object?[] _resultIconGOs = new object?[MAX_SLOTS];
        private static readonly object?[] _resultIconImages = new object?[MAX_SLOTS];
        private static readonly object?[] _resultIconSprites = new object?[4];
        private static bool _resultIconSpritesLoaded = false;

        internal static void Init(MelonLogger.Instance log, bool useLuminanceStrategy, string modsDir, bool diagnosticMode = false)
        {
            _log = log;
            _modsDir = modsDir;
            _diagnosticMode = diagnosticMode;

            if (useLuminanceStrategy)
            {
                ActiveStrategy = new LuminancePreservingStrategy();
                if (_diagnosticMode)
                    log.Msg("ReticleColorizer: Using LuminancePreserving strategy (fallback).");
            }
            else
            {
                ActiveStrategy = new HsvHueShiftStrategy();
                if (_diagnosticMode)
                    log.Msg("ReticleColorizer: Using HsvHueShift strategy (primary).");
            }
        }

        /// <summary>
        /// Apply tint to the reticle for a given curidx.
        /// Called in the hook AFTER the trampoline (so the animator has set its color).
        /// The reticle has 3 child Image components (no Image on root) — we tint ALL of them.
        /// </summary>
        internal static void ApplyToReticle(int curidx, AffinityResult affinity)
        {
            if (curidx < 0 || curidx >= MAX_SLOTS) return;

            try
            {
                // Get reticle GameObject (via reflection to avoid CoreModule ref)
                object? reticleGO = ReflectionCache.GetTargetMark2D(curidx);
                if (reticleGO == null)
                {
                    if (_diagnosticMode && _diagCount < MAX_DIAG) { _diagCount++; _log?.Msg($"PE2: GetTargetMark2D({curidx}) returned null"); }
                    return;
                }

                // Get or cache the child Image components array
                object?[]? images = _cachedImageArrays[curidx];
                if (images == null && ReflectionCache.T_Image != null)
                {
                    images = ResolveChildImages(reticleGO);
                    _cachedImageArrays[curidx] = images;

                    if (_diagnosticMode && _diagCount < MAX_DIAG)
                    {
                        _diagCount++;
                        _log?.Msg($"PE2: curidx={curidx} affinity={affinity} childImages={images?.Length ?? 0}");
                    }
                }

                if (images == null || images.Length == 0) return;

                // One-time: dump sprite/texture info for the reticle
                if (_diagnosticMode && !_spriteInfoLogged && images[0] != null)
                {
                    _spriteInfoLogged = true;
                    LogSpriteInfo(images);
                }

                // Swap the reticle sprite to our desaturated white version.
                // This makes multiplicative tinting produce clean, vibrant colors.
                SwapToWhiteSprite(images[0]!);

                // Always hide animated overlay layers — solid static tint looks better.
                SetAnimatedLayersVisible(images, false);

                // Read color from the first Image (they all share the same animator tint)
                if (!ReflectionCache.ReadColor(images[0]!, out float r, out float g, out float b, out float a))
                {
                    if (_diagnosticMode && _diagCount < MAX_DIAG) { _diagCount++; _log?.Msg($"PE2: ReadColor failed for curidx={curidx}"); }
                    return;
                }

                // Apply the tint strategy
                ActiveStrategy.Apply(r, g, b, a, affinity,
                    out float dr, out float dg, out float db, out float da);

                // ONE-TIME deep diagnostic of the write path
                if (_diagnosticMode && _diagCount < MAX_DIAG)
                {
                    _diagCount++;
                    bool hasNative = ReflectionCache.N_SetColor != null;
                    IntPtr ptr0 = hasNative ? ReflectionCache.GetIl2CppPointerPublic(images[0]!) : IntPtr.Zero;
                    _log?.Msg($"PE2 WRITE: src=({r:F2},{g:F2},{b:F2},{a:F2}) dst=({dr:F2},{dg:F2},{db:F2},{da:F2}) " +
                              $"native={hasNative} ptr=0x{ptr0:X} mip=0x{ReflectionCache.Mip_SetColor:X}");
                }

                // Write tinted color to ALL child Images
                for (int i = 0; i < images.Length; i++)
                {
                    if (images[i] != null)
                        ReflectionCache.WriteColor(images[i]!, dr, dg, db, da);
                }

                // Cache for OnUpdate reapplication
                _cachedR[curidx] = dr;
                _cachedG[curidx] = dg;
                _cachedB[curidx] = db;
                _cachedA[curidx] = da;
                _slotActive[curidx] = true;

                // Border sprites: white + black silhouettes behind the reticle for definition
                UpdateBorders(curidx, images[0], affinity);

                // Question mark overlay for Unknown affinity
                // Wrapped in try-catch — child GO manipulation during draw can be fragile
                try
                {
                    UpdateQuestionMark(curidx, affinity, reticleGO);
                    UpdateResultIcon(curidx, affinity, reticleGO);
                }
                catch { }
            }
            catch (Exception ex)
            {
                _log?.Error($"ReticleColorizer.ApplyToReticle({curidx}): {ex.Message}");
            }
        }

        /// <summary>
        /// Resolve all Image components in children of the reticle GO.
        /// Returns a C# array of IL2CPP Image component objects.
        /// </summary>
        private static object?[]? ResolveChildImages(object reticleGO)
        {
            try
            {
                // GetComponentsInChildren<Image>() returns Il2CppArrayBase<Image>
                var result = ReflectionCache.GetComponentsInChildren(reticleGO, ReflectionCache.T_Image);
                if (result == null) return null;

                // The result is an Il2CppArrayBase — it implements IList/IEnumerable
                // Try to access it as an array via Count/Length and indexer
                var resultType = result.GetType();

                // Try Length property
                var lenProp = resultType.GetProperty("Length") ?? resultType.GetProperty("Count");
                if (lenProp == null)
                {
                    // Try the 'count' field
                    var countField = resultType.GetField("count");
                    if (countField == null)
                    {
                        _log?.Warning("PE2: Cannot determine image array length");
                        return null;
                    }
                }

                int len = 0;
                if (lenProp != null)
                    len = (int)lenProp.GetValue(result)!;

                if (len == 0) return null;

                // Access elements via indexer
                var indexer = resultType.GetProperty("Item");
                if (indexer == null)
                {
                    // Try array-style access
                    var getMethod = resultType.GetMethod("get_Item");
                    if (getMethod == null)
                    {
                        _log?.Warning("PE2: Cannot access image array elements");
                        return null;
                    }
                }

                var images = new object?[len];
                for (int i = 0; i < len; i++)
                {
                    if (indexer != null)
                        images[i] = indexer.GetValue(result, new object[] { i });
                    else
                        images[i] = resultType.GetMethod("get_Item")?.Invoke(result, new object[] { i });
                }

                return images;
            }
            catch (Exception ex)
            {
                _log?.Warning($"PE2: ResolveChildImages: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Re-apply cached colors for all active slots (all child Images per slot).
        /// Called every frame from OnUpdate to fight animator overrides between hook calls.
        /// </summary>
        internal static void ReapplyCachedColors()
        {
            for (int i = 0; i < MAX_SLOTS; i++)
            {
                if (!_slotActive[i]) continue;
                ReapplyForIndex(i);
            }
        }

        /// <summary>
        /// Re-apply cached color for a specific slot (used by TargetMark2DAnim hook).
        /// </summary>
        internal static void ReapplyForIndex(int curidx)
        {
            if (curidx < 0 || curidx >= MAX_SLOTS || !_slotActive[curidx]) return;
            try
            {
                var images = _cachedImageArrays[curidx];
                if (images == null) return;
                for (int i = 0; i < images.Length; i++)
                {
                    if (images[i] != null)
                        ReflectionCache.WriteColor(images[i]!, _cachedR[curidx], _cachedG[curidx], _cachedB[curidx], _cachedA[curidx]);
                }
            }
            catch { }
        }

        /// <summary>Clear all cached state (e.g., when leaving battle).</summary>
        internal static void ClearAll()
        {
            Array.Clear(_slotActive, 0, MAX_SLOTS);
            Array.Clear(_cachedImageArrays, 0, MAX_SLOTS);
            Array.Clear(_animatorsDisabled, 0, MAX_SLOTS);
            Array.Clear(_outlineBlack, 0, MAX_SLOTS);
            Array.Clear(_outlineWhite, 0, MAX_SLOTS);
            Array.Clear(_bordersCreated, 0, MAX_SLOTS);
            // Reset question marks and sprites for fresh reload next battle
            Array.Clear(_questionGOs, 0, MAX_SLOTS);
            Array.Clear(_resultIconGOs, 0, MAX_SLOTS);
            Array.Clear(_resultIconImages, 0, MAX_SLOTS);
            Array.Clear(_resultIconSprites, 0, _resultIconSprites.Length);
            _resultIconSpritesLoaded = false;
            _whiteSpriteLoaded = false;
            _whiteSprite = null;
            _spriteInfoLogged = false;
        }

        /// <summary>
        /// Add/manage stacked Outline components on the reticle for border definition.
        /// Two outlines: black outer (4px) + white inner (2px).
        /// Only enabled for Normal/Unknown.
        /// </summary>
        private static void UpdateBorders(int curidx, object? image, AffinityResult affinity)
        {
            if (image == null || ReflectionCache.T_Outline == null) return;
            bool wantBorders = true; // always show black outline for definition

            try
            {
                if (!_bordersCreated[curidx])
                {
                    _bordersCreated[curidx] = true;

                    var goProp = image.GetType().GetProperty("gameObject");
                    if (goProp == null) return;
                    var go = goProp.GetValue(image);
                    if (go == null) return;

                    // Add black outline only
                    _outlineBlack[curidx] = ReflectionCache.AddComponent(go, ReflectionCache.T_Outline);
                    if (_outlineBlack[curidx] != null)
                    {
                        SetOutlineProps(_outlineBlack[curidx]!, 0f, 0f, 0f, 1f, 2f, 2f);
                    }

                    _log?.Msg($"PE2: Stacked outlines created for curidx={curidx}");
                }

                // Enable/disable based on affinity
                SetEnabled(_outlineBlack[curidx], wantBorders);
                SetEnabled(_outlineWhite[curidx], wantBorders);
            }
            catch { }
        }

        private static void SetOutlineProps(object outline, float r, float g, float b, float a,
                                             float distX, float distY)
        {
            var colorProp = outline.GetType().GetProperty("effectColor");
            if (colorProp != null && ReflectionCache.Ctor_Color4f != null)
                colorProp.SetValue(outline, ReflectionCache.Ctor_Color4f.Invoke(
                    new object[] { r, g, b, a }));

            var distProp = outline.GetType().GetProperty("effectDistance");
            if (distProp != null && ReflectionCache.Ctor_Vector2 != null)
                distProp.SetValue(outline, ReflectionCache.Ctor_Vector2.Invoke(
                    new object[] { distX, distY }));
        }

        /// <summary>
        /// Swap the reticle's sprite to our desaturated white version.
        /// Only loads the PNG once; applies to each Image component's sprite.
        /// </summary>
        private static void SwapToWhiteSprite(object image)
        {
            // Load the white sprite once
            if (!_whiteSpriteLoaded)
            {
                _whiteSpriteLoaded = true;
                string path = System.IO.Path.Combine(_modsDir, "icons", "reticle_white.png");
                if (System.IO.File.Exists(path))
                {
                    _whiteSprite = AffinityBoard.LoadSpritePublic(path);
                    if (_diagnosticMode)
                        _log?.Msg($"PE2: White reticle sprite loaded: {(_whiteSprite != null ? "OK" : "FAIL")}");
                }
                else
                {
                    _log?.Warning($"PE2: reticle_white.png not found at {path}");
                }
            }

            // Swap the sprite on this Image and force Simple render mode
            if (_whiteSprite != null && ReflectionCache.P_Image_Sprite != null)
            {
                try
                {
                    ReflectionCache.P_Image_Sprite.SetValue(image, _whiteSprite);

                    // Force Image.type = Simple (0) — the original may be Sliced/Filled
                    // which doesn't work correctly with our non-atlas sprite
                    var typeProp = image.GetType().GetProperty("type");
                    if (typeProp != null)
                        typeProp.SetValue(image, Enum.ToObject(typeProp.PropertyType, 0));
                }
                catch { }
            }
        }

        /// <summary>Show/hide the question mark overlay on the reticle center.</summary>
        private static void UpdateQuestionMark(int curidx, AffinityResult affinity, object reticleGO)
        {
            bool wantQuestion = (affinity == AffinityResult.Unknown);

            if (_questionGOs[curidx] == null && ReflectionCache.T_TextComponent != null)
            {
                // Parent GO for both layers
                var go = ReflectionCache.MakeChild(reticleGO, $"PE2Q{curidx}");
                if (go != null)
                {
                    _questionGOs[curidx] = go;
                    ReflectionCache.SetRect(go, CenterOverlaySize, CenterOverlaySize, 0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f);

                    // BLACK shadow "?" behind (same size, offset down-right)
                    var shadowGO = ReflectionCache.MakeChild(go, $"PE2QSh{curidx}");
                    if (shadowGO != null)
                    {
                        var shText = ReflectionCache.AddComponent(shadowGO, ReflectionCache.T_TextComponent);
                        if (shText != null) ConfigureQuestionText(shText, 68f, 0f, 0f, 0f, 1f);
                        ReflectionCache.SetRect(shadowGO, CenterOverlaySize, CenterOverlaySize, 0.5f, 0.5f, 0.5f, 0.5f, 3f, -3f);
                    }

                    // WHITE foreground "?" on top
                    var fgGO = ReflectionCache.MakeChild(go, $"PE2QFg{curidx}");
                    if (fgGO != null)
                    {
                        var fgText = ReflectionCache.AddComponent(fgGO, ReflectionCache.T_TextComponent);
                        if (fgText != null)
                        {
                            ConfigureQuestionText(fgText, 68f, 1f, 1f, 1f, 1f);

                            // TMP outline via material shader — thick dark border, no jaggies
                            if (ReflectionCache.UseTMP)
                            {
                                try
                                {
                                    var matProp = fgText.GetType().GetProperty("fontMaterial");
                                    var mat = matProp?.GetValue(fgText);
                                    if (mat != null)
                                    {
                                        var setFloat = mat.GetType().GetMethod("SetFloat",
                                            new[] { typeof(string), typeof(float) });
                                        // Thick outline
                                        setFloat?.Invoke(mat, new object[] { "_OutlineWidth", 0.35f });

                                        var setColor = mat.GetType().GetMethod("SetColor",
                                            new[] { typeof(string), ReflectionCache.T_Color! });
                                        if (setColor != null && ReflectionCache.Ctor_Color4f != null)
                                        {
                                            var black = ReflectionCache.Ctor_Color4f.Invoke(
                                                new object[] { 0f, 0f, 0f, 1f });
                                            setColor.Invoke(mat, new object[] { "_OutlineColor", black });
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                        ReflectionCache.SetRect(fgGO, CenterOverlaySize, CenterOverlaySize, 0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f);

                        // Drop shadow via Unity Shadow component (offset down-right)
                        try
                        {
                            // Shadow is the base class of Outline — search for it
                            Type? shadowType = null;
                            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                            {
                                try
                                {
                                    foreach (var t in asm.GetTypes())
                                        if (t.Name == "Shadow" && t.Namespace != null && t.Namespace.Contains("UI")
                                            && !t.Namespace.Contains("Melon") && shadowType == null)
                                            shadowType = t;
                                }
                                catch { }
                            }

                            if (shadowType != null)
                            {
                                var shadow = ReflectionCache.AddComponent(fgGO, shadowType);
                                if (shadow != null)
                                {
                                    var colorProp = shadow.GetType().GetProperty("effectColor");
                                    if (colorProp != null && ReflectionCache.Ctor_Color4f != null)
                                        colorProp.SetValue(shadow, ReflectionCache.Ctor_Color4f.Invoke(
                                            new object[] { 0f, 0f, 0f, 0.8f }));

                                    var distProp = shadow.GetType().GetProperty("effectDistance");
                                    if (distProp != null && ReflectionCache.Ctor_Vector2 != null)
                                        distProp.SetValue(shadow, ReflectionCache.Ctor_Vector2.Invoke(
                                            new object[] { 3f, -3f }));
                                }
                            }
                        }
                        catch { }
                    }

                    if (_diagnosticMode)
                        _log?.Msg($"PE2: Question mark (dual layer) created for curidx={curidx}");
                }
            }

            if (_questionGOs[curidx] == null) return;
            ReflectionCache.SetActive(_questionGOs[curidx], wantQuestion);
        }

        /// <summary>Show/hide centered affinity result icons on the reticle.</summary>
        private static void UpdateResultIcon(int curidx, AffinityResult affinity, object reticleGO)
        {
            object? sprite = GetResultIconSprite(affinity);
            bool wantIcon = sprite != null;

            if (_resultIconGOs[curidx] == null && ReflectionCache.T_Image != null)
            {
                var go = ReflectionCache.MakeChild(reticleGO, $"PE2R{curidx}");
                if (go != null)
                {
                    _resultIconGOs[curidx] = go;
                    ReflectionCache.SetRect(go, CenterOverlaySize, CenterOverlaySize, 0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f);

                    var iconGO = ReflectionCache.MakeChild(go, $"PE2RFg{curidx}");
                    if (iconGO != null)
                    {
                        var image = ReflectionCache.AddComponent(iconGO, ReflectionCache.T_Image);
                        if (image != null)
                        {
                            _resultIconImages[curidx] = image;
                            ConfigureResultIconImage(image);
                        }

                        ReflectionCache.SetRect(iconGO, ResultIconSize, ResultIconSize, 0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f);
                    }

                    if (_diagnosticMode)
                        _log?.Msg($"PE2: Result icon overlay created for curidx={curidx}");
                }
            }

            if (_resultIconGOs[curidx] == null) return;

            if (wantIcon && _resultIconImages[curidx] != null)
            {
                try { ReflectionCache.P_Image_Sprite?.SetValue(_resultIconImages[curidx], sprite); } catch { }
                ReflectionCache.SetActive(_resultIconGOs[curidx], true);
            }
            else
            {
                ReflectionCache.SetActive(_resultIconGOs[curidx], false);
            }
        }

        private static void EnsureResultIconSpritesLoaded()
        {
            if (_resultIconSpritesLoaded) return;
            _resultIconSpritesLoaded = true;

            string packagedDir = System.IO.Path.Combine(_modsDir, "icons", "reticleresults");
            string legacyDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_modsDir) ?? ".", "ui models", "reticleresults");
            string[] fileNames = { "resisticon.png", "blockicon.png", "reflecticon.png", "absorbicon.png" };

            for (int i = 0; i < fileNames.Length; i++)
            {
                string path = System.IO.Path.Combine(packagedDir, fileNames[i]);
                if (!System.IO.File.Exists(path))
                    path = System.IO.Path.Combine(legacyDir, fileNames[i]);

                if (System.IO.File.Exists(path))
                {
                    _resultIconSprites[i] = AffinityBoard.LoadSpritePublic(path);
                    if (_diagnosticMode)
                        _log?.Msg($"PE2: Reticle result icon {fileNames[i]} loaded: {(_resultIconSprites[i] != null ? "OK" : "FAIL")}");
                }
                else
                {
                    _log?.Warning($"PE2: Reticle result icon missing: expected {System.IO.Path.Combine(packagedDir, fileNames[i])}");
                }
            }
        }

        private static object? GetResultIconSprite(AffinityResult affinity)
        {
            EnsureResultIconSpritesLoaded();

            return affinity switch
            {
                AffinityResult.Resist => _resultIconSprites[0],
                AffinityResult.Null => _resultIconSprites[1],
                AffinityResult.Reflect => _resultIconSprites[2],
                AffinityResult.Drain => _resultIconSprites[3],
                _ => null
            };
        }

        private static void ConfigureResultIconImage(object image)
        {
            try
            {
                var colorProp = image.GetType().GetProperty("color");
                if (colorProp != null && ReflectionCache.Ctor_Color4f != null)
                    colorProp.SetValue(image, ReflectionCache.Ctor_Color4f.Invoke(new object[] { 1f, 1f, 1f, 1f }));

                var typeProp = image.GetType().GetProperty("type");
                if (typeProp != null)
                    typeProp.SetValue(image, Enum.ToObject(typeProp.PropertyType, 0));

                var preserveAspectProp = image.GetType().GetProperty("preserveAspect");
                preserveAspectProp?.SetValue(image, true);
            }
            catch { }
        }

        /// <summary>Configure a TMP/Text component as a centered "?" with given size and color.</summary>
        private static void ConfigureQuestionText(object text, float fontSize, float r, float g, float b, float a)
        {
            ReflectionCache.SetText(text, "?");
            ReflectionCache.P_Text_FontSize?.SetValue(text,
                ReflectionCache.UseTMP ? (object)fontSize : (object)(int)fontSize);

            try
            {
                var cp = text.GetType().GetProperty("color");
                if (cp != null && ReflectionCache.Ctor_Color4f != null)
                    cp.SetValue(text, ReflectionCache.Ctor_Color4f.Invoke(new object[] { r, g, b, a }));
            }
            catch { }

            if (ReflectionCache.UseTMP)
            {
                var alP = ReflectionCache.P_Text_Alignment;
                if (alP != null) alP.SetValue(text, Enum.ToObject(alP.PropertyType, 514));
                ReflectionCache.P_Text_WordWrap?.SetValue(text, false);
                var font = ReflectionCache.P_TMP_DefaultFontAsset?.GetValue(null);
                if (font != null) ReflectionCache.P_Text_Font?.SetValue(text, font);
            }
            else
            {
                var alP = ReflectionCache.P_Text_Alignment;
                if (alP != null) alP.SetValue(text, Enum.ToObject(alP.PropertyType, 4));
            }
        }

        /// <summary>Log sprite and texture metadata for all reticle Image components.</summary>
        private static void LogSpriteInfo(object?[] images)
        {
            try
            {
                var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

                for (int i = 0; i < images.Length; i++)
                {
                    if (images[i] == null) continue;
                    var sprite = ReflectionCache.P_Image_Sprite?.GetValue(images[i]);
                    if (sprite == null) { _log?.Msg($"PE2 SPRITE[{i}]: no sprite"); continue; }

                    var spriteType = sprite.GetType();
                    string spriteName = spriteType.GetProperty("name", bf)?.GetValue(sprite)?.ToString() ?? "?";

                    // Sprite.rect — position within the atlas
                    var rectProp = spriteType.GetProperty("rect", bf);
                    string rectStr = "?";
                    if (rectProp != null)
                    {
                        var rect = rectProp.GetValue(sprite);
                        if (rect != null)
                        {
                            var rt = rect.GetType();
                            float rx = (float)(rt.GetProperty("x", bf)?.GetValue(rect) ?? 0);
                            float ry = (float)(rt.GetProperty("y", bf)?.GetValue(rect) ?? 0);
                            float rw = (float)(rt.GetProperty("width", bf)?.GetValue(rect) ?? 0);
                            float rh = (float)(rt.GetProperty("height", bf)?.GetValue(rect) ?? 0);
                            rectStr = $"({rx},{ry},{rw},{rh})";
                        }
                    }

                    // Sprite.texture — the source Texture2D
                    var texProp = spriteType.GetProperty("texture", bf);
                    string texName = "?";
                    string texSize = "?";
                    if (texProp != null)
                    {
                        var tex = texProp.GetValue(sprite);
                        if (tex != null)
                        {
                            var texType = tex.GetType();
                            texName = texType.GetProperty("name", bf)?.GetValue(tex)?.ToString() ?? "?";
                            int tw = (int)(texType.GetProperty("width", bf)?.GetValue(tex) ?? 0);
                            int th = (int)(texType.GetProperty("height", bf)?.GetValue(tex) ?? 0);
                            texSize = $"{tw}x{th}";
                        }
                    }

                    _log?.Msg($"PE2 SPRITE[{i}]: name=\"{spriteName}\" rect={rectStr} texture=\"{texName}\" size={texSize}");
                }
            }
            catch (Exception ex)
            {
                _log?.Warning($"PE2 LogSpriteInfo: {ex.Message}");
            }
        }

        private static void SetEnabled(object? component, bool enabled)
        {
            if (component == null) return;
            var prop = component.GetType().GetProperty("enabled");
            prop?.SetValue(component, enabled);
        }

        /// <summary>
        /// Show or hide the animated overlay layers (images[1+]).
        /// Hidden for Normal/Unknown (ghost effect). Visible for affinity colors (adds vibrancy).
        /// </summary>
        private static void SetAnimatedLayersVisible(object?[] images, bool visible)
        {
            try
            {
                var goProperty = images[0]?.GetType().GetProperty("gameObject");
                if (goProperty == null) return;

                for (int i = 1; i < images.Length; i++)
                {
                    if (images[i] == null) continue;
                    var childGO = goProperty.GetValue(images[i]);
                    if (childGO != null)
                        ReflectionCache.SetActive(childGO, visible);
                }
            }
            catch { }
        }
    }
}
