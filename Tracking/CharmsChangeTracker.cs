using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReplayLogger
{
    internal sealed class CharmsChangeTracker
    {
        private readonly HashSet<int> equipped = new();
        private readonly List<string> changes = new();
        private readonly List<string> inlineEvents = new();

        public IReadOnlyList<string> Changes => changes;
        public IReadOnlyList<string> InlineEvents => inlineEvents;

        public void Reset()
        {
            equipped.Clear();
            changes.Clear();
            inlineEvents.Clear();
            SnapshotCurrent();
        }

        public void Update(string arenaName, long lastUnixTime, StreamWriter writer = null)
        {
            if (PlayerData.instance?.equippedCharms == null)
            {
                return;
            }

            HashSet<int> current = new(PlayerData.instance.equippedCharms);

            foreach (int charm in current)
            {
                if (!equipped.Contains(charm))
                {
                    long delta = DateTimeOffset.Now.ToUnixTimeMilliseconds() - lastUnixTime;
                    changes.Add($"|{arenaName ?? "UnknownArena"}|+{delta}|Equipped {FormatCharm(charm)}");
                    LogInline(writer, arenaName, delta, $"Equipped {FormatCharm(charm)}");
                }
            }

            foreach (int charm in equipped)
            {
                if (!current.Contains(charm))
                {
                    long delta = DateTimeOffset.Now.ToUnixTimeMilliseconds() - lastUnixTime;
                    changes.Add($"|{arenaName ?? "UnknownArena"}|+{delta}|Unequipped {FormatCharm(charm)}");
                    LogInline(writer, arenaName, delta, $"Unequipped {FormatCharm(charm)}");
                }
            }

            equipped.Clear();
            foreach (int c in current)
            {
                equipped.Add(c);
            }
        }

        public void Write(StreamWriter writer, string separator = "---------------------------------------------------")
        {
            if (writer == null)
            {
                return;
            }

            LogWrite.EncryptedLine(writer, "Charms:");
            if (changes.Count == 0)
            {
                LogWrite.EncryptedLine(writer, "  (no changes)");
            }
            else
            {
                foreach (string entry in changes)
                {
                    LogWrite.EncryptedLine(writer, entry);
                }
            }

            LogWrite.EncryptedLine(writer, string.Empty);
            if (!string.IsNullOrEmpty(separator))
            {
                LogWrite.EncryptedLine(writer, separator);
            }
        }

        private void SnapshotCurrent()
        {
            equipped.Clear();
            if (PlayerData.instance?.equippedCharms != null)
            {
                foreach (int c in PlayerData.instance.equippedCharms)
                {
                    equipped.Add(c);
                }
            }
        }

        private void LogInline(StreamWriter writer, string arenaName, long delta, string action)
        {
            string arena = string.IsNullOrEmpty(arenaName) ? "UnknownArena" : arenaName;
            string entry = $"Charms|{arena}|+{delta}|{action}";
            inlineEvents.Add(entry);
            if (writer != null)
            {
                LogWrite.EncryptedLine(writer, entry);
            }
        }

        private static string FormatCharm(int charmId)
        {
            try
            {
                return ((Charm)charmId).ToString();
            }
            catch
            {
                return $"Charm {charmId}";
            }
        }
    }
}
