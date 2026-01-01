using GlobalEnums;
using IL;
using Modding;
using On;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using UnityEngine;
using MonoMod.RuntimeDetour;
using UObject = UnityEngine.Object;

namespace ReplayLogger
{
    public partial class ReplayLogger : Mod, ICustomMenuMod, IGlobalSettings<ReplayLoggerSettings>
    {
        internal static ReplayLogger Instance;

        internal CustomCanvas customCanvas;
        private string dllDir;
        private string modsDir;

        private StreamWriter writer;
        private string lastString;
        private string lastScene;
        private (string name, List<string> list) currentPanteon;

        private long lastUnixTime;
        private long startUnixTime;

        private bool isPlayChalange = false;
        private int bossCounter;

        private const int LogQueueCapacity = 20000;
        private const int BufferedSectionThreshold = 200;
        private BufferedLogSection DamageAnfInv;
        private BufferedLogSection InvWarn;
        private BufferedLogSection speedWarnBuffer;
        private BufferedLogSection hitWarnBuffer;
        private readonly List<string> keyLogBuffer = new();
        private const int KeyLogFlushIntervalMs = 200;
        private const int KeyLogFlushBatchSize = 50;
        private long lastKeyLogFlushTime;

        private readonly SpeedWarnTracker speedWarnTracker = new();
        private readonly HitWarnTracker hitWarnTracker = new();
        private readonly FlukenestTracker flukenestTracker = new();

        private readonly DamageChangeTracker damageChangeTracker = new();
        private readonly List<string> debugModEvents = new();
        private readonly DebugModEventsTracker debugModEventsTracker = new();
        private readonly DebugHotkeysTracker debugHotkeysTracker = new();
        private readonly DebugMenuTracker debugMenuTracker = new();
        private readonly CharmsChangeTracker charmsChangeTracker = new();
        private readonly GodhomeQolTracker godhomeQolTracker = new();
        private static Hook debugKillAllHook;
        private static Hook debugKillSelfHook;

        public ReplayLogger() : base(ModInfo.Name) { }
        public override string GetVersion() => ModInfo.Version;

        public override void Initialize()
        {
            Instance = this;
            On.SceneLoad.Begin += OpenFile;
            On.GameManager.Update += CheckPressedKey;
            ModHooks.ApplicationQuitHook += Close;
            On.QuitToMenu.Start += QuitToMenu_Start;
            On.BossSceneController.Update += BossSceneController_Update;
            On.HeroController.FixedUpdate += HeroController_FixedUpdate;
            On.SceneLoad.RecordEndTime += SceneLoad_RecordEndTime;
            On.SpellFluke.DoDamage += SpellFluke_DoDamage;

            ModHooks.HitInstanceHook += ModHooks_HitInstanceHook;
            ModHooks.AfterSavegameLoadHook += OnAfterSavegameLoad;

            On.BossSequenceController.FinishLastBossScene += BossSequenceController_FinishLastBossScene;

            dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            modsDir = new DirectoryInfo(dllDir).Parent.FullName;

            ModsChecking.PrimeHeavyModCache(modsDir);


            lastString = KeyloggerLogEncryption.GenerateKeyAndIV();

            CustomCanvas.flagSpriteFalse = CustomCanvas.LoadEmbeddedSprite("Geo.png");
            CustomCanvas.flagSpriteTrue = CustomCanvas.LoadEmbeddedSprite("ElegantKey.png");

            DamageAnfInv = null;
            InvWarn = null;
            debugModEventsTracker.Reset();
            debugHotkeysTracker.InitializeBindings();

            InitializeHotkeys();
            InitializeRebindListener();
        }

        private void OnAfterSavegameLoad(SaveGameData _)
        {
            HardwareFingerprint.Prime();
        }

        string isСhallengeСompleted = "-";
        private void BossSequenceController_FinishLastBossScene(On.BossSequenceController.orig_FinishLastBossScene orig, BossSceneController self)
        {
            isСhallengeСompleted = "+";
            orig(self);
        }



        private HitInstance ModHooks_HitInstanceHook(HutongGames.PlayMaker.Fsm owner, HitInstance hit)
        {
            long unixTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            string ownerName = owner.GameObject.GetFullPath();

            if (owner == null || owner.GameObject == null)
            {
                return hit;
            }

            damageChangeTracker.Track(ownerName, lastScene, unixTime - lastUnixTime, hit.DamageDealt, hit.Multiplier);

            return hit;


        }

        private void SceneLoad_RecordEndTime(On.SceneLoad.orig_RecordEndTime orig, SceneLoad self, SceneLoad.Phases phase)
        {
            orig(self, phase);
            if (phase == SceneLoad.Phases.UnloadUnusedAssets)
            {
                Self_Finish();
            }
        }

        private void Self_Finish()
        {
            if (!isPlayChalange) return;
            infoBoss.Clear();
        }

        private void HeroController_FixedUpdate(On.HeroController.orig_FixedUpdate orig, HeroController self)
        {
            InvCheck();
            orig(self);
        }

        private static void InitializeDebugModHooks()
        {
            if (debugKillAllHook != null && debugKillSelfHook != null)
            {
                return;
            }

            try
            {
                Type bindableType = FindType("DebugMod.BindableFunctions");
                if (bindableType == null)
                {
                    return;
                }

                if (debugKillAllHook == null)
                {
                    MethodInfo killAll = bindableType.GetMethod("KillAll", BindingFlags.Public | BindingFlags.Static);
                    if (killAll != null)
                    {
                        debugKillAllHook = new Hook(killAll, typeof(ReplayLogger).GetMethod(nameof(DebugKillAllDetour), BindingFlags.NonPublic | BindingFlags.Static));
                    }
                }

                if (debugKillSelfHook == null)
                {
                    MethodInfo killSelf = bindableType.GetMethod("KillSelf", BindingFlags.Public | BindingFlags.Static);
                    if (killSelf != null)
                    {
                        debugKillSelfHook = new Hook(killSelf, typeof(ReplayLogger).GetMethod(nameof(DebugKillSelfDetour), BindingFlags.NonPublic | BindingFlags.Static));
                    }
                }
            }
            catch (Exception e)
            {
                Modding.Logger.LogWarn($"ReplayLogger: failed to hook DebugMod functions (pantheon): {e.Message}");
            }
        }

        private static void DisposeDebugModHooks()
        {
            try
            {
                debugKillAllHook?.Dispose();
            }
            catch { }

            try
            {
                debugKillSelfHook?.Dispose();
            }
            catch { }

            debugKillAllHook = null;
            debugKillSelfHook = null;
        }

        private static void DebugKillAllDetour(Action orig)
        {
            orig();
            ReplayLogger logger = Instance;
            if (logger != null && logger.isPlayChalange && logger.writer != null)
            {
                logger.debugMenuTracker.LogManualChange(logger.writer, logger.lastScene, logger.lastUnixTime, "Cheats/Kill All", null, "Executed");
            }
        }

        private static void DebugKillSelfDetour(Action orig)
        {
            orig();
            ReplayLogger logger = Instance;
            if (logger != null && logger.isPlayChalange && logger.writer != null)
            {
                logger.debugMenuTracker.LogManualChange(logger.writer, logger.lastScene, logger.lastUnixTime, "Cheats/Kill Self", null, "Executed");
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private void MonitorDebugModUi()
        {
            if (!isPlayChalange || writer == null)
            {
                return;
            }

            string arena = lastScene;
            debugModEventsTracker.Update(writer, arena, lastUnixTime);
            debugMenuTracker.Update(writer, arena, lastUnixTime);
            charmsChangeTracker.Update(arena, lastUnixTime, writer);
            godhomeQolTracker.Update(arena);
        }

        Dictionary<HealthManager, (int maxHP, int lastHP)> infoBoss = new();
        bool isInvincible = false;
        float invTimer;

        public void InvCheck()
        {
            if (!isPlayChalange) return;

            bool shouldBeInvincible =
            (HeroController.instance.cState.invulnerable ||
             PlayerData.instance.isInvincible ||
             HeroController.instance.cState.shadowDashing ||
             HeroController.instance.damageMode == DamageMode.HAZARD_ONLY ||
             HeroController.instance.damageMode == DamageMode.NO_DAMAGE);

            var bossList = infoBoss.GetKeysWithUniqueGameObject().Values;


            if (shouldBeInvincible && !isInvincible)
            {
                isInvincible = true;
                invTimer = 0f;

                long unixTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                string hpInfo = "";
                foreach (var kvp in bossList)
                {
                    hpInfo += $"|{infoBoss[kvp].lastHP}/{infoBoss[kvp].maxHP}";
                }
                DamageAnfInv.Add($"\u00A0+{unixTime - lastUnixTime}{hpInfo}|(INV ON)|");
            }

            if (!shouldBeInvincible && isInvincible)
            {
                isInvincible = false;
                long unixTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                string hpInfo = "";
                foreach (var kvp in bossList)
                {
                    hpInfo += $"|{infoBoss[kvp].lastHP}/{infoBoss[kvp].maxHP}";

                }
                DamageAnfInv.Add($"\u00A0+{unixTime - lastUnixTime}{hpInfo}|(INV OFF, {invTimer.ToString("F3")})|");
                if (invTimer > 2.6f)
                {
                    string warning = $"|{lastScene}|+{unixTime - lastUnixTime}{hpInfo}|(INV OFF, {invTimer.ToString("F3")})";

                    InvWarn.Add(warning);
                }
                invTimer = 0f;
            }

            if (isInvincible)
                invTimer += Time.fixedDeltaTime;
        }

        bool isChange;

        public void EnemyUpdate()
        {
            if (!isPlayChalange) return;

            List<HealthManager> healthManagers = new();

            float searchRadius = 100f;
            int enemyLayer = Physics2D.AllLayers;

            Collider2D[] colliders = Physics2D.OverlapBoxAll(HeroController.instance.transform.position, Vector2.one * searchRadius, 0f, enemyLayer);

            foreach (Collider2D collider in colliders)
            {
                GameObject enemyObject = collider.gameObject;

                if (enemyObject.activeInHierarchy)
                {
                    HealthManager healthManager = enemyObject.GetComponent<HealthManager>();

                    if (healthManager != null)
                    {
                        healthManagers.Add(healthManager);
                    }

                }
            }

            if (healthManagers != null || healthManagers.Count > 0)
            {

                foreach (HealthManager healthManager in healthManagers.ToList())
                {
                    if (healthManager != null && healthManager.hp > 0 && !infoBoss.ContainsKey(healthManager))
                    {
                        infoBoss.Add(healthManager, (healthManager.hp, 0));
                    }


                }

            }


            long unixTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            string hpInfo = "";

            var bossKeys = infoBoss.GetKeysWithUniqueGameObject().Values;
            foreach (var boss in infoBoss.Keys)
            {
                if (!bossKeys.Contains(boss) && !boss.isDead) continue;
                if (boss.hp != infoBoss[boss].lastHP)
                {
                    infoBoss[boss] = (infoBoss[boss].maxHP, boss.hp);
                    isChange = true;
                }

                hpInfo += $"|{infoBoss[boss].lastHP}/{infoBoss[boss].maxHP}";
            }

            if (isChange)
            {
                DamageAnfInv.Add($"\u00A0+{unixTime - lastUnixTime}{hpInfo}|");
            }
            isChange = false;

            infoBoss.RemoveAll(kvp => kvp.Key.isDead == true || kvp.Key.hp <= 0);


        }

        private void BossSceneController_Update(On.BossSceneController.orig_Update orig, BossSceneController self)
        {
            EnemyUpdate();
            orig(self);
        }


        private IEnumerator QuitToMenu_Start(On.QuitToMenu.orig_Start orig, QuitToMenu self)
        {
            Close();
            return orig(self);
        }

        private void StartLoad()
        {
            customCanvas?.StartUpdateSprite();
        }

        private string currentNameLog;
        private void OpenFile(On.SceneLoad.orig_Begin orig, SceneLoad self)
        {
            try
            {
                var dataTimeNow = DateTimeOffset.Now;
                lastUnixTime = dataTimeNow.ToUnixTimeMilliseconds();
                var dataTime = dataTimeNow.ToString("dd.MM.yyyy HH:mm:ss.fff");
                if (isPlayChalange && self.TargetSceneName.Contains("GG_End_Seq"))
                {
                    Close();
                }

                if (self.TargetSceneName.Contains("GG_Boss_Door") || (self.TargetSceneName.Contains("GG_Vengefly_V") && lastScene == "GG_Atrium_Roof"))
                {

                    startUnixTime = lastUnixTime;
                    int curentPlayTime = (int)(PlayerData.instance.playTime * 100);
                    isPlayChalange = true;

                    try
                    {
                        lastString = KeyloggerLogEncryption.GenerateKeyAndIV();
                        currentNameLog = Path.Combine(dllDir, $"KeyLog{DateTime.UtcNow.Ticks}.log");
                        DamageAnfInv?.Clear();
                        InvWarn?.Clear();
                        speedWarnBuffer?.Clear();
                        hitWarnBuffer?.Clear();
                        DamageAnfInv = new BufferedLogSection($"{currentNameLog}.damage.tmp", BufferedSectionThreshold);
                        InvWarn = new BufferedLogSection($"{currentNameLog}.warn.tmp", BufferedSectionThreshold);
                        speedWarnBuffer = new BufferedLogSection($"{currentNameLog}.speed.tmp", BufferedSectionThreshold);
                        hitWarnBuffer = new BufferedLogSection($"{currentNameLog}.hit.tmp", BufferedSectionThreshold);
                        writer = new AsyncLogWriter(currentNameLog, append: false, LogQueueCapacity);
                        AheSettingsManager.RefreshSnapshot();
                        speedWarnTracker.Reset(Mathf.Max(Time.timeScale, 0f));
                        hitWarnTracker.Reset();
                        bool initialDebugUiVisible = DebugModIntegration.TryGetUiVisible(out bool visible) && visible;
                        debugModEventsTracker.Reset(initialDebugUiVisible);
                        debugMenuTracker.Reset(initialDebugUiVisible);
                        debugHotkeysTracker.InitializeBindings();
                        keyLogBuffer.Clear();
                        lastKeyLogFlushTime = 0;
                        charmsChangeTracker.Reset();
                        CoreSessionLogger.WriteEncryptedModSnapshot(writer, modsDir, "---------------------------------------------------");
                        LogWrite.EncryptedLine(writer, CoreSessionLogger.BuildEquippedCharmsLine());
                        CoreSessionLogger.WriteEncryptedSkillLines(writer, "---------------------------------------------------");
                          speedWarnTracker.LogInitial(writer, lastUnixTime);
                          InitializeDebugModHooks();
                          godhomeQolTracker.Reset();
                          godhomeQolTracker.StartFight(self.TargetSceneName, lastUnixTime);
  
  
                      }
                    catch (Exception e)
                    {
                        Modding.Logger.LogError("Ошибка при открытии файла: " + e.Message);
                    }

                    int seed = (int)(lastUnixTime ^ curentPlayTime);

                    customCanvas = new CustomCanvas(new NumberInCanvas(seed), new LoadingSprite(lastString));

                    if (self.TargetSceneName.Contains("GG_Vengefly_V") && lastScene == "GG_Atrium_Roof")
                    {
                        currentPanteon = ("P5", Panteons.P5.ToList());
                        bossCounter++;

                        DamageAnfInv.Add($"{dataTime}|{lastUnixTime}|{self.TargetSceneName}| {bossCounter}*");

                        LogWrite.EncryptedLine(writer, $"{dataTime}|{lastUnixTime}|{curentPlayTime}|{self.TargetSceneName}| {bossCounter}*");
                    }
                    else
                    {

                        DamageAnfInv.Add($"{dataTime}|{lastUnixTime}|{self.TargetSceneName}|");

                        LogWrite.EncryptedLine(writer, $"{dataTime}|{lastUnixTime}|{curentPlayTime}|{self.TargetSceneName}|");
                    }



                }
                else if (isPlayChalange)
                {
                    if (currentPanteon.list == null && lastScene.Contains("GG_Boss_Door"))
                    {
                        if (self.TargetSceneName == Panteons.P1[0])
                            currentPanteon = ("P1", Panteons.P1.ToList());
                        if (self.TargetSceneName == Panteons.P2[0])
                            currentPanteon = ("P2", Panteons.P2.ToList());
                        if (self.TargetSceneName == Panteons.P3[0])
                            currentPanteon = ("P3", Panteons.P3.ToList());
                        if (self.TargetSceneName == Panteons.P4[0])
                            currentPanteon = ("P4", Panteons.P4.ToList());
                    }
                    else if (currentPanteon.list != null)
                    {

                        int targetIndex = currentPanteon.list.IndexOf((self.TargetSceneName));
                        int lastSceneIndex = currentPanteon.list.IndexOf(lastScene);
                        godhomeQolTracker.StartFight(self.TargetSceneName, lastUnixTime);


                        if (targetIndex == -1 || (lastSceneIndex != -1 && !(IsValidNextScene(currentPanteon.list, lastSceneIndex, self.TargetSceneName))))
                        {
                            Close();
                        }
                        if (lastScene == "GG_Spa")
                        {
                            currentPanteon.list?.Remove(lastScene);
                        }


                    }
                    List<string> skipScenes = new List<string> { "GG_Spa", "GG_Engine", "GG_Unn", "GG_Engine_Root", "GG_Wyrm", "GG_Engine_Prime", "GG_Atrium", "GG_Atrium_Roof" };

                    if (!skipScenes.Contains(self.TargetSceneName))
                        bossCounter++;

                    StartLoad();
                    DamageAnfInv.Add($"{dataTime}|{lastUnixTime}|{self.TargetSceneName}{((!skipScenes.Contains(self.TargetSceneName)) ? $"| {bossCounter}*" : "")}");

                    LogWrite.EncryptedLine(writer, $"{dataTime}|{lastUnixTime}|{self.TargetSceneName}|{{sprite}}{self.TargetSceneName}{((!skipScenes.Contains(self.TargetSceneName)) ? $"| {bossCounter}*" : "")}");

                }
            }
            catch (Exception e)
            {
                Modding.Logger.Log(e.Message);
            }
            lastScene = self.TargetSceneName;
            orig(self);
        }
        private bool IsValidNextScene(List<string> panteonList, int lastSceneIndex, string targetSceneName)
        {
            int nextIndex = lastSceneIndex + 1;

            if (nextIndex >= panteonList.Count) return false;

            string expectedNextScene = panteonList[nextIndex];

            if (expectedNextScene != targetSceneName)
            {
                nextIndex++;
                expectedNextScene = panteonList[nextIndex];

            }

            return expectedNextScene == targetSceneName;
        }

        public static string ConvertUnixTimeToDateTimeString(long unixTimeMilliseconds)
        {
            DateTimeOffset dateTimeOffset = DateTimeOffset.FromUnixTimeMilliseconds(unixTimeMilliseconds);

            string dateTimeString = dateTimeOffset.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss.fff");

            return dateTimeString;
        }
        public static string ConvertUnixTimeToTimeString(long unixTimeMilliseconds)
        {
            TimeSpan span = TimeSpan.FromMilliseconds(Math.Max(0, unixTimeMilliseconds));
            if (span.Days > 0)
            {
                return $"{span.Days:D2}.{span:hh\\:mm\\:ss\\.fff}";
            }
            return span.ToString(@"hh\:mm\:ss\.fff");
        }

        private void Close()
        {
            try
            {
                if (writer != null)
                {
                    long EndTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                    string sessionTime = ConvertUnixTimeToTimeString((long)(Time.realtimeSinceStartup * 1000f));
                    FlushKeyLogBufferIfNeeded(EndTime, force: true);
                    LogWrite.EncryptedLine(writer, $"StartTime: {ConvertUnixTimeToDateTimeString(startUnixTime)}, EndTime: {ConvertUnixTimeToDateTimeString(EndTime)}, TimeInPlay: {ConvertUnixTimeToTimeString(EndTime - startUnixTime)}, SessionTime: {sessionTime}");

                    CoreSessionLogger.WriteDamageInvSection(writer, DamageAnfInv, separatorAfter: null);

                    LogWrite.EncryptedLine(writer, $"StartTime: {ConvertUnixTimeToDateTimeString(startUnixTime)}, EndTime: {ConvertUnixTimeToDateTimeString(EndTime)}, TimeInPlay: {ConvertUnixTimeToTimeString(EndTime - startUnixTime)}, SessionTime: {sessionTime}");
                    CoreSessionLogger.WriteSeparator(writer);
                    LogWrite.EncryptedLine(writer, "\n\n");

                    CoreSessionLogger.WriteWarningsSection(writer, InvWarn);

                    LogWrite.EncryptedLine(writer, "\n\n");
                    if (speedWarnBuffer != null)
                    {
                        speedWarnBuffer.AddRange(speedWarnTracker.Warnings);
                    }
                    speedWarnTracker.ClearWarnings();
                    CoreSessionLogger.WriteSpeedWarningsSection(writer, speedWarnBuffer);

                    LogWrite.EncryptedLine(writer, "HitWarn:");
                    if (hitWarnBuffer != null)
                    {
                        hitWarnBuffer.AddRange(hitWarnTracker.Warnings);
                        hitWarnBuffer.WriteEncryptedLines(writer);
                    }
                    LogWrite.EncryptedLine(writer, "\n\n");
                    LogWrite.EncryptedLine(writer, "---------------------------------------------------");
                    hitWarnTracker.Reset();

                    charmsChangeTracker.Write(writer);
                    DamageChangeTracker.WriteSection(writer, damageChangeTracker);
                    damageChangeTracker.Reset();
                    FlukenestTracker.WriteSectionWithSeparator(writer, flukenestTracker);
                    flukenestTracker.Reset();
                    AheSettingsManager.WriteSettingsWithSeparator(writer);
                    ZoteSettingsManager.WriteSettingsWithSeparator(writer);
                    CollectorPhasesSettingsManager.WriteSettingsWithSeparator(writer);
                    SafeGodseekerQolIntegration.WriteSettingsWithSeparator(writer);
                    godhomeQolTracker.WriteSection(writer);
                    DebugModEventsWriter.Write(writer, debugModEventsTracker.Events);
                    DebugHotKeysWriter.Write(writer, debugHotkeysTracker.Bindings, debugHotkeysTracker.Activations);
                    debugMenuTracker.WriteSection(writer);
                    CoreSessionLogger.WriteNoBlurSettings(writer);
                    CoreSessionLogger.WriteCustomizableAbilitiesSettings(writer);
                    CoreSessionLogger.WriteControlSettings(writer);
                    LogWrite.EncryptedLine(writer, '\n' + isСhallengeСompleted + '\n');

                    HardwareFingerprint.WriteEncryptedLine(writer);
                    CoreSessionLogger.WriteEncryptedModSnapshot(writer, modsDir, "---------------------------------------------------");

                    LogWrite.Raw(writer, lastString);
                    writer.Flush();
                    writer.Close();
                    writer = null;


                    string panteonDir = Path.Combine(dllDir, currentPanteon.name);
                    if (!Directory.Exists(panteonDir))
                    {
                        Directory.CreateDirectory(panteonDir);
                    }


                    string dataTimeNow = DateTimeOffset.FromUnixTimeMilliseconds(lastUnixTime).ToLocalTime().ToString("dd-MM-yyyy HH-mm-ss");
                    string newPath = Path.Combine(panteonDir, $"{isСhallengeСompleted}{currentPanteon.name} ({dataTimeNow}).log");

                    if (File.Exists(currentNameLog))
                    {
                        File.Move(currentNameLog, newPath);
                        string pantheonName = currentPanteon.name ?? "Unknown";
                        SavedLogTracker.Record(newPath, "Pantheons", pantheonName, "None");
                    }
                }
                isСhallengeСompleted = "-";
                bossCounter = 0;
                startUnixTime = 0;
                isPlayChalange = false;
                customCanvas?.DestroyCanvas();
                currentPanteon = (null, null);
            }
            catch (Exception ex)
            {
                Modding.Logger.Log(ex.Message);
            }
            finally
            {
                DamageAnfInv?.Clear();
                InvWarn?.Clear();
                speedWarnBuffer?.Clear();
                hitWarnBuffer?.Clear();
                DamageAnfInv = null;
                InvWarn = null;
                speedWarnBuffer = null;
                hitWarnBuffer = null;
                AheSettingsManager.Reset();
                ZoteSettingsManager.Reset();
                CollectorPhasesSettingsManager.Reset();
                godhomeQolTracker.Reset();
                debugHotkeysTracker.Reset();
                debugMenuTracker.Reset();
                keyLogBuffer.Clear();
                lastKeyLogFlushTime = 0;
                DisposeDebugModHooks();
            }
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

        public static Dictionary<string, List<string>> SortLogsByObjectName(List<string> logs)
        {
            Dictionary<string, List<string>> sortedLogs = new Dictionary<string, List<string>>();

            foreach (string log in logs)
            {
                string objectName = ExtractObjectName(log);

                if (objectName != null)
                {
                    if (sortedLogs.ContainsKey(objectName))
                    {
                        sortedLogs[objectName].Add(log);
                    }
                    else
                    {
                        sortedLogs[objectName] = new List<string> { log };
                    }
                }
            }

            return sortedLogs;
        }


        float lastFps = 0f;

        private void SpellFluke_DoDamage(On.SpellFluke.orig_DoDamage orig, SpellFluke self, GameObject obj, int upwardRecursionAmount, bool burst)
        {
            FlukenestTracker.HandleDoDamage(isPlayChalange, writer, flukenestTracker, currentPanteon.name ?? lastScene, lastUnixTime, orig, self, obj, upwardRecursionAmount, burst);
        }
    }
}
