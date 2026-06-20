using System;
using System.Text;

namespace PreyEyes2
{
    public partial class PreyEyes2Mod
    {
        private static readonly int[] _trackedUnitDemonIds = new int[MAX_SLOTS];
        private static readonly int[] _trackedUnitHp = new int[MAX_SLOTS];
        private static readonly int[] _trackedUnitMaxHp = new int[MAX_SLOTS];
        private static int _combatTraceFrames;
        private static int _deathFreezeFrames;
        private static int _lastDeathFreezeStartFrame = -1;
        private static string _lastCombatSnapshot = string.Empty;
        private static string _lastDeathFreezeReason = string.Empty;

        static PreyEyes2Mod()
        {
            ResetTrackedUnitArrays();
        }

        private static void BeginCombatTrace(string reason, int tarStat, bool commandMenuActive, bool resolutionActive)
        {
            if (_combatTraceFrames < 180)
                _combatTraceFrames = 180;

            string snapshot = BuildCombatSnapshot(logChanges: false, commandMenuActive, resolutionActive, tarStat);
            _lastCombatSnapshot = snapshot;
            PreyEyes2Trace.Log(
                "combat",
                $"watch start reason={reason} frame={_frameCount} tarStat={tarStat} commandMenu={commandMenuActive} resolution={resolutionActive} iconHealth={BuffDisplay.DescribeIconHealth()} snapshot={snapshot}");
        }

        private static void TraceCombatWindow(bool inBattle, int tarStat, bool commandMenuActive, bool resolutionActive)
        {
            if (!inBattle)
            {
                ResetCombatTraceState();
                return;
            }

            if (IsDeathFreezeActive())
            {
                TraceDeathFreezeWindow(tarStat, commandMenuActive, resolutionActive);
                return;
            }

            if ((resolutionActive || !commandMenuActive) && _combatTraceFrames < 45)
                _combatTraceFrames = 45;

            if (_combatTraceFrames <= 0)
                return;

            string snapshot = BuildCombatSnapshot(logChanges: true, commandMenuActive, resolutionActive, tarStat);
            bool snapshotChanged = !string.Equals(snapshot, _lastCombatSnapshot, StringComparison.Ordinal);
            bool shouldLogFrame = ShouldTraceCombatFrame(_combatTraceFrames);

            if (snapshotChanged || shouldLogFrame)
            {
                PreyEyes2Trace.Log(
                    "combat",
                    $"watch frame={_frameCount} remaining={_combatTraceFrames} tarStat={tarStat} commandMenu={commandMenuActive} resolution={resolutionActive} iconHealth={BuffDisplay.DescribeIconHealth()} snapshot={snapshot}");
                _lastCombatSnapshot = snapshot;
            }

            if (_combatTraceFrames == 1)
            {
                PreyEyes2Trace.Log(
                    "combat",
                    $"watch end frame={_frameCount} iconHealth={BuffDisplay.DescribeIconHealth()} snapshot={snapshot}");
            }

            _combatTraceFrames--;
        }

        private static void ResetCombatTraceState()
        {
            _combatTraceFrames = 0;
            _deathFreezeFrames = 0;
            _lastDeathFreezeStartFrame = -1;
            _lastCombatSnapshot = string.Empty;
            _lastDeathFreezeReason = string.Empty;
            ResetTrackedUnitArrays();
        }

        private static void ResetTrackedUnitArrays()
        {
            for (int i = 0; i < MAX_SLOTS; i++)
            {
                _trackedUnitDemonIds[i] = int.MinValue;
                _trackedUnitHp[i] = int.MinValue;
                _trackedUnitMaxHp[i] = int.MinValue;
            }
        }

        private static string BuildCombatSnapshot(bool logChanges, bool commandMenuActive, bool resolutionActive, int tarStat)
        {
            var sb = new StringBuilder(256);
            sb.Append("P[");
            for (int slot = 0; slot < 4; slot++)
            {
                if (slot > 0)
                    sb.Append(',');
                AppendCombatSnapshotSlot(sb, slot, logChanges, commandMenuActive, resolutionActive, tarStat);
            }

            sb.Append("] E[");
            for (int slot = 4; slot < MAX_SLOTS; slot++)
            {
                if (slot > 4)
                    sb.Append(',');
                AppendCombatSnapshotSlot(sb, slot, logChanges, commandMenuActive, resolutionActive, tarStat);
            }

            sb.Append(']');
            return sb.ToString();
        }

        private static void AppendCombatSnapshotSlot(
            StringBuilder sb,
            int slot,
            bool logChanges,
            bool commandMenuActive,
            bool resolutionActive,
            int tarStat)
        {
            bool ok = ReflectionCache.TryGetUnitVitals(slot, out int demonId, out int hp, out int maxHp);
            if (!ok)
            {
                demonId = ReflectionCache.GetDemonId(slot);
                hp = -1;
                maxHp = -1;
                PreyEyes2Trace.LogLimited(
                    $"combat-vitals-miss:{slot}:{demonId}",
                    12,
                    "combat",
                    $"slot={slot} side={DescribeBattleSide(slot)} vitals unavailable demonId={demonId} frame={_frameCount}");
            }

            if (logChanges)
                TraceCombatSlotChange(slot, demonId, hp, maxHp, commandMenuActive, resolutionActive, tarStat, ok);

            sb.Append(slot);
            sb.Append(':');
            sb.Append(demonId);
            sb.Append('@');
            sb.Append(hp);
            sb.Append('/');
            sb.Append(maxHp);
        }

        private static void TraceCombatSlotChange(
            int slot,
            int demonId,
            int hp,
            int maxHp,
            bool commandMenuActive,
            bool resolutionActive,
            int tarStat,
            bool haveVitals)
        {
            int prevDemonId = _trackedUnitDemonIds[slot];
            int prevHp = _trackedUnitHp[slot];
            int prevMaxHp = _trackedUnitMaxHp[slot];

            _trackedUnitDemonIds[slot] = demonId;
            _trackedUnitHp[slot] = hp;
            _trackedUnitMaxHp[slot] = maxHp;

            if (prevDemonId == int.MinValue && prevHp == int.MinValue && prevMaxHp == int.MinValue)
                return;

            if (prevDemonId == demonId && prevHp == hp && prevMaxHp == maxHp)
                return;

            string side = DescribeBattleSide(slot);
            string vitality = haveVitals ? $"{prevHp}/{prevMaxHp}->{hp}/{maxHp}" : "unknown";
            PreyEyes2Trace.Log(
                "combat",
                $"slot={slot} side={side} demonId={prevDemonId}->{demonId} hp={vitality} tarStat={tarStat} commandMenu={commandMenuActive} resolution={resolutionActive} frame={_frameCount}");

            if (prevHp > 0 && hp == 0)
            {
                PreyEyes2Trace.Log(
                    "combat",
                    $"KO slot={slot} side={side} demonId={demonId} frame={_frameCount} commandMenu={commandMenuActive} resolution={resolutionActive}");
                BeginDeathFreeze($"ko slot={slot} side={side} demonId={demonId}", tarStat, commandMenuActive, resolutionActive);
            }

            if (prevDemonId > 0 && demonId <= 0)
            {
                PreyEyes2Trace.Log(
                    "combat",
                    $"slot vanished slot={slot} side={side} prevDemonId={prevDemonId} frame={_frameCount} commandMenu={commandMenuActive} resolution={resolutionActive}");
                BeginDeathFreeze($"vanish slot={slot} side={side} demonId={prevDemonId}", tarStat, commandMenuActive, resolutionActive);
            }
        }

        private static bool ShouldTraceCombatFrame(int remaining)
        {
            return remaining >= 176 || remaining <= 8 || (remaining % 15) == 0;
        }

        private static void BeginDeathFreeze(string reason, int tarStat, bool commandMenuActive, bool resolutionActive)
        {
            if (_lastDeathFreezeStartFrame == _frameCount && _deathFreezeFrames > 0)
                return;

            _lastDeathFreezeStartFrame = _frameCount;
            _lastDeathFreezeReason = reason;
            if (_deathFreezeFrames < DeathFreezeDurationFrames)
                _deathFreezeFrames = DeathFreezeDurationFrames;

            PreyEyes2Trace.Log(
                "combat",
                $"death-freeze start reason={reason} frame={_frameCount} tarStat={tarStat} commandMenu={commandMenuActive} resolution={resolutionActive} iconHealth={BuffDisplay.DescribeIconHealth()}");
        }

        private static void TraceDeathFreezeWindow(int tarStat, bool commandMenuActive, bool resolutionActive)
        {
            if (_deathFreezeFrames <= 0)
                return;

            if (ShouldTraceDeathFreezeFrame(_deathFreezeFrames))
            {
                PreyEyes2Trace.Log(
                    "combat",
                    $"death-freeze frame={_frameCount} remaining={_deathFreezeFrames} reason={_lastDeathFreezeReason} tarStat={tarStat} commandMenu={commandMenuActive} resolution={resolutionActive} iconHealth={BuffDisplay.DescribeIconHealth()}");
            }

            if (_deathFreezeFrames == 1)
                PreyEyes2Trace.Log("combat", $"death-freeze end frame={_frameCount} reason={_lastDeathFreezeReason}");

            _deathFreezeFrames--;
        }

        private static bool ShouldTraceDeathFreezeFrame(int remaining)
        {
            return remaining >= DeathFreezeDurationFrames - 4 || remaining <= 8 || (remaining % 20) == 0;
        }

        private static bool IsDeathFreezeActive()
        {
            return _deathFreezeFrames > 0;
        }

        private static string DescribeBattleSide(int slot)
        {
            return slot < 4 ? "party" : "enemy";
        }
    }
}
