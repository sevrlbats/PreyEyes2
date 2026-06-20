using System;
using System.Reflection;
using Il2Cpp;
using MelonLoader;

namespace PreyEyes2
{
    internal static partial class BuffDisplay
    {
        private const int PartySlots = 4;
        private const int StatCount = 4;
        private const int MaxBuffStage = 4;

        private static readonly int[] HojoIndices = { 4, 7, 6, 5 };
        private static readonly string[] StatPrefixes = { "att", "def", "acc", "mag" };

        private static MelonLogger.Instance? _log;
        private static string _assetDir = string.Empty;

        private static bool _initialized;
        private static object? _canvasGO;
        private static object? _partyRootGO;
        private static object? _enemyRootGO;
        private static readonly object?[] _partyRowGOs = new object?[PartySlots];
        private static readonly object?[,] _partyStatIcons = new object?[PartySlots, StatCount];
        private static readonly object?[,] _partyStatImages = new object?[PartySlots, StatCount];
        private static readonly object?[] _enemyStatIcons = new object?[StatCount];
        private static readonly object?[] _enemyStatImages = new object?[StatCount];
        private static readonly object?[,,] _buffSprites = new object?[StatCount, 2, MaxBuffStage];
        private static readonly string?[,,] _buffSpriteLabels = new string?[StatCount, 2, MaxBuffStage];
        private static readonly int[,] _lastPartyStages = new int[PartySlots, StatCount];
        private static readonly int[,] _renderedPartyStages = new int[PartySlots, StatCount];
        private static readonly int[] _lastEnemyStages = new int[StatCount];
        private static readonly int[] _renderedEnemyStages = new int[StatCount];
        private static readonly int[] _lastPartyVisibility = new int[PartySlots];
        private static readonly int[] _lastPartyDemonIds = new int[PartySlots];
        private static readonly BindingFlags InstanceMemberFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static int _lastEnemyVisible = -1;
        private static int _renderedEnemyFormIndex = -2;
        private static int _lastEnemyDemonId = int.MinValue;
        private static int _missingSpriteLogCount;
        private static int _spriteApplyFailureLogCount;
        private static int _highlightedEnemy = -1;
        private static bool _lastReadBlockHidden;
        private static bool _lastSuppressed;

        static BuffDisplay()
        {
            for (int slot = 0; slot < PartySlots; slot++)
            {
                _lastPartyVisibility[slot] = -1;
                _lastPartyDemonIds[slot] = int.MinValue;
                for (int stat = 0; stat < StatCount; stat++)
                {
                    _lastPartyStages[slot, stat] = int.MinValue;
                    _renderedPartyStages[slot, stat] = int.MinValue;
                }
            }

            for (int stat = 0; stat < StatCount; stat++)
            {
                _lastEnemyStages[stat] = int.MinValue;
                _renderedEnemyStages[stat] = int.MinValue;
            }
        }

        internal static void Init(MelonLogger.Instance log, string modsDir)
        {
            _log = log;
            _assetDir = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(modsDir) ?? ".",
                "ui models",
                "kajakunda");
            PreyEyes2Trace.Log("buff", $"asset dir={_assetDir}");
        }

        internal static void OnUpdate(bool inBattle, bool allowStageReads, bool suppressVisuals)
        {
            if (!inBattle)
            {
                _highlightedEnemy = -1;
                HideAll();
                _lastReadBlockHidden = false;
                _lastSuppressed = false;
                PreyEyes2Trace.LogLimited("buff:not-in-battle", 4, "buff", "OnUpdate hid all because battle state is false");
                return;
            }

            if (!EnsureInitialized())
                return;

            if (suppressVisuals)
            {
                if (!_lastSuppressed)
                {
                    PreyEyes2Trace.Log("buff", $"visuals suppressed during resolution highlighted={_highlightedEnemy} state={DescribeTraceState()}");
                    SuspendVisuals();
                }
                _lastSuppressed = true;
                _lastReadBlockHidden = false;
                return;
            }

            if (_lastSuppressed)
                PreyEyes2Trace.Log("buff", $"visuals restored after resolution highlighted={_highlightedEnemy} state={DescribeTraceState()}");
            _lastSuppressed = false;

            if (!allowStageReads)
            {
                if (!_lastReadBlockHidden)
                {
                    PreyEyes2Trace.Log("buff", $"visuals hidden while stage reads are blocked highlighted={_highlightedEnemy} state={DescribeTraceState()}");
                    SuspendVisuals();
                }
                _lastReadBlockHidden = true;
                return;
            }

            if (_lastReadBlockHidden)
                PreyEyes2Trace.Log("buff", $"visuals restored when stage reads resumed highlighted={_highlightedEnemy} state={DescribeTraceState()}");
            _lastReadBlockHidden = false;

            SetPanelVisible(_partyRootGO, true);
            UpdateParty();

            if (_highlightedEnemy >= PartySlots)
                UpdateEnemy(_highlightedEnemy);
            else
                SetPanelVisible(_enemyRootGO, false);
        }

        internal static void OnTargetDrawn(int curidx, int targetCount)
        {
            if (curidx < PartySlots)
                return;

            if (targetCount != 1)
            {
                PreyEyes2Trace.Log("buff", $"OnTargetDrawn curidx={curidx} targetCount={targetCount} -> clearing enemy buffs");
                OnNoTargets();
                return;
            }

            if (_highlightedEnemy != curidx)
            {
                PreyEyes2Trace.Log("buff", $"OnTargetDrawn curidx={curidx} targetCount={targetCount} -> highlight enemy");
                InvalidateEnemyRenderCache();
            }
            _highlightedEnemy = curidx;
        }

        internal static void OnNoTargets()
        {
            if (_highlightedEnemy >= PartySlots)
                PreyEyes2Trace.Log("buff", $"OnNoTargets clearing highlightedEnemy={_highlightedEnemy}");
            _highlightedEnemy = -1;
            InvalidateEnemyRenderCache();
            SetPanelVisible(_enemyRootGO, false);
        }

        private static void UpdateParty()
        {
            for (int slot = 0; slot < PartySlots; slot++)
            {
                try
                {
                    int demonId = ReflectionCache.GetDemonId(slot);
                    bool visible = slot == 0 || demonId > 0 || HasAnyBuff(slot);
                    if (_lastPartyVisibility[slot] != (visible ? 1 : 0))
                    {
                        _lastPartyVisibility[slot] = visible ? 1 : 0;
                        PreyEyes2Trace.Log("buff", $"party slot={slot} visible={visible} demonId={demonId}");
                        InvalidatePartyRenderCache(slot);
                    }

                    if (_lastPartyDemonIds[slot] != demonId)
                    {
                        PreyEyes2Trace.Log("buff", $"party slot={slot} demonId changed {_lastPartyDemonIds[slot]}->{demonId}");
                        _lastPartyDemonIds[slot] = demonId;
                        InvalidatePartyRenderCache(slot);
                    }

                    SetPanelVisible(_partyRowGOs[slot], visible);
                    if (!visible)
                        continue;

                    for (int stat = 0; stat < StatCount; stat++)
                    {
                        int stage = ReadBuffStage(slot, stat);
                        if (_lastPartyStages[slot, stat] != stage)
                        {
                            _lastPartyStages[slot, stat] = stage;
                            PreyEyes2Trace.Log(
                                "buff",
                                $"party slot={slot} stat={StatPrefixes[stat]} stage={stage} sprite={DescribeSprite(stat, stage)}");
                        }

                        if (_renderedPartyStages[slot, stat] != stage)
                        {
                            bool rendered = UpdateIcon(_partyStatIcons[slot, stat], _partyStatImages[slot, stat], stat, stage, $"party[{slot}].{StatPrefixes[stat]}");
                            _renderedPartyStages[slot, stat] = rendered ? stage : int.MinValue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    PreyEyes2Trace.LogException("buff", $"UpdateParty slot={slot}", ex);
                }
            }
        }

        private static bool HasAnyBuff(int formIndex)
        {
            for (int stat = 0; stat < StatCount; stat++)
            {
                if (ReadBuffStage(formIndex, stat) != 0)
                    return true;
            }

            return false;
        }

        private static void UpdateEnemy(int formIndex)
        {
            try
            {
                int demonId = ReflectionCache.GetDemonId(formIndex);
                if (demonId <= 0)
                {
                    SetPanelVisible(_enemyRootGO, false);
                    if (_lastEnemyVisible != 0)
                    {
                        _lastEnemyVisible = 0;
                        PreyEyes2Trace.Log("buff", $"enemy formIndex={formIndex} hidden because demonId<=0");
                    }
                    return;
                }

                if (_renderedEnemyFormIndex != formIndex || _lastEnemyDemonId != demonId)
                {
                    PreyEyes2Trace.Log("buff", $"enemy render context changed formIndex={_renderedEnemyFormIndex}->{formIndex} demonId={_lastEnemyDemonId}->{demonId}");
                    _renderedEnemyFormIndex = formIndex;
                    _lastEnemyDemonId = demonId;
                    InvalidateEnemyRenderCache();
                    _renderedEnemyFormIndex = formIndex;
                    _lastEnemyDemonId = demonId;
                }

                bool anyVisible = false;
                for (int stat = 0; stat < StatCount; stat++)
                {
                    int stage = ReadBuffStage(formIndex, stat);
                    if (_lastEnemyStages[stat] != stage)
                    {
                        _lastEnemyStages[stat] = stage;
                        PreyEyes2Trace.Log(
                            "buff",
                            $"enemy formIndex={formIndex} stat={StatPrefixes[stat]} stage={stage} sprite={DescribeSprite(stat, stage)}");
                    }

                    if (_renderedEnemyStages[stat] != stage)
                    {
                        bool rendered = UpdateIcon(_enemyStatIcons[stat], _enemyStatImages[stat], stat, stage, $"enemy[{formIndex}].{StatPrefixes[stat]}");
                        _renderedEnemyStages[stat] = rendered ? stage : int.MinValue;
                    }

                    if (stage != 0 && GetSprite(stat, stage) != null)
                        anyVisible = true;
                }

                if (_lastEnemyVisible != (anyVisible ? 1 : 0))
                {
                    _lastEnemyVisible = anyVisible ? 1 : 0;
                    PreyEyes2Trace.Log("buff", $"enemy panel visible={anyVisible} formIndex={formIndex} demonId={demonId}");
                }
                SetPanelVisible(_enemyRootGO, anyVisible);
            }
            catch (Exception ex)
            {
                PreyEyes2Trace.LogException("buff", $"UpdateEnemy formIndex={formIndex}", ex);
                SetPanelVisible(_enemyRootGO, false);
            }
        }

        private static int ReadBuffStage(int formIndex, int statIndex)
        {
            try
            {
                int stage = nbCalc.nbGetHojoCounter(formIndex, HojoIndices[statIndex]);
                if (stage > MaxBuffStage) return MaxBuffStage;
                if (stage < -MaxBuffStage) return -MaxBuffStage;
                return stage;
            }
            catch
            {
                PreyEyes2Trace.LogLimited($"buff-stage-read:{formIndex}:{statIndex}", 4, "buff", $"ReadBuffStage failed formIndex={formIndex} stat={StatPrefixes[statIndex]}");
                return 0;
            }
        }

        private static bool UpdateIcon(object? iconGO, object? image, int statIndex, int stage, string slotKey)
        {
            if (iconGO == null)
                return false;

            object? sprite = GetSprite(statIndex, stage);
            if (sprite == null)
            {
                ClearIconSprite(image, slotKey);
                if (image != null) ReflectionCache.WriteColor(image, 1f, 1f, 1f, 0f);
                if (stage != 0 && _missingSpriteLogCount < 8)
                {
                    _missingSpriteLogCount++;
                    _log?.Warning($"BuffDisplay: missing sprite for stat={StatPrefixes[statIndex]} stage={stage}");
                    PreyEyes2Trace.LogLimited(
                        $"icon-missing:{slotKey}:{stage}",
                        2,
                        "buff",
                        $"{slotKey} missing sprite target={DescribeSprite(statIndex, stage)}");
                }
                return stage == 0;
            }

            if (image == null)
            {
                ReflectionCache.SetActive(iconGO, false);
                return false;
            }

            try
            {
                var typeProp = image.GetType().GetProperty("type");
                if (typeProp != null)
                    typeProp.SetValue(image, Enum.ToObject(typeProp.PropertyType, 0));
                bool assigned = AssignSpriteReferences(image, sprite, slotKey, statIndex, stage);
                if (typeProp != null)
                    typeProp.SetValue(image, Enum.ToObject(typeProp.PropertyType, 0));

                if (!assigned)
                {
                    ClearIconSprite(image, slotKey);
                    ReflectionCache.WriteColor(image, 1f, 1f, 1f, 0f);
                    PreyEyes2Trace.LogLimited(
                        $"icon-bind-failed:{slotKey}:{stage}",
                        2,
                        "buff",
                        $"{slotKey} bind failed stage={stage} target={DescribeSprite(statIndex, stage)} source={DescribeObject(sprite)}");
                    return false;
                }

                bool colorOk = ReflectionCache.WriteColor(image, 1f, 1f, 1f, 1f);
                PreyEyes2Trace.LogLimited(
                    $"icon-applied:{slotKey}:{stage}",
                    2,
                    "buff",
                    $"{slotKey} applied stage={stage} target={DescribeSprite(statIndex, stage)} actualType={typeProp?.GetValue(image)?.ToString() ?? "null"} colorOk={colorOk} source={DescribeObject(sprite)}");
                return true;
            }
            catch (Exception ex)
            {
                PreyEyes2Trace.LogException("buff", $"UpdateIcon {slotKey} stage={stage}", ex);
                if (image != null) ReflectionCache.WriteColor(image, 1f, 1f, 1f, 0f);
                return false;
            }
        }

        private static object? GetSprite(int statIndex, int stage)
        {
            if (stage == 0)
                return null;

            int direction = stage > 0 ? 1 : 0;
            int rankIndex = Math.Min(Math.Abs(stage), MaxBuffStage) - 1;
            return _buffSprites[statIndex, direction, rankIndex];
        }

        private static void HideAll()
        {
            SuspendVisuals();
        }

        internal static string DescribeTraceState()
        {
            int visibleRows = 0;
            int visiblePartyIcons = 0;
            for (int slot = 0; slot < PartySlots; slot++)
            {
                if (_lastPartyVisibility[slot] == 1)
                    visibleRows++;

                for (int stat = 0; stat < StatCount; stat++)
                {
                    if (_renderedPartyStages[slot, stat] != 0 && _renderedPartyStages[slot, stat] != int.MinValue)
                        visiblePartyIcons++;
                }
            }

            int visibleEnemyIcons = 0;
            for (int stat = 0; stat < StatCount; stat++)
            {
                if (_renderedEnemyStages[stat] != 0 && _renderedEnemyStages[stat] != int.MinValue)
                    visibleEnemyIcons++;
            }

            return $"rows={visibleRows} partyIcons={visiblePartyIcons} enemyIcons={visibleEnemyIcons} highlighted={_highlightedEnemy} enemyVisible={_lastEnemyVisible}";
        }

        internal static string DescribeIconHealth()
        {
            int activeIcons = 0;
            int suspiciousIcons = 0;
            string? firstSuspicious = null;

            for (int slot = 0; slot < PartySlots; slot++)
            {
                for (int stat = 0; stat < StatCount; stat++)
                    InspectIconHealth(_partyStatIcons[slot, stat], _partyStatImages[slot, stat], $"party[{slot}].{StatPrefixes[stat]}", ref activeIcons, ref suspiciousIcons, ref firstSuspicious);
            }

            for (int stat = 0; stat < StatCount; stat++)
                InspectIconHealth(_enemyStatIcons[stat], _enemyStatImages[stat], $"enemy.{StatPrefixes[stat]}", ref activeIcons, ref suspiciousIcons, ref firstSuspicious);

            return firstSuspicious == null
                ? $"active={activeIcons} suspicious={suspiciousIcons}"
                : $"active={activeIcons} suspicious={suspiciousIcons} first={firstSuspicious}";
        }

        private static string DescribeSprite(int statIndex, int stage)
        {
            if (stage == 0)
                return "none";

            int direction = stage > 0 ? 1 : 0;
            int rankIndex = Math.Min(Math.Abs(stage), MaxBuffStage) - 1;
            string? label = _buffSpriteLabels[statIndex, direction, rankIndex];
            return label ?? $"{StatPrefixes[statIndex]}:{(direction == 1 ? "up" : "down")}:{rankIndex + 1}:unlabeled";
        }

        private static void InvalidatePartyRenderCache(int slot)
        {
            for (int stat = 0; stat < StatCount; stat++)
                _renderedPartyStages[slot, stat] = int.MinValue;
        }

        private static void InvalidateEnemyRenderCache()
        {
            _renderedEnemyFormIndex = -2;
            _lastEnemyDemonId = int.MinValue;
            for (int stat = 0; stat < StatCount; stat++)
                _renderedEnemyStages[stat] = int.MinValue;
        }

        private static void SuspendVisuals()
        {
            SetPanelVisible(_partyRootGO, false);
            SetPanelVisible(_enemyRootGO, false);

            for (int slot = 0; slot < PartySlots; slot++)
                InvalidatePartyRenderCache(slot);

            InvalidateEnemyRenderCache();
        }

        private static void ClearIconSprite(object? image, string slotKey)
        {
            if (image == null)
                return;

            try
            {
                Type imageType = image.GetType();
                PropertyInfo? spriteProp = imageType.GetProperty("sprite", InstanceMemberFlags);
                PropertyInfo? overrideSpriteProp = imageType.GetProperty("overrideSprite", InstanceMemberFlags);
                PropertyInfo? textureProp = GetTextureProperty(imageType);
                textureProp?.SetValue(image, null);
                overrideSpriteProp?.SetValue(image, null);
                spriteProp?.SetValue(image, null);
                imageType.GetProperty("enabled")?.SetValue(image, false);
                RefreshImage(image);
                PreyEyes2Trace.LogLimited($"icon-cleared:{slotKey}", 2, "buff", $"{slotKey} cleared and hidden");
            }
            catch (Exception ex)
            {
                PreyEyes2Trace.LogException("buff", $"clear sprite {slotKey}", ex, 4);
            }
        }

        private static bool AssignSpriteReferences(object image, object sprite, string slotKey, int statIndex, int stage)
        {
            Type imageType = image.GetType();
            PropertyInfo? spriteProp = ReflectionCache.P_Image_Sprite ?? imageType.GetProperty("sprite", InstanceMemberFlags);
            PropertyInfo? overrideSpriteProp = imageType.GetProperty("overrideSprite", InstanceMemberFlags);
            PropertyInfo? textureProp = GetTextureProperty(imageType);
            bool isRawImage = IsRawImageComponent(imageType);
            object? spriteBefore = TryGetMemberValue(image, spriteProp);
            object? textureBefore = TryGetMemberValue(image, textureProp);
            imageType.GetProperty("preserveAspect")?.SetValue(image, true);
            imageType.GetProperty("enabled")?.SetValue(image, true);
            imageType.GetProperty("raycastTarget")?.SetValue(image, false);

            object? textureTarget = null;
            if (isRawImage)
            {
                textureTarget = TryGetSpriteTexture(sprite);
                textureProp?.SetValue(image, textureTarget);
            }
            else
            {
                spriteProp?.SetValue(image, sprite);
            }

            RefreshImage(image);
            object? spriteAfter = TryGetMemberValue(image, spriteProp);
            object? overrideAfter = TryGetMemberValue(image, overrideSpriteProp);
            object? textureAfter = TryGetMemberValue(image, textureProp);
            object? mainTexture = TryGetMemberValue(image, imageType.GetProperty("mainTexture", InstanceMemberFlags));
            bool whiteFallbackSuspect = !isRawImage && spriteAfter == null && overrideAfter == null && IsUnityWhiteTexture(mainTexture);
            bool assigned = isRawImage
                ? textureAfter != null && !IsUnityWhiteTexture(textureAfter)
                : spriteAfter != null || overrideAfter != null;

            if (!assigned && _spriteApplyFailureLogCount < 8)
            {
                _spriteApplyFailureLogCount++;
                _log?.Warning($"BuffDisplay: sprite assignment still empty for stat={StatPrefixes[statIndex]} stage={stage}");
            }

            if (whiteFallbackSuspect)
            {
                PreyEyes2Trace.LogLimited(
                    $"icon-white-fallback:{slotKey}:{stage}",
                    6,
                    "buff",
                    $"{slotKey} white-fallback suspect stage={stage} target={DescribeSprite(statIndex, stage)} source={DescribeObject(sprite)} tex={DescribeObject(mainTexture)}");
            }

            PreyEyes2Trace.LogLimited(
                $"icon-state:{slotKey}:{stage}",
                2,
                "buff",
                $"{slotKey} state stage={stage} target={DescribeSprite(statIndex, stage)} source={DescribeObject(sprite)} component={imageType.Name} spriteBefore={DescribeObject(spriteBefore)} spriteAfter={DescribeObject(spriteAfter)} override={DescribeObject(overrideAfter)} textureBefore={DescribeObject(textureBefore)} textureTarget={DescribeObject(textureTarget)} textureAfter={DescribeObject(textureAfter)} mainTex={DescribeObject(mainTexture)}");
            return assigned;
        }

        private static void InspectIconHealth(object? iconGO, object? image, string slotKey, ref int activeIcons, ref int suspiciousIcons, ref string? firstSuspicious)
        {
            if (!IsActive(iconGO) || image == null)
                return;

            activeIcons++;
            Type imageType = image.GetType();
            PropertyInfo? textureProp = GetTextureProperty(imageType);
            if (IsRawImageComponent(imageType))
            {
                object? texture = TryGetMemberValue(image, textureProp);
                if (texture == null || IsUnityWhiteTexture(texture))
                {
                    suspiciousIcons++;
                    firstSuspicious ??= $"{slotKey}:{DescribeObject(texture)}";
                }
                return;
            }

            object? sprite = TryGetMemberValue(image, imageType.GetProperty("sprite", InstanceMemberFlags));
            object? overrideSprite = TryGetMemberValue(image, imageType.GetProperty("overrideSprite", InstanceMemberFlags));
            object? mainTexture = TryGetMemberValue(image, imageType.GetProperty("mainTexture", InstanceMemberFlags));
            if (sprite == null && overrideSprite == null && mainTexture != null)
            {
                suspiciousIcons++;
                firstSuspicious ??= $"{slotKey}:{DescribeObject(mainTexture)}";
            }
        }

        private static bool IsActive(object? go)
        {
            if (go == null)
                return false;

            try
            {
                object? activeSelf = go.GetType().GetProperty("activeSelf", InstanceMemberFlags)?.GetValue(go);
                return activeSelf is bool value && value;
            }
            catch
            {
                return false;
            }
        }

        private static void RefreshImage(object image)
        {
            TryInvokeNoArg(image, "SetAllDirty");
            TryInvokeNoArg(image, "SetMaterialDirty");
            TryInvokeNoArg(image, "SetVerticesDirty");
            TryInvokeNoArg(image, "SetLayoutDirty");
        }

        private static void TryInvokeNoArg(object obj, string methodName)
        {
            try
            {
                obj.GetType().GetMethod(methodName, InstanceMemberFlags, null, Type.EmptyTypes, null)?.Invoke(obj, null);
            }
            catch { }
        }

        private static object? TryGetMemberValue(object target, PropertyInfo? prop)
        {
            try
            {
                return prop?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        private static object? TryGetMemberValue(object target, FieldInfo? field)
        {
            try
            {
                return field?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        private static PropertyInfo? GetTextureProperty(Type imageType)
        {
            PropertyInfo? prop = imageType.GetProperty("texture", InstanceMemberFlags);
            if (prop != null)
                return prop;

            Type? rawImageType = ReflectionCache.T_RawImage;
            if (rawImageType != null && rawImageType.IsAssignableFrom(imageType))
                return ReflectionCache.P_RawImage_Texture;

            return null;
        }

        private static object? TryGetSpriteTexture(object sprite)
        {
            try
            {
                return sprite.GetType().GetProperty("texture", InstanceMemberFlags)?.GetValue(sprite);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsRawImageComponent(Type imageType)
        {
            Type? rawImageType = ReflectionCache.T_RawImage;
            return rawImageType != null && rawImageType.IsAssignableFrom(imageType);
        }

        private static bool IsUnityWhiteTexture(object? texture)
        {
            if (texture == null)
                return false;

            string description = DescribeObject(texture);
            return description.IndexOf("UnityWhite", StringComparison.OrdinalIgnoreCase) >= 0
                || description.IndexOf("White", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string DescribeObject(object? candidate)
        {
            if (candidate == null)
                return "null";

            try
            {
                string? name = candidate.GetType().GetProperty("name", InstanceMemberFlags)?.GetValue(candidate)?.ToString();
                return string.IsNullOrEmpty(name) ? candidate.GetType().Name : name;
            }
            catch
            {
                return candidate.GetType().Name;
            }
        }

    }
}
