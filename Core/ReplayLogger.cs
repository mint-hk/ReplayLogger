using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GlobalEnums;
using System.Globalization;
using UnityEngine;
using System.Text.RegularExpressions;
using System.Reflection;

namespace ReplayLogger
{
    
    
    
    internal static class CoreSessionLogger
    {
        private static readonly Dictionary<int, string> CustomCharmDisplayNames = new()
        {
            { (int)Charm.MarkOfPurity, "Mark of Purity" },
            { (int)Charm.VesselsLament, "Vessel's Lament" },
            { (int)Charm.BoonOfHallownest, "Boon of Hallownest" },
            { (int)Charm.AbyssalBloom, "Abyssal Bloom" }
        };

        public static IReadOnlyList<string> GetEncryptedModSnapshot(string modsDir)
        {
            if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir))
            {
                return new List<string>();
            }

            List<string> snapshot = ModsChecking.ScanMods(modsDir);
            if (snapshot == null || snapshot.Count == 0)
            {
                return new List<string>();
            }
            return snapshot;
        }

        public static void WriteEncryptedModSnapshot(StreamWriter writer, string modsDir, string separatorAfter = null)
        {
            if (writer == null)
            {
                return;
            }
            foreach (string line in GetEncryptedModSnapshot(modsDir))
            {
                LogWrite.EncryptedLine(writer, line);
            }

            if (!string.IsNullOrEmpty(separatorAfter))
            {
                LogWrite.EncryptedLine(writer, separatorAfter);
            }

        }

        public static string BuildEquippedCharmsLine()
        {
            StringBuilder builder = new("\nEquipped charms => ");
            int initialLength = builder.Length;
            HashSet<string> seen = new(StringComparer.Ordinal);
            int totalCost = 0;

            void AppendCharm(int charmId, string name)
            {
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                {
                    return;
                }

                int cost = GetCharmCost(charmId);
                totalCost += Math.Max(cost, 0);
                if (cost >= 0)
                {
                    builder.Append($"{name} ({cost}), ");
                }
                else
                {
                    builder.Append($"{name}, ");
                }
            }

            if (PlayerData.instance?.equippedCharms != null)
            {
                foreach (int charm in PlayerData.instance.equippedCharms)
                {
                    AppendCharm(charm, GetCharmDisplayName(charm));
                }
            }

            foreach (string customCharm in PaleCourtCharmIntegration.GetEquippedCharmNames())
            {
                if (!string.IsNullOrWhiteSpace(customCharm) && seen.Add(customCharm))
                {
                    builder.Append($"{customCharm}, ");
                }
            }

            if (BossSequenceController.BoundCharms)
            {
                builder.Append(" => BOUND CHARMS");
            }
            else if (builder.Length > initialLength)
            {
                builder.Length -= 2;
                builder.Append($" | Total Cost: {totalCost}");
            }

            builder.Append('\n');
            return builder.ToString();
        }

        public static IReadOnlyList<string> BuildSkillLines()
        {
            PlayerData data = PlayerData.instance;
            if (data == null)
            {
                return Array.Empty<string>();
            }

            string OnOff(bool value) => value ? "On" : "Off";

            List<string> lines = new() { "Skills:" };

            lines.Add($"  Scream: {data.screamLevel}");
            lines.Add($"  Fireball: {data.fireballLevel}");
            lines.Add($"  Quake: {data.quakeLevel}");

            string dash = data.hasShadowDash ? "Shade" : data.hasDash ? "Dash" : "None";
            lines.Add($"  Dash: {dash}");
            lines.Add($"  Mantis Claw: {OnOff(data.hasWalljump)}");
            lines.Add($"  Monarch Wings: {OnOff(data.hasDoubleJump)}");
            lines.Add($"  Crystal Heart: {OnOff(data.hasSuperDash)}");
            lines.Add($"  Isma's Tear: {OnOff(data.hasAcidArmour)}");

            string dreamNail = data.dreamNailUpgraded ? "Awoken" : data.hasDreamNail ? "Normal" : "None";
            lines.Add($"  Dream Nail: {dreamNail}");
            lines.Add($"  Dream Gate: {OnOff(data.hasDreamGate)}");

            lines.Add($"  Great Slash: {OnOff(data.hasDashSlash)}");
            lines.Add($"  Dash Slash: {OnOff(data.hasUpwardSlash)}");
            lines.Add($"  Cyclone Slash: {OnOff(data.hasCyclone)}");

            return lines;
        }

        public static void WriteEncryptedSkillLines(StreamWriter writer, string separatorAfter = null)
        {
            if (writer == null)
            {
                return;
            }

            IReadOnlyList<string> skills = BuildSkillLines();
            if (skills.Count > 0)
            {
                foreach (string line in skills)
                {
                    LogWrite.EncryptedLine(writer, line);
                }
            }

            if (!string.IsNullOrEmpty(separatorAfter))
            {
                LogWrite.EncryptedLine(writer, separatorAfter);
            }

        }

        public static void WriteDamageInvSection(StreamWriter writer, IEnumerable<string> logs, string separatorAfter = "---------------------------------------------------")
        {
            if (writer == null)
            {
                return;
            }

            LogWrite.EncryptedLine(writer, "\n------------------------DAMAGE INV------------------------\n");

            if (logs != null)
            {
                foreach (string log in logs)
                {
                    LogWrite.EncryptedLine(writer, log);
                }
            }

            if (!string.IsNullOrEmpty(separatorAfter))
            {
                LogWrite.EncryptedLine(writer, separatorAfter);
            }

        }

        public static void WriteDamageInvSection(StreamWriter writer, BufferedLogSection logs, string separatorAfter = "---------------------------------------------------")
        {
            if (writer == null)
            {
                return;
            }

            LogWrite.EncryptedLine(writer, "\n------------------------DAMAGE INV------------------------\n");
            logs?.WriteEncryptedLines(writer);

            if (!string.IsNullOrEmpty(separatorAfter))
            {
                LogWrite.EncryptedLine(writer, separatorAfter);
            }

        }

        public static void WriteSeparator(StreamWriter writer, string separator = "---------------------------------------------------")
        {
            if (writer == null || string.IsNullOrEmpty(separator))
            {
                return;
            }

            LogWrite.EncryptedLine(writer, separator);
        }

        public static void WriteNoBlurSettings(StreamWriter writer, string separator = "---------------------------------------------------")
        {
            if (writer == null)
            {
                return;
            }

            IReadOnlyList<string> noBlurSettings = NoBlurIntegration.GetSettingsLines();
            if (noBlurSettings.Count > 0)
            {
                foreach (string line in noBlurSettings)
                {
                    LogWrite.EncryptedLine(writer, line);
                }

                LogWrite.EncryptedLine(writer, string.Empty);
            }

            if (!string.IsNullOrEmpty(separator) && noBlurSettings.Count > 0)
            {
                LogWrite.EncryptedLine(writer, separator);
            }
        }

        public static void WriteCustomizableAbilitiesSettings(StreamWriter writer, string separator = "---------------------------------------------------")
        {
            if (writer == null)
            {
                return;
            }

            IReadOnlyList<string> caSettings = CustomizableAbilitiesIntegration.GetSettingsLines();
            if (caSettings.Count > 0)
            {
                foreach (string line in caSettings)
                {
                    LogWrite.EncryptedLine(writer, line);
                }

                LogWrite.EncryptedLine(writer, string.Empty);
            }

            if (!string.IsNullOrEmpty(separator) && caSettings.Count > 0)
            {
                LogWrite.EncryptedLine(writer, separator);
            }
        }

        public static void WriteControlSettings(StreamWriter writer, string separator = "---------------------------------------------------")
        {
            if (writer == null)
            {
                return;
            }

            LogWrite.EncryptedLine(writer, "CONTROL:");

            IReadOnlyList<string> lines = BuildControlLines();
            if (lines.Count == 0)
            {
                LogWrite.EncryptedLine(writer, "  (unavailable)");
            }
            else
            {
                foreach (string line in lines)
                {
                    LogWrite.EncryptedLine(writer, line);
                }
            }

            LogWrite.EncryptedLine(writer, string.Empty);
            if (!string.IsNullOrEmpty(separator))
            {
                LogWrite.EncryptedLine(writer, separator);
            }
        }

        private static IReadOnlyList<string> BuildControlLines()
        {
            List<string> lines = new();

            try
            {
                GameManager gm = GameManager.instance;
                if (gm == null)
                {
                    return lines;
                }

                object inputHandler = gm.inputHandler;
                if (inputHandler == null)
                {
                    return lines;
                }

                object inputActions = GetMemberValue(inputHandler, "inputActions");
                if (inputActions == null)
                {
                    return lines;
                }

                (string Label, string Member)[] bindings =
                {
                    ("Up", "up"),
                    ("Down", "down"),
                    ("Left", "left"),
                    ("Right", "right"),
                    ("Jump", "jump"),
                    ("Attack", "attack"),
                    ("Dash", "dash"),
                    ("Focus/Cast", "castSpell"),
                    ("Focus/Cast", "cast"),
                    ("Quick Map", "quickMap"),
                    ("Super Dash", "superDash"),
                    ("Dream Nail", "dreamNail"),
                    ("Quick Cast", "quickCast"),
                    ("Inventory", "inventory"),
                    ("Inventory", "openInventory")
                };

                foreach ((string label, string member) in bindings)
                {
                    if (TryDescribeBinding(inputActions, member, out string bindingText))
                    {
                        lines.Add($"{label}: {bindingText}");
                    }
                }
            }
            catch
            {
                
            }

            return lines;
        }

        private static bool TryDescribeBinding(object inputActions, string memberName, out string description)
        {
            description = null;
            if (inputActions == null || string.IsNullOrEmpty(memberName))
            {
                return false;
            }

            object action = GetMemberValue(inputActions, memberName);
            if (action == null)
            {
                return false;
            }

            try
            {
                
                object bindingsObj = GetMemberValue(action, "Bindings");
                if (bindingsObj is System.Collections.IEnumerable enumerable)
                {
                    List<string> parts = new();
                    foreach (object binding in enumerable)
                    {
                        if (binding == null)
                        {
                            continue;
                        }

                        if (TryFormatBinding(binding, out string formatted))
                        {
                            parts.Add(formatted);
                        }
                    }

                    if (parts.Count > 0)
                    {
                        description = string.Join(" / ", parts);
                        return true;
                    }
                }
            }
            catch
            {
                
            }

            if (TryFormatBinding(action, out string fallbackFormatted))
            {
                description = fallbackFormatted;
                return true;
            }

            return false;
        }

        private static bool TryFormatBinding(object binding, out string formatted)
        {
            formatted = null;
            if (binding == null)
            {
                return false;
            }

            Type bindingType = binding.GetType();

            if (string.Equals(bindingType.FullName, "InControl.KeyBindingSource", StringComparison.Ordinal))
            {
                if (TryGetStringMember(binding, "Name", out string name) ||
                    TryGetStringMethod(binding, "ToString", out name))
                {
                    formatted = name;
                    return true;
                }

                object key = GetMemberValue(binding, "Key");
                if (key != null)
                {
                    formatted = key.ToString();
                    return true;
                }
            }

            if (string.Equals(bindingType.FullName, "InControl.DeviceBindingSource", StringComparison.Ordinal))
            {
                if (TryGetStringMember(binding, "Name", out string name) ||
                    TryGetStringMethod(binding, "ToString", out name))
                {
                    formatted = name;
                    return true;
                }

                object control = GetMemberValue(binding, "Control");
                object device = GetMemberValue(binding, "DeviceName");
                if (control != null)
                {
                    string ctrl = control.ToString();
                    string dev = device?.ToString();
                    formatted = string.IsNullOrEmpty(dev) ? ctrl : $"{dev}:{ctrl}";
                    return true;
                }
            }

            string fallback = binding.ToString();
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                formatted = fallback.Trim();
                return true;
            }

            return false;
        }

        private static object GetMemberValue(object instance, string memberName)
        {
            if (instance == null || string.IsNullOrEmpty(memberName))
            {
                return null;
            }

            Type type = instance.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

            PropertyInfo prop = type.GetProperty(memberName, flags);
            if (prop != null)
            {
                try
                {
                    return prop.GetValue(instance);
                }
                catch
                {
                    
                }
            }

            FieldInfo field = type.GetField(memberName, flags);
            if (field != null)
            {
                try
                {
                    return field.GetValue(instance);
                }
                catch
                {
                    
                }
            }

            return null;
        }

        private static bool TryGetStringMember(object instance, string memberName, out string value)
        {
            value = null;
            object raw = GetMemberValue(instance, memberName);
            if (raw == null)
            {
                return false;
            }

            string str = raw.ToString();
            if (string.IsNullOrWhiteSpace(str))
            {
                return false;
            }

            value = str.Trim();
            return true;
        }

        private static bool TryGetStringMethod(object instance, string methodName, out string value)
        {
            value = null;
            if (instance == null || string.IsNullOrEmpty(methodName))
            {
                return false;
            }

            try
            {
                MethodInfo method = instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
                if (method != null && method.GetParameters().Length == 0)
                {
                    object result = method.Invoke(instance, null);
                    string str = result?.ToString();
                    if (!string.IsNullOrWhiteSpace(str))
                    {
                        value = str.Trim();
                        return true;
                    }
                }
            }
            catch
            {
                
            }

            return false;
        }

        public static void WriteWarningsSection(StreamWriter writer, IEnumerable<string> warnings, string separatorAfter = "---------------------------------------------------")
        {
            if (writer == null)
            {
                return;
            }

            LogWrite.EncryptedLine(writer, "Warnings:");
            if (warnings != null)
            {
                foreach (string warning in warnings)
                {
                    LogWrite.EncryptedLine(writer, warning);
                }
            }

            if (!string.IsNullOrEmpty(separatorAfter))
            {
                LogWrite.EncryptedLine(writer, separatorAfter);
            }

        }

        public static void WriteWarningsSection(StreamWriter writer, BufferedLogSection warnings, string separatorAfter = "---------------------------------------------------")
        {
            if (writer == null)
            {
                return;
            }

            LogWrite.EncryptedLine(writer, "Warnings:");
            warnings?.WriteEncryptedLines(writer);

            if (!string.IsNullOrEmpty(separatorAfter))
            {
                LogWrite.EncryptedLine(writer, separatorAfter);
            }

        }

        public static void WriteSpeedWarningsSection(StreamWriter writer, IEnumerable<string> warnings, string separatorAfter = "---------------------------------------------------")
        {
            if (writer == null)
            {
                return;
            }

            LogWrite.EncryptedLine(writer, "SpeedWarn:");
            if (warnings != null)
            {
                foreach (string warning in warnings)
                {
                    LogWrite.EncryptedLine(writer, warning);
                }
            }

            if (!string.IsNullOrEmpty(separatorAfter))
            {
                LogWrite.EncryptedLine(writer, separatorAfter);
            }

        }

        public static void WriteSpeedWarningsSection(StreamWriter writer, BufferedLogSection warnings, string separatorAfter = "---------------------------------------------------")
        {
            if (writer == null)
            {
                return;
            }

            LogWrite.EncryptedLine(writer, "SpeedWarn:");
            warnings?.WriteEncryptedLines(writer);

            if (!string.IsNullOrEmpty(separatorAfter))
            {
                LogWrite.EncryptedLine(writer, separatorAfter);
            }

        }

        public static void AddSpeedWarning(List<string> buffer, string arenaName, long deltaMs, float defaultScale, float currentScale, double durationSeconds)
        {
            if (buffer == null)
            {
                return;
            }

            arenaName ??= "UnknownArena";
            string warnEntry = $"|{arenaName}|+{deltaMs}|Default {(defaultScale * 100f):F0}% ({defaultScale:F3}) -> {(currentScale * 100f):F0}% ({currentScale:F3})|Duration {durationSeconds.ToString("F2", CultureInfo.InvariantCulture)}s";
            buffer.Add(warnEntry);
        }

        private static int GetCharmCost(int charmId)
        {
            PlayerData data = PlayerData.instance;
            if (data == null)
            {
                return -1;
            }

            try
            {
                return charmId switch
                {
                    36 => data.charmCost_36,
                    40 => data.charmCost_40,
                    _ => data.GetIntInternal($"charmCost_{charmId}")
                };
            }
            catch
            {
                return -1;
            }
        }

        private static string GetCharmDisplayName(int charmId)
        {
            if (CustomCharmDisplayNames.TryGetValue(charmId, out string friendlyName))
            {
                return friendlyName;
            }

            return ((Charm)charmId).ToString();
        }
    }

    public sealed class SpeedWarnTracker
    {
        private const float TimeScaleTolerance = 0.001f;
        private readonly List<string> warnings = new();

        private float lastLoggedTimeScale = 1f;
        private float defaultTimeScale = 1f;
        private bool speedDeviationActive;
        private bool speedWarningIssued;
        private long speedDeviationStartUnix;
        private float deviationReferenceTimeScale = 1f;

        public IReadOnlyList<string> Warnings => warnings;

        public void Reset(float currentScale)
        {
            float clamped = Mathf.Max(currentScale, 0f);
            lastLoggedTimeScale = clamped;
            defaultTimeScale = clamped;
            ResetDeviationTracking();
        }

        public void ClearWarnings() => warnings.Clear();

        public void LogInitial(StreamWriter writer, long lastUnixTime)
        {
            WriteTimeScaleChange(writer, lastUnixTime, lastLoggedTimeScale, true);
        }

        public void Update(StreamWriter writer, string arenaName, long lastUnixTime)
        {
            float currentScale = Mathf.Max(Time.timeScale, 0f);
            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            if (Mathf.Abs(currentScale - defaultTimeScale) < TimeScaleTolerance)
            {
                ResetDeviationTracking();
            }
            else
            {
                if (!speedDeviationActive || Mathf.Abs(currentScale - deviationReferenceTimeScale) >= TimeScaleTolerance)
                {
                    speedDeviationActive = true;
                    speedWarningIssued = false;
                    speedDeviationStartUnix = now;
                    deviationReferenceTimeScale = currentScale;
                }
                else if (!speedWarningIssued && now - speedDeviationStartUnix >= 3000)
                {
                    LogSpeedWarning(writer, arenaName, lastUnixTime, currentScale, now);
                    speedWarningIssued = true;
                }
            }

            if (Mathf.Abs(currentScale - lastLoggedTimeScale) >= TimeScaleTolerance)
            {
                lastLoggedTimeScale = currentScale;
                WriteTimeScaleChange(writer, lastUnixTime, currentScale, false);
            }
        }

        private void WriteTimeScaleChange(StreamWriter writer, long lastUnixTime, float scale, bool initial)
        {
            if (writer == null || !initial)
            {
                return;
            }

            long unixTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            string label = initial ? "GameSpeedStart" : "GameSpeedChange";
            string entry = $"{label}|+{unixTime - lastUnixTime}|{(scale * 100f):F0}% ({scale:F3})";
            LogWrite.EncryptedLine(writer, entry);
        }

        private void LogSpeedWarning(StreamWriter writer, string arenaName, long lastUnixTime, float currentScale, long now)
        {
            if (writer == null)
            {
                return;
            }

            double durationSeconds = (now - speedDeviationStartUnix) / 1000.0;
            string entry = $"SpeedWarn|+{now - lastUnixTime}|Default {(defaultTimeScale * 100f):F0}% ({defaultTimeScale:F3}) -> {(currentScale * 100f):F0}% ({currentScale:F3})|Duration {durationSeconds.ToString("F2", CultureInfo.InvariantCulture)}s";
            LogWrite.EncryptedLine(writer, entry);

            CoreSessionLogger.AddSpeedWarning(warnings, arenaName, now - lastUnixTime, defaultTimeScale, currentScale, durationSeconds);
        }

        private void ResetDeviationTracking()
        {
            speedDeviationActive = false;
            speedWarningIssued = false;
            speedDeviationStartUnix = 0;
            deviationReferenceTimeScale = defaultTimeScale;
        }
    }

    public sealed class HitWarnTracker
    {
        private readonly List<string> warnings = new();
        private int? lastHealth;
        private int? lastLifeblood;

        public IReadOnlyList<string> Warnings => warnings;

        public void Reset()
        {
            warnings.Clear();
            lastHealth = null;
            lastLifeblood = null;
        }

        public void ClearWarnings() => warnings.Clear();

        public void Update(StreamWriter writer, string arenaName, long lastUnixTime)
        {
            if (writer == null)
            {
                return;
            }

            PlayerData data = PlayerData.instance;
            if (data == null)
            {
                return;
            }

            TrackMasks(writer, arenaName, lastUnixTime, data.health);
            TrackLifeblood(writer, arenaName, lastUnixTime, Mathf.Max(0, data.healthBlue));
        }

        private void TrackMasks(StreamWriter writer, string arenaName, long lastUnixTime, int currentHealth)
        {
            if (!lastHealth.HasValue)
            {
                lastHealth = currentHealth;
                return;
            }

            if (currentHealth < lastHealth.Value)
            {
                int lost = lastHealth.Value - currentHealth;
                LogMaskChange(writer, arenaName, lastUnixTime, lastHealth.Value, currentHealth, -lost);
            }
            else if (currentHealth > lastHealth.Value)
            {
                int gained = currentHealth - lastHealth.Value;
                LogMaskChange(writer, arenaName, lastUnixTime, lastHealth.Value, currentHealth, gained);
            }

            lastHealth = currentHealth;
        }

        private void TrackLifeblood(StreamWriter writer, string arenaName, long lastUnixTime, int currentLifeblood)
        {
            if (!lastLifeblood.HasValue)
            {
                lastLifeblood = currentLifeblood;
                return;
            }

            if (currentLifeblood < lastLifeblood.Value)
            {
                int lostBlue = lastLifeblood.Value - currentLifeblood;
                LogLifebloodChange(writer, arenaName, lastUnixTime, lastLifeblood.Value, currentLifeblood, -lostBlue);
            }
            else if (currentLifeblood > lastLifeblood.Value)
            {
                int gainedBlue = currentLifeblood - lastLifeblood.Value;
                LogLifebloodChange(writer, arenaName, lastUnixTime, lastLifeblood.Value, currentLifeblood, gainedBlue);
            }

            lastLifeblood = currentLifeblood;
        }

        private void LogMaskChange(StreamWriter writer, string arenaName, long lastUnixTime, int prev, int current, int delta)
        {
            long unixTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            string sign = delta >= 0 ? "+" : string.Empty;
            string entry = delta >= 0
                ? $"Heal|+{unixTime - lastUnixTime}|{prev}->{current}|{sign}{delta} mask(s)"
                : $"HitWarn|+{unixTime - lastUnixTime}|{prev}->{current}|{sign}{delta} mask(s)";

            LogWrite.EncryptedLine(writer, entry);

            string arena = string.IsNullOrEmpty(arenaName) ? "UnknownArena" : arenaName;
            string warnEntry = $"|{arena}|+{unixTime - lastUnixTime}|{prev}->{current}|{sign}{delta} mask(s)";
            warnings.Add(warnEntry);
        }

        private void LogLifebloodChange(StreamWriter writer, string arenaName, long lastUnixTime, int prev, int current, int delta)
        {
            long unixTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            string sign = delta >= 0 ? "+" : string.Empty;
            string entry = delta >= 0
                ? $"Heal|Lifeblood|+{unixTime - lastUnixTime}|{prev}->{current}|{sign}{delta}"
                : $"HitWarn|Lifeblood|+{unixTime - lastUnixTime}|{prev}->{current}|{sign}{delta} lifeblood";

            LogWrite.EncryptedLine(writer, entry);

            string arena = string.IsNullOrEmpty(arenaName) ? "UnknownArena" : arenaName;
            string warnEntry = $"|{arena}|+{unixTime - lastUnixTime}|Lifeblood {prev}->{current}|{sign}{delta}";
            warnings.Add(warnEntry);
        }
    }

    public sealed class DamageChangeTracker
    {
        private readonly Dictionary<string, HashSet<int>> damageValuesByOwner = new();
        private readonly Dictionary<string, HashSet<float>> multiplierValuesByOwner = new();
        private readonly List<string> changes = new();

        public IReadOnlyList<string> Changes => changes;

        public void Reset()
        {
            damageValuesByOwner.Clear();
            multiplierValuesByOwner.Clear();
            changes.Clear();
        }

        public void Track(string ownerName, string sceneName, long deltaMs, int damageDealt, float multiplier)
        {
            if (string.IsNullOrEmpty(ownerName))
            {
                return;
            }

            if (!damageValuesByOwner.TryGetValue(ownerName, out HashSet<int> damages))
            {
                damages = new HashSet<int>();
                damageValuesByOwner[ownerName] = damages;
            }

            if (!multiplierValuesByOwner.TryGetValue(ownerName, out HashSet<float> multipliers))
            {
                multipliers = new HashSet<float>();
                multiplierValuesByOwner[ownerName] = multipliers;
            }

            bool newDamage = damages.Add(damageDealt);
            if (newDamage)
            {
                changes.Add($"Add NEW unique damage: {ownerName}-{sceneName}/{deltaMs} #{damageDealt}");
            }

            bool newMultiplier = multipliers.Add(multiplier);
            if (newMultiplier)
            {
                changes.Add($"Add NEW unique multiplier: {ownerName}-{sceneName}/{deltaMs} #{multiplier}");
            }
        }

        public static Dictionary<string, List<string>> SortLogsByObjectName(IEnumerable<string> logs)
        {
            Dictionary<string, List<string>> sortedLogs = new(StringComparer.Ordinal);
            if (logs == null)
            {
                return sortedLogs;
            }

            foreach (string log in logs)
            {
                string objectName = ExtractObjectName(log);
                if (objectName == null)
                {
                    continue;
                }

                if (!sortedLogs.TryGetValue(objectName, out List<string> list))
                {
                    list = new List<string>();
                    sortedLogs[objectName] = list;
                }
                list.Add(log);
            }

            return sortedLogs;
        }

        public static void WriteSection(StreamWriter writer, DamageChangeTracker tracker)
        {
            if (writer == null || tracker == null)
            {
                return;
            }

            LogWrite.EncryptedLine(writer, "DamageChange:");
            foreach (var entry in SortLogsByObjectName(tracker.Changes))
            {
                LogWrite.EncryptedLine(writer, $"{entry.Key}:");
                foreach (string log in entry.Value)
                {
                    LogWrite.EncryptedLine(writer, $"  {log}");
                }
                LogWrite.EncryptedLine(writer, "\n");
            }

            LogWrite.EncryptedLine(writer, "---------------------------------------------------");
        }

        private static string ExtractObjectName(string log)
        {
            string pattern = @"^Add NEW unique (?:damage|multiplier): (.*?)-";
            Match match = Regex.Match(log, pattern);

            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }
            return null;
        }
    }

    public sealed class FlukenestTracker
    {
        private readonly List<string> entries = new();
        private int? lastDamage;
        private readonly FieldInfo flukeDamageField =
            typeof(SpellFluke).GetField("damage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        public IReadOnlyList<string> Entries => entries;

        public void Reset()
        {
            entries.Clear();
            lastDamage = null;
        }

        public void Track(GameObject target, string arenaName, long deltaMs, SpellFluke self)
        {
            if (self == null || flukeDamageField == null)
            {
                return;
            }

            int damage = 0;
            try
            {
                object raw = flukeDamageField.GetValue(self);
                if (raw is int intDamage)
                {
                    damage = intDamage;
                }
            }
            catch
            {
                
            }

            if (lastDamage.HasValue && lastDamage.Value == damage)
            {
                return;
            }

            lastDamage = damage;
            string targetName = target != null ? target.name : "null";
            string arena = string.IsNullOrEmpty(arenaName) ? "UnknownArena" : arenaName;
            entries.Add($"Flukenest: {targetName}-{arena}/{deltaMs} #{damage}");
        }

        public static void WriteSection(StreamWriter writer, FlukenestTracker tracker)
        {
            if (writer == null || tracker == null)
            {
                return;
            }

            LogWrite.EncryptedLine(writer, "Flukenest:");
            if (tracker.Entries.Count == 0)
            {
                LogWrite.EncryptedLine(writer, "  (none)");
            }
            else
            {
                foreach (string log in tracker.Entries)
                {
                    LogWrite.EncryptedLine(writer, log);
                }
            }
            LogWrite.EncryptedLine(writer, "\n");
        }

        public static void WriteSectionWithSeparator(StreamWriter writer, FlukenestTracker tracker, string separator = "---------------------------------------------------")
        {
            if (writer == null || tracker == null)
            {
                return;
            }

            WriteSection(writer, tracker);
            if (!string.IsNullOrEmpty(separator))
            {
                LogWrite.EncryptedLine(writer, separator);
            }
        }

        public static void TrackGlobal(bool isLogging, StreamWriter writer, FlukenestTracker tracker, string arenaName, long lastUnixTime, SpellFluke self, GameObject target)
        {
            if (!isLogging || writer == null || tracker == null)
            {
                return;
            }

            long unixTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            tracker.Track(target, arenaName, unixTime - lastUnixTime, self);
        }

        public static void HandleDoDamage(bool isLogging, StreamWriter writer, FlukenestTracker tracker, string arenaName, long lastUnixTime, On.SpellFluke.orig_DoDamage orig, SpellFluke self, GameObject obj, int upwardRecursionAmount, bool burst)
        {
            try
            {
                TrackGlobal(isLogging, writer, tracker, arenaName, lastUnixTime, self, obj);
            }
            catch (Exception e)
            {
                Modding.Logger.LogWarn($"ReplayLogger: failed to log Flukenest damage: {e.Message}");
            }

            orig(self, obj, upwardRecursionAmount, burst);
        }
    }
}
