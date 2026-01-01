using System;
using System.Collections.Generic;
using System.IO;

namespace ReplayLogger
{
    internal sealed class GodhomeQolTracker
    {
        private readonly GodhomeQolCollectorPhasesTracker collectorPhases = new();
        private readonly FastSuperDashTracker fastSuperDash = new();
        private readonly DreamshieldSettingsTracker dreamshieldSettings = new();
        private readonly CarefreeMelodyResetTracker carefreeMelodyReset = new();
        private readonly BossChallengeSettingsTracker bossChallengeSettings = new();
        private long lastUpdateTime;
        private const int UpdateThrottleMs = 1000;

        public void Reset()
        {
            collectorPhases.Reset();
            fastSuperDash.Reset();
            dreamshieldSettings.Reset();
            carefreeMelodyReset.Reset();
            bossChallengeSettings.Reset();
            lastUpdateTime = 0;
        }

        public void StartFight(string arenaName, long baseUnixTime)
        {
            collectorPhases.StartFight(arenaName, baseUnixTime);
            fastSuperDash.StartFight(arenaName, baseUnixTime);
            dreamshieldSettings.StartFight(arenaName, baseUnixTime);
            carefreeMelodyReset.StartFight(arenaName, baseUnixTime);
            bossChallengeSettings.StartFight(arenaName, baseUnixTime);
            lastUpdateTime = 0;
        }

        public void Update(string arenaName)
        {
            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            if (lastUpdateTime > 0 && now - lastUpdateTime < UpdateThrottleMs)
            {
                return;
            }

            lastUpdateTime = now;
            collectorPhases.Update(arenaName);
            fastSuperDash.Update(arenaName);
            dreamshieldSettings.Update(arenaName);
            carefreeMelodyReset.Update(arenaName);
            bossChallengeSettings.Update(arenaName);
        }

        public void WriteSection(StreamWriter writer, string separator = "---------------------------------------------------")
        {
            if (writer == null)
            {
                return;
            }

            if (!collectorPhases.HasData && !fastSuperDash.HasData && !dreamshieldSettings.HasData && !carefreeMelodyReset.HasData && !bossChallengeSettings.HasData)
            {
                return;
            }

            LogWrite.EncryptedLine(writer, "GodhomeQoL:");
            string blockSeparator = string.IsNullOrEmpty(separator) ? "---------------------------------------------------" : separator;
            int blocksWritten = 0;

            if (collectorPhases.HasData)
            {
                collectorPhases.WriteSection(writer);
                blocksWritten++;
            }

            if (fastSuperDash.HasData)
            {
                if (blocksWritten > 0)
                {
                    LogWrite.EncryptedLine(writer, blockSeparator);
                }
                fastSuperDash.WriteSection(writer);
                blocksWritten++;
            }

            if (dreamshieldSettings.HasData)
            {
                if (blocksWritten > 0)
                {
                    LogWrite.EncryptedLine(writer, blockSeparator);
                }
                dreamshieldSettings.WriteSection(writer);
                blocksWritten++;
            }

            if (carefreeMelodyReset.HasData)
            {
                if (blocksWritten > 0)
                {
                    LogWrite.EncryptedLine(writer, blockSeparator);
                }
                carefreeMelodyReset.WriteSection(writer);
                blocksWritten++;
            }

            if (bossChallengeSettings.HasData)
            {
                if (blocksWritten > 0)
                {
                    LogWrite.EncryptedLine(writer, blockSeparator);
                }
                bossChallengeSettings.WriteSection(writer);
                blocksWritten++;
            }

            LogWrite.EncryptedLine(writer, string.Empty);
            if (!string.IsNullOrEmpty(separator))
            {
                LogWrite.EncryptedLine(writer, separator);
            }
        }
    }
}
