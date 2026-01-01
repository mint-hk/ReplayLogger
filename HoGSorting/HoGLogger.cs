using GlobalEnums;
using HutongGames.PlayMaker;
using Modding;
using On;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using MonoMod.RuntimeDetour;
using HKHealthManager = global::HealthManager;

namespace ReplayLogger
{
    internal static class HoGLogger
    {
        private static readonly object SyncRoot = new();
        private static readonly string DllDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        private static readonly string ModsDirectory = new DirectoryInfo(DllDirectory).Parent.FullName;

        private static bool hooksInitialized;
        private static readonly Dictionary<int, string> CustomCharmDisplayNames = new()
        {
            { (int)Charm.MarkOfPurity, "Mark of Purity" },
            { (int)Charm.VesselsLament, "Vessel's Lament" },
            { (int)Charm.BoonOfHallownest, "Boon of Hallownest" },
            { (int)Charm.AbyssalBloom, "Abyssal Bloom" }
        };

        private static bool isLogging;
        private static string activeArena;
        private static int? bossLevelInFight;
        private static string lastSceneName = string.Empty;
        private static string lastSceneBeforeArena = string.Empty;
        private static string currentTempFile;
        private static StreamWriter writer;
        private static CustomCanvas customCanvas;
        private static string masterKeyBlob;
        private static HoGBucketInfo currentBucketInfo = HoGBucketInfo.CreateDefault(null);
        private static string pendingHoGDefaultFolder = HoGLoggerConditions.DefaultBucket;
        private static readonly Dictionary<string, BossHpState> bossHpStates = new(StringComparer.Ordinal);
        private const int BufferedSectionThreshold = 200;
        private const int LogQueueCapacity = 20000;

        private static long lastUnixTime;
        private static long startUnixTime;
        private static int bossCounter;
        private static float lastFps;
        private static SpeedWarnTracker speedWarnTracker = new();
        private static HitWarnTracker hitWarnTracker = new();
        private static FlukenestTracker flukenestTracker = new();
        private static DebugModEventsTracker debugModEventsTracker = new();
        private static DebugHotkeysTracker debugHotkeysTracker = new();
        private static CharmsChangeTracker charmsChangeTracker = new();
        private static GodhomeQolTracker godhomeQolTracker = new();

        private static BufferedLogSection damageAndInv;
        private static BufferedLogSection invWarnings;
        private static BufferedLogSection speedWarnBuffer;
        private static BufferedLogSection hitWarnBuffer;
        private static readonly List<string> keyLogBuffer = new();
        private const int KeyLogFlushIntervalMs = 200;
        private const int KeyLogFlushBatchSize = 50;
        private static long lastKeyLogFlushTime;
        private static List<string> debugModEvents = new();
        private static List<string> debugHotkeyBindings = new();
        private static List<string> debugHotkeyEvents = new();
        private static DamageChangeTracker damageChangeTracker = new();
        private static DebugMenuTracker debugMenuTracker = new();
        private static int currentAttemptIndex = 1;
        private static long lastLoggedDeltaMs = -1;

        private static Dictionary<HKHealthManager, (int maxHP, int lastHP)> infoBoss = new();
        private static Dictionary<KeyCode, List<string>> debugHotkeysByKey = new();
        private static Hook debugKillAllHook;
        private static Hook debugKillSelfHook;
        private static FieldInfo bossLevelField;
        private static PropertyInfo bossLevelProperty;
        private static Type bossLevelOwnerType;
        private static FieldInfo bossSceneField;
        private static PropertyInfo bossSceneProperty;
        private static Type bossSceneOwnerType;
        private static FieldInfo bossSceneLevelField;
        private static PropertyInfo bossSceneLevelProperty;
        private static Type bossSceneLevelOwnerType;
        private static Func<int?> staticBossLevelGetter;
        private static Type paleCourtLevelOwnerType;
        private static FieldInfo paleCourtLevelField;
        private static PropertyInfo paleCourtLevelProperty;

        private static bool isInvincible;
        private static bool isChange;
        private static float invTimer;
        private static int enemyUpdateFrameCount = 0;
        private const int EnemyUpdateInterval = 3; // Update every 3 frames instead of every frame
        private static readonly List<HKHealthManager> cachedHealthManagers = new();

        [ModuleInitializer]
        internal static void InitializeModule()
        {
            InitializeHooks();
        }

        private static void InitializeHooks()
        {
            if (hooksInitialized)
            {
                return;
            }

            ModsChecking.PrimeHeavyModCache(ModsDirectory);

            On.SceneLoad.Begin += SceneLoad_Begin;
            On.SceneLoad.RecordEndTime += SceneLoad_RecordEndTime;
            On.GameManager.Update += GameManager_Update;
            On.BossSceneController.Update += BossSceneController_Update;
            On.HeroController.FixedUpdate += HeroController_FixedUpdate;
            On.QuitToMenu.Start += QuitToMenu_Start;
            On.SpellFluke.DoDamage += SpellFluke_DoDamage;

            ModHooks.HitInstanceHook += ModHooks_HitInstanceHook;
            ModHooks.ApplicationQuitHook += ApplicationQuit;
            HoGRoomConditions.Initialize();
            HoGRoomConditions.BossHpDetected += OnBossHpDetected;

            lastSceneName = GameManager.instance?.sceneName ?? string.Empty;
            hooksInitialized = true;
        }

        private static void SceneLoad_Begin(On.SceneLoad.orig_Begin orig, SceneLoad self)
        {
            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            lastUnixTime = now;

            string targetScene = self.TargetSceneName ?? string.Empty;
            string previousScene = lastSceneName;

            if (!isLogging && HoGLoggerConditions.ShouldStartLogging(previousScene, targetScene))
            {
                lastSceneBeforeArena = previousScene;
                StartLogging(targetScene);
            }
            else if (isLogging && HoGLoggerConditions.ShouldStopLogging(activeArena, targetScene))
            {
                StopLogging(targetScene);
            }

            lastSceneName = targetScene;
            orig(self);
        }

        private static void SceneLoad_RecordEndTime(On.SceneLoad.orig_RecordEndTime orig, SceneLoad self, SceneLoad.Phases phase)
        {
            orig(self, phase);
            if (phase == SceneLoad.Phases.UnloadUnusedAssets && isLogging)
            {
                infoBoss.Clear();
            }
        }

        private static readonly KeyCode[] CachedKeyCodes = (KeyCode[])Enum.GetValues(typeof(KeyCode));
        private static readonly KeyCode[] RelevantKeyCodes = GenerateRelevantKeyCodes();

        private static KeyCode[] GenerateRelevantKeyCodes()
        {
            var list = new System.Collections.Generic.List<KeyCode>();
            for (int i = (int)KeyCode.A; i <= (int)KeyCode.Z; i++) list.Add((KeyCode)i);
            for (int i = (int)KeyCode.Alpha0; i <= (int)KeyCode.Alpha9; i++) list.Add((KeyCode)i);
            for (int i = (int)KeyCode.Keypad0; i <= (int)KeyCode.Keypad9; i++) list.Add((KeyCode)i);
            for (int i = (int)KeyCode.F1; i <= (int)KeyCode.F15; i++) list.Add((KeyCode)i);
            for (int i = (int)KeyCode.JoystickButton0; i <= (int)KeyCode.JoystickButton19; i++) list.Add((KeyCode)i);

            list.AddRange(new[] {
                KeyCode.Space, KeyCode.Return, KeyCode.Escape, KeyCode.Backspace, KeyCode.Tab,
                KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl,
                KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.LeftArrow, KeyCode.RightArrow,
                KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.Mouse0, KeyCode.Mouse1, KeyCode.Mouse2
            });
            return list.ToArray();
        }

        private static void GameManager_Update(On.GameManager.orig_Update orig, GameManager self)
        {
            orig(self);
            if (!isLogging || writer == null)
            {
                return;
            }

            MonitorTimeScale();
            MonitorHeroHealth();
            MonitorDebugModUi();
            debugMenuTracker.Update(writer, activeArena, lastUnixTime);
            godhomeQolTracker.Update(activeArena);

            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            DateTimeOffset relative = DateTimeOffset.FromUnixTimeMilliseconds(now - startUnixTime);
            customCanvas?.UpdateTime(relative.ToString("HH:mm:ss"));

            // OPTIMIZED: Early exit if no input detected - saves ~21,000 checks per second at 60fps
            if (!Input.anyKeyDown && !Input.GetMouseButtonDown(0) && !Input.GetMouseButtonDown(1) && !Input.GetMouseButtonDown(2))
            {
                FlushKeyLogBufferIfNeeded(now);
                return;
            }

            // Only check relevant keys (~100 instead of 350)
            foreach (KeyCode keyCode in RelevantKeyCodes)
            {
                if (!Input.GetKeyDown(keyCode) && !Input.GetKeyUp(keyCode))
                {
                    continue;
                }

                string keyStatus = Input.GetKeyDown(keyCode) ? "+" : "-";
                long unixTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                float fps = Time.unscaledDeltaTime == 0 ? lastFps : 1f / Time.unscaledDeltaTime;
                lastFps = fps;

                customCanvas?.UpdateWatermark(keyCode);

                int watermarkNumber = customCanvas?.numberInCanvas?.Number ?? 0;
                Color watermarkColorStruct = customCanvas?.numberInCanvas?.Color ?? Color.white;
                string watermarkColor = ColorUtility.ToHtmlStringRGBA(watermarkColorStruct);

                long delta = unixTime - lastUnixTime;

                if (lastLoggedDeltaMs >= 0 && delta < lastLoggedDeltaMs)
                {
                    currentAttemptIndex++;
                    bossCounter++;
                    string timestamp = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    long playTime = (int)(PlayerData.instance.playTime * 100);
                    string startLine = $"{timestamp}|{unixTime}|{playTime}|{activeArena}| {bossCounter}*";
                    damageAndInv.Add(startLine);

                    try
                    {
                        FlushKeyLogBufferIfNeeded(unixTime, force: true);
                        LogWrite.EncryptedLine(writer, startLine);
                    }
                    catch (Exception e)
                    {
                        Modding.Logger.LogWarn($"HoGLogger: failed to write attempt separator/start line: {e.Message}");
                    }
                }

                lastLoggedDeltaMs = delta;

                string formattedKey = JoystickKeyMapper.FormatKey(keyCode);
                string logEntry = $"+{delta}|{formattedKey}|{keyStatus}|{watermarkNumber}|#{watermarkColor}|{fps.ToString("F0")}|";
                try
                {
                    keyLogBuffer.Add(logEntry);
                }
                catch (Exception e)
                {
                    Modding.Logger.LogError($"HoGLogger: failed to write key entry: {e.Message}");
                }

                if (keyStatus == "+" && debugHotkeysByKey.Count > 0 && debugHotkeysByKey.TryGetValue(keyCode, out List<string> debugActions))
                {
                    debugHotkeysTracker.TrackActivation(keyCode, activeArena ?? "UnknownArena", lastUnixTime, unixTime);
                    foreach (string action in debugActions)
                    {
                        LogDebugHotkeyActivation(action, keyCode, unixTime);
                    }
                }
            }

            FlushKeyLogBufferIfNeeded(now);
        }

        private static void BossSceneController_Update(On.BossSceneController.orig_Update orig, BossSceneController self)
        {
            if (isLogging)
            {
                CaptureBossLevel(self);
                EnemyUpdate();
            }
            orig(self);
        }

        private static void CaptureBossLevel(BossSceneController controller)
        {
            if (controller == null || !IsDifficultyArena(activeArena))
            {
                return;
            }

            if (IsPaleCourtArena(activeArena) && !IsTisoArena(activeArena))
            {
                int? paleCourtLevel = TryReadPaleCourtLevel();
                if (IsValidBossLevelForArena(paleCourtLevel, activeArena))
                {
                    if (!bossLevelInFight.HasValue || bossLevelInFight.Value != paleCourtLevel.Value)
                    {
                        bossLevelInFight = paleCourtLevel.Value;
                    }
                    return;
                }
            }

            int? level = TryReadBossLevel(controller);
            if (!IsValidBossLevelForArena(level, activeArena))
            {
                return;
            }

            if (!bossLevelInFight.HasValue || bossLevelInFight.Value != level.Value)
            {
                bossLevelInFight = level.Value;
            }
        }

        private static int? TryReadBossLevel(BossSceneController controller)
        {
            if (controller == null)
            {
                return null;
            }

            int? direct = TryReadLevelFromInstance(
                controller,
                new[] { "bossLevel", "BossLevel", "bossSceneLevel", "BossSceneLevel", "bossSceneTier", "BossSceneTier", "bossDifficulty", "BossDifficulty" },
                ref bossLevelOwnerType,
                ref bossLevelField,
                ref bossLevelProperty);

            if (IsValidBossLevelForArena(direct, activeArena))
            {
                return direct;
            }

            int? fromScene = TryReadBossLevelFromBossScene(controller);
            if (IsValidBossLevelForArena(fromScene, activeArena))
            {
                return fromScene;
            }

            int? fromStatic = TryReadBossLevelFromStatic(activeArena);
            if (IsValidBossLevelForArena(fromStatic, activeArena))
            {
                return fromStatic;
            }

            return null;
        }

        private static int? TryReadPaleCourtLevel()
        {
            if (paleCourtLevelOwnerType == null || (paleCourtLevelField == null && paleCourtLevelProperty == null))
            {
                paleCourtLevelOwnerType = FindType("BossManagement.CustomWP") ?? FindTypeByName("CustomWP");
                if (paleCourtLevelOwnerType != null)
                {
                    const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    paleCourtLevelField = paleCourtLevelOwnerType.GetField("lev", flags)
                        ?? paleCourtLevelOwnerType.GetField("Lev", flags)
                        ?? paleCourtLevelOwnerType.GetField("level", flags)
                        ?? paleCourtLevelOwnerType.GetField("Level", flags);

                    if (paleCourtLevelField == null)
                    {
                        paleCourtLevelProperty = paleCourtLevelOwnerType.GetProperty("lev", flags)
                            ?? paleCourtLevelOwnerType.GetProperty("Lev", flags)
                            ?? paleCourtLevelOwnerType.GetProperty("level", flags)
                            ?? paleCourtLevelOwnerType.GetProperty("Level", flags);
                    }
                }
            }

            if (paleCourtLevelOwnerType == null || (paleCourtLevelField == null && paleCourtLevelProperty == null))
            {
                return null;
            }

            try
            {
                object raw = paleCourtLevelProperty != null
                    ? paleCourtLevelProperty.GetValue(null)
                    : paleCourtLevelField.GetValue(null);

                return TryConvertToInt(raw);
            }
            catch
            {
                return null;
            }
        }

        private static int? TryReadBossLevelFromBossScene(BossSceneController controller)
        {
            object bossScene = TryReadBossSceneObject(controller);
            if (bossScene == null)
            {
                return null;
            }

            return TryReadLevelFromInstance(
                bossScene,
                new[] { "bossLevel", "BossLevel", "bossSceneLevel", "BossSceneLevel", "bossSceneTier", "BossSceneTier", "bossDifficulty", "BossDifficulty", "difficulty", "Difficulty" },
                ref bossSceneLevelOwnerType,
                ref bossSceneLevelField,
                ref bossSceneLevelProperty);
        }

        private static object TryReadBossSceneObject(BossSceneController controller)
        {
            if (controller == null)
            {
                return null;
            }

            Type type = controller.GetType();
            if (bossSceneOwnerType != type)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                bossSceneField = type.GetField("bossScene", flags) ?? type.GetField("BossScene", flags);
                bossSceneProperty = type.GetProperty("bossScene", flags) ?? type.GetProperty("BossScene", flags);
                bossSceneOwnerType = type;
            }

            try
            {
                return bossSceneProperty != null
                    ? bossSceneProperty.GetValue(controller)
                    : bossSceneField?.GetValue(controller);
            }
            catch
            {
                return null;
            }
        }

        private static int? TryReadLevelFromInstance(object instance, string[] candidateNames, ref Type cachedOwner, ref FieldInfo cachedField, ref PropertyInfo cachedProperty)
        {
            if (instance == null || candidateNames == null || candidateNames.Length == 0)
            {
                return null;
            }

            Type type = instance.GetType();
            if (cachedOwner != type)
            {
                cachedField = null;
                cachedProperty = null;
                cachedOwner = type;

                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (string name in candidateNames)
                {
                    cachedField = type.GetField(name, flags);
                    if (cachedField != null)
                    {
                        break;
                    }

                    cachedProperty = type.GetProperty(name, flags);
                    if (cachedProperty != null)
                    {
                        break;
                    }
                }
            }

            if (cachedField == null && cachedProperty == null)
            {
                return null;
            }

            try
            {
                object raw = cachedProperty != null
                    ? cachedProperty.GetValue(instance)
                    : cachedField?.GetValue(instance);

                return TryConvertToInt(raw);
            }
            catch
            {
                return null;
            }
        }

        private static int? TryReadBossLevelFromStatic(string arenaName)
        {
            if (staticBossLevelGetter != null)
            {
                int? cachedValue = staticBossLevelGetter();
                if (IsValidBossLevelForArena(cachedValue, arenaName))
                {
                    return cachedValue;
                }

                staticBossLevelGetter = null;
            }

            string[] typeNames =
            {
                "BossStatue",
                "BossStatueController",
                "BossChallengeUI",
                "BossSceneController",
                "BossStatueUI",
                "GodhomeManager",
                "GGManager",
                "BossSequenceController"
            };

            string[] memberNames =
            {
                "bossLevel",
                "BossLevel",
                "ggBossLevel",
                "GGBossLevel",
                "currentBossLevel",
                "CurrentBossLevel",
                "bossSceneLevel",
                "BossSceneLevel",
                "bossDifficulty",
                "BossDifficulty",
                "difficulty",
                "Difficulty",
                "statueLevel",
                "bossStatueLevel",
                "bossChallengeLevel"
            };

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (string typeName in typeNames)
            {
                Type type = FindTypeByName(typeName);
                if (type == null)
                {
                    continue;
                }

                foreach (string memberName in memberNames)
                {
                    FieldInfo field = type.GetField(memberName, flags);
                    if (field != null)
                    {
                        staticBossLevelGetter = () => TryConvertToInt(field.GetValue(null));
                        int? value = staticBossLevelGetter();
                        if (IsValidBossLevelForArena(value, arenaName))
                        {
                            return value;
                        }
                        staticBossLevelGetter = null;
                        continue;
                    }

                    PropertyInfo property = type.GetProperty(memberName, flags);
                    if (property != null)
                    {
                        staticBossLevelGetter = () => TryConvertToInt(property.GetValue(null));
                        int? value = staticBossLevelGetter();
                        if (IsValidBossLevelForArena(value, arenaName))
                        {
                            return value;
                        }
                        staticBossLevelGetter = null;
                    }
                }
            }

            return null;
        }

        private static bool IsValidBossLevel(int? level) =>
            level.HasValue && level.Value >= 0 && level.Value <= 3;

        private static bool IsValidBossLevelForArena(int? level, string arenaName)
        {
            if (!IsValidBossLevel(level))
            {
                return false;
            }

            if (level.Value == 0 && IsVariantArena(arenaName) && !IsAttunedVariantAllowed(arenaName))
            {
                return false;
            }

            return true;
        }

        private static int? TryConvertToInt(object raw)
        {
            if (raw == null)
            {
                return null;
            }

            try
            {
                if (raw is int intValue)
                {
                    return intValue;
                }

                Type rawType = raw.GetType();
                if (rawType.IsEnum)
                {
                    return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                }

                return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static void HeroController_FixedUpdate(On.HeroController.orig_FixedUpdate orig, HeroController self)
        {
            if (isLogging)
            {
                InvCheck();
            }
            orig(self);
        }

        private static IEnumerator QuitToMenu_Start(On.QuitToMenu.orig_Start orig, QuitToMenu self)
        {
            StopLogging("QuitToMenu");
            return orig(self);
        }

        private static void ApplicationQuit()
        {
            StopLogging("ApplicationQuit");
        }

        private static HitInstance ModHooks_HitInstanceHook(HutongGames.PlayMaker.Fsm owner, HitInstance hit)
        {
            if (!isLogging || owner?.GameObject == null)
            {
                return hit;
            }

            long unixTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            string ownerName = owner.GameObject.GetFullPath();

            damageChangeTracker.Track(ownerName, activeArena, unixTime - lastUnixTime, hit.DamageDealt, hit.Multiplier);

            return hit;
        }

        private static void SpellFluke_DoDamage(On.SpellFluke.orig_DoDamage orig, SpellFluke self, GameObject obj, int upwardRecursionAmount, bool burst)
        {
            FlukenestTracker.HandleDoDamage(isLogging, writer, flukenestTracker, activeArena, lastUnixTime, orig, self, obj, upwardRecursionAmount, burst);
        }

        private static void MonitorAttemptSeparator()
        {
            long relativeMs = DateTimeOffset.Now.ToUnixTimeMilliseconds() - startUnixTime;

            if (lastLoggedDeltaMs >= 0 && relativeMs < lastLoggedDeltaMs)
            {
                currentAttemptIndex++;
                string timestamp = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture);
                string separator = $"-----ATTEMPT #{currentAttemptIndex}----- {timestamp}";

                damageAndInv.Add(separator);

                if (writer != null)
                {
                    try
                    {
                        LogWrite.EncryptedLine(writer, separator);
                    }
                    catch (Exception e)
                    {
                        Modding.Logger.LogWarn($"HoGLogger: failed to write attempt separator: {e.Message}");
                    }
                }
            }

            lastLoggedDeltaMs = relativeMs;
        }

        private static void StartLogging(string arenaName)
        {
            lock (SyncRoot)
            {
                if (isLogging || string.IsNullOrEmpty(arenaName))
                {
                    return;
                }

                try
                {
                    EnsureCanvasSpritesLoaded();

                    startUnixTime = lastUnixTime;
                    bossCounter = 1;
                    masterKeyBlob = KeyloggerLogEncryption.GenerateKeyAndIV();
                    AllHallownestEnhancedToggleSnapshot snapshot = AheSettingsManager.RefreshSnapshot();
                    pendingHoGDefaultFolder = HoGLoggerConditions.DefaultBucket;

                    bool requiresHp = HoGStoragePlanner.RequiresHp(arenaName);
                    if (requiresHp)
                    {
                        ResetBossHpState(arenaName);
                    }

                    int? initialHp = requiresHp ? TryGetBossHp(arenaName) : null;

                    HoGStoragePlan initialPlan = HoGStoragePlanner.GetPlan(arenaName, snapshot, initialHp, lastSceneBeforeArena);
                    ApplyHoGStoragePlan(initialPlan);
                    HoGRoomConditions.MarkPendingScene(initialPlan.NeedsHp ? arenaName : null);
                    string tempDir = Path.GetTempPath();
                    currentTempFile = Path.Combine(tempDir, $"ReplayLoggerHoG_{Guid.NewGuid():N}.log");
                    damageAndInv = new BufferedLogSection($"{currentTempFile}.damage.tmp", BufferedSectionThreshold);
                    invWarnings = new BufferedLogSection($"{currentTempFile}.warn.tmp", BufferedSectionThreshold);
                    speedWarnBuffer = new BufferedLogSection($"{currentTempFile}.speed.tmp", BufferedSectionThreshold);
                    hitWarnBuffer = new BufferedLogSection($"{currentTempFile}.hit.tmp", BufferedSectionThreshold);

                    writer = new AsyncLogWriter(currentTempFile, append: false, LogQueueCapacity);

                    CoreSessionLogger.WriteEncryptedModSnapshot(writer, ModsDirectory, "---------------------------------------------------");

                    string equippedCharms = CoreSessionLogger.BuildEquippedCharmsLine();
                    LogWrite.EncryptedLine(writer, equippedCharms);
                    CoreSessionLogger.WriteEncryptedSkillLines(writer, "---------------------------------------------------");

                    int currentPlayTime = (int)(PlayerData.instance.playTime * 100);
                    int seed = (int)(lastUnixTime ^ currentPlayTime);

                    customCanvas = new CustomCanvas(new NumberInCanvas(seed), new LoadingSprite(masterKeyBlob));
                    customCanvas?.StartUpdateSprite();

                    string timestamp = DateTimeOffset.FromUnixTimeMilliseconds(lastUnixTime).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture);

                    damageAndInv?.Clear();
                    invWarnings?.Clear();
                    speedWarnBuffer?.Clear();
                    hitWarnBuffer?.Clear();
                    speedWarnTracker.ClearWarnings();
                    hitWarnTracker.Reset();
                    debugModEvents = new();
                    debugHotkeyBindings = new();
                    debugHotkeyEvents = new();
                    keyLogBuffer.Clear();
                    lastKeyLogFlushTime = 0;
                    damageChangeTracker.Reset();
                    flukenestTracker.Reset();
                    infoBoss = new();
                    debugHotkeysByKey = new();
                    currentAttemptIndex = 1;
                    lastLoggedDeltaMs = -1;
                    charmsChangeTracker.Reset();
                    bossLevelInFight = null;
                    if (IsPaleCourtArena(arenaName) && !IsTisoArena(arenaName))
                    {
                        int? paleCourtLevel = TryReadPaleCourtLevel();
                        if (IsValidBossLevelForArena(paleCourtLevel, arenaName))
                        {
                            bossLevelInFight = paleCourtLevel.Value;
                        }
                    }

                    damageAndInv.Add($"{timestamp}|{lastUnixTime}|{arenaName}| {bossCounter}*");
                    LogWrite.EncryptedLine(writer, $"{timestamp}|{lastUnixTime}|{currentPlayTime}|{arenaName}| {bossCounter}*");

                    speedWarnTracker.Reset(Mathf.Max(Time.timeScale, 0f));
                    InitializeDebugModHooks();
                    bool initialDebugUiVisible = DebugModIntegration.TryGetUiVisible(out bool visible) && visible;
                    debugModEventsTracker.Reset(initialDebugUiVisible);
                    InitializeDebugHotkeys();
                    debugMenuTracker.Reset(initialDebugUiVisible);
                    speedWarnTracker.LogInitial(writer, lastUnixTime);
                    godhomeQolTracker.Reset();
                    godhomeQolTracker.StartFight(arenaName, lastUnixTime);

                    activeArena = arenaName;
                    isLogging = true;
                }
                catch (Exception e)
                {
                    Modding.Logger.LogError($"HoGLogger: failed to start log for {arenaName}: {e.Message}");
                    StopLogging("InitFailed");
                }
            }
        }

        private static void StopLogging(string exitScene)
        {
            lock (SyncRoot)
            {
                if (!isLogging)
                {
                    return;
                }

                try
                {
                    FinalizeLog(exitScene);
                }
                catch (Exception e)
                {
                    Modding.Logger.LogError($"HoGLogger: failed to finalize log: {e.Message}");
                }
                finally
                {
                    CleanupState();
                }
            }
        }

        private static void FinalizeLog(string exitScene)
        {
            if (writer == null)
            {
                return;
            }
            _ = exitScene;

            long endUnixTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            string sessionTime = ReplayLogger.ConvertUnixTimeToTimeString((long)(Time.realtimeSinceStartup * 1000f));
            FlushKeyLogBufferIfNeeded(endUnixTime, force: true);

            LogWrite.EncryptedLine(writer, $"StartTime: {ReplayLogger.ConvertUnixTimeToDateTimeString(startUnixTime)}, EndTime: {ReplayLogger.ConvertUnixTimeToDateTimeString(endUnixTime)}, TimeInPlay: {ReplayLogger.ConvertUnixTimeToTimeString(endUnixTime - startUnixTime)}, SessionTime: {sessionTime}");
            CoreSessionLogger.WriteDamageInvSection(writer, damageAndInv, separatorAfter: null);
            LogWrite.EncryptedLine(writer, $"StartTime: {ReplayLogger.ConvertUnixTimeToDateTimeString(startUnixTime)}, EndTime: {ReplayLogger.ConvertUnixTimeToDateTimeString(endUnixTime)}, TimeInPlay: {ReplayLogger.ConvertUnixTimeToTimeString(endUnixTime - startUnixTime)}, SessionTime: {sessionTime}");
            CoreSessionLogger.WriteSeparator(writer);
            LogWrite.EncryptedLine(writer, "\n\n");
            CoreSessionLogger.WriteWarningsSection(writer, invWarnings);

            LogWrite.EncryptedLine(writer, "\n\n");
            if (speedWarnBuffer != null)
            {
                speedWarnBuffer.AddRange(speedWarnTracker.Warnings);
            }
            speedWarnTracker.ClearWarnings();
            CoreSessionLogger.WriteSpeedWarningsSection(writer, speedWarnBuffer);

            LogWrite.EncryptedLine(writer, "\n\n");
            LogWrite.EncryptedLine(writer, "HitWarn:");
            if (hitWarnBuffer != null)
            {
                hitWarnBuffer.AddRange(hitWarnTracker.Warnings);
            }
            hitWarnTracker.ClearWarnings();
            hitWarnBuffer?.WriteEncryptedLines(writer);
            LogWrite.EncryptedLine(writer, "\n\n");
            LogWrite.EncryptedLine(writer, "---------------------------------------------------");
            DamageChangeTracker.WriteSection(writer, damageChangeTracker);
            FlukenestTracker.WriteSectionWithSeparator(writer, flukenestTracker);
            charmsChangeTracker.Write(writer);

            RefreshBucketInfo(force: true);

            LogWrite.EncryptedLine(writer, "-");
            AheSettingsManager.WriteSettingsWithSeparator(writer);
            LogWrite.EncryptedLine(writer, $"HoG Bucket: {currentBucketInfo.BucketLabel ?? HoGLoggerConditions.DefaultBucket}");
            LogWrite.EncryptedLine(writer, string.Empty);
            LogWrite.EncryptedLine(writer, "---------------------------------------------------");
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

            HardwareFingerprint.WriteEncryptedLine(writer);
            CoreSessionLogger.WriteEncryptedModSnapshot(writer, ModsDirectory, "---------------------------------------------------");

            LogWrite.Raw(writer, masterKeyBlob);
            writer.Flush();
            writer.Dispose();

            MoveTempFileToFinalLocation();
            customCanvas?.ClearHud();
        }

        private static void CleanupState()
        {
            string arenaToReset = activeArena;
            writer = null;
            customCanvas?.DestroyCanvasDelayed(2.0f);
            customCanvas = null;
            masterKeyBlob = null;
            speedWarnTracker = new SpeedWarnTracker();
            damageAndInv?.Clear();
            invWarnings?.Clear();
            speedWarnBuffer?.Clear();
            hitWarnBuffer?.Clear();
            damageAndInv = null;
            invWarnings = null;
            speedWarnBuffer = null;
            hitWarnBuffer = null;
            debugModEvents = new();
            debugHotkeyBindings = new();
            debugHotkeyEvents = new();
            keyLogBuffer.Clear();
            lastKeyLogFlushTime = 0;
            debugHotkeysTracker.Reset();
            debugMenuTracker.Reset();
            godhomeQolTracker.Reset();
            damageChangeTracker = new();
            flukenestTracker = new();
            currentAttemptIndex = 1;
            lastLoggedDeltaMs = -1;
            hitWarnTracker = new HitWarnTracker();
            infoBoss = new();
            debugHotkeysByKey = new();
            isLogging = false;
            activeArena = null;
            bossLevelInFight = null;
            lastSceneBeforeArena = string.Empty;
            bossCounter = 0;
            isInvincible = false;
            invTimer = 0f;
            currentTempFile = null;
            currentBucketInfo = HoGBucketInfo.CreateDefault(null);
            AheSettingsManager.Reset();
            pendingHoGDefaultFolder = HoGLoggerConditions.DefaultBucket;
            speedWarnTracker = new SpeedWarnTracker();
            debugModEventsTracker.Reset();
            HoGRoomConditions.MarkPendingScene(null);

            if (HoGStoragePlanner.RequiresHp(arenaToReset))
            {
                ResetBossHpState(arenaToReset);
            }
        }

        private static void MoveTempFileToFinalLocation()
        {
            if (string.IsNullOrEmpty(currentTempFile) || activeArena == null)
            {
                return;
            }

            try
            {
                RefreshBucketInfo(force: true);
                string displayName = currentBucketInfo.BossFolder ?? HoGLoggerConditions.GetDisplayName(activeArena);
                string bossFolderForSave = string.IsNullOrWhiteSpace(currentBucketInfo.BossFolder)
                    ? HoGLoggerConditions.GetDisplayName(activeArena)
                    : currentBucketInfo.BossFolder;
                string fileLabel = string.IsNullOrEmpty(currentBucketInfo.FilePrefix) ? displayName : currentBucketInfo.FilePrefix;
                if (string.Equals(activeArena, "GG_Radiance", StringComparison.Ordinal))
                {
                    string anyRadianceRoot = HoGStoragePlanner.ResolveAnyRadianceRootFolder();
                    if (string.Equals(anyRadianceRoot, "AnyRadiance 3.0", StringComparison.Ordinal))
                    {
                        fileLabel = anyRadianceRoot;
                    }
                }
                string timeSuffix = DateTimeOffset.FromUnixTimeMilliseconds(lastUnixTime).ToLocalTime().ToString("dd-MM-yyyy HH-mm-ss", CultureInfo.InvariantCulture);
                string rootFolder = string.IsNullOrEmpty(currentBucketInfo.RootFolder) ? HoGLoggerConditions.DefaultBucket : currentBucketInfo.RootFolder;

                bool isP5Health = SafeGodseekerQolIntegration.IsP5HealthEnabled();
                if (isP5Health)
                {
                    rootFolder = "P5 HEALTH";
                }

                string finalDir = Path.Combine(DllDirectory, rootFolder, displayName);
                string difficultyFolder = GetDifficultyFolderName();
                string difficultyFolderForSave = string.IsNullOrWhiteSpace(difficultyFolder) ? "None" : difficultyFolder;
                if (!string.IsNullOrEmpty(difficultyFolder))
                {
                    finalDir = Path.Combine(finalDir, difficultyFolder);
                }
                Directory.CreateDirectory(finalDir);

                string prefix = GetDifficultyPrefix();
                string p5Prefix = isP5Health ? "P5 HP " : string.Empty;
                string finalPath = Path.Combine(finalDir, $"{p5Prefix}{prefix}{fileLabel} ({timeSuffix}).log");
                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }

                if (File.Exists(currentTempFile))
                {
                    File.Move(currentTempFile, finalPath);
                    SavedLogTracker.Record(finalPath, rootFolder, bossFolderForSave, difficultyFolderForSave);
                    string toastText = $"{currentBucketInfo.BucketLabel ?? HoGLoggerConditions.DefaultBucket}: {Path.GetFileName(finalPath)}";
                    SavedLogToast.Record(toastText);
                    customCanvas?.ShowSavedFileToast(toastText, ReplayLogger.GetHudToastSeconds());
                }
            }
            catch (Exception e)
            {
                Modding.Logger.LogError($"HoGLogger: failed to move log file: {e.Message}");
            }
        }

        private static string GetDifficultyPrefix()
        {
            if (!IsDifficultyArena(activeArena))
            {
                return string.Empty;
            }

            string label = GetDifficultyLabel();
            return $"[{label}] ";
        }

        private static string GetDifficultyFolderName()
        {
            if (!IsDifficultyArena(activeArena))
            {
                return null;
            }

            return GetDifficultyLabel();
        }

        private static string GetDifficultyLabel()
        {
            if (!bossLevelInFight.HasValue)
            {
                return "None";
            }

            switch (bossLevelInFight.Value)
            {
                case 0:
                    return "Attuned";
                case 1:
                    return "Ascended";
                case 2:
                case 3:
                    return "Radiant";
                default:
                    return "None";
            }
        }

        private static void ApplyDifficultyBucketOverride()
        {
            if (IsPaleCourtArena(activeArena) || !IsDifficultyArena(activeArena))
            {
                return;
            }

            AllHallownestEnhancedToggleSnapshot snapshot = AheSettingsManager.RefreshSnapshot();
            string rootFolder = ResolveHoGRoot(snapshot);
            if (string.Equals(activeArena, "GG_Radiance", StringComparison.Ordinal))
            {
                string anyRadianceRoot = HoGStoragePlanner.ResolveAnyRadianceRootFolder();
                if (!string.IsNullOrEmpty(anyRadianceRoot))
                {
                    rootFolder = anyRadianceRoot;
                }
                else
                {
                    bool coreToggles = snapshot.Available &&
                        snapshot.MainSwitch &&
                        snapshot.StrengthenAllBoss &&
                        snapshot.StrengthenAllMonsters;
                    if (coreToggles && snapshot.MoreRadiance)
                    {
                        rootFolder = "HoG AHE+";
                    }
                }
            }

            string bossFolder = currentBucketInfo.BossFolder ?? HoGLoggerConditions.GetDisplayName(activeArena);
            string filePrefix = currentBucketInfo.FilePrefix;
            currentBucketInfo = new HoGBucketInfo(rootFolder, bossFolder, rootFolder, filePrefix);
            pendingHoGDefaultFolder = rootFolder;
        }

        private static string ResolveHoGRoot(AllHallownestEnhancedToggleSnapshot snapshot)
        {
            if (snapshot.Available && snapshot.MainSwitch && snapshot.StrengthenAllBoss && snapshot.StrengthenAllMonsters)
            {
                return snapshot.OriginalHp ? "HoG AHE" : "HoG AHE+";
            }

            return "HoG";
        }

        private static void ApplyHoGStoragePlan(HoGStoragePlan plan)
        {
            currentBucketInfo = plan.BucketInfo;
            pendingHoGDefaultFolder = plan.BucketInfo.RootFolder ?? HoGLoggerConditions.DefaultBucket;

            if (string.IsNullOrEmpty(plan.HpScene))
            {
                return;
            }

            BossHpState state = GetBossHpState(plan.HpScene);
            if (state == null)
            {
                return;
            }

            state.Waiting = plan.NeedsHp;

            if (plan.HpValue.HasValue)
            {
                state.Cached = plan.HpValue;
                state.Highest = Math.Max(state.Highest, plan.HpValue.Value);
            }

            if (!plan.NeedsHp)
            {
                HoGRoomConditions.MarkPendingScene(null);
            }
        }

        private static void OnBossHpDetected(string sceneName, int hp)
        {
            if (string.IsNullOrEmpty(sceneName) || hp <= 0)
            {
                return;
            }

            BossHpState state = GetBossHpState(sceneName);
            if (state == null)
            {
                return;
            }

            state.Cached = hp;
            state.Highest = Math.Max(state.Highest, hp);
            state.Min = Math.Min(state.Min, hp);
            state.Waiting = false;

            if (isLogging && string.Equals(activeArena, sceneName, StringComparison.Ordinal))
            {
                HoGStoragePlan plan = HoGStoragePlanner.GetPlan(activeArena, AheSettingsManager.CurrentSnapshot, state.Highest, lastSceneBeforeArena);
                ApplyHoGStoragePlan(plan);
            }
        }

        private static int? TryGetBossHp(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                return null;
            }

            bool captureMinimum = string.Equals(sceneName, "GG_Nailmasters", StringComparison.Ordinal);
            int? minHp = null;

            foreach (HKHealthManager manager in UnityEngine.Object.FindObjectsOfType<HKHealthManager>())
            {
                if (manager == null || manager.gameObject == null)
                {
                    continue;
                }

                Scene scene = manager.gameObject.scene;
                if (!scene.IsValid() || !string.Equals(scene.name, sceneName, StringComparison.Ordinal))
                {
                    continue;
                }

                int hp = manager.hp;
                if (hp > 0)
                {
                    BossHpState state = GetBossHpState(sceneName);
                    if (state != null)
                    {
                        state.Cached = hp;
                        state.Highest = Math.Max(state.Highest, hp);
                        state.Min = Math.Min(state.Min, hp);
                    }

                    if (captureMinimum)
                    {
                        if (!minHp.HasValue || hp < minHp.Value)
                        {
                            minHp = hp;
                        }
                        continue;
                    }

                    return hp;
                }
            }

            return minHp;
        }

        private static void RefreshBucketInfo(bool force)
        {
            if (activeArena == null)
            {
                return;
            }

            if (!force && !IsWaitingForHp(activeArena) && currentBucketInfo.RootFolder != HoGLoggerConditions.DefaultBucket)
            {
                return;
            }

            AllHallownestEnhancedToggleSnapshot snapshot = AheSettingsManager.RefreshSnapshot();
            if (snapshot.Available)
            {
                
                string previousScene = lastSceneBeforeArena;
                HoGStoragePlan plan = HoGStoragePlanner.GetPlan(activeArena, snapshot, GetStoredHp(activeArena), previousScene);
                ApplyHoGStoragePlan(plan);
            }

            ApplyDifficultyBucketOverride();
        }

        private static bool IsDifficultyArena(string arenaName)
        {
            if (string.IsNullOrEmpty(arenaName))
            {
                return false;
            }

            if (arenaName.StartsWith("GG_", StringComparison.Ordinal))
            {
                return true;
            }

            return IsPaleCourtArena(arenaName);
        }

        private static bool IsPaleCourtArena(string arenaName)
        {
            if (string.IsNullOrEmpty(arenaName))
            {
                return false;
            }

            if (IsTisoArena(arenaName))
            {
                return true;
            }

            if (string.Equals(arenaName, HoGLoggerConditions.PaleCourtDryyaScene, StringComparison.Ordinal) ||
                string.Equals(arenaName, HoGLoggerConditions.PaleCourtHegemolScene, StringComparison.Ordinal) ||
                string.Equals(arenaName, HoGLoggerConditions.PaleCourtZemerScene, StringComparison.Ordinal) ||
                string.Equals(arenaName, HoGLoggerConditions.PaleCourtIsmaScene, StringComparison.Ordinal) ||
                string.Equals(arenaName, HoGLoggerConditions.ChampionsCallScene, StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(arenaName, HoGLoggerConditions.PaleCourtWhiteDefenderScene, StringComparison.Ordinal))
            {
                return string.Equals(lastSceneBeforeArena, HoGLoggerConditions.PaleCourtEntryScene, StringComparison.Ordinal);
            }

            return false;
        }

        private static bool IsTisoArena(string arenaName) =>
            string.Equals(arenaName, "GG_Brooding_Mawlek_V", StringComparison.Ordinal) &&
            PaleCourtStatueIntegration.IsAltStatueMawlekEnabled();

        private static bool IsVariantArena(string arenaName) =>
            !string.IsNullOrEmpty(arenaName) && arenaName.EndsWith("_V", StringComparison.Ordinal);

        private static bool IsAttunedVariantAllowed(string arenaName) =>
            string.Equals(arenaName, "GG_Mantis_Lords_V", StringComparison.Ordinal) ||
            (string.Equals(arenaName, "GG_Brooding_Mawlek_V", StringComparison.Ordinal) &&
             PaleCourtStatueIntegration.IsAltStatueMawlekEnabled());

        private static void EnsureCanvasSpritesLoaded()
        {
            if (CustomCanvas.flagSpriteTrue == null)
            {
                CustomCanvas.flagSpriteTrue = CustomCanvas.LoadEmbeddedSprite("ElegantKey.png");
            }
            if (CustomCanvas.flagSpriteFalse == null)
            {
                CustomCanvas.flagSpriteFalse = CustomCanvas.LoadEmbeddedSprite("Geo.png");
            }
        }
        private static BossHpState GetBossHpState(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName) || !HoGStoragePlanner.RequiresHp(sceneName))
            {
                return null;
            }

            if (!bossHpStates.TryGetValue(sceneName, out BossHpState state))
            {
                state = new BossHpState();
                bossHpStates[sceneName] = state;
            }

            return state;
        }

        private static void ResetBossHpState(string sceneName)
        {
            var state = GetBossHpState(sceneName);
            if (state == null)
            {
                return;
            }

            state.Waiting = false;
            state.Cached = null;
            state.Highest = 0;
            state.Min = int.MaxValue;
        }

        private static bool IsWaitingForHp(string sceneName)
        {
            var state = GetBossHpState(sceneName);
            return state != null && state.Waiting;
        }

        private static int? GetStoredHp(string sceneName)
        {
            var state = GetBossHpState(sceneName);
            if (state == null)
            {
                return null;
            }

            if (string.Equals(sceneName, "GG_Nailmasters", StringComparison.Ordinal) &&
                state.Min < int.MaxValue)
            {
                return state.Min;
            }

            if (state.Highest > 0)
            {
                return state.Highest;
            }

            return state.Cached;
        }
        private static void InvCheck()
        {
            bool shouldBeInvincible =
                HeroController.instance.cState.invulnerable ||
                PlayerData.instance.isInvincible ||
                HeroController.instance.cState.shadowDashing ||
                HeroController.instance.damageMode == DamageMode.HAZARD_ONLY ||
                HeroController.instance.damageMode == DamageMode.NO_DAMAGE;

            var bossList = infoBoss.GetKeysWithUniqueGameObject().Values;

            if (shouldBeInvincible && !isInvincible)
            {
                isInvincible = true;
                invTimer = 0f;

                long unixTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                // OPTIMIZED: Use StringBuilder instead of string concatenation
                StringBuilder hpInfoBuilder = new StringBuilder();
                foreach (var boss in bossList)
                {
                    hpInfoBuilder.Append($"|{infoBoss[boss].lastHP}/{infoBoss[boss].maxHP}");
                }
                damageAndInv.Add($"\u00A0+{unixTime - lastUnixTime}{hpInfoBuilder}|(INV ON)|");
            }

            if (!shouldBeInvincible && isInvincible)
            {
                isInvincible = false;
                long unixTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                // OPTIMIZED: Use StringBuilder instead of string concatenation
                StringBuilder hpInfoBuilder = new StringBuilder();
                foreach (var boss in bossList)
                {
                    hpInfoBuilder.Append($"|{infoBoss[boss].lastHP}/{infoBoss[boss].maxHP}");
                }
                string hpInfo = hpInfoBuilder.ToString();
                damageAndInv.Add($"\u00A0+{unixTime - lastUnixTime}{hpInfo}|(INV OFF, {invTimer.ToString("F3", CultureInfo.InvariantCulture)})|");
                if (invTimer > 2.6f)
                {
                    invWarnings.Add($"|{activeArena}|+{unixTime - lastUnixTime}{hpInfo}|(INV OFF, {invTimer.ToString("F3", CultureInfo.InvariantCulture)})");
                }
                invTimer = 0f;
            }

            if (isInvincible)
            {
                invTimer += Time.fixedDeltaTime;
            }
        }

        private static void EnemyUpdate()
        {
            // OPTIMIZED: Only run expensive physics query every N frames
            enemyUpdateFrameCount++;
            if (enemyUpdateFrameCount % EnemyUpdateInterval != 0)
            {
                // Still check HP changes on cached bosses
                CheckBossHpChanges();
                return;
            }

            cachedHealthManagers.Clear();

            // OPTIMIZED: Reduced search radius from 100f to 50f
            float searchRadius = 50f;
            int enemyLayer = Physics2D.AllLayers;

            Collider2D[] colliders = Physics2D.OverlapBoxAll(HeroController.instance.transform.position, Vector2.one * searchRadius, 0f, enemyLayer);

            // OPTIMIZED: Direct iteration without intermediate list
            for (int i = 0; i < colliders.Length; i++)
            {
                if (!colliders[i].gameObject.activeInHierarchy)
                {
                    continue;
                }

                HKHealthManager enemyHealthManager = colliders[i].GetComponent<HKHealthManager>();
                if (enemyHealthManager != null && enemyHealthManager.hp > 0)
                {
                    cachedHealthManagers.Add(enemyHealthManager);
                    if (!infoBoss.ContainsKey(enemyHealthManager))
                    {
                        infoBoss.Add(enemyHealthManager, (enemyHealthManager.hp, 0));
                    }
                }
            }

            CheckBossHpChanges();
        }

        private static void CheckBossHpChanges()
        {
            long unixTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            // OPTIMIZED: Use StringBuilder instead of string concatenation
            StringBuilder hpInfoBuilder = new StringBuilder();

            var bossKeys = infoBoss.GetKeysWithUniqueGameObject().Values;
            // OPTIMIZED: Direct iteration without .ToList()
            foreach (var boss in infoBoss.Keys)
            {
                if (!bossKeys.Contains(boss) && !boss.isDead)
                {
                    continue;
                }

                if (boss.hp != infoBoss[boss].lastHP)
                {
                    infoBoss[boss] = (infoBoss[boss].maxHP, boss.hp);
                    isChange = true;
                }

                hpInfoBuilder.Append($"|{infoBoss[boss].lastHP}/{infoBoss[boss].maxHP}");
            }

            if (isChange)
            {
                damageAndInv.Add($"\u00A0+{unixTime - lastUnixTime}{hpInfoBuilder}|");
            }
            isChange = false;

            infoBoss.RemoveAll(kvp => kvp.Key.isDead || kvp.Key.hp <= 0);

            if (!string.IsNullOrEmpty(activeArena) && HoGStoragePlanner.RequiresHp(activeArena))
            {
                BossHpState state = GetBossHpState(activeArena);
                if (state != null)
                {
                    int newHpMetric;
                    if (string.Equals(activeArena, HoGLoggerConditions.PaleCourtWhiteDefenderScene, StringComparison.Ordinal))
                    {
                        newHpMetric = infoBoss.GetKeysWithUniqueGameObject().Values.Sum(hm => infoBoss[hm].maxHP);
                        state.SumMax = Math.Max(state.SumMax, newHpMetric);
                    }
                    else
                    {
                        newHpMetric = infoBoss.GetKeysWithUniqueGameObject().Values.Select(hm => infoBoss[hm].maxHP).DefaultIfEmpty(0).Max();
                    }

                    if (newHpMetric > state.Highest)
                    {
                        state.Highest = newHpMetric;
                        state.Cached = newHpMetric;
                        if (state.Waiting)
                        {
                            HoGStoragePlan plan = HoGStoragePlanner.GetPlan(activeArena, AheSettingsManager.CurrentSnapshot, newHpMetric, lastSceneBeforeArena);
                            ApplyHoGStoragePlan(plan);
                        }
                    }
                }
            }
        }

        private static void MonitorHeroHealth()
        {
            if (!isLogging || writer == null)
            {
                return;
            }

            string roomName = GameManager.instance?.sceneName ?? activeArena;
            hitWarnTracker.Update(writer, roomName, lastUnixTime);
            FlushWarningsIfNeeded(hitWarnBuffer, hitWarnTracker.Warnings, hitWarnTracker.ClearWarnings);
            charmsChangeTracker.Update(activeArena, lastUnixTime);
        }

        private static void MonitorDebugModUi()
        {
            if (!isLogging || writer == null)
            {
                return;
            }

            debugModEventsTracker.Update(writer, activeArena, lastUnixTime);
        }

        private static void MonitorTimeScale()
        {
            if (!isLogging || writer == null)
            {
                speedWarnTracker.Reset(Mathf.Max(Time.timeScale, 0f));
                speedWarnTracker.ClearWarnings();
                return;
            }

            speedWarnTracker.Update(writer, activeArena, lastUnixTime);
            FlushWarningsIfNeeded(speedWarnBuffer, speedWarnTracker.Warnings, speedWarnTracker.ClearWarnings);
        }

        private static void FlushWarningsIfNeeded(BufferedLogSection buffer, IReadOnlyList<string> warnings, Action clearAction)
        {
            if (buffer == null || warnings == null)
            {
                return;
            }

            if (warnings.Count < BufferedSectionThreshold)
            {
                return;
            }

            buffer.AddRange(warnings);
            clearAction?.Invoke();
        }

        private static void FlushKeyLogBufferIfNeeded(long now, bool force = false)
        {
            if (writer == null || keyLogBuffer.Count == 0)
            {
                return;
            }

            if (!force)
            {
                if (now - lastKeyLogFlushTime < KeyLogFlushIntervalMs && keyLogBuffer.Count < KeyLogFlushBatchSize)
                {
                    return;
                }
            }

            foreach (string entry in keyLogBuffer)
            {
                LogWrite.EncryptedLine(writer, entry);
            }

            keyLogBuffer.Clear();
            lastKeyLogFlushTime = now;
        }

        private static void LogDebugHotkeyActivation(string actionName, KeyCode keyCode, long unixTime)
        {
            if (writer == null)
            {
                return;
            }

            long delta = unixTime - lastUnixTime;
            string entry = $"DebugHotkey|+{delta}|{actionName}|{keyCode}";
            LogWrite.EncryptedLine(writer, entry);

            string arenaName = activeArena ?? "UnknownArena";
            debugHotkeyEvents.Add($"  |{arenaName}|+{delta}|{actionName} ({keyCode})");
        }



        private static void LogDebugModUiEvent()
        {
            
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
                        debugKillAllHook = new Hook(killAll, typeof(HoGLogger).GetMethod(nameof(DebugKillAllDetour), BindingFlags.Static | BindingFlags.NonPublic));
                    }
                }

                if (debugKillSelfHook == null)
                {
                    MethodInfo killSelf = bindableType.GetMethod("KillSelf", BindingFlags.Public | BindingFlags.Static);
                    if (killSelf != null)
                    {
                        debugKillSelfHook = new Hook(killSelf, typeof(HoGLogger).GetMethod(nameof(DebugKillSelfDetour), BindingFlags.Static | BindingFlags.NonPublic));
                    }
                }
            }
            catch (Exception e)
            {
                Modding.Logger.LogWarn($"HoGLogger: failed to hook DebugMod functions: {e.Message}");
            }
        }

        private static void DebugKillAllDetour(Action orig)
        {
            orig();
            if (isLogging && writer != null)
            {
                debugMenuTracker.LogManualChange(writer, activeArena, lastUnixTime, "Cheats/Kill All", null, "Executed");
            }
        }

        private static void DebugKillSelfDetour(Action orig)
        {
            orig();
            if (isLogging && writer != null)
            {
                debugMenuTracker.LogManualChange(writer, activeArena, lastUnixTime, "Cheats/Kill Self", null, "Executed");
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

        private static Type FindTypeByName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(typeName, false);
                if (type != null)
                {
                    return type;
                }
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type type in assembly.GetTypes())
                    {
                        if (type != null && string.Equals(type.Name, typeName, StringComparison.Ordinal))
                        {
                            return type;
                        }
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    foreach (Type type in ex.Types)
                    {
                        if (type != null && string.Equals(type.Name, typeName, StringComparison.Ordinal))
                        {
                            return type;
                        }
                    }
                }
                catch
                {
                    
                }
            }

            return null;
        }

        private static void InitializeDebugHotkeys()
        {
            debugHotkeysTracker.InitializeBindings();
            debugHotkeysByKey = new Dictionary<KeyCode, List<string>>();
            foreach (var pair in debugHotkeysTracker.ActionsByKey)
            {
                debugHotkeysByKey[pair.Key] = new List<string>(pair.Value);
            }
            debugHotkeyBindings = new List<string>(debugHotkeysTracker.Bindings);
            debugHotkeyEvents = new List<string>(debugHotkeysTracker.Activations);
        }

        private sealed class BossHpState
        {
            public bool Waiting;
            public int? Cached;
            public int Highest;
            public int SumMax;
            public int Min = int.MaxValue;
        }

        internal static CustomCanvas GetActiveCanvas() => customCanvas;
    }
}

#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
#endif
