using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MelonLoader;

namespace PreyEyes2
{
    /// <summary>
    /// Fog-of-war knowledge system. Affinities start unknown and are learned through:
    /// 1. Attacking with an element (learns that specific element for that demon species)
    /// 2. Killing a demon (reveals all affinities for that species)
    /// 3. Analyzing/Spyglass (reveals all, even on bosses where vanilla Analyze fails)
    /// 4. Fusing/recruiting (reveals all)
    ///
    /// Knowledge persists per save file (keyed by player name) in JSON.
    /// Cathedral of Shadows always shows full knowledge (no fog-of-war).
    /// </summary>
    internal static class KnowledgeStore
    {
        private static MelonLogger.Instance? _log;
        private static string _filePath = "";

        // Per-element knowledge: demonId → set of known element attr indices (0-6)
        private static readonly Dictionary<int, HashSet<int>> _elementKnown = new();
        // Fully known demons: all 7 elements revealed
        private static readonly HashSet<int> _fullyKnown = new();

        // Player name for save file keying
        private static string _playerName = "";
        private static bool _playerNameResolved = false;

        // Dirty flag + debounced save
        private static bool _dirty = false;
        private static int _saveCountdown = 0;
        private const int SAVE_INTERVAL = 300; // ~5 seconds at 60fps

        internal static bool IsDirty => _dirty;

        // ═══════════════════════════════════════════════════
        //  INIT
        // ═══════════════════════════════════════════════════

        internal static void Init(MelonLogger.Instance log, string modsDir)
        {
            _log = log;
            _filePath = Path.Combine(modsDir, "PreyEyes2_knowledge.json");
            log.Msg($"KnowledgeStore: file={_filePath}");
        }

        /// <summary>Ensure player name is resolved and knowledge is loaded.</summary>
        private static void EnsureLoaded()
        {
            if (_playerNameResolved) return;
            _playerNameResolved = true;

            _playerName = ReflectionCache.GetPlayerName();
            _log?.Msg($"KnowledgeStore: playerName=\"{_playerName}\"");
            Load();
        }

        // ═══════════════════════════════════════════════════
        //  QUERIES
        // ═══════════════════════════════════════════════════

        // Valid attrs: 0-4,6-7 (elements), 8-10 (ailments). Attr 5 unused. 10 total trackable.
        private const int TOTAL_TRACKABLE = 10;
        private static bool IsValidAttr(int attr) => (attr >= 0 && attr <= 4) || (attr >= 6 && attr <= 10);

        /// <summary>Is the given attr known for this demon species?</summary>
        internal static bool IsElementKnown(int demonId, int attr)
        {
            EnsureLoaded();
            if (demonId <= 0 || !IsValidAttr(attr)) return false;
            if (_fullyKnown.Contains(demonId)) return true;
            return _elementKnown.TryGetValue(demonId, out var known) && known.Contains(attr);
        }

        /// <summary>Is this demon fully known (all 7 elements)?</summary>
        internal static bool IsFullyKnown(int demonId)
        {
            EnsureLoaded();
            return _fullyKnown.Contains(demonId);
        }

        // ═══════════════════════════════════════════════════
        //  LEARNING
        // ═══════════════════════════════════════════════════

        /// <summary>Learn a single attr for a demon species (element 0-6 or ailment 8-10).</summary>
        internal static void LearnElement(int demonId, int attr)
        {
            if (demonId <= 0 || !IsValidAttr(attr)) return;
            if (_fullyKnown.Contains(demonId)) return;

            if (!_elementKnown.TryGetValue(demonId, out var known))
            {
                known = new HashSet<int>();
                _elementKnown[demonId] = known;
            }

            if (known.Add(attr))
            {
                _dirty = true;
                _log?.Msg($"KnowledgeStore: Learned attr={attr} for demonId={demonId}");

                if (known.Count >= TOTAL_TRACKABLE)
                {
                    _fullyKnown.Add(demonId);
                    _elementKnown.Remove(demonId);
                    _log?.Msg($"KnowledgeStore: DemonId={demonId} now fully known (via individual learning)");
                }
            }
        }

        /// <summary>Learn ALL elements for a demon species (kill, analyze, fuse).</summary>
        internal static void LearnAll(int demonId)
        {
            if (demonId <= 0) return;
            if (_fullyKnown.Contains(demonId)) return;

            _fullyKnown.Add(demonId);
            _elementKnown.Remove(demonId); // no longer need per-element tracking
            _dirty = true;
            _log?.Msg($"KnowledgeStore: DemonId={demonId} fully revealed");
        }

        // ═══════════════════════════════════════════════════
        //  PERSISTENCE
        // ═══════════════════════════════════════════════════

        /// <summary>Call from OnUpdate — saves when dirty after countdown expires.</summary>
        internal static void Tick()
        {
            if (!_dirty) return;
            _saveCountdown--;
            if (_saveCountdown <= 0)
            {
                Save();
                _saveCountdown = SAVE_INTERVAL;
            }
        }

        /// <summary>Force save now (e.g., on mod shutdown).</summary>
        internal static void ForceSave()
        {
            if (_dirty) Save();
        }

        private static void Save()
        {
            try
            {
                // Build JSON manually (no dependency on System.Text.Json)
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.Append($"  \"{EscapeJson(_playerName)}\": {{");
                sb.AppendLine();

                // Fully known list
                sb.Append("    \"fullyKnown\": [");
                bool first = true;
                foreach (int id in _fullyKnown)
                {
                    if (!first) sb.Append(",");
                    sb.Append(id);
                    first = false;
                }
                sb.AppendLine("],");

                // Per-element knowledge
                sb.AppendLine("    \"elements\": {");
                first = true;
                foreach (var kvp in _elementKnown)
                {
                    if (!first) sb.AppendLine(",");
                    sb.Append($"      \"{kvp.Key}\": [");
                    bool firstAttr = true;
                    foreach (int attr in kvp.Value)
                    {
                        if (!firstAttr) sb.Append(",");
                        sb.Append(attr);
                        firstAttr = false;
                    }
                    sb.Append("]");
                    first = false;
                }
                sb.AppendLine();
                sb.AppendLine("    }");

                sb.AppendLine("  }");
                sb.AppendLine("}");

                File.WriteAllText(_filePath, sb.ToString());
                _dirty = false;
                _log?.Msg($"KnowledgeStore: Saved ({_fullyKnown.Count} fully known, {_elementKnown.Count} partial)");
            }
            catch (Exception ex)
            {
                _log?.Error($"KnowledgeStore.Save: {ex.Message}");
            }
        }

        private static void Load()
        {
            _fullyKnown.Clear();
            _elementKnown.Clear();

            if (!File.Exists(_filePath))
            {
                _log?.Msg("KnowledgeStore: No save file found, starting fresh.");
                return;
            }

            try
            {
                string json = File.ReadAllText(_filePath);

                // Find our player's section
                int pIdx = json.IndexOf("\"" + _playerName + "\"");
                if (pIdx < 0)
                {
                    _log?.Msg($"KnowledgeStore: No data for \"{_playerName}\", starting fresh.");
                    return;
                }

                // Parse fullyKnown array
                int fkIdx = json.IndexOf("\"fullyKnown\"", pIdx);
                if (fkIdx >= 0)
                {
                    int arrStart = json.IndexOf('[', fkIdx);
                    int arrEnd = json.IndexOf(']', arrStart);
                    if (arrStart >= 0 && arrEnd >= 0)
                    {
                        string arr = json.Substring(arrStart + 1, arrEnd - arrStart - 1).Trim();
                        if (!string.IsNullOrEmpty(arr))
                            foreach (var s in arr.Split(','))
                                if (int.TryParse(s.Trim(), out int id))
                                    _fullyKnown.Add(id);
                    }
                }

                // Parse elements object
                int elIdx = json.IndexOf("\"elements\"", pIdx);
                if (elIdx >= 0)
                {
                    int objStart = json.IndexOf('{', elIdx);
                    int objEnd = FindMatchingBrace(json, objStart);
                    if (objStart >= 0 && objEnd >= 0)
                    {
                        string inner = json.Substring(objStart + 1, objEnd - objStart - 1);
                        // Parse each "demonId": [attrs...] pair
                        int pos = 0;
                        while (pos < inner.Length)
                        {
                            int qStart = inner.IndexOf('"', pos);
                            if (qStart < 0) break;
                            int qEnd = inner.IndexOf('"', qStart + 1);
                            if (qEnd < 0) break;
                            string key = inner.Substring(qStart + 1, qEnd - qStart - 1);

                            int aStart = inner.IndexOf('[', qEnd);
                            int aEnd = inner.IndexOf(']', aStart);
                            if (aStart < 0 || aEnd < 0) break;

                            if (int.TryParse(key, out int demonId))
                            {
                                string attrs = inner.Substring(aStart + 1, aEnd - aStart - 1).Trim();
                                var known = new HashSet<int>();
                                if (!string.IsNullOrEmpty(attrs))
                                    foreach (var s in attrs.Split(','))
                                        if (int.TryParse(s.Trim(), out int attr))
                                            known.Add(attr);
                                if (known.Count > 0)
                                    _elementKnown[demonId] = known;
                            }

                            pos = aEnd + 1;
                        }
                    }
                }

                _log?.Msg($"KnowledgeStore: Loaded {_fullyKnown.Count} fully known, {_elementKnown.Count} partial for \"{_playerName}\"");
            }
            catch (Exception ex)
            {
                _log?.Error($"KnowledgeStore.Load: {ex.Message}");
            }
        }

        private static int FindMatchingBrace(string s, int openIdx)
        {
            int depth = 0;
            for (int i = openIdx; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>Clear all knowledge (for testing).</summary>
        internal static void PurgeAll()
        {
            _fullyKnown.Clear();
            _elementKnown.Clear();
            _dirty = false;
        }
    }
}
