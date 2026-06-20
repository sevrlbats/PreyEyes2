using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;

namespace PreyEyes2
{
    /// <summary>Typed affinity result — what happens when this element hits this demon.</summary>
    internal enum AffinityResult
    {
        Weak,       // Bonus damage / press turn advantage
        Resist,     // Reduced damage
        Null,       // No damage
        Reflect,    // Bounced back
        Drain,      // Absorbed / heals target
        Normal,     // Standard damage
        Unknown     // Fog-of-war: not yet learned
    }

    /// <summary>
    /// Resolves the affinity result for a skill hitting a target.
    /// Reads the selected skill from the command menu, maps to element attr,
    /// queries nbGetAisyo, and decodes the bitmask.
    ///
    /// Also contains Light/Dark diagnostic logging for empirical element ordering.
    /// </summary>
    internal static class AffinityResolver
    {
        private static MelonLogger.Instance? _log;

        // Special command IDs
        private const int CMD_ATTACK = 32768;
        private const int CMD_RETREAT = 32769;
        private const int CMD_PASS = 32770;

        // Cached skill — commStat may be 0 during targeting (skill already selected),
        // so we cache the last successful read from command menu polling in OnUpdate.
        private static int _cachedSkillId = -1;

        // Affinity bitmask constants
        private const uint MASK_WEAK = 0x80000000;
        private const uint MASK_NULL = 0x10000;
        private const uint MASK_REFLECT = 0x20000;
        private const uint MASK_DRAIN = 0x40000;
        private const uint NORMAL_VALUE = 100;

        // Diagnostic logging state
        internal static bool DiagnosticMode = false;
        private static readonly HashSet<int> _diagLoggedDemons = new();
        private static int _diagErrorCount = 0;
        private const int MAX_DIAG_ERRORS = 10;

        internal static void Init(MelonLogger.Instance log, bool diagnosticMode)
        {
            _log = log;
            DiagnosticMode = diagnosticMode;

            if (diagnosticMode)
                log.Msg("AffinityResolver: Diagnostic mode ON — will log raw aisyo values for new demons.");
        }

        // ═══════════════════════════════════════════════════
        //  SKILL READING
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Poll the command menu for the currently selected skill.
        /// Call this every frame from OnUpdate — it caches the result for use during targeting,
        /// since commStat may be 0 by the time the targeting cursor draws.
        /// </summary>
        internal static void PollSelectedSkill()
        {
            try
            {
                var commData = nbCommSelProcess.GetCommSelProcessData();
                if (commData == null) return;

                var commlist = commData.commlist;
                var cursors = commData.nowcursor;
                if (commlist == null || cursors == null) return;

                int type = commData.nowtype;
                if (type < 0 || type >= commlist.Length) return;

                var list = commlist[type];
                if (list == null) return;

                int cursor = cursors[type];
                if (cursor < 0 || cursor >= list.Length) return;

                int skill = list[cursor];
                if (skill != _cachedSkillId)
                {
                    _cachedSkillId = skill;
                }
            }
            catch { }
        }

        /// <summary>
        /// Read the cached selected skill ID.
        /// Returns the skill ID, or -1 if never successfully read.
        /// </summary>
        internal static int ReadSelectedSkill()
        {
            return _cachedSkillId;
        }

        /// <summary>
        /// Get the element attribute for a skill ID.
        /// Returns the attr index (0=phys, 1=fire, etc.), or -1 if not applicable.
        /// </summary>
        internal static int GetSkillElement(int skillId)
        {
            if (skillId < 0) return -1;

            // Non-elemental commands
            if (skillId == CMD_RETREAT || skillId == CMD_PASS) return -1;

            // Attack command → physical (attr 0)
            if (skillId == CMD_ATTACK) return 0;

            try
            {
                int attr = nbCalc.nbGetNormalSkillAttr(skillId);
                // Filter out support/debuff (attr 14+) and invalid values
                if (attr < 0 || attr >= 14) return -1;
                return attr;
            }
            catch { return -1; }
        }

        // ═══════════════════════════════════════════════════
        //  AFFINITY QUERY
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Query the raw affinity bitmask and decode it.
        /// skillId: the actual skill (or 110 for generic test with Attack command)
        /// curidx: formation index of the target
        /// attr: element attribute index
        /// </summary>
        internal static AffinityResult QueryAffinity(int skillId, int curidx, int attr)
        {
            if (attr < 0) return AffinityResult.Normal;

            try
            {
                // For Attack command, use skillId=110 (generic physical test)
                int querySkill = (skillId == CMD_ATTACK) ? 110 : skillId;

                uint aisyo = nbCalc.nbGetAisyo(querySkill, curidx, attr);
                return DecodeAisyo(aisyo);
            }
            catch { return AffinityResult.Normal; }
        }

        /// <summary>
        /// Query affinity for a specific element attr against a target.
        /// Uses datGetAisyo (direct lookup from unit work) — no skill context needed.
        /// Falls back to nbGetAisyo with skill 0 if datGetAisyo unavailable.
        /// </summary>
        private static bool _boardPathLogged = false;
        internal static AffinityResult QueryAffinityForAttr(int curidx, int attr)
        {
            if (attr < 0 || attr > 7) return AffinityResult.Normal;

            // Knowledge gate: check before querying game data
            int demonId = ReflectionCache.GetDemonId(curidx);
            if (!KnowledgeStore.IsElementKnown(demonId, attr))
                return AffinityResult.Unknown;

            try
            {
                // Primary: datGetAisyo — direct lookup, no skill dependency
                if (ReflectionCache.N_DatGetAisyo != null && ReflectionCache.N_GetUnitWork != null)
                {
                    IntPtr uw = ReflectionCache.N_GetUnitWork(curidx, ReflectionCache.Mip_GetUnitWork);
                    if (uw != IntPtr.Zero)
                    {
                        uint aisyo = ReflectionCache.N_DatGetAisyo(uw, attr, ReflectionCache.Mip_DatGetAisyo);
                        if (!_boardPathLogged) { _boardPathLogged = true; _log?.Msg($"PE2: Board using datGetAisyo path, uw=0x{uw:X}"); }
                        return DecodeAisyo(aisyo);
                    }
                }

                // Fallback: nbGetAisyo with skill 0
                if (!_boardPathLogged) { _boardPathLogged = true; _log?.Msg("PE2: Board using nbGetAisyo fallback"); }
                uint aisyo2 = nbCalc.nbGetAisyo(0, curidx, attr);
                return DecodeAisyo(aisyo2);
            }
            catch { return AffinityResult.Normal; }
        }

        /// <summary>
        /// Decode the raw aisyo bitmask into a typed result.
        /// Priority order matters: Weak > Drain > Reflect > Null > Resist > Normal
        /// </summary>
        internal static AffinityResult DecodeAisyo(uint aisyo)
        {
            if ((aisyo & MASK_WEAK) != 0) return AffinityResult.Weak;
            if ((aisyo & MASK_DRAIN) != 0) return AffinityResult.Drain;
            if ((aisyo & MASK_REFLECT) != 0) return AffinityResult.Reflect;
            if ((aisyo & MASK_NULL) != 0 || aisyo == 0) return AffinityResult.Null;
            if (aisyo < NORMAL_VALUE && aisyo > 0) return AffinityResult.Resist;
            return AffinityResult.Normal;
        }

        // ═══════════════════════════════════════════════════
        //  HIGH-LEVEL: RESOLVE FOR RETICLE
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Main entry point: resolve what color the reticle should be for this target.
        /// Phase 1: always queries (no fog-of-war). Phase 4 adds knowledge gate.
        /// </summary>
        internal static AffinityResult ResolveForReticle(int curidx)
        {
            int skillId = ReadSelectedSkill();
            if (skillId < 0) return AffinityResult.Normal;

            int attr = GetSkillElement(skillId);
            if (attr < 0) return AffinityResult.Normal;

            // Knowledge gate: if we don't know this element for this demon, show Unknown
            int demonId = ReflectionCache.GetDemonId(curidx);
            if (!KnowledgeStore.IsElementKnown(demonId, attr))
                return AffinityResult.Unknown;

            // Diagnostic logging (runs once per new demon in diagnostic mode)
            if (DiagnosticMode)
                TryLogDiagnostics(curidx);

            return QueryAffinity(skillId, curidx, attr);
        }

        // ═══════════════════════════════════════════════════
        //  DIAGNOSTIC LOGGING (Light/Dark investigation)
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Log raw nbGetAisyo values for attrs 0-9 for a demon we haven't seen yet.
        /// This data resolves whether attrs 5 and 6 are Light and Dark or reversed.
        /// </summary>
        private static void TryLogDiagnostics(int curidx)
        {
            if (_diagErrorCount >= MAX_DIAG_ERRORS) return;

            try
            {
                int demonId = ReflectionCache.GetDemonId(curidx);
                if (demonId <= 0) return;
                if (_diagLoggedDemons.Contains(demonId)) return;
                _diagLoggedDemons.Add(demonId);

                string line = $"PE2 DIAG: demonId={demonId} curidx={curidx} | ";
                string[] attrNames = { "phys", "fire", "ice", "elec", "force",
                                       "attr5", "attr6", "attr7", "attr8", "attr9" };

                for (int a = 0; a < 10; a++)
                {
                    try
                    {
                        uint raw = nbCalc.nbGetAisyo(110, curidx, a);
                        string decoded = DecodeAisyo(raw).ToString();
                        line += $"{attrNames[a]}=0x{raw:X8}({decoded}) ";
                    }
                    catch
                    {
                        line += $"{attrNames[a]}=ERR ";
                    }
                }

                _log?.Msg(line);
            }
            catch
            {
                _diagErrorCount++;
            }
        }

        /// <summary>Clear cached skill (e.g., on battle exit).</summary>
        internal static void ClearCache()
        {
            _cachedSkillId = -1;
        }

        /// <summary>Clear diagnostic state (e.g., on new battle).</summary>
        internal static void ResetDiagnostics()
        {
            // Don't clear _diagLoggedDemons — we want per-species, not per-battle
        }
    }
}
