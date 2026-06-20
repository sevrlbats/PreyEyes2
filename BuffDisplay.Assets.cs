using System;
using System.IO;

namespace PreyEyes2
{
    internal static partial class BuffDisplay
    {
        private const float PartyRowWidth = 122f;
        private const float PartyRowHeight = 26f;
        private const float PartyRowX = -632f;
        private const float PartyTopRowY = 286f;
        private const float PartyRowStep = 64f;

        private const float EnemyPanelWidth = 62f;
        private const float EnemyPanelHeight = 62f;
        private const float EnemyPanelX = 146f;
        private const float EnemyPanelY = 355f;

        private const float SlotIconSize = 26f;
        private const float SlotGap = 6f;

        private static bool EnsureInitialized()
        {
            if (_initialized)
                return _canvasGO != null;

            _initialized = true;

            try
            {
                LoadSprites();
                ResolveSpriteFallbacks();
                CreateCanvas();
                CreatePanels();
                HideAll();
                return _canvasGO != null;
            }
            catch (Exception ex)
            {
                _log?.Error($"BuffDisplay init failed: {ex.Message}");
                return false;
            }
        }

        private static void LoadSprites()
        {
            if (!Directory.Exists(_assetDir))
            {
                _log?.Warning($"BuffDisplay: missing asset folder {_assetDir}");
                return;
            }

            int loaded = 0;
            for (int stat = 0; stat < StatCount; stat++)
            {
                for (int direction = 0; direction < 2; direction++)
                {
                    string suffix = direction == 0 ? "down" : "up";
                    for (int rank = 1; rank <= MaxBuffStage; rank++)
                    {
                        string fileName = $"{StatPrefixes[stat]}{suffix}{rank}.png";
                        string path = Path.Combine(_assetDir, fileName);
                        _buffSprites[stat, direction, rank - 1] = AffinityBoard.LoadSpritePublic(path);
                        if (_buffSprites[stat, direction, rank - 1] != null)
                        {
                            _buffSpriteLabels[stat, direction, rank - 1] = fileName;
                            loaded++;
                            PreyEyes2Trace.LogLimited($"buff-load:{fileName}", 1, "buff", $"loaded {fileName}");
                        }
                        else
                        {
                            PreyEyes2Trace.LogLimited($"buff-miss:{fileName}", 1, "buff", $"missing {fileName}");
                        }
                    }
                }
            }

            _log?.Msg($"BuffDisplay: loaded {loaded}/{StatCount * 2 * MaxBuffStage} kajakunda sprites");
        }

        private static void ResolveSpriteFallbacks()
        {
            int fallbackCount = 0;
            for (int stat = 0; stat < StatCount; stat++)
            {
                for (int direction = 0; direction < 2; direction++)
                {
                    for (int rank = 0; rank < MaxBuffStage; rank++)
                    {
                        if (_buffSprites[stat, direction, rank] != null)
                            continue;

                        object? replacement = FindNearestAvailableSprite(stat, direction, rank);
                        if (replacement == null)
                            continue;

                        _buffSprites[stat, direction, rank] = replacement;
                        _buffSpriteLabels[stat, direction, rank] = FindNearestAvailableSpriteLabel(stat, direction, rank);
                        fallbackCount++;
                    }
                }
            }

            if (fallbackCount > 0)
                _log?.Warning($"BuffDisplay: applied {fallbackCount} sprite fallback(s) for missing kajakunda art");
        }

        private static object? FindNearestAvailableSprite(int stat, int direction, int targetRank)
        {
            for (int distance = 1; distance < MaxBuffStage; distance++)
            {
                int lower = targetRank - distance;
                if (lower >= 0 && _buffSprites[stat, direction, lower] != null)
                    return _buffSprites[stat, direction, lower];

                int upper = targetRank + distance;
                if (upper < MaxBuffStage && _buffSprites[stat, direction, upper] != null)
                    return _buffSprites[stat, direction, upper];
            }

            return null;
        }

        private static string? FindNearestAvailableSpriteLabel(int stat, int direction, int targetRank)
        {
            for (int distance = 1; distance < MaxBuffStage; distance++)
            {
                int lower = targetRank - distance;
                if (lower >= 0 && _buffSpriteLabels[stat, direction, lower] != null)
                    return _buffSpriteLabels[stat, direction, lower];

                int upper = targetRank + distance;
                if (upper < MaxBuffStage && _buffSpriteLabels[stat, direction, upper] != null)
                    return _buffSpriteLabels[stat, direction, upper];
            }

            return null;
        }

        private static void CreateCanvas()
        {
            if (ReflectionCache.Ctor_GameObject_String == null || ReflectionCache.T_Canvas == null)
                return;

            _canvasGO = ReflectionCache.Ctor_GameObject_String.Invoke(new object[] { "PE2BuffCanvas" });
            object? canvas = ReflectionCache.AddComponent(_canvasGO, ReflectionCache.T_Canvas);
            if (canvas == null)
                return;

            ReflectionCache.P_Canvas_RenderMode?.SetValue(
                canvas,
                Enum.ToObject(ReflectionCache.P_Canvas_RenderMode.PropertyType, 0));
            ReflectionCache.P_Canvas_SortingOrder?.SetValue(canvas, 9998);

            if (ReflectionCache.T_CanvasScaler != null)
            {
                object? scaler = ReflectionCache.AddComponent(_canvasGO, ReflectionCache.T_CanvasScaler);
                if (scaler != null)
                {
                    try
                    {
                        var uiScaleMode = scaler.GetType().GetProperty("uiScaleMode");
                        uiScaleMode?.SetValue(scaler, Enum.ToObject(uiScaleMode.PropertyType, 1));

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

        private static void CreatePanels()
        {
            if (_canvasGO == null)
                return;

            _partyRootGO = CreateRoot("PE2PartyBuffs");
            if (_partyRootGO == null)
                return;

            CreatePartyPanel(_partyRootGO);

            _enemyRootGO = CreateEnemyRoot("PE2EnemyBuffs");
            if (_enemyRootGO != null)
                CreateEnemyPanel(_enemyRootGO);
        }

        private static object? CreateRoot(string name)
        {
            if (_canvasGO == null)
                return null;

            object? rootGO = ReflectionCache.MakeChild(_canvasGO, name);
            if (rootGO == null)
                return null;

            EnsureInvisibleUiRect(rootGO);
            ReflectionCache.SetRect(rootGO, 0f, 0f, 1f, 0f, 1f, 0f, 0f, 0f);
            return rootGO;
        }

        private static object? CreateEnemyRoot(string name)
        {
            if (_canvasGO == null)
                return null;

            object? rootGO = ReflectionCache.MakeChild(_canvasGO, name);
            if (rootGO == null)
                return null;

            EnsureInvisibleUiRect(rootGO);
            ReflectionCache.SetRect(rootGO, EnemyPanelWidth, EnemyPanelHeight, 0.5f, 0.5f, 0.5f, 0.5f, EnemyPanelX, EnemyPanelY);
            return rootGO;
        }

        private static void CreatePartyPanel(object panelGO)
        {
            float totalWidth = StatCount * SlotIconSize + (StatCount - 1) * SlotGap;
            float slotStartX = -totalWidth / 2f + SlotIconSize / 2f;

            for (int slot = 0; slot < PartySlots; slot++)
            {
                float rowY = PartyTopRowY - slot * PartyRowStep;
                object? rowGO = ReflectionCache.MakeChild(panelGO, $"PE2PartyRow{slot}");
                if (rowGO == null)
                    continue;

                _partyRowGOs[slot] = rowGO;
                EnsureInvisibleUiRect(rowGO);
                ReflectionCache.SetRect(rowGO, PartyRowWidth, PartyRowHeight, 0.5f, 0.5f, 0.5f, 0.5f, PartyRowX, rowY);

                for (int stat = 0; stat < StatCount; stat++)
                {
                    float slotX = slotStartX + stat * (SlotIconSize + SlotGap);
                    _partyStatIcons[slot, stat] = CreateIcon(rowGO, $"PE2PartyIcon{slot}_{stat}", slotX, 0f, out _partyStatImages[slot, stat]);
                }
            }
        }

        private static void CreateEnemyPanel(object panelGO)
        {
            float offset = SlotIconSize / 2f + 4f;
            _enemyStatIcons[0] = CreateIcon(panelGO, "PE2EnemyIconAtt", -offset, offset, out _enemyStatImages[0]);
            _enemyStatIcons[1] = CreateIcon(panelGO, "PE2EnemyIconDef", offset, offset, out _enemyStatImages[1]);
            _enemyStatIcons[2] = CreateIcon(panelGO, "PE2EnemyIconAcc", -offset, -offset, out _enemyStatImages[2]);
            _enemyStatIcons[3] = CreateIcon(panelGO, "PE2EnemyIconMag", offset, -offset, out _enemyStatImages[3]);
            SetPanelVisible(panelGO, false);
        }

        private static object? CreateIcon(object parentGO, string name, float centerX, float centerY, out object? image)
        {
            image = null;
            object? iconGO = ReflectionCache.MakeChild(parentGO, name);
            if (iconGO == null)
                return null;

            Type? componentType = ReflectionCache.T_Image;
            image = ReflectionCache.AddComponent(iconGO, componentType);
            if (image != null)
            {
                Type imageType = image.GetType();
                var spriteProp = imageType.GetProperty("sprite");
                var overrideSpriteProp = imageType.GetProperty("overrideSprite");
                var textureProp = imageType.GetProperty("texture");
                var typeProp = imageType.GetProperty("type");
                if (typeProp != null)
                    typeProp.SetValue(image, Enum.ToObject(typeProp.PropertyType, 0));
                imageType.GetProperty("preserveAspect")?.SetValue(image, true);
                imageType.GetProperty("raycastTarget")?.SetValue(image, false);
                ReflectionCache.WriteColor(image, 1f, 1f, 1f, 0f);
                PreyEyes2Trace.LogLimited(
                    $"buff-create-icon:{name}",
                    1,
                    "buff",
                    $"created icon {name} component={image.GetType().Name} overrideProp={(overrideSpriteProp != null)}");
            }

            ReflectionCache.SetRect(iconGO, SlotIconSize, SlotIconSize, 0.5f, 0.5f, 0.5f, 0.5f, centerX, centerY);
            ReflectionCache.SetActive(iconGO, true);
            return iconGO;
        }

        private static void EnsureInvisibleUiRect(object go)
        {
            if (ReflectionCache.GetComponent(go, ReflectionCache.T_RectTransform) != null)
                return;

            object? image = ReflectionCache.AddComponent(go, ReflectionCache.T_Image);
            if (image != null)
            {
                image.GetType().GetProperty("overrideSprite")?.SetValue(image, null);
                image.GetType().GetProperty("sprite")?.SetValue(image, null);
                ReflectionCache.P_Image_Sprite?.SetValue(image, null);
                ReflectionCache.WriteColor(image, 0f, 0f, 0f, 0f);
                image.GetType().GetProperty("enabled")?.SetValue(image, false);
                PreyEyes2Trace.LogLimited($"buff-invisible-root:{go.GetHashCode()}", 1, "buff", "created invisible ui rect");
            }
        }

        private static void SetPanelVisible(object? panelGO, bool visible)
        {
            ReflectionCache.SetActive(panelGO, visible);
        }
    }
}
