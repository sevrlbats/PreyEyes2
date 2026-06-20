using System;
using System.Collections.Generic;
using MelonLoader;

namespace PreyEyes2
{
    internal static class PreyEyes2Trace
    {
        private static readonly Dictionary<string, int> _counts = new Dictionary<string, int>();
        private static MelonLogger.Instance? _log;
        private static int _sequence;

        internal static void Init(MelonLogger.Instance log)
        {
            _log = log;
            Log("trace", "diagnostic tracing enabled");
        }

        internal static void Log(string area, string message)
        {
            _log?.Msg($"PE2 TRACE {++_sequence:D5} [{area}] {message}");
        }

        internal static void LogLimited(string key, int limit, string area, string message)
        {
            if (!_counts.TryGetValue(key, out int count))
                count = 0;

            if (count >= limit)
                return;

            _counts[key] = count + 1;
            Log(area, message);
        }

        internal static void LogException(string area, string action, Exception ex, int limit = 8)
        {
            string key = $"ex:{area}:{action}";
            LogLimited(
                key,
                limit,
                area,
                $"{action} threw {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}");
        }
    }
}
