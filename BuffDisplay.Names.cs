using System;
using System.Reflection;

namespace PreyEyes2
{
    internal static partial class BuffDisplay
    {
        private static MethodInfo? _getDevilName;
        private static bool _nameLookupResolved;

        private static string GetUnitName(int formIndex)
        {
            if (formIndex == 0)
            {
                string playerName = ReflectionCache.GetPlayerName();
                return string.IsNullOrWhiteSpace(playerName) || playerName == "Unknown"
                    ? "Demi-fiend"
                    : playerName;
            }

            int demonId = ReflectionCache.GetDemonId(formIndex);
            if (demonId <= 0)
                return formIndex < PartySlots ? $"Ally {formIndex}" : "Target";

            ResolveNameLookup();
            if (_getDevilName != null)
            {
                try
                {
                    object? result = _getDevilName.Invoke(null, new object[] { demonId });
                    if (result != null)
                    {
                        string? name = result.ToString();
                        if (!string.IsNullOrWhiteSpace(name))
                            return name;
                    }
                }
                catch { }
            }

            return formIndex < PartySlots ? $"Ally {formIndex}" : $"Enemy {formIndex - PartySlots + 1}";
        }

        private static void ResolveNameLookup()
        {
            if (_nameLookupResolved)
                return;

            _nameLookupResolved = true;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? devilNameType = assembly.GetType("Il2Cpp.datDevilName", false);
                if (devilNameType == null)
                    continue;

                _getDevilName = devilNameType.GetMethod("Get", new[] { typeof(int) })
                    ?? devilNameType.GetMethod("get_Item", new[] { typeof(int) });
                if (_getDevilName != null)
                    break;
            }
        }
    }
}
