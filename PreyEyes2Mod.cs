using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2Cpp;
using MelonLoader;
using MelonLoader.NativeUtils;

[assembly: MelonInfo(typeof(PreyEyes2.PreyEyes2Mod), "PreyEyes2", "2.5.5", "local")]
[assembly: MelonGame(null, "smt3hd")]

namespace PreyEyes2
{
    public partial class PreyEyes2Mod : MelonMod
    {
        // ── Hook: nbDrawTargetCursor ──
        // void nbDrawTargetCursor(nbMainProcessData_t data, nbFormation_t form,
        //                         sdfDrawTag_t tag, int curidx)
        // Native adds IntPtr methodInfo as last param.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void d_DrawTargetCursor(IntPtr data, IntPtr form, IntPtr tag,
                                                  int curidx, IntPtr methodInfo);

        private static NativeHook<d_DrawTargetCursor>? _hookDrawCursor;
        private static d_DrawTargetCursor? _delDrawCursor; // prevent GC

        // ── Hook: cmbGetStatTarget — Cathedral demon view detection ──
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr d_CmbGetStatTarget(IntPtr methodInfo);
        private static NativeHook<d_CmbGetStatTarget>? _hookCmbGetStat;
        private static d_CmbGetStatTarget? _delCmbGetStat;
        private static IntPtr _lastCmbStatResult = IntPtr.Zero;
        private static int _cmbStatCallFrame = -999;

        // ── Hook: SetAnalyzePacket (Analyze/Spyglass used) ──
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void d_SetAnalyzePacket(IntPtr actionData, int targetForm, int param2, IntPtr methodInfo);
        private static NativeHook<d_SetAnalyzePacket>? _hookAnalyze;
        private static d_SetAnalyzePacket? _delAnalyze;

        // ── Hook: datAddDevil (recruitment/fusion) ──
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int d_AddDevil(int a, int b, int c, IntPtr methodInfo);
        private static NativeHook<d_AddDevil>? _hookAddDevil;
        private static d_AddDevil? _delAddDevil;

        // ── Frame tracking ──
        private const int MAX_SLOTS = 12;
        private static readonly bool[] _boardDrawnThisFrame = new bool[MAX_SLOTS];
        private static readonly bool[] _liveBoardDrawnThisFrame = new bool[MAX_SLOTS];
        private static int _targetsThisFrame = 0; // count targets per frame for AoE detection
        private static int _liveTargetsThisFrame = 0; // distinct live enemy targets only; stale KO draws do not hide the board
        private static bool _wasInBattle = false;
        private static int _frameCount = 0;
        private static int _errorCount = 0;
        private const int MAX_ERRORS = 20;
        private const int DeathFreezeDurationFrames = 180;
        private static bool _lastCommandMenuActive = false;
        private static int _resolutionTraceFrames = 0;
        private static int _lastResolutionTraceStartFrame = -1;
        private const int TargetDrawGraceFrames = 8;
        private static int _lastTargetDrawFrame = -9999;
        private static bool _lastResolutionActive = false;
        private static bool _lastDeathFreezeActive = false;
        private static int _lastCursorTraceCuridx = -1;
        private static int _lastCursorTraceTargetCount = -1;
        private static int _lastCursorTraceDemonId = int.MinValue;

        // ── Learning triggers ──
        private static int _prevTarStat = 0;
        private static readonly bool[] _boardDrawnLastFrame = new bool[MAX_SLOTS];

        // ── Cathedral ──
        private static int _lastCathedralDemonId = -1;
        private static int _cathedralStaleCount = 0;

        // ── AnyMenu config ──
        internal static bool BoardEnabled = true;

        // ── Kill detection: track alive enemy IDs per curidx ──
        private static readonly int[] _aliveEnemyIds = new int[MAX_SLOTS];
        private static bool _enemyIdsInitialized = false;
        private static int _multiKillSkipLogCount = 0;

        // ── Mod folder path ──
        private static string _modsDir = "";
        private static bool _diagnosticMode = false;

        public override void OnInitializeMelon()
        {
            try
            {
                // Resolve Mods directory
                _modsDir = GetModsDir();

                bool diagMode = File.Exists(Path.Combine(_modsDir, "PreyEyes2_diag"));
                bool traceMode = diagMode || File.Exists(Path.Combine(_modsDir, "PreyEyes2_trace"));
                _diagnosticMode = diagMode;

                // Initialize subsystems
                ReflectionCache.Init(LoggerInstance);
                PreyEyes2Trace.Init(LoggerInstance, traceMode);

                bool useLuminance = File.Exists(Path.Combine(_modsDir, "PreyEyes2_luminance"));
                ReticleColorizer.Init(LoggerInstance, useLuminance, _modsDir, traceMode);
                AffinityResolver.Init(LoggerInstance, diagMode);

                AffinityBoard.Init(LoggerInstance, _modsDir, traceMode);
                KnowledgeStore.Init(LoggerInstance, _modsDir);
                BuffDisplay.Init(LoggerInstance);

                // Register hooks
                RegisterHooks();

                // Register with AnyMenu (if present)
                RegisterAnyMenu();

                LoggerInstance.Msg("PreyEyes2 v2.5.5 initialized.");
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"PreyEyes2 init failed: {ex}");
            }
        }

        // ═══════════════════════════════════════════════════
        //  HOOK REGISTRATION
        // ═══════════════════════════════════════════════════

        private void RegisterHooks()
        {
            // Hook nbDrawTargetCursor — scan for NativeMethodInfoPtr on nbMisc
            _delDrawCursor = Hook_DrawTargetCursor;

            IntPtr? hookPtr = FindNativeMethodPtr(typeof(nbMisc), "nbDrawTargetCursor");
            if (hookPtr == null)
            {
                // Fallback: scan all types
                hookPtr = ScanForNativeMethod("nbDrawTargetCursor");
            }

            if (hookPtr.HasValue && hookPtr.Value != IntPtr.Zero)
            {
                _hookDrawCursor = new NativeHook<d_DrawTargetCursor>(
                    hookPtr.Value,
                    Marshal.GetFunctionPointerForDelegate(_delDrawCursor));
                _hookDrawCursor.Attach();
                LoggerInstance.Msg($"nbDrawTargetCursor hooked at 0x{hookPtr.Value:X}");
            }
            else
            {
                LoggerInstance.Error("FAILED to find nbDrawTargetCursor — reticle tinting will not work.");
            }

            // Hook SetAnalyzePacket — fires when Analyze/Spyglass is used
            _delAnalyze = Hook_SetAnalyzePacket;
            IntPtr? analyzePtr = ScanForNativeMethod("SetAnalyzePacket");
            if (analyzePtr.HasValue && analyzePtr.Value != IntPtr.Zero)
            {
                _hookAnalyze = new NativeHook<d_SetAnalyzePacket>(
                    analyzePtr.Value, Marshal.GetFunctionPointerForDelegate(_delAnalyze));
                _hookAnalyze.Attach();
                LoggerInstance.Msg("SetAnalyzePacket hooked.");
            }

            // Hook cmbGetStatTarget — detect Cathedral demon view
            _delCmbGetStat = Hook_CmbGetStatTarget;
            IntPtr? cmbPtr = ScanForNativeMethod("cmbGetStatTarget");
            if (cmbPtr.HasValue && cmbPtr.Value != IntPtr.Zero)
            {
                _hookCmbGetStat = new NativeHook<d_CmbGetStatTarget>(
                    cmbPtr.Value, Marshal.GetFunctionPointerForDelegate(_delCmbGetStat));
                _hookCmbGetStat.Attach();
                LoggerInstance.Msg("cmbGetStatTarget hooked.");
            }

            // Hook datAddDevil — fires on recruitment/fusion
            _delAddDevil = Hook_AddDevil;
            IntPtr? addDevilPtr = ScanForNativeMethod("datAddDevil");
            if (addDevilPtr.HasValue && addDevilPtr.Value != IntPtr.Zero)
            {
                _hookAddDevil = new NativeHook<d_AddDevil>(
                    addDevilPtr.Value, Marshal.GetFunctionPointerForDelegate(_delAddDevil));
                _hookAddDevil.Attach();
                LoggerInstance.Msg("datAddDevil hooked.");
            }

        }

        /// <summary>Find a NativeMethodInfoPtr field on a specific type.</summary>
        private IntPtr? FindNativeMethodPtr(Type type, string methodName)
        {
            try
            {
                foreach (var f in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (f.Name.Contains("NativeMethodInfoPtr") && f.Name.Contains(methodName))
                    {
                        IntPtr mip = (IntPtr)f.GetValue(null)!;
                        IntPtr fp = Marshal.ReadIntPtr(mip);
                        if (fp != IntPtr.Zero) return fp;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>Scan all loaded assemblies for a NativeMethodInfoPtr field matching a method name.</summary>
        private IntPtr? ScanForNativeMethod(string methodName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                        foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                        {
                            if (f.Name.Contains("NativeMethodInfoPtr") && f.Name.Contains(methodName))
                            {
                                IntPtr mip = (IntPtr)f.GetValue(null)!;
                                IntPtr fp = Marshal.ReadIntPtr(mip);
                                if (fp != IntPtr.Zero)
                                {
                                    LoggerInstance.Msg($"Found {methodName} on {t.Name}");
                                    return fp;
                                }
                            }
                        }
                }
                catch { }
            }
            return null;
        }

        // ═══════════════════════════════════════════════════
        //  HOOK DETOUR
        // ═══════════════════════════════════════════════════

        private static void Hook_DrawTargetCursor(IntPtr data, IntPtr form, IntPtr tag,
                                                   int curidx, IntPtr methodInfo)
        {
            // 1. Call original — game renders reticle, animator sets pink color
            _hookDrawCursor!.Trampoline(data, form, tag, curidx, methodInfo);

            // 2. Validate curidx range
            if (curidx < 0 || curidx >= MAX_SLOTS) return;

            // 3. Mark this curidx as drawn this frame (only for enemies)
            if (curidx >= 4)
            {
                _boardDrawnThisFrame[curidx] = true;
                _lastTargetDrawFrame = _frameCount;
                _targetsThisFrame++;
            }

            // 4. Resolve affinity and apply tint
            // Party targets (curidx 0-3): always Normal/white
            // Enemy targets (curidx 4-11): resolve actual affinity
            try
            {
                AffinityResult result = (curidx < 4)
                    ? AffinityResult.Normal
                    : AffinityResolver.ResolveForReticle(curidx);
                ReticleColorizer.ApplyToReticle(curidx, result);
                int demonId = ReflectionCache.GetDemonId(curidx);
                bool liveEnemyTarget = curidx >= 4 && IsLiveEnemyTarget(curidx, demonId);
                if (liveEnemyTarget && !_liveBoardDrawnThisFrame[curidx])
                {
                    _liveBoardDrawnThisFrame[curidx] = true;
                    _liveTargetsThisFrame++;
                }

                if (curidx >= 4 &&
                    (_lastCursorTraceCuridx != curidx ||
                     _lastCursorTraceTargetCount != _targetsThisFrame ||
                     _lastCursorTraceDemonId != demonId))
                {
                    _lastCursorTraceCuridx = curidx;
                    _lastCursorTraceTargetCount = _targetsThisFrame;
                    _lastCursorTraceDemonId = demonId;
                    PreyEyes2Trace.Log("cursor", $"target curidx={curidx} targetCount={_targetsThisFrame} result={result} demonId={demonId}");
                }

                // 5. Update affinity board (enemies only)
                if (liveEnemyTarget && BoardEnabled)
                {
                    var reticleGO = ReflectionCache.GetTargetMark2D(curidx);
                    AffinityBoard.OnTargetDrawn(curidx, _liveTargetsThisFrame, reticleGO);
                }
            }
            catch (Exception ex)
            {
                PreyEyes2Trace.LogException("cursor", $"Hook_DrawTargetCursor curidx={curidx}", ex, MAX_ERRORS);
                if (_errorCount < MAX_ERRORS)
                {
                    _errorCount++;
                    MelonLogger.Error($"PE2 Hook({curidx}): {ex.Message}");
                }
            }
        }

        // ── Cathedral demon view hook — tracks when game actively queries stat target ──
        private static IntPtr Hook_CmbGetStatTarget(IntPtr methodInfo)
        {
            var result = _hookCmbGetStat!.Trampoline(methodInfo);
            _cmbStatCallFrame = _frameCount;

            if (result != IntPtr.Zero)
            {
                _lastCmbStatResult = result;
                try
                {
                    int demonId = 0;
                    if (ReflectionCache.N_GetUnitID != null)
                        demonId = ReflectionCache.N_GetUnitID(result, ReflectionCache.Mip_GetUnitID);

                    // Optional raw affinity scan for element-order investigation.
                    if (_diagnosticMode && demonId > 0 && demonId != _lastCathedralDemonId && _cathedralStaleCount < 30
                        && ReflectionCache.N_DatGetAisyo != null)
                    {
                        _cathedralStaleCount++;
                        _lastCathedralDemonId = demonId;
                        // Scan attrs 0-20 to find ailment indices
                        string diag = $"PE2 FULL SCAN id={demonId}: ";
                        for (int a = 0; a <= 20; a++)
                        {
                            try
                            {
                                uint raw = ReflectionCache.N_DatGetAisyo(result, a, ReflectionCache.Mip_DatGetAisyo);
                                if (raw != 0x64 && raw != 0) // only log non-Normal, non-zero
                                    diag += $"a{a}=0x{raw:X}({AffinityResolver.DecodeAisyo(raw)}) ";
                            }
                            catch { }
                        }
                        MelonLogger.Msg(diag);
                    }

                    if (demonId > 0 && demonId < 1000 && BoardEnabled)
                        AffinityBoard.OnCathedralTarget(result);
                }
                catch { }
            }
            else
            {
                // Null result — hide instantly
                _lastCmbStatResult = IntPtr.Zero;
                try { AffinityBoard.OnCathedralClosed(); } catch { }
            }

            return result;
        }

        // ── Analyze/Spyglass hook — reveals ALL affinities (even on bosses) ──
        private static void Hook_SetAnalyzePacket(IntPtr actionData, int targetForm, int param2, IntPtr methodInfo)
        {
            _hookAnalyze!.Trampoline(actionData, targetForm, param2, methodInfo);
            try
            {
                int demonId = ReflectionCache.GetDemonId(targetForm);
                if (demonId > 0)
                {
                    KnowledgeStore.LearnAll(demonId);
                    MelonLogger.Msg($"PE2: Analyze → fully revealed demonId={demonId}");
                }
            }
            catch { }
        }

        // ── Recruitment/fusion hook — reveals ALL affinities ──
        private static int Hook_AddDevil(int a, int b, int c, IntPtr methodInfo)
        {
            int result = _hookAddDevil!.Trampoline(a, b, c, methodInfo);
            try
            {
                // Parameter 'a' is the demon ID
                if (a > 0)
                {
                    KnowledgeStore.LearnAll(a);
                    MelonLogger.Msg($"PE2: AddDevil → fully revealed demonId={a}");
                }
            }
            catch { }
            return result;
        }


        // ═══════════════════════════════════════════════════
        //  PER-FRAME UPDATE
        // ═══════════════════════════════════════════════════

        public override void OnUpdate()
        {
            _frameCount++;

            // Wait a few frames for game to stabilize
            if (_frameCount < 120) return;

            try
            {
                bool inBattle = nbMainProcess.nbGetMainProcessData() != null;
                int tarStat = 0;
                bool commandMenuActive = false;
                if ((_frameCount % 60) == 0)
                    PreyEyes2Trace.Log("update", $"frame={_frameCount} inBattle={inBattle} targetsThisFrame={_targetsThisFrame} wasInBattle={_wasInBattle}");

                if (inBattle)
                {
                    try
                    {
                        tarStat = nbTarSelProcess.nbGetTarSelStat();
                        if (tarStat != _prevTarStat)
                            PreyEyes2Trace.Log("update", $"tarStat changed {_prevTarStat}->{tarStat} frame={_frameCount}");
                        if (tarStat == 0)
                        {
                            AffinityBoard.OnNoTargets();
                        }

                        commandMenuActive = HasCommandMenuData();
                        if (commandMenuActive != _lastCommandMenuActive)
                            PreyEyes2Trace.Log("update", $"commandMenuActive changed {_lastCommandMenuActive}->{commandMenuActive} frame={_frameCount} tarStat={tarStat}");
                        if (_lastCommandMenuActive && !commandMenuActive)
                            BeginResolutionTrace("command-menu-closed", tarStat, commandMenuActive);
                        _lastCommandMenuActive = commandMenuActive;
                    }
                    catch (Exception ex)
                    {
                        PreyEyes2Trace.LogException("update", "read tarStat", ex);
                    }
                }
                else
                {
                    _lastCommandMenuActive = false;
                    _resolutionTraceFrames = 0;
                    _lastDeathFreezeActive = false;
                    ResetCombatTraceState();
                }

                // Buff/debuff overlay (START to toggle)
                bool resolutionActive = _resolutionTraceFrames > 0;
                TraceCombatWindow(inBattle, tarStat, commandMenuActive, resolutionActive);
                bool deathFreezeActive = IsDeathFreezeActive();
                if (_lastResolutionActive != resolutionActive || _lastDeathFreezeActive != deathFreezeActive)
                    PreyEyes2Trace.Log("update", $"combat guard state resolution={resolutionActive} deathFreeze={deathFreezeActive} tarStat={tarStat} commandMenu={commandMenuActive} frame={_frameCount}");
                _lastResolutionActive = resolutionActive;
                _lastDeathFreezeActive = deathFreezeActive;
                BuffDisplay.OnUpdate(inBattle);

                if (inBattle)
                {
                    _wasInBattle = true;

                    // Poll the command menu every frame — caches skill ID for the hook
                    AffinityResolver.PollSelectedSkill();

                    // Re-apply cached colors to fight animator overrides between hook calls
                    ReticleColorizer.ReapplyCachedColors();

                    // Heavier death-window diagnostics: track unit HP/IDs through resolution and no-menu phases.

                    // ── Learning trigger: tarStat transition (attack confirmed) ──
                    try
                    {
                        if (_prevTarStat != 0 && tarStat == 0)
                        {
                            BeginResolutionTrace("tarstat-transition", tarStat, commandMenuActive);

                            // Player confirmed targeting — learn the skill's element
                            int skillId = AffinityResolver.ReadSelectedSkill();
                            int attr = AffinityResolver.GetSkillElement(skillId);
                            if (attr >= 0 && (attr <= 4 || (attr >= 6 && attr <= 10)))
                            {
                                for (int i = 4; i < MAX_SLOTS; i++)
                                {
                                    if (_boardDrawnLastFrame[i])
                                    {
                                        int demonId = ReflectionCache.GetDemonId(i);
                                        if (demonId > 0)
                                            KnowledgeStore.LearnElement(demonId, attr);
                                    }
                                }
                            }

                            AffinityBoard.OnNoTargets();
                        }
                        _prevTarStat = tarStat;
                    }
                    catch (Exception ex)
                    {
                        PreyEyes2Trace.LogException("update", "learn selected skill element", ex);
                    }

                    // ── Kill detection: check for newly dead enemies ──
                    try
                    {
                        bool allowKillDetection = !resolutionActive && !deathFreezeActive && commandMenuActive;
                        if (!allowKillDetection)
                        {
                            if (deathFreezeActive)
                            {
                                if (ShouldTraceResolutionFrame(_resolutionTraceFrames))
                                    PreyEyes2Trace.Log("update", $"skipping kill detection frame={_frameCount} because death freeze is active");
                            }
                            else if (resolutionActive)
                            {
                                if (ShouldTraceResolutionFrame(_resolutionTraceFrames))
                                    PreyEyes2Trace.Log("update", $"skipping kill detection frame={_frameCount} because resolution window is active");
                            }
                            else
                            {
                                PreyEyes2Trace.LogLimited(
                                    $"kill-detection-blocked:{commandMenuActive}:{tarStat}",
                                    8,
                                    "update",
                                    $"skipping kill detection frame={_frameCount} because commandMenu={commandMenuActive} tarStat={tarStat}");
                            }
                        }
                        else
                        {
                            int changedCount = 0;
                            int[] changedIds = new int[MAX_SLOTS];

                            for (int i = 4; i < MAX_SLOTS; i++)
                            {
                                int currentId = ReflectionCache.GetDemonId(i);
                                if (_enemyIdsInitialized && _aliveEnemyIds[i] > 0 && (currentId <= 0 || currentId != _aliveEnemyIds[i]))
                                {
                                    changedIds[changedCount++] = _aliveEnemyIds[i];
                                }
                                _aliveEnemyIds[i] = currentId;
                            }

                            if (changedCount == 1)
                            {
                                KnowledgeStore.LearnAll(changedIds[0]);
                                MelonLogger.Msg($"PE2: Kill detected — demonId={changedIds[0]}, fully revealed");
                                PreyEyes2Trace.Log("update", $"kill detection changedCount=1 demonId={changedIds[0]} frame={_frameCount}");
                            }
                            else if (changedCount > 1 && _multiKillSkipLogCount < 4)
                            {
                                _multiKillSkipLogCount++;
                                MelonLogger.Warning($"PE2: Skipping multi-enemy kill learning for {changedCount} slot changes this frame");
                                PreyEyes2Trace.Log("update", $"kill detection changedCount={changedCount} frame={_frameCount}");
                            }

                            _enemyIdsInitialized = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        PreyEyes2Trace.LogException("update", "kill detection", ex);
                    }

                    // FPS unlockers can run extra update ticks between target-cursor draws.
                    // Keep the board stable through short draw-hook gaps, but still hide
                    // immediately for real target-end states handled above (tarStat == 0).
                    if (_targetsThisFrame == 0)
                    {
                        bool targetDrawRecently = (_frameCount - _lastTargetDrawFrame) <= TargetDrawGraceFrames;
                        if (!targetDrawRecently)
                        {
                            if (CountActiveTargets(_boardDrawnLastFrame) > 0)
                                BeginResolutionTrace("targets-disappeared", tarStat, commandMenuActive);
                            AffinityBoard.OnNoTargets();
                        }
                    }

                    TraceResolutionWindow(inBattle, tarStat, commandMenuActive);

                    // Debounced knowledge persistence
                    KnowledgeStore.Tick();

                    // Save the most recent real target draw. During the FPS-unlock grace window,
                    // preserve it so action-confirm learning still sees the last selected enemy.
                    if (_targetsThisFrame > 0 || (_frameCount - _lastTargetDrawFrame) > TargetDrawGraceFrames)
                        Array.Copy(_boardDrawnThisFrame, _boardDrawnLastFrame, MAX_SLOTS);
                    Array.Clear(_boardDrawnThisFrame, 0, MAX_SLOTS);
                    Array.Clear(_liveBoardDrawnThisFrame, 0, MAX_SLOTS);
                    _targetsThisFrame = 0;
                    _liveTargetsThisFrame = 0;
                }
                else if (_wasInBattle)
                {
                    // Just left battle — clean up
                    _wasInBattle = false;
                    ReticleColorizer.ClearAll();
                    AffinityResolver.ClearCache();
                    AffinityBoard.ResetForNewBattle();
                    KnowledgeStore.ForceSave();
                    Array.Clear(_boardDrawnThisFrame, 0, MAX_SLOTS);
                    Array.Clear(_liveBoardDrawnThisFrame, 0, MAX_SLOTS);
                    Array.Clear(_boardDrawnLastFrame, 0, MAX_SLOTS);
                    _lastTargetDrawFrame = -9999;
                    _targetsThisFrame = 0;
                    _liveTargetsThisFrame = 0;
                    _prevTarStat = 0;
                    _lastCommandMenuActive = false;
                    _resolutionTraceFrames = 0;
                    _lastResolutionActive = false;
                    _lastDeathFreezeActive = false;
                    _lastCursorTraceCuridx = -1;
                    _lastCursorTraceTargetCount = -1;
                    _lastCursorTraceDemonId = int.MinValue;
                    Array.Clear(_aliveEnemyIds, 0, MAX_SLOTS);
                    _enemyIdsInitialized = false;
                    ResetCombatTraceState();
                }

                // Cathedral: hide when cmbGetStatTarget hook stops firing.
                if (!inBattle)
                {
                    int framesSinceHook = _frameCount - _cmbStatCallFrame;
                    if (framesSinceHook > 1)
                        AffinityBoard.OnCathedralClosed();
                }
            }
            catch (Exception ex)
            {
                PreyEyes2Trace.LogException("update", "OnUpdate outer", ex, 16);
            }
        }

        // ═══════════════════════════════════════════════════
        //  ANYMENU INTEGRATION
        // ═══════════════════════════════════════════════════

        private void RegisterAnyMenu()
        {
            try
            {
                // AnyMenu handles all settings persistence — no local config needed

                // Find AnyMenu via reflection (no hard dependency)
                var amType = Type.GetType("AnyMenu.AnyMenuMod, AnyMenu");
                if (amType == null)
                {
                    LoggerInstance.Msg("PreyEyes2: AnyMenu not found — no menu integration.");
                    return;
                }

                var regSetting = amType.GetMethod("RegisterModSetting", BindingFlags.Public | BindingFlags.Static);
                if (regSetting == null)
                {
                    LoggerInstance.Warning("PreyEyes2: AnyMenu.RegisterModSetting not found.");
                    return;
                }

                // Register "Affinity Board" toggle under "Prey Eyes" mod group
                // AnyMenu will auto-restore saved state during registration
                Action onSelect = () =>
                {
                    BoardEnabled = !BoardEnabled;
                    if (!BoardEnabled)
                        AffinityBoard.OnNoTargets();
                    MelonLogger.Msg($"PreyEyes2: Affinity Board {(BoardEnabled ? "ON" : "OFF")}");
                };
                Func<bool> isActive = () => BoardEnabled;

                // 7 params: modName, settingLabel, onSelect, isActive, caption, options, getCurrentOption
                regSetting.Invoke(null, new object?[] { "Prey Eyes", "Affinity Board", onSelect, isActive, (string?)null, (string[]?)null, (Func<int>?)null });
                LoggerInstance.Msg($"PreyEyes2: Registered with AnyMenu (board={BoardEnabled})");
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning($"PreyEyes2: AnyMenu registration failed: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════
        //  CLEANUP
        // ═══════════════════════════════════════════════════

        public override void OnDeinitializeMelon()
        {
            _hookDrawCursor?.Detach();
            _hookAnalyze?.Detach();
            _hookAddDevil?.Detach();
            _hookCmbGetStat?.Detach();
            KnowledgeStore.ForceSave();
            LoggerInstance.Msg("PreyEyes2: Hooks detached. Knowledge saved. Goodbye.");
        }

        // ═══════════════════════════════════════════════════
        //  UTILITY
        // ═══════════════════════════════════════════════════

        private static string GetModsDir()
        {
            var dir = Path.GetDirectoryName(typeof(nbCalc).Assembly.Location);
            if (dir != null && dir.Contains("Il2CppAssemblies"))
                dir = Path.GetDirectoryName(Path.GetDirectoryName(dir));
            return Path.Combine(dir ?? ".", "Mods");
        }

        private static bool HasCommandMenuData()
        {
            try
            {
                var commData = nbCommSelProcess.GetCommSelProcessData();
                return commData != null && commData.commlist != null && commData.nowcursor != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLiveEnemyTarget(int curidx, int demonId)
        {
            if (curidx < 4 || demonId <= 0)
                return false;

            if (ReflectionCache.TryGetUnitVitals(curidx, out int vitalsDemonId, out int hp, out _))
            {
                if (vitalsDemonId <= 0)
                    return false;

                if (hp == 0)
                    return false;
            }

            return true;
        }

        private static void BeginResolutionTrace(string reason, int tarStat, bool commandMenuActive)
        {
            if (_lastResolutionTraceStartFrame == _frameCount && _resolutionTraceFrames > 0)
                return;

            _lastResolutionTraceStartFrame = _frameCount;
            _resolutionTraceFrames = 90;
            BeginCombatTrace(reason, tarStat, commandMenuActive, true);

            int skillId = AffinityResolver.ReadSelectedSkill();
            int attr = AffinityResolver.GetSkillElement(skillId);
            PreyEyes2Trace.Log(
                "update",
                $"resolution-window start reason={reason} frame={_frameCount} tarStat={tarStat} skill={skillId} attr={attr} targetsLastFrame={CountActiveTargets(_boardDrawnLastFrame)} targetsThisFrame={_targetsThisFrame}");
        }

        private static void TraceResolutionWindow(bool inBattle, int tarStat, bool commandMenuActive)
        {
            if (_resolutionTraceFrames <= 0)
                return;

            if (ShouldTraceResolutionFrame(_resolutionTraceFrames))
            {
                PreyEyes2Trace.Log(
                    "update",
                    $"resolution-window frame={_frameCount} remaining={_resolutionTraceFrames} inBattle={inBattle} tarStat={tarStat} commandMenu={commandMenuActive} targetsThisFrame={_targetsThisFrame} targetsLastFrame={CountActiveTargets(_boardDrawnLastFrame)}");
            }

            if (_resolutionTraceFrames == 1)
                PreyEyes2Trace.Log("update", $"resolution-window end frame={_frameCount}");
            _resolutionTraceFrames--;
        }

        private static bool ShouldTraceResolutionFrame(int remaining)
        {
            return remaining >= 88 || remaining <= 5 || (remaining % 10) == 0;
        }

        private static int CountActiveTargets(bool[] slots)
        {
            int count = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i])
                    count++;
            }

            return count;
        }
    }
}
