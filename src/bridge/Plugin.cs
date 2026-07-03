using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace TaskbarHeroModMenu;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "xoker.taskbarhero.modmenu";
    public const string PluginName = "TaskbarHero Trainer Bridge";
    public const string PluginVersion = "1.8.5";
    public const string PipeName = "TaskbarHeroTrainerPipe";
    private static readonly bool EnableRuntimeHarmonyPatches = false;
    internal static readonly bool EnableForcedSaveRequests = false;

    internal static ManualLogSource LogSource;
    private static ModActions _actions;
    private static TrainerPipeServer _pipeServer;
    private static Harmony _safeLifecycleHarmony;
    private static int _safeGameplayPatchCount;
    private static int _shutdownStarted;

    internal static bool IsShuttingDown => _shutdownStarted != 0;
    internal static bool RuntimeHarmonyPatchesEnabled => EnableRuntimeHarmonyPatches;
    internal static int SafeGameplayPatchCount => _safeGameplayPatchCount;

    public override void Load()
    {
        LogSource = Log;
        _actions = new ModActions();
        _pipeServer = new TrainerPipeServer(_actions);

        LogSource.LogInfo($"{PluginName} {PluginVersion} loading");
        FileLog($"{PluginName} {PluginVersion} Load()");
        LogSource.LogInfo("Bridge Load entered");

        if (EnableRuntimeHarmonyPatches)
        {
            try
            {
                new Harmony(PluginGuid).PatchAll(typeof(Plugin).Assembly);
                FileLog("Harmony patches loaded");
                LogSource.LogInfo("Harmony patches loaded");
            }
            catch (Exception ex)
            {
                FileLog("Harmony patch load failed: " + ex);
                LogSource.LogError("Harmony patch load failed: " + ex);
            }
        }
        else
        {
            FileLog("Harmony runtime patches disabled for updated TaskbarHero build stability.");
            LogSource.LogInfo("Harmony runtime patches disabled for updated TaskbarHero build stability.");
            InstallSafeLifecyclePatches();
        }

        LogSource.LogInfo("Starting trainer pipe server");
        _pipeServer.Start();
        LogSource.LogInfo("Trainer pipe server start requested");
    }

    private static void InstallSafeLifecyclePatches()
    {
        try
        {
            _safeLifecycleHarmony = new Harmony(PluginGuid + ".safe_lifecycle");
            var postLoad = AccessTools.Method(typeof(global::TaskbarHero.PlayerSaveData), nameof(global::TaskbarHero.PlayerSaveData.PostLoad));
            var preSave = AccessTools.Method(typeof(global::TaskbarHero.PlayerSaveData), nameof(global::TaskbarHero.PlayerSaveData.PreSave));
            if (postLoad != null)
            {
                _safeLifecycleHarmony.Patch(postLoad, postfix: new HarmonyMethod(typeof(TrainerPlayerSaveDataPostLoadPatch), nameof(TrainerPlayerSaveDataPostLoadPatch.Postfix)));
            }

            if (preSave != null)
            {
                _safeLifecycleHarmony.Patch(preSave, postfix: new HarmonyMethod(typeof(TrainerPlayerSaveDataPreSavePatch), nameof(TrainerPlayerSaveDataPreSavePatch.Postfix)));
            }

            int stashUiPagePatches = 0;
            stashUiPagePatches += TryPatchPostfix(_safeLifecycleHarmony, AccessTools.Method(typeof(global::TaskbarHero.UI.UI_RemakeStash), "hnd"), typeof(TrainerStashUiPageUnlockPatch), nameof(TrainerStashUiPageUnlockPatch.Postfix));
            stashUiPagePatches += TryPatchPostfix(_safeLifecycleHarmony, AccessTools.Method(typeof(global::TaskbarHero.UI.UI_RemakeStash), "OnEnable"), typeof(TrainerStashUiPageUnlockPatch), nameof(TrainerStashUiPageUnlockPatch.Postfix));
            int tradeShipUiPatches = 0;
            int gameplayPatches = InstallSelectiveGameplayPatches(_safeLifecycleHarmony);
            _safeGameplayPatchCount = gameplayPatches;
            FileLog($"Safe save lifecycle patches loaded: PostLoad={postLoad != null}, PreSave={preSave != null}, StashPageUI={stashUiPagePatches}, TradeShipUI={tradeShipUiPatches}, Gameplay={gameplayPatches}.");
            LogSource.LogInfo("Safe save lifecycle patches loaded");
        }
        catch (Exception ex)
        {
            FileLog("Safe lifecycle patch load failed: " + ex);
            LogSource.LogError("Safe lifecycle patch load failed: " + ex);
        }
    }

    private static int TryPatchPostfix(Harmony harmony, MethodInfo target, Type patchType, string patchMethod)
    {
        if (harmony == null || target == null)
        {
            return 0;
        }

        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(patchType, patchMethod));
            return 1;
        }
        catch (Exception ex)
        {
            FileLog($"Safe patch failed for {target.DeclaringType?.FullName}.{target.Name}: {ex.Message}");
            return 0;
        }
    }

    private static int InstallSelectiveGameplayPatches(Harmony harmony)
    {
        int patched = 0;
        patched += TryPatchClass(harmony, typeof(GodModeHeroDamagePatch));

        return patched;
    }

    private static int TryPatchClass(Harmony harmony, Type patchType)
    {
        if (harmony == null || patchType == null)
        {
            return 0;
        }

        try
        {
            var methods = harmony.CreateClassProcessor(patchType).Patch();
            return methods?.Count ?? 0;
        }
        catch (Exception ex)
        {
            FileLog($"Safe patch class failed for {patchType.FullName}: {ex.Message}");
            return 0;
        }
    }

    public override bool Unload()
    {
        BeginShutdown("BepInEx unload");
        try
        {
            _safeLifecycleHarmony?.UnpatchSelf();
        }
        catch
        {
        }

        FileLog($"{PluginName} {PluginVersion} Unload()");
        return true;
    }

    internal static void BeginShutdown(string reason)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 0)
        {
            FileLog("Shutdown begin: " + reason);
        }

        _actions?.StopBackgroundActions();
        ModActions.BeginShutdown();
        _pipeServer?.Stop();
    }

    internal static void FileLog(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Paths.PluginPath, "TaskbarHeroModMenu.runtime.log"),
                $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never prevent the bridge from loading.
        }
    }
}

internal sealed class TrainerPipeServer
{
    private readonly ModActions _actions;
    private Thread _thread;
    private volatile bool _stopping;

    public TrainerPipeServer(ModActions actions)
    {
        _actions = actions;
    }

    public void Start()
    {
        Plugin.FileLog("Pipe server starting");
        Plugin.LogSource?.LogInfo("Pipe server starting");
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "TaskbarHeroTrainerPipe"
        };
        _thread.Start();
    }

    public void Stop()
    {
        _stopping = true;

        try
        {
            using var client = new NamedPipeClientStream(".", Plugin.PipeName, PipeDirection.Out);
            client.Connect(200);
        }
        catch
        {
            // Best-effort unblock for game shutdown.
        }

        try
        {
            if (_thread != null && _thread.IsAlive && Thread.CurrentThread != _thread)
            {
                _thread.Join(500);
            }
        }
        catch
        {
            // Shutdown must keep moving even if the pipe thread is already gone.
        }
    }

    private void ThreadMain()
    {
        while (!_stopping)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    Plugin.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);

                Plugin.FileLog("Pipe server listening");
                Plugin.LogSource?.LogInfo("Pipe server listening");
                pipe.WaitForConnection();
                if (_stopping)
                {
                    break;
                }

                using var reader = new StreamReader(pipe);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                string command = reader.ReadLine() ?? string.Empty;
                string response = _actions.ExecuteCommand(command);
                writer.WriteLine(SingleLine(response));
            }
            catch (Exception ex)
            {
                if (_stopping || Plugin.IsShuttingDown)
                {
                    break;
                }

                Plugin.FileLog("Pipe server error: " + ex);
                Plugin.LogSource?.LogError("Pipe server error: " + ex);
                Thread.Sleep(500);
            }
        }
    }

    private static string SingleLine(string text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? "Sin respuesta."
            : text.Replace('\r', ' ').Replace('\n', ' ');
    }
}

internal sealed class ModActions
{
    internal static volatile bool ForceHeroLevelPersistence;
    internal static volatile bool ForceHeroSavePersistence;
    internal static volatile bool IsGameQuitting;

    private const long DefaultCurrencyAmount = 999_999_999;
    private const int DefaultHeroLevel = 50;
    private const int DefaultHeroAbilityPoints = 0;
    private const int DefaultHeroAllocatedAbilityPoints = 999;
    private const int MaxItemClonesPerClick = 20;
    private const int FullSetEnchantSlotsPerItem = 6;
    private const int FullSetEnchantSlotsPerRecipe = 2;
    private const int EnchantCountSlotCount = 3;
    private const int ClassGemPackPerRecipe = 20;
    private const float DefaultFixedDeltaTime = 0.02f;
    private const float MinGameSpeed = 0.1f;
    private const float MaxGameSpeed = 10f;
    private const float GameSpeedEpsilon = 0.001f;
    private const int GameSpeedStatusBoostSource = 941415;
    private const int StashPageUnlockSource = 941416;
    private const int GodModeStatusBoostSource = 941417;
    private const int TargetStashPageUnlockCount = 99;
    private const int MaxSafeTradeShipSlots = 24;
    private const string PreferredHeroFormationFileName = "preferred_hero_formation.txt";

    private static readonly string[] SharedClassGearTypes =
    {
        "HELMET",
        "ARMOR",
        "GLOVES",
        "BOOTS",
        "AMULET",
        "EARING",
        "RING",
        "BRACER"
    };

    internal static volatile bool OneHitKillEnabled;
    internal static volatile bool GodModeEnabled;
    internal static volatile bool ForceHeroUnlockChecks = true;
    internal static volatile bool ForceDlcOwnershipChecks = true;
    internal static volatile bool ForceSkillPointPersistence;
    internal static volatile int ForcedSkillPoints;
    private static readonly int[] DefaultPersistentHeroFormation = { 101, 601, 501 };
    private static readonly Lazy<MethodInfo> AccountStatusManagerGetterMethod = new(() => AccessTools.PropertyGetter(typeof(global::zb), "bfuz") ?? AccessTools.Method(typeof(global::zb), "kqe"));
    private static int _accountStatusLookupLogCount;
    private readonly object _sync = new();
    private static float _desiredGameSpeed = 1f;
    private static long _lastGameSpeedReapplyLogTick;
    private static long _lastGodModeReapplyLogTick;
    private static long _lastStashPageUiLogTick;
    private static global::TaskbarHero.PlayerSaveData _lastKnownSaveData;
    private readonly Thread _gameplayReapplyThread;
    private volatile bool _stopBackgroundActions;

    [ThreadStatic]
    private static bool _il2cppAttached;

    private struct SlotUnlockResult
    {
        public int InventoryChanged;
        public int StashChanged;
        public int TradeShipChanged;
        public int TradeShipAdded;
        public int TradeShipCatalogCount;
        public int RuntimeInventory;
        public int RuntimeStash;
        public int RuntimeStashPages;
        public int RuntimeStashTabs;
        public int RuntimeTradeShipUi;
        public string RuntimeStashPageDetail;
    }

    public ModActions()
    {
        _gameplayReapplyThread = new Thread(GameplayReapplyLoop)
        {
            IsBackground = true,
            Name = "TaskbarHeroTrainerGameplayReapply"
        };
        _gameplayReapplyThread.Start();
    }

    public void StopBackgroundActions()
    {
        _stopBackgroundActions = true;
        try
        {
            if (_gameplayReapplyThread != null && _gameplayReapplyThread.IsAlive && Thread.CurrentThread != _gameplayReapplyThread)
            {
                _gameplayReapplyThread.Join(1000);
            }
        }
        catch
        {
        }
    }

    private void GameplayReapplyLoop()
    {
        Thread.Sleep(1500);
        while (!_stopBackgroundActions && !Plugin.IsShuttingDown && !IsGameQuitting)
        {
            try
            {
                if (Math.Abs(_desiredGameSpeed - 1f) > GameSpeedEpsilon)
                {
                    ReapplyGameSpeedIfNeeded("background");
                }

                if (GodModeEnabled && ShouldRunGodModeBackgroundReapply())
                {
                    ApplyGodModeStatusSafe("background");
                    LogGodModeReapply();
                }
            }
            catch (Exception ex)
            {
                Plugin.FileLog("Gameplay reapply loop failed: " + ex.Message);
            }

            Thread.Sleep(500);
        }
    }

    private static void LogGodModeReapply()
    {
        Plugin.FileLog("God mode reaplicado desde background.");
    }

    private static bool ShouldRunGodModeBackgroundReapply()
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref _lastGodModeReapplyLogTick);
        if (now - previous < 5000)
        {
            return false;
        }

        Interlocked.Exchange(ref _lastGodModeReapplyLogTick, now);
        return true;
    }

    internal static void BeginShutdown()
    {
        IsGameQuitting = true;
        OneHitKillEnabled = false;
        GodModeEnabled = false;
        _desiredGameSpeed = 1f;
    }

    public string ExecuteCommand(string command)
    {
        if (IsGameQuitting || Plugin.IsShuttingDown)
        {
            return "El juego se esta cerrando; no ejecuto comandos del trainer.";
        }

        string normalized = (command ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.StartsWith("ADD_ITEM:", StringComparison.Ordinal))
        {
            string rawKey = normalized.Substring("ADD_ITEM:".Length);
            return int.TryParse(rawKey, out int itemKey)
                ? AddItemByKey(itemKey)
                : $"ItemKey invalido: {rawKey}";
        }

        if (normalized.StartsWith("ONE_HIT:", StringComparison.Ordinal))
        {
            string state = normalized.Substring("ONE_HIT:".Length);
            return SetOneHitKill(state == "ON" || state == "TRUE" || state == "1");
        }

        if (normalized.StartsWith("GOD_MODE:", StringComparison.Ordinal))
        {
            string state = normalized.Substring("GOD_MODE:".Length);
            return SetGodMode(state == "ON" || state == "TRUE" || state == "1");
        }

        if (normalized.StartsWith("HERO_BYPASS:", StringComparison.Ordinal))
        {
            string state = normalized.Substring("HERO_BYPASS:".Length);
            return SetHeroBypass(state == "ON" || state == "TRUE" || state == "1");
        }

        if (normalized.StartsWith("BEST_GEAR:", StringComparison.Ordinal))
        {
            string classType = command.Substring("BEST_GEAR:".Length).Trim();
            return AddBestClassGearSet(classType);
        }

        if (normalized.StartsWith("FULL_BEST_GEAR:", StringComparison.Ordinal))
        {
            string classType = command.Substring("FULL_BEST_GEAR:".Length).Trim();
            return AddFullBestClassGearSet(classType);
        }

        if (normalized.StartsWith("GEM_PACK:", StringComparison.Ordinal))
        {
            string classType = command.Substring("GEM_PACK:".Length).Trim();
            return SocketEquippedClassGems(classType);
        }

        if (normalized.StartsWith("SOCKET_EQUIPPED_GEMS:", StringComparison.Ordinal))
        {
            string classType = command.Substring("SOCKET_EQUIPPED_GEMS:".Length).Trim();
            return SocketEquippedClassGems(classType);
        }

        if (normalized.StartsWith("GAME_SPEED:", StringComparison.Ordinal))
        {
            string rawSpeed = command.Substring("GAME_SPEED:".Length).Trim();
            return TryParseSpeed(rawSpeed, out float speed)
                ? SetGameSpeed(speed)
                : $"Velocidad invalida: {rawSpeed}";
        }

        if (normalized == "GAME_SPEED_STATUS")
        {
            return GetGameSpeedStatus();
        }

        if (normalized.StartsWith("HERO_ABILITY_POINTS:", StringComparison.Ordinal))
        {
            string rawPoints = normalized.Substring("HERO_ABILITY_POINTS:".Length);
            return int.TryParse(rawPoints, out int points)
                ? SetHeroAbilityPoints(points)
                : $"Puntos de habilidad invalidos: {rawPoints}";
        }

        return normalized switch
        {
            "PING" => "Bridge listo.",
            "REFRESH" => Refresh(),
            "RESTORE_WINDOW" => "RESTORE_WINDOW desactivado: el trainer ya no modifica tamano ni posicion de la ventana.",
            "CURRENCIES" => SetAllCurrencies(),
            "HEROES" => SetAllHeroes(),
            "FIX_EQUIPPED_DUPES" => FixEquippedItemDuplicates(),
            "REPAIR_EQUIPPED_DUPES" => RepairEquippedItemDuplicates(),
            "RESTORE_HEROES" => RestorePersistentHeroes(),
            "UNLOCK_HEROES_ONLY" => UnlockHeroesOnly(),
            "SKILL_POINTS_0" => SetHeroAbilityPoints(0),
            "SKILL_POINTS_999" => SetHeroAbilityPoints(999),
            "UNLOCK_SLOTS" => UnlockInventoryAndStash(),
            "UNLOCK_TRADE_SHIP" => "Trade Ship no se desbloquea desde el trainer: Steam/backend valida esos slots y rechaza los no autorizados.",
            "LIST_ITEM_KEYS" => ListKnownItemKeys(),
            "LIST_BEST_GEAR" => ListBestClassGearSets(),
            "GEM_PACK" => "Usa SOCKET_EQUIPPED_GEMS:Knight/Ranger/Sorcerer/Priest/Hunter/Slayer.",
            "BEST_GEAR_KNIGHT" => AddBestClassGearSet("Knight"),
            "BEST_GEAR_RANGER" => AddBestClassGearSet("Ranger"),
            "BEST_GEAR_SORCERER" => AddBestClassGearSet("Sorcerer"),
            "BEST_GEAR_PRIEST" => AddBestClassGearSet("Priest"),
            "BEST_GEAR_HUNTER" => AddBestClassGearSet("Hunter"),
            "BEST_GEAR_SLAYER" => AddBestClassGearSet("Slayer"),
            "GEM_PACK_KNIGHT" => SocketEquippedClassGems("Knight"),
            "GEM_PACK_RANGER" => SocketEquippedClassGems("Ranger"),
            "GEM_PACK_SORCERER" => SocketEquippedClassGems("Sorcerer"),
            "GEM_PACK_PRIEST" => SocketEquippedClassGems("Priest"),
            "GEM_PACK_HUNTER" => SocketEquippedClassGems("Hunter"),
            "GEM_PACK_SLAYER" => SocketEquippedClassGems("Slayer"),
            "SOCKET_EQUIPPED_GEMS_KNIGHT" => SocketEquippedClassGems("Knight"),
            "SOCKET_EQUIPPED_GEMS_RANGER" => SocketEquippedClassGems("Ranger"),
            "SOCKET_EQUIPPED_GEMS_SORCERER" => SocketEquippedClassGems("Sorcerer"),
            "SOCKET_EQUIPPED_GEMS_PRIEST" => SocketEquippedClassGems("Priest"),
            "SOCKET_EQUIPPED_GEMS_HUNTER" => SocketEquippedClassGems("Hunter"),
            "SOCKET_EQUIPPED_GEMS_SLAYER" => SocketEquippedClassGems("Slayer"),
            "FULL_BEST_GEAR_KNIGHT" => AddFullBestClassGearSet("Knight"),
            "FULL_BEST_GEAR_RANGER" => AddFullBestClassGearSet("Ranger"),
            "FULL_BEST_GEAR_SORCERER" => AddFullBestClassGearSet("Sorcerer"),
            "FULL_BEST_GEAR_PRIEST" => AddFullBestClassGearSet("Priest"),
            "FULL_BEST_GEAR_HUNTER" => AddFullBestClassGearSet("Hunter"),
            "FULL_BEST_GEAR_SLAYER" => AddFullBestClassGearSet("Slayer"),
            "KNIGHT_COSMIC_SET" => AddBestClassGearSet("Knight"),
            "PETS" => UnlockAllPets(),
            "ONE_HIT" => SetOneHitKill(!OneHitKillEnabled),
            "GOD_MODE" => SetGodMode(!GodModeEnabled),
            _ => $"Comando no reconocido: {command}"
        };
    }

    public string Refresh()
    {
        lock (_sync)
        {
            try
            {
                var manager = GetSaveManager();
                var save = GetSaveDataOrNull();
                int currencyCount = save?.currenySaveDatas?.Count ?? 0;
                int heroCount = save?.heroSaveDatas?.Count ?? 0;
                int inventoryCount = save?.inventorySaveDatas?.Count ?? 0;
                int inventoryUnlocked = CountUnlockedInventory(save);
                int inventoryEmpty = CountEmptyUnlockedInventory(save);
                int stashCount = save?.stashSaveDatas?.Count ?? 0;
                int stashUnlocked = CountUnlockedStash(save);
                int tradeCount = save?.remakeTradingStashSaveDatas?.Count ?? 0;
                int tradeUnlocked = CountUnlockedTradeShip(save);
                int tradeCatalogCount = CountTradeShipCatalogSlots(GetDataManager());
                int itemCount = save?.itemSaveDatas?.Count ?? 0;
                int petCount = save?.PetSaveData?.Count ?? 0;
                int petUnlocked = CountUnlockedPets(save);
                int heroCatalogCount = GetDataManager()?.heroInfoData?.Count ?? 0;
                string heroLevels = BuildHeroLevelSummary(save);
                string status = manager == null
                    ? "Save manager no listo. Entra en partida y pulsa Refresh."
                    : $"Runtime listo. Monedas {currencyCount}, heroes save {heroCount}/catalogo {heroCatalogCount} [{heroLevels}], inv {inventoryUnlocked}/{inventoryCount} ({inventoryEmpty} libres), alijo {stashUnlocked}/{stashCount}, trade ship {tradeUnlocked}/{tradeCount} catalogo {tradeCatalogCount}, items {itemCount}, mascotas {petUnlocked}/{petCount}, runtime patches {(Plugin.RuntimeHarmonyPatchesEnabled ? "ON" : "OFF")}, gameplay patches {Plugin.SafeGameplayPatchCount}, save forzado {(Plugin.EnableForcedSaveRequests ? "ON" : "OFF")}, hero bypass {((Plugin.RuntimeHarmonyPatchesEnabled || !Plugin.EnableForcedSaveRequests) && ForceHeroUnlockChecks ? "ON" : "OFF")}.";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Refresh fallo", ex);
            }
        }
    }

    public string SetAllCurrencies()
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                int changed = 0;
                int runtimeChanged = 0;
                var currencies = save.currenySaveDatas;
                for (int i = 0; i < currencies.Count; i++)
                {
                    var currency = currencies[i];
                    if (currency == null)
                    {
                        continue;
                    }

                    long oldValue = currency.Quantity;
                    long newValue = AddSaturating(oldValue, DefaultCurrencyAmount);
                    currency.Quantity = newValue;
                    if (AddRuntimeCurrency(currency.Key, DefaultCurrencyAmount, out long runtimeOld, out long runtimeNow))
                    {
                        runtimeChanged++;
                        Plugin.FileLog($"Runtime currency key={currency.Key} {runtimeOld} -> {runtimeNow}");
                        currency.Quantity = runtimeNow;
                        newValue = runtimeNow;
                    }

                    changed++;
                    Plugin.FileLog($"Currency key={currency.Key} {oldValue} -> {newValue} (+{DefaultCurrencyAmount})");
                }

                string saveStatus = RequestSave();
                string status = $"Monedas sumadas +{DefaultCurrencyAmount:N0}: save {changed}, runtime {runtimeChanged}. {saveStatus}";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Set currencies fallo", ex);
            }
        }
    }

    public string SetAllHeroes()
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                if (IsSuspiciousFreshSave(save))
                {
                    string blocked = "Bloqueado HEROES: el save cargado parece partida nueva/vacia (" + DescribeSafetyShape(save) + "). Restaura backup antes de guardar.";
                    Plugin.FileLog(blocked);
                    return blocked;
                }

                ForceHeroUnlockChecks = true;
                ForceDlcOwnershipChecks = true;
                ForceHeroSavePersistence = true;
                ForceHeroLevelPersistence = true;
                int normalized = NormalizeHeroCatalogAvailability();
                int added = EnsureAllHeroSaveData(save);
                int changed = 0;
                int abilityTreeChanged = 0;
                int targetHeroLevel = GetTargetHeroLevel();
                int runtimeChanged = RefreshHeroManagerRuntime();
                var heroes = save.heroSaveDatas;
                for (int i = 0; i < heroes.Count; i++)
                {
                    var hero = heroes[i];
                    if (hero == null)
                    {
                        continue;
                    }

                    hero.HeroLevel = targetHeroLevel;
                    hero.IsUnLock = true;
                    hero.HeroExp = 0f;
                    hero.AbilityPoint = DefaultHeroAbilityPoints;
                    abilityTreeChanged += NormalizeHeroAbilityTree(hero);
                    changed++;
                    runtimeChanged += RefreshRuntimeHero(hero);
                    Plugin.FileLog($"Hero key={hero.heroKey} level={targetHeroLevel} unlocked=true freeAbilityPoints={DefaultHeroAbilityPoints} allocated={hero.AllocatedHeroAbilityPoint} groups={CountArray(hero.unlockedAttributeGroupKeys)}");
                }

                runtimeChanged += RefreshHeroManagerRuntime();
                string runtimeSkills = BuildRuntimeHeroAbilitySummary(save);
                string saveStatus = RequestSave();
                string status = $"Heroes actualizados: save {changed}, skills {abilityTreeChanged}, runtime {runtimeChanged}, anadidos {added}, catalogo normalizado {normalized}. Bypass DLC/runtime ON. {DescribeHeroCatalog()} Niveles save: {BuildHeroLevelSummary(save)}. Skills: {BuildHeroAbilitySummary(save)}. Skills runtime: {runtimeSkills}. {saveStatus}";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Set heroes fallo", ex);
            }
        }
    }

    public string UnlockHeroesOnly()
    {
        return RestorePersistentHeroes();
    }

    public string RestorePersistentHeroes()
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                string status = RestoreHeroesAndFormationLoaded(save, saveAfterChange: true, source: "manual");
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Restore heroes fallo", ex);
            }
        }
    }

    internal static void ApplySaveLifecycleUnlocks(global::TaskbarHero.PlayerSaveData save, string source)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting || save == null || save.Pointer == IntPtr.Zero)
        {
            return;
        }

        _lastKnownSaveData = save;

        if (IsSuspiciousFreshSave(save))
        {
            Plugin.FileLog($"Save lifecycle unlock blocked ({source}): suspicious save {DescribeSafetyShape(save)}.");
            return;
        }

        ForceHeroUnlockChecks = true;
        ForceDlcOwnershipChecks = true;

        int normalized = NormalizeHeroCatalogAvailability();
        int added = EnsureMissingHeroSaveDataUnlockedOnly(save);
        int unlocked = UnlockExistingHeroSaves(save);
        int skillPoints = ReapplyForcedSkillPoints(save);
        int formationChanged = RestorePreferredHeroFormation(save, out string formationSummary);
        SlotUnlockResult slots = UnlockInventoryAndStashSaveData(save);

        if (normalized > 0 || added > 0 || unlocked > 0 || skillPoints > 0 || formationChanged > 0 || slots.InventoryChanged > 0 || slots.StashChanged > 0)
        {
            Plugin.FileLog($"Save lifecycle unlock ({source}): heroes nuevos {added}, desbloqueados {unlocked}, skill points {skillPoints}, catalogo {normalized}, formacion {formationSummary}, inv {slots.InventoryChanged}, alijo {slots.StashChanged}, trade untouched, niveles {BuildHeroLevelSummary(save)}. No forced save.");
        }

        if (GodModeEnabled)
        {
            ApplyGodModeStatusSafe("SaveLifecycle/" + source);
        }
    }

    private static string RestoreHeroesAndFormationLoaded(
        global::TaskbarHero.PlayerSaveData save,
        bool saveAfterChange,
        string source)
    {
        if (IsSuspiciousFreshSave(save))
        {
            return $"Bloqueado restore heroes ({source}): el save cargado parece partida nueva/vacia ({DescribeSafetyShape(save)}). No se guardo nada.";
        }

        ForceHeroUnlockChecks = true;
        ForceDlcOwnershipChecks = true;

        int normalized = NormalizeHeroCatalogAvailability();
        int added = EnsureMissingHeroSaveDataUnlockedOnly(save);
        int unlocked = UnlockExistingHeroSaves(save);
        int formationChanged = RestorePreferredHeroFormation(save, out string formationSummary);
        bool saveChanged = added > 0 || unlocked > 0 || formationChanged > 0;

        int runtimeChanged = 0;
        if (normalized > 0 || saveChanged)
        {
            runtimeChanged += RefreshHeroManagerRuntime();
            var heroes = save?.heroSaveDatas;
            if (heroes != null)
            {
                for (int i = 0; i < heroes.Count; i++)
                {
                    var hero = heroes[i];
                    if (hero != null)
                    {
                        runtimeChanged += RefreshRuntimeHero(hero);
                    }
                }
            }
        }

        string saveStatus = saveAfterChange && saveChanged
            ? RequestSave()
            : "Sin cambios persistentes; no se guardo.";
        return $"Heroes persistentes ({source}): nuevos {added}, desbloqueados {unlocked}, formacion {formationSummary}, runtime {runtimeChanged}, catalogo normalizado {normalized}. No se tocaron items/stash/progreso. {DescribeHeroCatalog()} Niveles save: {BuildHeroLevelSummary(save)}. Skills: {BuildHeroAbilitySummary(save)}. {saveStatus}";
    }

    private static bool IsSuspiciousFreshSave(global::TaskbarHero.PlayerSaveData save)
    {
        if (save == null)
        {
            return false;
        }

        int itemCount = save.itemSaveDatas?.Count ?? 0;
        int inventoryUnlocked = CountUnlockedInventory(save);
        int inventoryEmpty = CountEmptyUnlockedInventory(save);
        int stashUnlocked = CountUnlockedStash(save);
        int heroCount = save.heroSaveDatas?.Count ?? 0;
        return itemCount == 0 &&
               heroCount > 0 &&
               inventoryUnlocked > 0 &&
               inventoryUnlocked == inventoryEmpty &&
               stashUnlocked > 0;
    }

    private static string DescribeSafetyShape(global::TaskbarHero.PlayerSaveData save)
    {
        if (save == null)
        {
            return "save=null";
        }

        return $"heroes={save.heroSaveDatas?.Count ?? 0},items={save.itemSaveDatas?.Count ?? 0},inv={CountEmptyUnlockedInventory(save)}/{CountUnlockedInventory(save)} empty/unlocked,stash={CountUnlockedStash(save)},trade={CountUnlockedTradeShip(save)}/{save?.remakeTradingStashSaveDatas?.Count ?? 0}";
    }

    private static int UnlockExistingHeroSaves(global::TaskbarHero.PlayerSaveData save)
    {
        int unlocked = 0;
        var heroes = save?.heroSaveDatas;
        if (heroes == null)
        {
            return unlocked;
        }

        for (int i = 0; i < heroes.Count; i++)
        {
            var hero = heroes[i];
            if (hero == null || hero.IsUnLock)
            {
                continue;
            }

            hero.IsUnLock = true;
            unlocked++;
        }

        return unlocked;
    }

    private static int RestorePreferredHeroFormation(global::TaskbarHero.PlayerSaveData save, out string summary)
    {
        var common = save?.commonSaveData;
        if (common == null)
        {
            summary = "sin commonSaveData";
            return 0;
        }

        int[] current = ReadArrangedHeroKeys(save);
        int[] preferred = ResolvePreferredHeroFormation(save, current);
        if (preferred.Length == 0)
        {
            summary = "sin formacion preferida";
            return 0;
        }

        if (SameFormation(current, preferred))
        {
            summary = FormatHeroFormation(current) + " ok";
            return 0;
        }

        common.arrangedHeroKey = BuildIntArray(preferred);
        summary = FormatHeroFormation(current) + " -> " + FormatHeroFormation(preferred);
        return 1;
    }

    private static int[] ResolvePreferredHeroFormation(global::TaskbarHero.PlayerSaveData save, int[] current)
    {
        int[] normalizedCurrent = NormalizeFormationKeys(current, save);
        if (ContainsPersistentHero(normalizedCurrent))
        {
            WritePreferredHeroFormation(normalizedCurrent, save);
            return normalizedCurrent;
        }

        int[] stored = NormalizeFormationKeys(ReadPreferredHeroFormation(), save);
        if (stored.Length > 0)
        {
            return stored;
        }

        return NormalizeFormationKeys(DefaultPersistentHeroFormation, save);
    }

    private static int[] ReadArrangedHeroKeys(global::TaskbarHero.PlayerSaveData save)
    {
        var raw = save?.commonSaveData?.arrangedHeroKey;
        if (raw == null || raw.Length == 0)
        {
            return Array.Empty<int>();
        }

        var values = new int[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            values[i] = raw[i];
        }

        return values;
    }

    private static int[] NormalizeFormationKeys(int[] keys, global::TaskbarHero.PlayerSaveData save)
    {
        if (keys == null || keys.Length == 0)
        {
            return Array.Empty<int>();
        }

        var normalized = new System.Collections.Generic.List<int>(3);
        for (int i = 0; i < keys.Length && normalized.Count < 3; i++)
        {
            int key = keys[i];
            if (key <= 0 || normalized.Contains(key))
            {
                continue;
            }

            var hero = FindHeroSaveData(save, key);
            if (hero != null && hero.IsUnLock)
            {
                normalized.Add(key);
            }
        }

        for (int i = 0; i < DefaultPersistentHeroFormation.Length && normalized.Count < 3; i++)
        {
            int key = DefaultPersistentHeroFormation[i];
            if (normalized.Contains(key))
            {
                continue;
            }

            var hero = FindHeroSaveData(save, key);
            if (hero != null && hero.IsUnLock)
            {
                normalized.Add(key);
            }
        }

        return normalized.ToArray();
    }

    private static bool ContainsPersistentHero(int[] keys)
    {
        if (keys == null)
        {
            return false;
        }

        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i] == 501 || keys[i] == 601)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameFormation(int[] left, int[] right)
    {
        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatHeroFormation(int[] keys)
    {
        if (keys == null || keys.Length == 0)
        {
            return "vacia";
        }

        var builder = new StringBuilder();
        for (int i = 0; i < keys.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(keys[i]);
        }

        return builder.ToString();
    }

    private static Il2CppStructArray<int> BuildIntArray(int[] values)
    {
        values ??= Array.Empty<int>();
        var array = new Il2CppStructArray<int>(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            array[i] = values[i];
        }

        return array;
    }

    private static Il2CppStructArray<ulong> BuildULongArray(ulong[] values)
    {
        values ??= Array.Empty<ulong>();
        var array = new Il2CppStructArray<ulong>(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            array[i] = values[i];
        }

        return array;
    }

    private static string GetTrainerDataDirectory()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TaskbarHeroTrainer");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string GetPreferredHeroFormationPath()
    {
        return Path.Combine(GetTrainerDataDirectory(), PreferredHeroFormationFileName);
    }

    private static int[] ReadPreferredHeroFormation()
    {
        try
        {
            string path = GetPreferredHeroFormationPath();
            if (!File.Exists(path))
            {
                return Array.Empty<int>();
            }

            string text = File.ReadAllText(path).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<int>();
            }

            string[] parts = text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var keys = new System.Collections.Generic.List<int>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                if (int.TryParse(parts[i], out int key) && key > 0)
                {
                    keys.Add(key);
                }
            }

            return keys.ToArray();
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Read preferred hero formation failed: " + ex.Message);
            return Array.Empty<int>();
        }
    }

    private static void WritePreferredHeroFormation(int[] keys, global::TaskbarHero.PlayerSaveData save)
    {
        try
        {
            int[] normalized = NormalizeFormationKeys(keys, save);
            if (normalized.Length == 0)
            {
                return;
            }

            File.WriteAllText(GetPreferredHeroFormationPath(), FormatHeroFormation(normalized));
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Write preferred hero formation failed: " + ex.Message);
        }
    }

    public string SetHeroAbilityPoints(int points)
    {
        lock (_sync)
        {
            try
            {
                points = Math.Clamp(points, 0, 999);
                ForceSkillPointPersistence = true;
                ForcedSkillPoints = points;
                var save = GetSaveData();
                var heroes = save.heroSaveDatas;
                int changed = 0;
                int runtimeReloaded = 0;

                for (int i = 0; i < heroes.Count; i++)
                {
                    var hero = heroes[i];
                    if (hero == null)
                    {
                        continue;
                    }

                    if (hero.AbilityPoint != points)
                    {
                        hero.AbilityPoint = points;
                        changed++;
                    }

                    runtimeReloaded += ReloadRuntimeHeroFromSave(hero);
                }

                string saveStatus = RequestSave();
                string status = $"Puntos libres de habilidad: {points}. Heroes save {changed}, runtime reload {runtimeReloaded}. Skills: {BuildHeroAbilitySummary(save)}. {saveStatus}";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Set skill points fallo", ex);
            }
        }
    }

    public string SetOneHitKill(bool enabled)
    {
        OneHitKillEnabled = enabled;
        string status = Plugin.RuntimeHarmonyPatchesEnabled
            ? (enabled ? "One hit kill activado." : "One hit kill desactivado.")
            : "One hit kill no aplicado: runtime patches OFF en esta build estable.";
        Plugin.FileLog(status);
        return status;
    }

    public string SetGodMode(bool enabled)
    {
        GodModeEnabled = enabled;
        string status = ApplyGodModeStatus(enabled);
        Plugin.FileLog(status);
        return status;
    }

    public string SetHeroBypass(bool enabled)
    {
        ForceHeroUnlockChecks = enabled;
        ForceDlcOwnershipChecks = enabled;
        string status = Plugin.RuntimeHarmonyPatchesEnabled
            ? (enabled ? "Bypass heroes/DLC activado." : "Bypass heroes/DLC desactivado.")
            : "Bypass heroes/DLC runtime no aplicado: runtime patches OFF en esta build estable.";
        Plugin.FileLog(status);
        return status;
    }

    public string SetGameSpeed(float speed)
    {
        lock (_sync)
        {
            try
            {
                speed = Math.Clamp(speed, MinGameSpeed, MaxGameSpeed);
                _desiredGameSpeed = speed;
                AttachIl2CppThread();
                ApplyGameSpeedValue(speed);
                string combatStatus = ApplyGameSpeedCombatStatus(speed);
                string godStatus = GodModeEnabled ? " " + ApplyGodModeStatusSafe("SetGameSpeed") : string.Empty;
                string status = $"Velocidad juego/combate: {speed.ToString("0.0", CultureInfo.InvariantCulture)}x persistente. {combatStatus}";
                status += godStatus;
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Set game speed fallo", ex);
            }
        }
    }

    public string GetGameSpeedStatus()
    {
        try
        {
            AttachIl2CppThread();
            float desired = _desiredGameSpeed;
            float current = global::UnityEngine.Time.timeScale;
            float currentFixed = global::UnityEngine.Time.fixedDeltaTime;
            float targetFixed = DefaultFixedDeltaTime * desired;
            return "Velocidad runtime: deseada " +
                   desired.ToString("0.0", CultureInfo.InvariantCulture) +
                   "x, actual " +
                   current.ToString("0.0", CultureInfo.InvariantCulture) +
                   "x, fixedDelta " +
                   currentFixed.ToString("0.000", CultureInfo.InvariantCulture) +
                   " objetivo " +
                   targetFixed.ToString("0.000", CultureInfo.InvariantCulture) +
                   ".";
        }
        catch (Exception ex)
        {
            return Fail("Game speed status fallo", ex);
        }
    }

    public string UnlockInventoryAndStash()
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                SlotUnlockResult slots = UnlockInventoryAndStashInMemory(save);
                string saveStatus = RequestSave();
                string pageDetail = string.IsNullOrWhiteSpace(slots.RuntimeStashPageDetail) ? string.Empty : $" ({slots.RuntimeStashPageDetail})";
                string status = $"Slots desbloqueados: inv {slots.InventoryChanged}, alijo {slots.StashChanged}; runtime inv {slots.RuntimeInventory}, alijo {slots.RuntimeStash}; paginas stash {slots.RuntimeStashPages}, tabs UI {slots.RuntimeStashTabs}{pageDetail}. Trade Ship no se toca porque Steam/backend valida esos slots. {saveStatus}";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Unlock inventory fallo", ex);
            }
        }
    }

    public string UnlockTradeShipSlots()
    {
        return "Trade Ship no se desbloquea desde el trainer: Steam/backend valida esos slots y rechaza los no autorizados.";
    }

    private static SlotUnlockResult UnlockInventoryAndStashInMemory(global::TaskbarHero.PlayerSaveData save)
    {
        SlotUnlockResult result = UnlockInventoryAndStashSaveData(save);

        result.RuntimeInventory = RefreshRuntimeInventorySlots();
        result.RuntimeStash = RefreshRuntimeStashSlots();
        result.RuntimeStashPages = ApplyStashPageRuntimeUnlock(out result.RuntimeStashPageDetail);
        result.RuntimeStashTabs = RefreshRuntimeStashTabButtons();
        return result;
    }

    private static SlotUnlockResult UnlockTradeShipSlotsInMemory(global::TaskbarHero.PlayerSaveData save)
    {
        SlotUnlockResult result = default;
        UnlockTradeShipSlotsSaveData(save, ref result);
        result.RuntimeTradeShipUi = RefreshRuntimeTradeShipUi();
        return result;
    }

    private static SlotUnlockResult UnlockInventoryAndStashSaveData(global::TaskbarHero.PlayerSaveData save)
    {
        SlotUnlockResult result = default;

        var inventory = save?.inventorySaveDatas;
        if (inventory != null)
        {
            for (int i = 0; i < inventory.Count; i++)
            {
                var slot = inventory[i];
                if (slot == null || slot.IsUnlock)
                {
                    continue;
                }

                slot.IsUnlock = true;
                result.InventoryChanged++;
            }
        }

        var stash = save?.stashSaveDatas;
        if (stash != null)
        {
            for (int i = 0; i < stash.Count; i++)
            {
                var slot = stash[i];
                if (slot == null || slot.IsUnLock)
                {
                    continue;
                }

                slot.IsUnLock = true;
                result.StashChanged++;
            }
        }

        return result;
    }

    private static void UnlockTradeShipSlotsSaveData(global::TaskbarHero.PlayerSaveData save, ref SlotUnlockResult result)
    {
        if (save == null || save.Pointer == IntPtr.Zero)
        {
            return;
        }

        var targetIndices = CollectTradeShipCatalogIndices(GetDataManager());
        result.TradeShipCatalogCount = targetIndices.Count;

        if (save.remakeTradingStashSaveDatas == null)
        {
            save.remakeTradingStashSaveDatas = new Il2CppSystem.Collections.Generic.List<global::TaskbarHero.EasySaveData.RemakeTradingStashSaveData>();
        }

        var slots = save.remakeTradingStashSaveDatas;
        var existing = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot == null)
            {
                continue;
            }

            existing.Add(slot.Index);
            if (!slot.IsUnLock)
            {
                slot.IsUnLock = true;
                result.TradeShipChanged++;
            }
        }

        if (targetIndices.Count == 0)
        {
            return;
        }

        for (int i = 0; i < targetIndices.Count; i++)
        {
            int index = targetIndices[i];
            if (existing.Contains(index))
            {
                continue;
            }

            var slot = new global::TaskbarHero.EasySaveData.RemakeTradingStashSaveData(index)
            {
                IsUnLock = true,
                ItemUniqueId = 0UL
            };
            slots.Add(slot);
            existing.Add(index);
            result.TradeShipAdded++;
        }
    }

    private static System.Collections.Generic.List<int> CollectTradeShipCatalogIndices(global::bas data)
    {
        var indices = new System.Collections.Generic.List<int>();
        var seen = new System.Collections.Generic.HashSet<int>();
        var catalog = data?.tradingStashInfoData;
        int count = catalog?.Count ?? 0;
        for (int i = 0; i < count && indices.Count < MaxSafeTradeShipSlots; i++)
        {
            var info = catalog[i];
            if (!IsValid(info))
            {
                continue;
            }

            int index = info.Index;
            if (index < 0 || index >= MaxSafeTradeShipSlots || !seen.Add(index))
            {
                continue;
            }

            indices.Add(index);
        }

        indices.Sort();
        return indices;
    }

    private static int RefreshRuntimeTradeShipUi()
    {
        int changed = 0;
        AttachIl2CppThread();
        try
        {
            var uis = global::UnityEngine.Resources.FindObjectsOfTypeAll<global::TaskbarHero.UI.UI_TradingStash>();
            int count = uis?.Length ?? 0;
            for (int i = 0; i < count; i++)
            {
                changed += UnlockRuntimeTradeShipUi(uis[i]);
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog("RefreshRuntimeTradeShipUi failed: " + ex.Message);
        }

        return changed;
    }

    private static int UnlockRuntimeTradeShipUi(global::TaskbarHero.UI.UI_TradingStash ui)
    {
        if (!IsValid(ui))
        {
            return 0;
        }

        int changed = 0;
        try
        {
            var slots = ui.TradingStashSlot ?? ReadPrivateField<Il2CppSystem.Collections.Generic.List<global::TaskbarHero.UI.TradingStashSlot>>(ui, "TradingStashSlot");
            int count = slots?.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                changed += BindAndUnlockRuntimeTradeShipSlotVisual(slots[i], i, ui.m_slotScrollRect);
            }

            changed += UnlockRuntimeTradeShipSlotVisualsByHierarchy(ui);
        }
        catch (Exception ex)
        {
            Plugin.FileLog("UnlockRuntimeTradeShipUi failed: " + ex.Message);
        }

        return changed;
    }

    private static int BindAndUnlockRuntimeTradeShipSlotVisual(global::TaskbarHero.UI.TradingStashSlot slot, int index, global::UnityEngine.UI.ScrollRect scrollRect)
    {
        int changed = 0;
        if (!IsValid(slot))
        {
            return changed;
        }

        try
        {
            var cache = GetOrCreateTradeShipCache(index);
            if (IsValid(cache))
            {
                changed += TryRuntimeCall("TradingStashSlot.lkj " + index, () => slot.lkj(cache, index, scrollRect));
            }

            changed += UnlockRuntimeTradeShipSlotVisual(slot);
            changed += HideEmptyTradeShipCooldown(slot, index);
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"BindAndUnlockRuntimeTradeShipSlotVisual failed index={index}: {ex.Message}");
        }

        return changed;
    }

    private static global::TaskbarHero.TradingStashCache GetOrCreateTradeShipCache(int index)
    {
        var cache = FindRuntimeTradingStashSlot(index);
        if (IsValid(cache))
        {
            return cache;
        }

        var info = FindTradeShipInfo(index);
        var saveSlot = FindTradeShipSaveSlot(GetSaveDataOrNull(), index);
        if (IsValid(info) && saveSlot != null)
        {
            try
            {
                return new global::TaskbarHero.TradingStashCache(info, saveSlot);
            }
            catch (Exception ex)
            {
                Plugin.FileLog($"Create TradingStashCache failed index={index}: {ex.Message}");
            }
        }

        return null;
    }

    private static global::TaskbarHero.Data.TradingStashInfoData FindTradeShipInfo(int index)
    {
        var catalog = GetDataManager()?.tradingStashInfoData;
        int count = catalog?.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            var info = catalog[i];
            if (IsValid(info) && info.Index == index)
            {
                return info;
            }
        }

        return null;
    }

    private static global::TaskbarHero.EasySaveData.RemakeTradingStashSaveData FindTradeShipSaveSlot(global::TaskbarHero.PlayerSaveData save, int index)
    {
        var slots = save?.remakeTradingStashSaveDatas;
        int count = slots?.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            var slot = slots[i];
            if (slot != null && slot.Index == index)
            {
                return slot;
            }
        }

        return null;
    }

    private static int HideEmptyTradeShipCooldown(global::TaskbarHero.UI.TradingStashSlot slot, int index)
    {
        var saveSlot = FindTradeShipSaveSlot(GetSaveDataOrNull(), index);
        if (saveSlot == null || saveSlot.ItemUniqueId != 0UL)
        {
            return 0;
        }

        int changed = 0;
        try
        {
            if (IsValid(slot.m_coolTimeViewBox))
            {
                changed += SetGameObjectActive(slot.m_coolTimeViewBox.gameObject, false);
            }
        }
        catch { }

        changed += SetNamedChildActive(slot.transform, "CooldownBox", false);
        return changed;
    }

    private static int UnlockRuntimeTradeShipSlotVisualsByHierarchy(global::TaskbarHero.UI.UI_TradingStash ui)
    {
        int changed = 0;
        int slotIndex = 0;
        UnlockRuntimeTradeShipSlotVisualsByHierarchy(ui.transform, ui.m_slotScrollRect, ref changed, ref slotIndex, 0, 260);
        return changed;
    }

    private static void UnlockRuntimeTradeShipSlotVisualsByHierarchy(global::UnityEngine.Transform transform, global::UnityEngine.UI.ScrollRect scrollRect, ref int changed, ref int slotIndex, int depth, int maxNodes)
    {
        if (!IsValid(transform) || maxNodes <= 0 || depth > 12)
        {
            return;
        }

        var gameObject = transform.gameObject;
        var slot = gameObject.GetComponent<global::TaskbarHero.UI.TradingStashSlot>();
        bool looksLikeSlot = IsValid(slot) || (gameObject.name?.StartsWith("TradingStashSlot", StringComparison.Ordinal) ?? false);
        if (looksLikeSlot)
        {
            if (!gameObject.activeSelf)
            {
                gameObject.SetActive(true);
                changed++;
            }

            if (IsValid(slot))
            {
                changed += BindAndUnlockRuntimeTradeShipSlotVisual(slot, slotIndex, scrollRect);
                slotIndex++;
            }
            else
            {
                changed += SetNamedChildActive(transform, "Locked", false);
                changed += SetNamedChildActive(transform, "ManualLocked", false);
                changed += SetNamedChildActive(transform, "button_interaction_TryUnlock", false);
            }
        }

        int children = transform.childCount;
        for (int i = 0; i < children && maxNodes > 0; i++, maxNodes--)
        {
            UnlockRuntimeTradeShipSlotVisualsByHierarchy(transform.GetChild(i), scrollRect, ref changed, ref slotIndex, depth + 1, maxNodes);
        }
    }

    private static int SetNamedChildActive(global::UnityEngine.Transform parent, string childName, bool active)
    {
        if (!IsValid(parent))
        {
            return 0;
        }

        try
        {
            var child = parent.Find(childName);
            return IsValid(child) ? SetGameObjectActive(child.gameObject, active) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int UnlockRuntimeTradeShipSlotVisual(global::TaskbarHero.UI.TradingStashSlot slot)
    {
        if (!IsValid(slot))
        {
            return 0;
        }

        int changed = 0;
        try
        {
            var gameObject = slot.gameObject;
            if (IsValid(gameObject) && !gameObject.activeSelf)
            {
                gameObject.SetActive(true);
                changed++;
            }

            changed += SetGameObjectActive(ReadPrivateField<global::UnityEngine.GameObject>(slot, "Go_ManualLock"), false);

            var unlockButton = ReadPrivateField<global::UnityEngine.UI.Button>(slot, "button_TryUnLock");
            if (IsValid(unlockButton))
            {
                if (unlockButton.interactable)
                {
                    unlockButton.interactable = false;
                    changed++;
                }

                changed += SetGameObjectActive(unlockButton.gameObject, false);
            }

            var costText = ReadPrivateField<global::UnityEngine.Component>(slot, "text_UnlockCost");
            if (IsValid(costText))
            {
                changed += SetGameObjectActive(costText.gameObject, false);
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog("UnlockRuntimeTradeShipSlotVisual failed: " + ex.Message);
        }

        return changed;
    }

    private static T ReadPrivateField<T>(object instance, string fieldName) where T : class
    {
        if (instance == null)
        {
            return null;
        }

        try
        {
            var field = AccessTools.Field(GetDeclaredInteropType(instance), fieldName) ??
                        AccessTools.Field(instance.GetType(), fieldName);
            return field?.GetValue(instance) as T;
        }
        catch
        {
            return null;
        }
    }

    private static Type GetDeclaredInteropType(object instance)
    {
        return instance switch
        {
            global::TaskbarHero.UI.UI_TradingStash => typeof(global::TaskbarHero.UI.UI_TradingStash),
            global::TaskbarHero.UI.TradingStashSlot => typeof(global::TaskbarHero.UI.TradingStashSlot),
            global::TaskbarHero.UI.UI_RemakeStash => typeof(global::TaskbarHero.UI.UI_RemakeStash),
            global::TaskbarHero.UI.StashTabButton => typeof(global::TaskbarHero.UI.StashTabButton),
            _ => instance.GetType()
        };
    }

    private static int SetGameObjectActive(global::UnityEngine.GameObject gameObject, bool active)
    {
        if (!IsValid(gameObject) || gameObject.activeSelf == active)
        {
            return 0;
        }

        gameObject.SetActive(active);
        return 1;
    }

    public string CloneExistingItemsToEmptyInventorySlots()
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                var source = FindCloneSourceItem(save);
                if (source == null)
                {
                    return "No hay item base para clonar. Consigue un item y pulsa Refresh.";
                }

                var inventory = save.inventorySaveDatas;
                var items = save.itemSaveDatas;
                if (inventory == null || items == null)
                {
                    return "Inventario/items no cargados todavia.";
                }

                ulong nextUniqueId = FindMaxKnownUniqueId(save) + 1UL;
                int created = 0;
                for (int i = 0; i < inventory.Count && created < MaxItemClonesPerClick; i++)
                {
                    var slot = inventory[i];
                    if (slot == null || !slot.IsUnlock || slot.ItemUniqueId != 0UL)
                    {
                        continue;
                    }

                    while (nextUniqueId == 0UL || UniqueIdExists(save, nextUniqueId))
                    {
                        nextUniqueId++;
                    }

                    var itemInfo = GetItemInfo(source.ItemKey);
                    if (itemInfo == null || itemInfo.Pointer == IntPtr.Zero)
                    {
                        Plugin.FileLog($"Clone source item info not found for key={source.ItemKey}");
                        nextUniqueId++;
                        continue;
                    }

                    var clone = new global::TaskbarHero.EasySaveData.ItemSaveData(source.ItemKey, nextUniqueId, 1)
                    {
                        IsChaotic = source.IsChaotic,
                        IsBlocked = false,
                        IsServerPendingItem = false
                    };

                    EnsureItemSaveData(save, clone);
                    slot.ItemUniqueId = nextUniqueId;
                    int runtimeSteps = RegisterRuntimeInventoryItem(save, itemInfo, clone, slot);
                    created++;
                    Plugin.FileLog($"Cloned item key={source.ItemKey} unique={nextUniqueId} into inventory slot={slot.Index} runtimeSteps={runtimeSteps}");
                    nextUniqueId++;
                }

                string saveStatus = created == 0 ? string.Empty : " " + RequestSave();
                string status = created == 0
                    ? "No hay slots de inventario desbloqueados y vacios."
                    : $"Items clonados: {created}.{saveStatus}";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Clone items fallo", ex);
            }
        }
    }

    public string AddItemByKey(int itemKey)
    {
        lock (_sync)
        {
            try
            {
                if (itemKey <= 0)
                {
                    return $"ItemKey invalido: {itemKey}";
                }

                NormalizeGearCatalogForTrainer();
                var itemInfo = GetItemInfo(itemKey);
                if (itemInfo == null || itemInfo.Pointer == IntPtr.Zero)
                {
                    return $"ItemKey {itemKey} no esta en el catalogo cargado.";
                }

                var save = GetSaveData();
                var inventory = save.inventorySaveDatas;
                var items = save.itemSaveDatas;
                if (inventory == null || items == null)
                {
                    return "Inventario/items no cargados todavia.";
                }

                var slot = FindFirstEmptyUnlockedInventorySlot(save);
                if (slot == null)
                {
                    return "No hay slot desbloqueado y vacio. Usa Unlock inventory/stash primero.";
                }

                ulong uniqueId = FindMaxKnownUniqueId(save) + 1UL;
                while (uniqueId == 0UL || UniqueIdExists(save, uniqueId))
                {
                    uniqueId++;
                }

                var newItem = new global::TaskbarHero.EasySaveData.ItemSaveData(itemKey, uniqueId, 1)
                {
                    IsChaotic = false,
                    IsBlocked = false,
                    IsServerPendingItem = false
                };

                EnsureItemSaveData(save, newItem);
                slot.ItemUniqueId = uniqueId;
                int runtimeSteps = RegisterRuntimeInventoryItem(save, itemInfo, newItem, slot);

                string saveStatus = RequestSave();
                string status = $"Item creado: key {itemKey}, tipo {itemInfo.ITEMTYPE}, nivel {itemInfo.Level}, unique {uniqueId}, slot {slot.Index}, runtime {runtimeSteps}. {saveStatus}";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Add item fallo", ex);
            }
        }
    }

    internal static void FilterRequestedTimeScale(ref float value)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting)
        {
            return;
        }

        float desired = _desiredGameSpeed;
        if (Math.Abs(desired - 1f) <= GameSpeedEpsilon)
        {
            return;
        }

        if (value <= GameSpeedEpsilon)
        {
            return;
        }

        if (Math.Abs(value - desired) > GameSpeedEpsilon)
        {
            value = desired;
            SetFixedDeltaTimeForSpeed(desired);
            LogGameSpeedReapply("Time.timeScale setter", desired);
        }
    }

    internal static void FilterRequestedFixedDeltaTime(ref float value)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting)
        {
            return;
        }

        float desired = _desiredGameSpeed;
        if (Math.Abs(desired - 1f) <= GameSpeedEpsilon)
        {
            return;
        }

        float target = DefaultFixedDeltaTime * desired;
        if (Math.Abs(value - DefaultFixedDeltaTime) <= GameSpeedEpsilon ||
            Math.Abs(value - target) > GameSpeedEpsilon)
        {
            value = target;
        }
    }

    internal static void ReapplyGameSpeedIfNeeded(string source)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting)
        {
            return;
        }

        float desired = _desiredGameSpeed;
        if (Math.Abs(desired - 1f) <= GameSpeedEpsilon)
        {
            return;
        }

        try
        {
            AttachIl2CppThread();
            float current = global::UnityEngine.Time.timeScale;
            float currentFixed = global::UnityEngine.Time.fixedDeltaTime;
            float targetFixed = DefaultFixedDeltaTime * desired;
            if (Math.Abs(current - desired) > GameSpeedEpsilon ||
                Math.Abs(currentFixed - targetFixed) > GameSpeedEpsilon)
            {
                ApplyGameSpeedValue(desired);
                LogGameSpeedReapply(source, desired);
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"Game speed reapply failed ({source}): {ex.Message}");
        }
    }

    private static void ApplyGameSpeedValue(float speed)
    {
        global::UnityEngine.Time.timeScale = speed;
        SetFixedDeltaTimeForSpeed(speed);
    }

    private static void SetFixedDeltaTimeForSpeed(float speed)
    {
        global::UnityEngine.Time.fixedDeltaTime = DefaultFixedDeltaTime * speed;
    }

    private static void LogGameSpeedReapply(string source, float speed)
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref _lastGameSpeedReapplyLogTick);
        if (now - previous < 3000)
        {
            return;
        }

        Interlocked.Exchange(ref _lastGameSpeedReapplyLogTick, now);
        Plugin.FileLog($"Game speed reaplicado {speed.ToString("0.0", CultureInfo.InvariantCulture)}x desde {source}.");
    }

    private static string ApplyGameSpeedCombatStatus(float speed)
    {
        try
        {
            var accountStatusManager = GetRuntimeAccountStatusManager(out string lookupDetail);
            if (!IsValid(accountStatusManager))
            {
                return "Bonus de ataque de heroes pendiente: AccountStatus no listo. " + lookupDetail;
            }

            float overSpeed = Math.Max(0f, speed - 1f);
            int attackSpeedBonus = Math.Clamp((int)Math.Round(overSpeed * 250f), 0, 5000);
            int attackDamagePercentBonus = Math.Clamp((int)Math.Round(overSpeed * 250f), 0, 5000);
            int armorPercentBonus = Math.Clamp((int)Math.Round(overSpeed * 500f), 0, 10000);
            int flatAttackDamageBonus = Math.Clamp((int)Math.Round(overSpeed * 50_000f), 0, 2_000_000);
            int flatArmorBonus = Math.Clamp((int)Math.Round(overSpeed * 250_000f), 0, 10_000_000);

            var bonuses = new (global::TaskbarHero.StatusSystem.EAccountStatus Status, int Value)[]
            {
                (global::TaskbarHero.StatusSystem.EAccountStatus.AllHeroAttackSpeed, attackSpeedBonus),
                (global::TaskbarHero.StatusSystem.EAccountStatus.AllHeroAttackDamagePercent, attackDamagePercentBonus),
                (global::TaskbarHero.StatusSystem.EAccountStatus.AllHeroAttackDamage, flatAttackDamageBonus),
                (global::TaskbarHero.StatusSystem.EAccountStatus.AllHeroArmorPercent, armorPercentBonus),
                (global::TaskbarHero.StatusSystem.EAccountStatus.AllHeroArmor, flatArmorBonus)
            };

            var summary = new StringBuilder();
            for (int i = 0; i < bonuses.Length; i++)
            {
                SetAccountStatusContribution(accountStatusManager, bonuses[i].Status, bonuses[i].Value, GameSpeedStatusBoostSource);
                int after = ReadAccountStatus(accountStatusManager, bonuses[i].Status);
                if (summary.Length > 0)
                {
                    summary.Append("; ");
                }

                summary.Append(bonuses[i].Status)
                    .Append(" +")
                    .Append(bonuses[i].Value)
                    .Append(" total ")
                    .Append(after);
            }

            return overSpeed > GameSpeedEpsilon
                ? $"Modo seguro heroes aplicado: {summary}."
                : $"Modo seguro heroes limpiado: {summary}.";
        }
        catch (Exception ex)
        {
            return Fail("Game speed combat status", ex);
        }
    }

    public string AddCraftingMaterials(string rawCraftingType, int requestedTier)
    {
        lock (_sync)
        {
            try
            {
                var data = GetDataManager();
                if (!TryParseCraftingType(rawCraftingType, out var craftingType))
                {
                    return $"Tipo de elaboracion invalido: {rawCraftingType}. Usa MainWeapon/SubWeapon/Helmet/Armor/Gloves/Boots/Accessory.";
                }

                var recipe = FindCraftingRecipe(data, craftingType, requestedTier);
                if (!IsValid(recipe))
                {
                    string tierLabel = requestedTier > 0 ? requestedTier.ToString(CultureInfo.InvariantCulture) : "auto";
                    return $"No encontre receta de elaboracion para {craftingType} tier {tierLabel}.";
                }

                var requirements = ParseCraftingMaterialRequirements(recipe.Material);
                if (requirements.Count == 0)
                {
                    return $"La receta {recipe.CraftingRecipeKey} de {craftingType} no tiene materiales parseables: {recipe.Material}";
                }

                var neededSummary = new StringBuilder();
                for (int i = 0; i < requirements.Count; i++)
                {
                    var requirement = requirements[i];
                    var itemInfo = GetItemInfo(requirement.ItemKey);
                    string itemLabel = IsValid(itemInfo)
                        ? $"{requirement.ItemKey}x{requirement.Amount}:{itemInfo.GRADE}:{itemInfo.ItemSynthesisType}"
                        : $"{requirement.ItemKey}x{requirement.Amount}:catalog-missing";
                    AppendShortSummary(neededSummary, itemLabel);
                }

                string status = $"Bloqueado craft mats {craftingType}: la elaboracion del cubo valida materiales contra Steam/backend. Crear estos materiales localmente provoca 'Sin respuesta del servidor' al confirmar. No se crearon items. Receta {recipe.CraftingRecipeKey}, tier {recipe.RecipeTier}, necesita {neededSummary}. Usa materiales reales obtenidos por el juego o un flujo local-only separado.";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Add crafting materials fallo", ex);
            }
        }
    }

    public string CleanLocalCraftingMaterials()
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                if (IsSuspiciousFreshSave(save))
                {
                    string blocked = "Bloqueado clean craft mats: el save cargado parece partida nueva/vacia (" + DescribeSafetyShape(save) + "). Restaura backup antes de guardar.";
                    Plugin.FileLog(blocked);
                    return blocked;
                }

                var hints = ReadLastLoggedCraftingMaterialHints(out ulong maxUniqueBeforeCraftLine);
                if (hints.Count == 0)
                {
                    return "No encontre en el log los materiales de elaboracion creados por el trainer. No se toco el save.";
                }

                var ids = new System.Collections.Generic.HashSet<ulong>();
                var summary = new StringBuilder();
                for (int i = 0; i < hints.Count; i++)
                {
                    var hint = hints[i];
                    ulong uniqueId = FindCraftingMaterialUniqueId(save, hint, maxUniqueBeforeCraftLine, out string source);
                    if (uniqueId == 0UL)
                    {
                        AppendShortSummary(summary, $"{hint.ItemKey}@slot{hint.SlotIndex}:not-found");
                        continue;
                    }

                    ids.Add(uniqueId);
                    AppendShortSummary(summary, $"{hint.ItemKey}@slot{hint.SlotIndex}:u{uniqueId}:{source}");
                }

                if (ids.Count == 0)
                {
                    return $"No encontre materiales locales de elaboracion para limpiar. Hints: {summary}. No se toco el save.";
                }

                int inventoryCleared = ClearInventoryItems(save, ids);
                int itemSavesRemoved = RemoveItemSaves(save, ids);
                int runtimeInventory = RefreshRuntimeInventorySlots();
                string saveStatus = RequestSave();
                string status = $"Materiales locales de elaboracion eliminados: itemSave {itemSavesRemoved}, inventario {inventoryCleared}, ids {ids.Count}, runtime inv {runtimeInventory}. No se toco stash/equipo. {summary}. {saveStatus}";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Clean craft mats fallo", ex);
            }
        }
    }

    public string AddBestClassGearSet(string classType)
    {
        return AddBestClassGearSet(classType, fillEnchantSlots: false);
    }

    public string AddFullBestClassGearSet(string classType)
    {
        string gear = AddBestClassGearSet(classType, fillEnchantSlots: false);
        string gems = AddClassGemPack(classType);
        return $"{gear} {gems}";
    }

    private string AddBestClassGearSet(string classType, bool fillEnchantSlots)
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                NormalizeGearCatalogForTrainer();
                int targetLevel = GetTargetHeroLevel();
                var plan = BuildBestClassGearSet(classType, targetLevel);
                if (plan.ItemKeys.Length == 0)
                {
                    return $"No encontre gear potente para clase {classType} en el catalogo cargado.";
                }

                int preClean = ClearEquippedItemInventoryReferences(save, out int preCleanRuntime, out string preCleanSlots);
                preClean += ReconcileInventorySlotUniqueReferences(save, out string preCleanDupes);
                int created = AddItemKeysToInventory(save, plan.ItemKeys, fillEnchantSlots, plan.ClassName, out string createdSummary, out int enchantSlots);
                string preCleanSummary = preClean > 0
                    ? $" Limpieza previa inv {preClean} (equip={preCleanSlots}, dup={preCleanDupes}, rt={preCleanRuntime})."
                    : string.Empty;
                string saveStatus = created == 0 && preClean == 0 ? string.Empty : " " + RequestSave();
                string status = created == 0
                    ? $"No hay slots libres para crear el set de {plan.ClassName}.{preCleanSummary}"
                    : fillEnchantSlots
                        ? $"Set FULL {plan.ClassName} creado para nivel {targetLevel}: {created}/{plan.ItemKeys.Length} piezas, {enchantSlots} ranuras aplicadas. Armas {plan.WeaponSummary}.{preCleanSummary} {createdSummary}. Gear creado como local no Steam/no market.{saveStatus}"
                        : $"Set {plan.ClassName} potente creado para nivel {targetLevel}: {created}/{plan.ItemKeys.Length}. Armas {plan.WeaponSummary}.{preCleanSummary} {createdSummary}. Gear creado como local no Steam/no market.{saveStatus}";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Best class gear set fallo", ex);
            }
        }
    }

    public string ListBestClassGearSets()
    {
        lock (_sync)
        {
            try
            {
                NormalizeGearCatalogForTrainer();
                int targetLevel = GetTargetHeroLevel();
                string summary = BuildBestClassGearSetSummary(targetLevel);
                string status = string.IsNullOrWhiteSpace(summary)
                    ? "No encontre sets de clase en el catalogo cargado."
                    : $"BEST_GEAR<=lv{targetLevel}: {summary}";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("List best gear fallo", ex);
            }
        }
    }

    public string AddClassGemPack(string classType)
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                string normalizedClass = NormalizeClassType(classType);
                int[] itemKeys = BuildClassGemPackItemKeys(normalizedClass);
                if (itemKeys.Length == 0)
                {
                    return $"No encontre materiales/gemas reales para {normalizedClass}.";
                }

                int created = AddItemKeysToInventory(save, itemKeys, fillEnchantSlots: false, normalizedClass, out string createdSummary, out _);
                string saveStatus = created == 0 ? string.Empty : " " + RequestSave();
                string status = created == 0
                    ? $"No hay slots libres para crear gemas de {normalizedClass}."
                    : $"Gemas {normalizedClass} creadas: {created}/{itemKeys.Length} materiales reales (decoracion/grabado/inscripcion). {createdSummary}.{saveStatus}";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Class gem pack fallo", ex);
            }
        }
    }

    public string SocketBestClassGear(string classType)
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                var data = GetDataManager();
                NormalizeGearCatalogForTrainer();
                string normalizedClass = NormalizeClassType(classType);
                var heroInfo = FindHeroInfoForClass(data?.heroInfoData, normalizedClass);
                var heroSave = IsValid(heroInfo) ? FindHeroSaveData(save, heroInfo.HeroKey) : null;
                int targetLevel = GetTargetHeroLevel();
                var plan = BuildBestClassGearSet(normalizedClass, targetLevel);
                if (plan.ItemKeys.Length == 0)
                {
                    return $"No encontre set objetivo para {normalizedClass}.";
                }

                int socketed = 0;
                int enchantSlots = 0;
                int consumed = 0;
                int missing = 0;
                var summary = new StringBuilder();
                for (int i = 0; i < plan.ItemKeys.Length; i++)
                {
                    int itemKey = plan.ItemKeys[i];
                    var itemSave = FindBestOwnedItemSave(save, itemKey, heroSave);
                    if (itemSave == null)
                    {
                        missing++;
                        AppendShortSummary(summary, $"{itemKey}:missing");
                        continue;
                    }

                    var itemInfo = GetItemInfo(itemKey);
                    if (!IsValid(itemInfo))
                    {
                        missing++;
                        AppendShortSummary(summary, $"{itemKey}:catalog-missing");
                        continue;
                    }

                    int itemConsumed = 0;
                    int applied = ApplyBestCatalogGemSockets(save, itemSave, itemInfo, normalizedClass, consumeExistingMaterials: true, out itemConsumed);
                    if (applied <= 0)
                    {
                        missing++;
                        AppendShortSummary(summary, $"{itemKey}:no-gems");
                        continue;
                    }

                    socketed++;
                    enchantSlots += applied;
                    consumed += itemConsumed;
                    RefreshRuntimeItemSave(itemSave);
                    AppendShortSummary(summary, $"{itemKey}:u{itemSave.UniqueId}:slots{applied}:consumed{itemConsumed}");
                }

                int runtimeInventory = RefreshRuntimeInventorySlots();
                int runtimeStash = RefreshRuntimeStashSlots();
                int runtimeTradingStash = RefreshRuntimeTradingStashSlots();
                string saveStatus = socketed == 0 ? string.Empty : " " + RequestSave();
                string status = socketed == 0
                    ? $"No pude engarzar {plan.ClassName}: faltan piezas o materiales validos. {summary}"
                    : $"Engarzado {plan.ClassName}: {socketed}/{plan.ItemKeys.Length} piezas, {enchantSlots} ranuras validas, gemas consumidas {consumed}, faltas {missing}, runtime inv {runtimeInventory}, stash {runtimeStash}, trading {runtimeTradingStash}. {summary}.{saveStatus}";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Socket class gear fallo", ex);
            }
        }
    }

    public string SocketEquippedClassGems(string classType)
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                if (IsSuspiciousFreshSave(save))
                {
                    string blocked = "Bloqueado socket gems: el save cargado parece partida nueva/vacia (" + DescribeSafetyShape(save) + "). Restaura backup antes de guardar.";
                    Plugin.FileLog(blocked);
                    return blocked;
                }

                var data = GetDataManager();
                NormalizeGearCatalogForTrainer();
                string normalizedClass = NormalizeClassType(classType);
                var heroInfo = FindHeroInfoForClass(data?.heroInfoData, normalizedClass);
                if (!IsValid(heroInfo))
                {
                    return $"No encontre heroe para clase {normalizedClass}.";
                }

                var heroSave = FindHeroSaveData(save, heroInfo.HeroKey);
                if (heroSave == null)
                {
                    return $"No encontre save del heroe {normalizedClass} key={heroInfo.HeroKey}.";
                }

                int inspected = 0;
                int socketed = 0;
                int enchantSlots = 0;
                int missing = 0;
                int skipped = 0;
                int runtimeChanged = 0;
                int equippedIdsUpdated = 0;
                bool fallbackUsed = false;
                string targetSource = "equippedItemIds";
                var seen = new System.Collections.Generic.HashSet<ulong>();
                var targets = new System.Collections.Generic.List<EquippedSocketTarget>();
                var summary = new StringBuilder();
                var equipped = heroSave.equippedItemIds;
                int originalEquippedLength = equipped?.Length ?? 0;

                if (equipped != null)
                {
                    for (int slotIndex = 0; slotIndex < equipped.Length; slotIndex++)
                    {
                        ulong uniqueId = equipped[slotIndex];
                        if (uniqueId == 0UL)
                        {
                            skipped++;
                            continue;
                        }

                        if (!seen.Add(uniqueId))
                        {
                            skipped++;
                            continue;
                        }

                        targets.Add(new EquippedSocketTarget
                        {
                            SlotIndex = slotIndex,
                            UniqueId = uniqueId,
                            Source = targetSource
                        });
                    }
                }
                else
                {
                    AppendShortSummary(summary, "equipped-array:null");
                }

                if (targets.Count == 0)
                {
                    var fallbackTargets = FindFallbackEquippedSetTargets(
                        save,
                        heroSave,
                        normalizedClass,
                        originalEquippedLength,
                        out ulong[] fallbackEquippedIds,
                        out string fallbackSummary);
                    if (!string.IsNullOrWhiteSpace(fallbackSummary))
                    {
                        AppendShortSummary(summary, fallbackSummary);
                    }

                    if (fallbackTargets.Count > 0)
                    {
                        targets = fallbackTargets;
                        fallbackUsed = true;
                        targetSource = "fallback-set";
                        if (fallbackEquippedIds.Length > 0)
                        {
                            heroSave.equippedItemIds = BuildULongArray(fallbackEquippedIds);
                            for (int i = 0; i < fallbackEquippedIds.Length; i++)
                            {
                                if (fallbackEquippedIds[i] != 0UL)
                                {
                                    equippedIdsUpdated++;
                                }
                            }
                        }
                    }
                }
                else
                {
                    int partialFilled = FillMissingEquippedSetTargets(
                        save,
                        heroSave,
                        normalizedClass,
                        originalEquippedLength,
                        equipped,
                        targets,
                        seen,
                        out string partialFallbackSummary);
                    if (!string.IsNullOrWhiteSpace(partialFallbackSummary))
                    {
                        AppendShortSummary(summary, partialFallbackSummary);
                    }

                    if (partialFilled > 0)
                    {
                        fallbackUsed = true;
                        if (string.Equals(targetSource, "equippedItemIds", StringComparison.Ordinal))
                        {
                            targetSource = "equippedItemIds+partial-fallback";
                        }

                        equippedIdsUpdated += partialFilled;
                    }
                }

                for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
                {
                    var target = targets[targetIndex];
                    ulong uniqueId = target.UniqueId;
                    inspected++;
                    var itemSave = target.ItemSave ?? FindItemSaveDataByUniqueId(save, uniqueId);
                    if (itemSave == null)
                    {
                        missing++;
                        AppendShortSummary(summary, $"slot{target.SlotIndex}:u{uniqueId}:missing");
                        continue;
                    }

                    var itemInfo = GetItemInfo(itemSave.ItemKey);
                    if (!IsValid(itemInfo))
                    {
                        missing++;
                        AppendShortSummary(summary, $"slot{target.SlotIndex}:{itemSave.ItemKey}:catalog-missing");
                        continue;
                    }

                    if (itemInfo.ITEMTYPE != global::TaskbarHero.Data.EItemType.GEAR)
                    {
                        skipped++;
                        AppendShortSummary(summary, $"slot{target.SlotIndex}:{itemSave.ItemKey}:not-gear");
                        continue;
                    }

                    NormalizeCatalogItemForTrainer(itemInfo);
                    int ignoredConsumed;
                    int applied = ApplyBestCatalogGemSockets(
                        save,
                        itemSave,
                        itemInfo,
                        normalizedClass,
                        consumeExistingMaterials: false,
                        out ignoredConsumed);
                    if (applied <= 0)
                    {
                        missing++;
                        AppendShortSummary(summary, $"slot{target.SlotIndex}:{itemSave.ItemKey}:no-catalog-gems");
                        continue;
                    }

                    socketed++;
                    enchantSlots += applied;
                    runtimeChanged += RefreshRuntimeItemSave(itemSave);
                    AppendShortSummary(summary, $"slot{target.SlotIndex}:{itemSave.ItemKey}:u{uniqueId}:slots{applied}");
                }

                if (socketed > 0)
                {
                    runtimeChanged += RefreshRuntimeHero(heroSave);
                }

                string saveStatus = socketed == 0 ? string.Empty : " " + RequestSave();
                string sourceSummary = fallbackUsed
                    ? $"{targetSource}, equippedIds actualizados {equippedIdsUpdated}"
                    : targetSource;
                string status = socketed == 0
                    ? $"No pude engarzar equipo equipado de {normalizedClass}: origen {sourceSummary}, revisados {inspected}, faltas {missing}, saltados {skipped}. No se toco inventario ni stash. {summary}"
                    : $"Gemas aplicadas en equipo equipado {normalizedClass}: origen {sourceSummary}, piezas {socketed}, ranuras {enchantSlots}, revisados {inspected}, faltas {missing}, saltados {skipped}, runtime {runtimeChanged}. No se toco inventario ni stash. {summary}.{saveStatus}";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Socket equipped class gems fallo", ex);
            }
        }
    }

    public string FixEquippedItemDuplicates()
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                if (IsSuspiciousFreshSave(save))
                {
                    string blocked = "Bloqueado limpiar duplicados: el save cargado parece partida nueva/vacia (" + DescribeSafetyShape(save) + ").";
                    Plugin.FileLog(blocked);
                    return blocked;
                }

                string report = BuildEquippedDuplicateReport(save);
                string status = "FIX_EQUIPPED_DUPES desactivado por seguridad: no modifica slots ni guarda. " + report;
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Fix equipped dupes fallo", ex);
            }
        }
    }

    public string RepairEquippedItemDuplicates()
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                if (IsSuspiciousFreshSave(save))
                {
                    string blocked = "Bloqueado reparar duplicados: el save cargado parece partida nueva/vacia (" + DescribeSafetyShape(save) + ").";
                    Plugin.FileLog(blocked);
                    return blocked;
                }

                string before = BuildEquippedDuplicateReport(save);
                int cleared = ClearEquippedItemInventoryReferences(save, out int runtimeSteps, out string clearedSlots);
                if (cleared == 0)
                {
                    string unchanged = "No habia referencias duplicadas de items equipados en inventario. " + before;
                    Plugin.FileLog(unchanged);
                    return unchanged;
                }

                int refreshed = RefreshRuntimeInventorySlots();
                string saveStatus = RequestSave();
                string after = BuildEquippedDuplicateReport(save);
                string status = $"Reparadas referencias duplicadas de equipo: slots inventario limpiados {cleared} ({clearedSlots}), runtime {runtimeSteps + refreshed}. No se clonaron items, no se borro itemSave y no se toco stash/trade. Antes: {before} Despues: {after} {saveStatus}";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Repair equipped dupes fallo", ex);
            }
        }
    }

    public string ListKnownItemKeys()
    {
        lock (_sync)
        {
            try
            {
                NormalizeGearCatalogForTrainer();
                var data = GetDataManager();
                var list = data?.itemInfoData;
                if (list == null)
                {
                    return "Catalogo de items no cargado todavia.";
                }

                if (list.Count == 0)
                {
                    return "Catalogo de items aun vacio; espera a que el juego termine de cargar y pulsa de nuevo.";
                }

                string filePath = WriteItemCatalogFile(list);
                string examples = BuildItemKeyExamples(list);
                string status = string.IsNullOrWhiteSpace(examples)
                    ? $"Catalogo cargado con {list.Count} entradas, pero sin claves visibles."
                    : $"Catalogo items {list.Count}. Lista completa: {filePath}. {examples}";
                Plugin.FileLog(status);
                return $"ITEM_LIST_FILE|{filePath}|{status}";
            }
            catch (Exception ex)
            {
                return Fail("List item keys fallo", ex);
            }
        }
    }

    public string ListCraftingRecipes()
    {
        lock (_sync)
        {
            try
            {
                NormalizeGearCatalogForTrainer();
                var data = GetDataManager();
                var recipes = CollectCraftingRecipes(data);
                if (recipes.Count == 0)
                {
                    return "Recetas de elaboracion no cargadas todavia.";
                }

                string filePath = WriteCraftingRecipeFile(data);
                string examples = BuildCraftingRecipeExamples(data);
                string status = $"Recetas de elaboracion exportadas: {filePath}. {examples}";
                Plugin.FileLog(status);
                return $"CRAFTING_RECIPE_FILE|{filePath}|{status}";
            }
            catch (Exception ex)
            {
                return Fail("List crafting recipes fallo", ex);
            }
        }
    }

    public string DiagnoseSocketedItems()
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                var data = GetDataManager();
                string path = WriteSocketedItemDiagnosticsFile(save, data);
                int itemCount = save?.itemSaveDatas?.Count ?? 0;
                string status = $"Diagnostico de engarces generado. Items save {itemCount}. No se modifico el save.";
                Plugin.FileLog($"{status} Archivo={path}");
                return $"ITEM_LIST_FILE|{path}|{status}";
            }
            catch (Exception ex)
            {
                return Fail("Diagnostico engarces fallo", ex);
            }
        }
    }

    public string ApplyAllAndSave()
    {
        lock (_sync)
        {
            string currencies = SetAllCurrencies();
            string heroes = SetAllHeroes();
            return $"{currencies} {heroes}";
        }
    }

    public string UnlockAllPets()
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                var data = GetDataManager();
                if (data == null)
                {
                    return "Catalogo de mascotas no cargado todavia.";
                }

                if (save.PetSaveData == null)
                {
                    save.PetSaveData = new Il2CppSystem.Collections.Generic.List<global::TaskbarHero.EasySaveData.PetSaveData>();
                }

                int changed = 0;
                int runtimeChanged = 0;
                int scanned = 0;
                int misses = 0;
                for (int petKey = 1; petKey <= 10000; petKey++)
                {
                    var info = GetPetInfo(data, petKey);
                    if (!IsValid(info) || info.PetKey <= 0)
                    {
                        if (scanned > 0 && ++misses >= 250)
                        {
                            break;
                        }

                        continue;
                    }

                    misses = 0;
                    scanned++;
                    var petSave = FindPetSaveData(save, info.PetKey);
                    if (petSave == null)
                    {
                        petSave = new global::TaskbarHero.EasySaveData.PetSaveData(info.PetKey, true, true);
                        save.PetSaveData.Add(petSave);
                        changed++;
                    }
                    else
                    {
                        if (!petSave.IsUnlock || !petSave.IsViewed)
                        {
                            changed++;
                        }

                        petSave.IsUnlock = true;
                        petSave.IsViewed = true;
                    }

                    runtimeChanged += RefreshRuntimePet(info.PetKey);
                }

                string saveStatus = RequestSave();
                string status = $"Mascotas desbloqueadas: save {changed}/{scanned}, runtime {runtimeChanged}. {saveStatus}";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Unlock pets fallo", ex);
            }
        }
    }

    public string RemoveTrainerItems()
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                var ids = ReadLoggedTrainerItemIds();
                if (ids.Count == 0)
                {
                    return "No encontre ids de items creados por el trainer en el log.";
                }

                int inventoryCleared = ClearInventoryItems(save, ids);
                int stashCleared = ClearStashItems(save, ids);
                int tradingCleared = ClearTradingStashItems(save, ids);
                int itemSavesRemoved = RemoveItemSaves(save, ids);
                int runtimeInventory = RefreshRuntimeInventorySlots();
                int runtimeStash = RefreshRuntimeStashSlots();
                int runtimeTrading = RefreshRuntimeTradingStashSlots();
                string saveStatus = RequestSave();
                string status = $"Items del trainer eliminados: itemSave {itemSavesRemoved}, inv {inventoryCleared}, alijo {stashCleared}, trade {tradingCleared}; ids detectados {ids.Count}; runtime inv {runtimeInventory}, alijo {runtimeStash}, trade {runtimeTrading}. {saveStatus}";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Remove trainer items fallo", ex);
            }
        }
    }

    private static int RegisterRuntimeInventoryItem(
        global::TaskbarHero.PlayerSaveData save,
        global::TaskbarHero.Data.ItemInfoData itemInfo,
        global::TaskbarHero.EasySaveData.ItemSaveData itemSave,
        global::TaskbarHero.EasySaveData.InventorySaveData slot)
    {
        int steps = 0;
        AttachIl2CppThread();

        if (slot != null && slot.ItemUniqueId != 0UL)
        {
            steps += ClearInventorySlotReference(slot, slot.ItemUniqueId, "replace target slot");
        }

        steps += TryAddRuntimeItemByGameApi(itemSave.ItemKey, itemSave.UniqueId);
        EnsureItemSaveData(save, itemSave);
        steps += KeepInventoryItemOnlyInSlot(save, itemSave.UniqueId, slot);

        var itemCache = FindRuntimeItem(itemSave.UniqueId);
        if (!IsValid(itemCache))
        {
            try
            {
                itemCache = CreateRuntimeItemCache(itemInfo, itemSave);
                steps++;
                Plugin.FileLog($"Runtime item cache constructed key={itemSave.ItemKey} unique={itemSave.UniqueId}");
            }
            catch (Exception ex)
            {
                Plugin.FileLog($"Runtime item cache construct failed key={itemSave.ItemKey} unique={itemSave.UniqueId}: {ex.Message}");
            }
        }

        if (IsValid(itemCache))
        {
            steps += TryRuntimeCall("uc.iyp item save refresh", () => TryInvokeInstanceMethod(itemCache, "iyp", itemSave));
            steps += TryRuntimeCall("uc.fcy item save refresh", () => TryInvokeInstanceMethod(itemCache, "fcy", itemSave));
            var inventoryRuntimeType = GameType("ua");
            steps += TryRuntimeCall("ua.njw", () => TryInvokeStaticMethod(inventoryRuntimeType, "njw", itemSave.UniqueId, itemCache));
            steps += TryRuntimeCall("ua.ivg", () => TryInvokeStaticMethod(inventoryRuntimeType, "ivg", itemSave.UniqueId, itemCache));
            steps += TryRuntimeCall("ua.lf", () => TryInvokeStaticMethod(inventoryRuntimeType, "lf", itemSave.UniqueId, itemCache));

            var slotCache = FindRuntimeInventorySlot(slot.Index);
            if (IsValid(slotCache))
            {
                steps += TryRuntimeCall("vg set item", () => TryInvokeAnyInstanceMethod(slotCache, itemSave.UniqueId, "jky", "jkz"));
                steps += TryRuntimeCall("vg refresh", () => TryInvokeAnyInstanceMethod(slotCache, Array.Empty<object>(), "jrr", "gqz", "jrs", "hto", "bpb"));
                steps += TryRuntimeCall("ua.hqt", () => TryInvokeStaticMethod(inventoryRuntimeType, "hqt", itemSave.UniqueId, slotCache));
                steps += TryRuntimeCall("ua.bgv", () => TryInvokeStaticMethod(inventoryRuntimeType, "bgv", itemSave.UniqueId, slotCache));
            }

            var inventoryManager = GetLocalInventoryManager();
            if (IsValid(inventoryManager))
            {
                steps += TryRuntimeCall("LocalInventoryManager set slot", () => TryInvokeAnyInstanceMethod(inventoryManager, new object[] { slot.Index, itemSave.UniqueId }, "ksh", "ksi", "ksj"));
            }
        }

        return steps;
    }

    private static int KeepInventoryItemOnlyInSlot(
        global::TaskbarHero.PlayerSaveData save,
        ulong uniqueId,
        global::TaskbarHero.EasySaveData.InventorySaveData targetSlot)
    {
        int changed = 0;
        var inventory = save?.inventorySaveDatas;
        if (inventory == null || uniqueId == 0UL || targetSlot == null)
        {
            return changed;
        }

        for (int i = 0; i < inventory.Count; i++)
        {
            var slot = inventory[i];
            if (slot == null || slot.Index == targetSlot.Index)
            {
                continue;
            }

            if (slot.ItemUniqueId == uniqueId)
            {
                Plugin.FileLog($"Clearing duplicate runtime-created inventory slot={slot.Index} unique={uniqueId}");
                changed += ClearInventorySlotReference(slot, uniqueId, "runtime-created duplicate");
                changed++;
            }
        }

        if (targetSlot.ItemUniqueId != uniqueId)
        {
            targetSlot.ItemUniqueId = uniqueId;
            changed++;
        }

        changed += RefreshRuntimeInventorySlot(targetSlot);
        return changed;
    }

    private static int ReconcileInventorySlotUniqueReferences(global::TaskbarHero.PlayerSaveData save, out string summary)
    {
        summary = "none";
        var inventory = save?.inventorySaveDatas;
        if (inventory == null)
        {
            return 0;
        }

        var seen = new Dictionary<ulong, int>();
        var builder = new StringBuilder();
        int changed = 0;
        for (int i = 0; i < inventory.Count; i++)
        {
            var slot = inventory[i];
            if (slot == null || slot.ItemUniqueId == 0UL)
            {
                continue;
            }

            ulong uniqueId = slot.ItemUniqueId;
            if (!seen.TryGetValue(uniqueId, out int firstSlot))
            {
                seen.Add(uniqueId, slot.Index);
                continue;
            }

            changed += ClearInventorySlotReference(slot, uniqueId, "inventory duplicate");
            AppendShortSummary(builder, $"inv{slot.Index}:u{uniqueId}->kept{firstSlot}");
        }

        summary = builder.Length == 0 ? "none" : builder.ToString();
        return changed;
    }

    private static int RefreshRuntimeInventorySlot(global::TaskbarHero.EasySaveData.InventorySaveData slot)
    {
        if (slot == null)
        {
            return 0;
        }

        int changed = 0;
        var slotCache = FindRuntimeInventorySlot(slot.Index);
        if (!IsValid(slotCache))
        {
            return changed;
        }

        changed += TryRuntimeCall("vg set item reconcile", () => TryInvokeAnyInstanceMethod(slotCache, slot.ItemUniqueId, "jky", "jkz"));
        changed += TryRuntimeCall("vg refresh reconcile", () => TryInvokeAnyInstanceMethod(slotCache, Array.Empty<object>(), "jrr", "gqz", "jrs", "hto", "bpb"));
        return changed;
    }

    private static int TryAddRuntimeItemByGameApi(int itemKey, ulong uniqueId)
    {
        int steps = 0;
        bool success = false;
        var itemApiType = GameType("ue");
        success |= TryAddRuntimeItemByGameApi("ue.jac", () => TryInvokeStaticMethod(itemApiType, "jac", itemKey, uniqueId, 1, 1, false), ref steps);
        if (!success)
        {
            success |= TryAddRuntimeItemByGameApi("ue.lwa", () => TryInvokeStaticMethod(itemApiType, "lwa", itemKey, uniqueId, 1, 1, false), ref steps);
        }

        if (!success)
        {
            success |= TryAddRuntimeItemByGameApi("ue.jad existing", () =>
            {
                return TryInvokeStaticMethod(itemApiType, "jad", uniqueId);
            }, ref steps);
        }

        return steps;
    }

    private static bool TryAddRuntimeItemByGameApi(string label, Func<object> action, ref int steps)
    {
        try
        {
            var result = action();
            steps++;
            int addResult = ReadObjectIntMember(result, "AddResult", 0);
            object itemType = ReadObjectMember(result, "ItemType");
            bool itemObject = IsValid(result as Il2CppObjectBase);
            Plugin.FileLog($"{label} result type={itemType ?? result?.GetType().Name ?? "null"} addResult={addResult} object={itemObject}");
            return itemObject || addResult >= 0;
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"{label} failed: {ex.Message}");
            return false;
        }
    }

    private static Il2CppObjectBase FindRuntimeItem(ulong uniqueId)
    {
        Il2CppObjectBase item = null;
        Type itemApiType = GameType("ue");
        if (itemApiType == null)
        {
            return null;
        }

        try { item = TryInvokeStaticMethod(itemApiType, "jad", uniqueId) as Il2CppObjectBase; } catch { }
        if (IsValid(item)) { return item; }

        try { item = TryInvokeStaticMethod(itemApiType, "iev", uniqueId) as Il2CppObjectBase; } catch { }
        if (IsValid(item)) { return item; }

        try { item = TryInvokeStaticMethod(itemApiType, "oif", uniqueId) as Il2CppObjectBase; } catch { }
        if (IsValid(item)) { return item; }

        try { item = TryInvokeStaticMethod(itemApiType, "dbc", uniqueId) as Il2CppObjectBase; } catch { }
        if (IsValid(item)) { return item; }

        try { item = TryInvokeStaticMethod(itemApiType, "izz", uniqueId) as Il2CppObjectBase; } catch { }
        return IsValid(item) ? item : null;
    }

    private static global::vg FindRuntimeInventorySlot(int index)
    {
        try
        {
            var slots = TryInvokeStaticMethod(GameType("ua"), "iul");
            if (slots != null && RuntimeDictionaryContainsKey(slots, index))
            {
                var dictionarySlot = RuntimeDictionaryGetValue(slots, index) as global::vg;
                if (IsValid(dictionarySlot))
                {
                    return dictionarySlot;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"FindRuntimeInventorySlot failed: {ex.Message}");
        }

        global::vg slot = null;
        var inventoryRuntimeType = GameType("ua");
        try { slot = TryInvokeStaticMethod(inventoryRuntimeType, "iux", index) as global::vg; } catch { }
        if (IsValid(slot)) { return slot; }

        try { slot = TryInvokeStaticMethod(inventoryRuntimeType, "kvx", index) as global::vg; } catch { }
        if (IsValid(slot)) { return slot; }

        try { slot = TryInvokeStaticMethod(inventoryRuntimeType, "ghu", index) as global::vg; } catch { }
        if (IsValid(slot)) { return slot; }

        try { slot = TryInvokeStaticMethod(inventoryRuntimeType, "hej", index) as global::vg; } catch { }
        if (IsValid(slot)) { return slot; }

        try { slot = TryInvokeStaticMethod(inventoryRuntimeType, "kpg", index) as global::vg; } catch { }
        return IsValid(slot) ? slot : null;
    }

    private static Il2CppObjectBase FindRuntimeStashSlot(int index)
    {
        try
        {
            var slot = TryInvokeStaticMethod(GameType("vb+Stash") ?? GameType("Stash"), "jkc", index) as Il2CppObjectBase;
            return IsValid(slot) ? slot : null;
        }
        catch
        {
            return null;
        }
    }

    private static global::TaskbarHero.TradingStashCache FindRuntimeTradingStashSlot(int index)
    {
        global::TaskbarHero.TradingStashCache slot = null;

        var tradingStashType = GameType("vb+va") ?? GameType("va");
        try { slot = TryInvokeStaticMethod(tradingStashType, "jlm", index) as global::TaskbarHero.TradingStashCache; } catch { }
        if (IsValid(slot)) { return slot; }

        try { slot = TryInvokeStaticMethod(tradingStashType, "ivo", index) as global::TaskbarHero.TradingStashCache; } catch { }
        if (IsValid(slot)) { return slot; }

        try { slot = TryInvokeStaticMethod(tradingStashType, "bop", index) as global::TaskbarHero.TradingStashCache; } catch { }
        return IsValid(slot) ? slot : null;
    }

    private static int RefreshRuntimeInventorySlots()
    {
        int changed = 0;
        AttachIl2CppThread();
        try
        {
            for (int i = 0; i < 260; i++)
            {
                var slot = FindRuntimeInventorySlot(i);
                if (!IsValid(slot))
                {
                    continue;
                }

                changed += TryRuntimeCall("vg refresh", () => TryInvokeAnyInstanceMethod(slot, Array.Empty<object>(), "jrr", "gqz", "jrs", "hto", "bpb"));
            }

            var inventoryRuntimeType = GameType("ua");
            changed += TryRuntimeCall("ua.iuw", () => TryInvokeStaticMethod(inventoryRuntimeType, "iuw"));
            changed += TryRuntimeCall("ua.ivm", () => TryInvokeStaticMethod(inventoryRuntimeType, "ivm"));
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"RefreshRuntimeInventorySlots failed: {ex.Message}");
        }

        return changed;
    }

    private static int ReadIntArg(object[] args, int index, int fallback)
    {
        if (args == null || index < 0 || index >= args.Length || args[index] == null)
        {
            return fallback;
        }

        try
        {
            return Convert.ToInt32(args[index], CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static int RefreshRuntimeStashSlots()
    {
        int changed = 0;
        AttachIl2CppThread();
        try
        {
            for (int i = 0; i < 420; i++)
            {
                var slot = FindRuntimeStashSlot(i);
                if (!IsValid(slot))
                {
                    continue;
                }

                changed += TryRuntimeCall("StashCache.jld", () => TryInvokeInstanceMethod(slot, "jld"));
            }

            changed += TryRuntimeCall("Stash.jka", () => TryInvokeStaticMethod(GameType("vb+Stash") ?? GameType("Stash"), "jka"));
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"RefreshRuntimeStashSlots failed: {ex.Message}");
        }

        return changed;
    }

    private static int ApplyStashPageRuntimeUnlock(out string detail)
    {
        detail = string.Empty;
        try
        {
            AttachIl2CppThread();
            var accountStatusManager = GetRuntimeAccountStatusManager(out string lookupDetail);
            if (!IsValid(accountStatusManager))
            {
                detail = "AccountStatus no listo: " + lookupDetail;
                return -1;
            }

            var status = global::TaskbarHero.StatusSystem.EAccountStatus.UnlockStashPageCount;
            int before = ReadAccountStatus(accountStatusManager, status);
            SetAccountStatusContribution(accountStatusManager, status, TargetStashPageUnlockCount, StashPageUnlockSource);
            int after = ReadAccountStatus(accountStatusManager, status);
            detail = $"UnlockStashPageCount {before}->{after}";
            return after;
        }
        catch (Exception ex)
        {
            detail = "UnlockStashPageCount fallo: " + ex.Message;
            Plugin.FileLog(detail);
            return -1;
        }
    }

    private static int RefreshRuntimeStashTabButtons()
    {
        int changed = 0;
        AttachIl2CppThread();
        try
        {
            var stashUis = global::UnityEngine.Resources.FindObjectsOfTypeAll<global::TaskbarHero.UI.UI_RemakeStash>();
            int count = stashUis?.Length ?? 0;
            for (int i = 0; i < count; i++)
            {
                changed += RefreshRuntimeStashTabButtons(stashUis[i]);
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog("RefreshRuntimeStashTabButtons failed: " + ex.Message);
        }

        return changed;
    }

    private static int RefreshRuntimeStashTabButtons(global::TaskbarHero.UI.UI_RemakeStash ui)
    {
        if (!IsValid(ui))
        {
            return 0;
        }

        int changed = 0;
        try
        {
            var tabs = ui.m_stashTabButtonList;
            int count = tabs?.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                changed += UnlockRuntimeStashTabButton(tabs[i]);
            }

            changed += UnlockRuntimeStashTabButton(ui.m_dlcTabButton);
        }
        catch (Exception ex)
        {
            Plugin.FileLog("RefreshRuntimeStashTabButtons(ui) failed: " + ex.Message);
        }

        return changed;
    }

    private static int UnlockRuntimeStashTabButton(global::TaskbarHero.UI.StashTabButton tab)
    {
        if (!IsValid(tab))
        {
            return 0;
        }

        int changed = 0;
        try
        {
            if (tab.m_isLockedDlcTab)
            {
                tab.m_isLockedDlcTab = false;
                changed++;
            }

            var button = tab.m_button;
            if (IsValid(button))
            {
                if (!button.interactable)
                {
                    button.interactable = true;
                    changed++;
                }

                if (!button.enabled)
                {
                    button.enabled = true;
                    changed++;
                }
            }

            var gameObject = tab.gameObject;
            if (IsValid(gameObject) && !gameObject.activeSelf)
            {
                gameObject.SetActive(true);
                changed++;
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog("UnlockRuntimeStashTabButton failed: " + ex.Message);
        }

        return changed;
    }

    internal static void ApplyStashPageUnlockForUi(global::TaskbarHero.UI.UI_RemakeStash ui, string source)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting || !IsValid(ui))
        {
            return;
        }

        try
        {
            int pages = ApplyStashPageRuntimeUnlock(out string detail);
            int tabs = RefreshRuntimeStashTabButtons(ui);
            LogStashPageUiRefresh(source, pages, tabs, detail);
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"Stash page UI unlock failed ({source}): {ex.Message}");
        }
    }

    internal static void ApplyTradeShipUnlockForUi(global::TaskbarHero.UI.UI_TradingStash ui, string source)
    {
        Plugin.FileLog($"Trade Ship UI unlock skipped ({source}): Steam/backend controls usable slots.");
    }

    private static void LogStashPageUiRefresh(string source, int pages, int tabs, string detail)
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref _lastStashPageUiLogTick);
        if (now - previous < 3000)
        {
            return;
        }

        Interlocked.Exchange(ref _lastStashPageUiLogTick, now);
        Plugin.FileLog($"Stash page unlock UI ({source}): pages {pages}, tabs {tabs}. {detail}");
    }

    private static int RefreshRuntimeTradingStashSlots()
    {
        Plugin.FileLog("RefreshRuntimeTradingStashSlots desactivado: vb+va/remakeTradingStash provoca duplicados internos durante la carga en la version actual.");
        return 0;
    }

    private static int RefreshRuntimeHero(global::TaskbarHero.EasySaveData.HeroSaveData hero)
    {
        int changed = 0;
        AttachIl2CppThread();
        try
        {
            var cache = FindRuntimeHero(hero.heroKey);
            if (!IsValid(cache))
            {
                return changed;
            }

            changed += TryRuntimeCall("vd.jpf level", () => cache.jpf(hero.HeroLevel));
            changed += TryRuntimeCall("vd save refresh", () => RefreshRuntimeHeroFromSaveData(cache, hero));
            changed += RefreshRuntimeHeroAbilityTree(cache, hero);
            changed += TryRuntimeCall("vd refresh", () => TryInvokeInstanceMethod(cache, "jra"));
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"RefreshRuntimeHero failed key={hero.heroKey}: {ex.Message}");
        }

        return changed;
    }

    private static int ReloadRuntimeHeroFromSave(global::TaskbarHero.EasySaveData.HeroSaveData hero)
    {
        int changed = 0;
        AttachIl2CppThread();
        try
        {
            var cache = FindRuntimeHero(hero.heroKey);
            if (!IsValid(cache))
            {
                return changed;
            }

            changed += TryRuntimeCall("vd save refresh", () =>
            {
                RefreshRuntimeHeroFromSaveData(cache, hero);
            });
            changed += TryRuntimeCall("vd refresh", () => InvokeFirstInstanceMethod(cache, "jqu", "jqe", "jqv", "jqw", "jrf", "fkl", "frf", "iyr", "gye", "fkg", "dnr", "lgz"));
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"ReloadRuntimeHeroFromSave failed key={hero.heroKey}: {ex.Message}");
        }

        return changed;
    }

    internal static int ReapplyForcedHeroLevels(global::TaskbarHero.PlayerSaveData save)
    {
        if (!ForceHeroLevelPersistence)
        {
            return 0;
        }

        if (IsSuspiciousFreshSave(save))
        {
            Plugin.FileLog("PreSave hero level persistence blocked: suspicious fresh save " + DescribeSafetyShape(save));
            return 0;
        }

        int changed = 0;
        var heroes = save?.heroSaveDatas;
        if (heroes == null)
        {
            return changed;
        }

        int targetHeroLevel = GetTargetHeroLevel();
        for (int i = 0; i < heroes.Count; i++)
        {
            var hero = heroes[i];
            if (hero == null)
            {
                continue;
            }

            if (hero.HeroLevel != targetHeroLevel)
            {
                hero.HeroLevel = targetHeroLevel;
                changed++;
            }

            hero.IsUnLock = true;
            hero.HeroExp = 0f;

            changed += NormalizeHeroAbilityTree(hero);
        }

        if (changed > 0)
        {
            Plugin.FileLog($"PreSave hero level persistence reapplied: {BuildHeroLevelSummary(save)}");
        }

        return changed;
    }

    internal static int ReapplyHeroUnlocks(global::TaskbarHero.PlayerSaveData save)
    {
        if (!ForceHeroSavePersistence)
        {
            return 0;
        }

        if (IsSuspiciousFreshSave(save))
        {
            Plugin.FileLog("PreSave hero unlock persistence blocked: suspicious fresh save " + DescribeSafetyShape(save));
            return 0;
        }

        int changed = 0;
        changed += NormalizeHeroCatalogAvailability();
        changed += EnsureAllHeroSaveData(save);

        var heroes = save?.heroSaveDatas;
        if (heroes == null)
        {
            return changed;
        }

        for (int i = 0; i < heroes.Count; i++)
        {
            var hero = heroes[i];
            if (hero == null)
            {
                continue;
            }

            if (!hero.IsUnLock)
            {
                hero.IsUnLock = true;
                changed++;
            }
        }

        if (changed > 0)
        {
            Plugin.FileLog($"Hero unlock persistence reapplied: {BuildHeroLevelSummary(save)}");
        }

        return changed;
    }

    internal static int ReapplyForcedSkillPoints(global::TaskbarHero.PlayerSaveData save)
    {
        if (!ForceSkillPointPersistence)
        {
            return 0;
        }

        if (IsSuspiciousFreshSave(save))
        {
            Plugin.FileLog("Skill point persistence blocked: suspicious fresh save " + DescribeSafetyShape(save));
            return 0;
        }

        int changed = 0;
        int points = Math.Clamp(ForcedSkillPoints, 0, 999);
        var heroes = save?.heroSaveDatas;
        if (heroes == null)
        {
            return changed;
        }

        for (int i = 0; i < heroes.Count; i++)
        {
            var hero = heroes[i];
            if (hero == null)
            {
                continue;
            }

            if (hero.AbilityPoint != points)
            {
                hero.AbilityPoint = points;
                changed++;
            }
        }

        if (changed > 0)
        {
            Plugin.FileLog($"Skill point persistence reapplied: {points}. {BuildHeroAbilitySummary(save)}");
        }

        return changed;
    }

    private static string ApplyGodModeStatus(bool enabled)
    {
        try
        {
            AttachIl2CppThread();
            var accountStatusManager = GetRuntimeAccountStatusManager(out string lookupDetail);
            if (!IsValid(accountStatusManager))
            {
                return (enabled ? "God mode pendiente: " : "God mode OFF pendiente: ") + lookupDetail;
            }

            string summary = ApplyGodModeStatusContributions(accountStatusManager, enabled);
            return enabled
                ? $"God mode activado por stats cuenta. {summary}"
                : $"God mode desactivado por stats cuenta. {summary}";
        }
        catch (Exception ex)
        {
            return Fail("God mode status", ex);
        }
    }

    private static string ApplyGodModeStatusSafe(string source)
    {
        try
        {
            return ApplyGodModeStatus(GodModeEnabled) + $" Reaplicado desde {source}.";
        }
        catch (Exception ex)
        {
            string status = $"God mode reapply fallo ({source}): {ex.Message}";
            Plugin.FileLog(status);
            return status;
        }
    }

    private static string ApplyGodModeStatusContributions(global::zb accountStatusManager, bool enabled)
    {
        int armor = enabled ? 100_000_000 : 0;
        int armorPercent = enabled ? 100_000 : 0;

        var bonuses = new (global::TaskbarHero.StatusSystem.EAccountStatus Status, int Value)[]
        {
            (global::TaskbarHero.StatusSystem.EAccountStatus.AllHeroArmor, armor),
            (global::TaskbarHero.StatusSystem.EAccountStatus.AllHeroArmorPercent, armorPercent)
        };

        var summary = new StringBuilder();
        for (int i = 0; i < bonuses.Length; i++)
        {
            SetAccountStatusContribution(accountStatusManager, bonuses[i].Status, bonuses[i].Value, GodModeStatusBoostSource);
            int after = ReadAccountStatus(accountStatusManager, bonuses[i].Status);
            if (summary.Length > 0)
            {
                summary.Append("; ");
            }

            summary.Append(bonuses[i].Status)
                .Append(" +")
                .Append(bonuses[i].Value)
                .Append(" total ")
                .Append(after);
        }

        return summary.ToString();
    }

    private static int GetTargetHeroLevel()
    {
        int maxLevel = FindMaxHeroLevelFromCatalog();
        return maxLevel > 0 ? Math.Max(DefaultHeroLevel, maxLevel) : DefaultHeroLevel;
    }

    private static int FindMaxHeroLevelFromCatalog()
    {
        var data = GetDataManager();
        if (data == null)
        {
            return 0;
        }

        int maxLevel = 0;
        int missesAfterMax = 0;
        for (int level = 1; level <= 150; level++)
        {
            try
            {
                var info = GetLevelInfo(data, level);
                if (info != null && info.Level > 0)
                {
                    maxLevel = Math.Max(maxLevel, info.Level);
                    missesAfterMax = 0;
                    continue;
                }
            }
            catch
            {
                // Some builds throw for missing level rows. Stop after a stable gap.
            }

            if (maxLevel > 0)
            {
                missesAfterMax++;
                if (missesAfterMax >= 10)
                {
                    break;
                }
            }
        }

        return maxLevel;
    }

    private static int NormalizeHeroAbilityTree(global::TaskbarHero.EasySaveData.HeroSaveData hero)
    {
        if (hero == null)
        {
            return 0;
        }

        int changed = 0;
        if (hero.AllocatedHeroAbilityPoint < DefaultHeroAllocatedAbilityPoints)
        {
            hero.AllocatedHeroAbilityPoint = DefaultHeroAllocatedAbilityPoints;
            changed++;
        }

        var groupKeys = BuildHeroAttributeGroupArray(hero.heroKey);
        if (groupKeys != null && groupKeys.Length > 0 && !SameIntSet(hero.unlockedAttributeGroupKeys, groupKeys))
        {
            hero.unlockedAttributeGroupKeys = groupKeys;
            changed += groupKeys.Length;
        }

        return changed;
    }

    private static int RefreshRuntimeHeroAbilityTree(global::vd cache, global::TaskbarHero.EasySaveData.HeroSaveData hero)
    {
        int changed = 0;
        if (!IsValid(cache) || hero == null)
        {
            return changed;
        }

        changed += TryRuntimeCall("vd save ability refresh", () => RefreshRuntimeHeroFromSaveData(cache, hero));

        var groups = hero.unlockedAttributeGroupKeys;
        if (groups != null)
        {
            for (int i = 0; i < groups.Length; i++)
            {
                int groupKey = groups[i];
                if (groupKey <= 0)
                {
                    continue;
                }

                changed += TryRuntimeCall("vb.jog attribute group", () =>
                {
                    TryInvokeInstanceIntBoolMethods(cache, groupKey);
                });
            }
        }

        return changed;
    }

    private static int SetRuntimeHeroAbilityPointsExact(global::vd cache, int desired)
    {
        return TryRuntimeCall("vd save ability points refresh", () => InvokeFirstInstanceMethod(cache, "jqu", "jqe", "jqv", "jqw", "jrf", "fkl", "frf", "iyr", "gye", "fkg", "dnr", "lgz"));
    }

    private static int SetRuntimeHeroAbilityPointsExact(global::TaskbarHero.EasySaveData.HeroSaveData hero)
    {
        if (hero == null)
        {
            return 0;
        }

        int changed = 0;
        AttachIl2CppThread();
        try
        {
            var cache = FindRuntimeHero(hero.heroKey);
            if (!IsValid(cache))
            {
                return 0;
            }

            changed += SetRuntimeHeroAbilityPointsExact(cache, hero.AbilityPoint);
            changed += TryRuntimeCall("vd ability points refresh", () => TryInvokeInstanceMethod(cache, "jra"));
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"SetRuntimeHeroAbilityPointsExact failed key={hero.heroKey}: {ex.Message}");
        }

        return changed;
    }

    private static int EnsureRuntimeHeroAbilityPoints(global::vd cache, int minimum)
    {
        return minimum > 0 ? TryRuntimeCall("vd ability points refresh", () => InvokeFirstInstanceMethod(cache, "jqu", "jqe", "jqv", "jqw", "jrf", "fkl", "frf", "iyr", "gye", "fkg", "dnr", "lgz")) : 0;
    }

    private static int ReadRuntimeInt(string label, Func<int> getter, int fallback)
    {
        try
        {
            return getter();
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"{label} read failed: {ex.Message}");
            return fallback;
        }
    }

    private static object InvokeInstanceMethod(object target, string methodName, params object[] args)
    {
        if (target == null)
        {
            throw new MissingMethodException("null", methodName);
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = target.GetType();
        while (type != null)
        {
            foreach (var method in type.GetMethods(flags))
            {
                if (method.Name == methodName && TryCoerceArguments(method.GetParameters(), args, out var coerced))
                {
                    return method.Invoke(target, coerced);
                }
            }

            type = type.BaseType;
        }

        throw new MissingMethodException(target.GetType().FullName, methodName);
    }

    private static bool TryInvokeInstanceMethod(object target, string methodName, params object[] args)
    {
        try
        {
            InvokeInstanceMethod(target, methodName, args);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"Invoke {target?.GetType().Name ?? "null"}.{methodName} failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryInvokeAnyInstanceMethod(object target, object arg, params string[] methodNames)
    {
        return TryInvokeAnyInstanceMethod(target, new[] { arg }, methodNames);
    }

    private static bool TryInvokeAnyInstanceMethod(object target, object[] args, params string[] methodNames)
    {
        foreach (string methodName in methodNames)
        {
            if (TryInvokeInstanceMethod(target, methodName, args))
            {
                return true;
            }
        }

        return false;
    }

    private static int InvokeFirstInstanceMethod(object target, params string[] methodNames)
    {
        for (int i = 0; i < methodNames.Length; i++)
        {
            if (TryInvokeInstanceMethod(target, methodNames[i]))
            {
                return 1;
            }
        }

        throw new MissingMethodException(target?.GetType().FullName ?? "null", string.Join("|", methodNames));
    }

    private static T TryInvokeInstanceObject<T>(object target, string methodName, params object[] args)
        where T : class
    {
        return InvokeInstanceMethod(target, methodName, args) as T;
    }

    private static object TryInvokeStaticMethod(Type type, string methodName, params object[] args)
    {
        if (type == null)
        {
            throw new MissingMethodException("null", methodName);
        }

        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var method in type.GetMethods(flags))
        {
            if (method.Name == methodName && TryCoerceArguments(method.GetParameters(), args, out var coerced))
            {
                return method.Invoke(null, coerced);
            }
        }

        throw new MissingMethodException(type.FullName, methodName);
    }

    private static object TryInvokeStaticMethodOrNull(Type type, string methodName, params object[] args)
    {
        try
        {
            return TryInvokeStaticMethod(type, methodName, args);
        }
        catch
        {
            return null;
        }
    }

    private static object ReadStaticMember(Type type, string memberName)
    {
        if (type == null || string.IsNullOrWhiteSpace(memberName))
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            var property = type.GetProperty(memberName, flags);
            if (property != null)
            {
                return property.GetValue(null);
            }

            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                return field.GetValue(null);
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"{type.FullName}.{memberName} static read failed: {ex.Message}");
        }

        return null;
    }

    private static Type GameType(string typeName)
    {
        var assemblyType = FindTypeInAssembly(typeof(global::TaskbarHero.PlayerSaveData).Assembly, typeName)
            ?? FindTypeInAssembly(typeof(global::vd).Assembly, typeName);
        return assemblyType ?? AccessTools.TypeByName(typeName);
    }

    private static Type FindTypeInAssembly(Assembly assembly, string typeName)
    {
        if (assembly == null || string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        try
        {
            var direct = assembly.GetType(typeName);
            if (direct != null)
            {
                return direct;
            }

            foreach (var type in assembly.GetTypes())
            {
                if (type.FullName == typeName || type.Name == typeName)
                {
                    return type;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool TryCoerceArguments(ParameterInfo[] parameters, object[] args, out object[] coerced)
    {
        coerced = null;
        if (parameters == null || args == null || parameters.Length != args.Length)
        {
            return false;
        }

        coerced = new object[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            if (!TryCoerceArgument(args[i], parameters[i].ParameterType, out coerced[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCoerceArgument(object value, Type targetType, out object coerced)
    {
        coerced = value;
        if (targetType == null)
        {
            return false;
        }

        Type effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (value == null)
        {
            return !effectiveType.IsValueType;
        }

        Type valueType = value.GetType();
        if (effectiveType.IsAssignableFrom(valueType))
        {
            return true;
        }

        try
        {
            if (effectiveType.IsEnum)
            {
                coerced = Enum.ToObject(effectiveType, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return true;
            }

            if (effectiveType == typeof(IntPtr) && value is long rawPointer)
            {
                coerced = new IntPtr(rawPointer);
                return true;
            }

            if (typeof(IConvertible).IsAssignableFrom(effectiveType) && value is IConvertible)
            {
                coerced = Convert.ChangeType(value, effectiveType, CultureInfo.InvariantCulture);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static object ReadObjectMember(object target, string memberName)
    {
        if (target == null || string.IsNullOrWhiteSpace(memberName))
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = target.GetType();
        while (type != null)
        {
            var property = type.GetProperty(memberName, flags);
            if (property != null)
            {
                return property.GetValue(target);
            }

            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                return field.GetValue(target);
            }

            type = type.BaseType;
        }

        return null;
    }

    private static int ReadObjectIntMember(object target, string memberName, int fallback)
    {
        try
        {
            object value = ReadObjectMember(target, memberName);
            return value == null ? fallback : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static bool RuntimeDictionaryContainsKey(object dictionary, int key)
    {
        try
        {
            var method = dictionary?.GetType().GetMethod("ContainsKey", BindingFlags.Instance | BindingFlags.Public);
            return method != null && Convert.ToBoolean(method.Invoke(dictionary, new object[] { key }), CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }
    }

    private static object RuntimeDictionaryGetValue(object dictionary, int key)
    {
        try
        {
            var property = dictionary?.GetType().GetProperty("Item", BindingFlags.Instance | BindingFlags.Public);
            return property?.GetValue(dictionary, new object[] { key });
        }
        catch
        {
            return null;
        }
    }

    private static long ReadRuntimeLong(object target, string methodName, long fallback)
    {
        try
        {
            object value = InvokeInstanceMethod(target, methodName);
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"{target?.GetType().Name ?? "null"}.{methodName} read failed: {ex.Message}");
            return fallback;
        }
    }

    private static long ReadRuntimeLongFlexible(object target, long fallback, params string[] names)
    {
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            try
            {
                object value = ReadObjectMember(target, name);
                if (value != null)
                {
                    return Convert.ToInt64(value, CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                // Keep trying method/property names from adjacent game builds.
            }

            try
            {
                object value = InvokeInstanceMethod(target, name);
                if (value != null)
                {
                    return Convert.ToInt64(value, CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                // Keep trying method/property names from adjacent game builds.
            }
        }

        return fallback;
    }

    private static Il2CppObjectBase CreateRuntimeItemCache(
        global::TaskbarHero.Data.ItemInfoData itemInfo,
        global::TaskbarHero.EasySaveData.ItemSaveData itemSave)
    {
        Type itemCacheType = AccessTools.TypeByName("uc");
        if (itemCacheType == null)
        {
            return null;
        }

        try
        {
            return Activator.CreateInstance(itemCacheType, itemInfo, itemSave, false) as Il2CppObjectBase;
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"CreateRuntimeItemCache failed key={itemSave?.ItemKey}: {ex.Message}");
            return null;
        }
    }

    private static bool TryInvokeInstanceIntBoolMethods(object target, int value)
    {
        if (target == null)
        {
            return false;
        }

        string[] candidates = { "jrg", "jpu", "jqr", "jqo", "jpe", "jpy", "kpj", "hdi", "ktz", "kpz", "qv" };
        bool invoked = false;
        for (int i = 0; i < candidates.Length; i++)
        {
            try
            {
                InvokeInstanceMethod(target, candidates[i], value);
                invoked = true;
            }
            catch
            {
                // Obfuscated method names differ between builds; try the next candidate.
            }
        }

        return invoked;
    }

    private static global::vd FindRuntimeHero(int heroKey)
    {
        global::vd hero = null;

        var heroRuntimeType = GameType("tz");
        try { hero = TryInvokeStaticMethod(heroRuntimeType, "ith", heroKey) as global::vd; } catch { }
        if (IsValid(hero)) { return hero; }

        try { hero = TryInvokeStaticMethod(heroRuntimeType, "itz", heroKey) as global::vd; } catch { }
        if (IsValid(hero)) { return hero; }

        try { hero = TryInvokeStaticMethod(heroRuntimeType, "ixf", heroKey) as global::vd; } catch { }
        if (IsValid(hero)) { return hero; }

        try { hero = TryInvokeStaticMethod(heroRuntimeType, "nch", heroKey) as global::vd; } catch { }
        if (IsValid(hero)) { return hero; }

        try { hero = TryInvokeStaticMethod(heroRuntimeType, "jvl", heroKey) as global::vd; } catch { }
        if (IsValid(hero)) { return hero; }

        try { hero = TryInvokeStaticMethod(heroRuntimeType, "olm", heroKey) as global::vd; } catch { }
        if (IsValid(hero)) { return hero; }

        try { hero = TryInvokeStaticMethod(heroRuntimeType, "us", heroKey) as global::vd; } catch { }
        if (IsValid(hero)) { return hero; }

        try { hero = TryInvokeStaticMethod(heroRuntimeType, "mwa", heroKey) as global::vd; } catch { }
        if (IsValid(hero)) { return hero; }

        try { hero = TryInvokeStaticMethod(heroRuntimeType, "dho", heroKey) as global::vd; } catch { }
        if (IsValid(hero)) { return hero; }

        try { hero = TryInvokeStaticMethod(heroRuntimeType, "itg", heroKey) as global::vd; } catch { }
        if (IsValid(hero)) { return hero; }

        try { hero = TryInvokeStaticMethod(heroRuntimeType, "ity", heroKey) as global::vd; } catch { }
        if (IsValid(hero)) { return hero; }

        try
        {
            var dictionary = ReadStaticMember(heroRuntimeType, "bevc");
            if (RuntimeDictionaryContainsKey(dictionary, heroKey))
            {
                hero = RuntimeDictionaryGetValue(dictionary, heroKey) as global::vd;
                if (IsValid(hero))
                {
                    return hero;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"FindRuntimeHero dictionary failed key={heroKey}: {ex.Message}");
        }

        try
        {
            var heroes = ReadStaticMember(heroRuntimeType, "bsmr")
                         ?? TryInvokeStaticMethodOrNull(heroRuntimeType, "itc")
                         ?? TryInvokeStaticMethodOrNull(heroRuntimeType, "itb");
            if (heroes is System.Collections.IEnumerable enumerable)
            {
                foreach (object entry in enumerable)
                {
                    hero = entry as global::vd;
                    if (!IsValid(hero))
                    {
                        continue;
                    }

                    int key = ReadObjectIntMember(hero, "jpa", 0);
                    if (key == heroKey)
                    {
                        return hero;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"FindRuntimeHero list failed key={heroKey}: {ex.Message}");
        }

        return null;
    }

    private static void RefreshRuntimeHeroFromSaveData(global::vd cache, global::TaskbarHero.EasySaveData.HeroSaveData hero)
    {
        if (!IsValid(cache) || hero == null)
        {
            return;
        }

        string[] candidates = { "jqd", "deq", "nlb", "edw", "nxr", "jqc" };
        bool invoked = false;
        for (int i = 0; i < candidates.Length; i++)
        {
            invoked |= TryInvokeInstanceMethod(cache, candidates[i], hero);
        }

        if (!invoked)
        {
            throw new MissingMethodException("vd", "HeroSaveData refresh");
        }
    }

    private static Il2CppStructArray<int> BuildHeroAttributeGroupArray(int heroKey)
    {
        var data = GetDataManager();
        var attributes = data?.attributeInfoDatas;
        if (attributes == null)
        {
            return null;
        }

        var keys = new System.Collections.Generic.SortedSet<int>();
        for (int i = 0; i < attributes.Count; i++)
        {
            var attribute = attributes[i];
            if (!IsValid(attribute) || attribute.HeroKey != heroKey || attribute.GroupKey <= 0)
            {
                continue;
            }

            keys.Add(attribute.GroupKey);
        }

        if (keys.Count == 0)
        {
            return null;
        }

        var result = new Il2CppStructArray<int>(keys.Count);
        int index = 0;
        foreach (int key in keys)
        {
            result[index++] = key;
        }

        return result;
    }

    private static bool SameIntSet(Il2CppStructArray<int> left, Il2CppStructArray<int> right)
    {
        if ((left == null || left.Length == 0) && (right == null || right.Length == 0))
        {
            return true;
        }

        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        var seen = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < left.Length; i++)
        {
            seen.Add(left[i]);
        }

        for (int i = 0; i < right.Length; i++)
        {
            if (!seen.Contains(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static int CountArray(Il2CppStructArray<int> values)
    {
        return values == null ? 0 : values.Length;
    }

    private static int RebuildHeroRuntimeFromSave()
    {
        int changed = 0;
        AttachIl2CppThread();
        changed += ClearHeroRuntimeDictionary("beux");
        changed += ClearHeroRuntimeDictionary("beuy");
        changed += RefreshHeroManagerRuntime();
        return changed;
    }

    private static int ClearHeroRuntimeDictionary(string fieldName)
    {
        try
        {
            Type heroRuntimeType = GameType("tz");
            FieldInfo field = heroRuntimeType?.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            object dictionary = field?.GetValue(null);
            MethodInfo clearMethod = dictionary?.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public);
            if (clearMethod == null)
            {
                Plugin.FileLog($"Hero runtime dictionary {fieldName} not found.");
                return 0;
            }

            clearMethod.Invoke(dictionary, null);
            return 1;
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"Clear hero runtime dictionary {fieldName} failed: {ex.Message}");
            return 0;
        }
    }

    private static string BuildRuntimeHeroAbilitySummary(global::TaskbarHero.PlayerSaveData save)
    {
        var heroes = save?.heroSaveDatas;
        if (heroes == null || heroes.Count == 0)
        {
            return "sin heroes";
        }

        var builder = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < heroes.Count && shown < 10; i++)
        {
            var hero = heroes[i];
            if (hero == null)
            {
                continue;
            }

            var cache = FindRuntimeHero(hero.heroKey);
            if (!IsValid(cache))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(hero.heroKey)
                .Append(" saveUsed=")
                .Append(hero.AllocatedHeroAbilityPoint)
                .Append(" saveAp=")
                .Append(hero.AbilityPoint);
            shown++;
        }

        return builder.Length == 0 ? "sin heroes" : builder.ToString();
    }

    private static int RefreshHeroManagerRuntime()
    {
        int changed = 0;
        AttachIl2CppThread();
        var heroRuntimeType = GameType("tz");
        changed += TryRuntimeCall("tz.itj", () => TryInvokeStaticMethod(heroRuntimeType, "itj"));
        changed += TryRuntimeCall("tz.ite", () => TryInvokeStaticMethod(heroRuntimeType, "ite"));
        changed += TryRuntimeCall("tz.itv", () => TryInvokeStaticMethod(heroRuntimeType, "itv"));
        changed += TryRuntimeCall("tz.itf", () => TryInvokeStaticMethod(heroRuntimeType, "itf"));
        changed += TryRuntimeCall("tz.kmg", () => TryInvokeStaticMethod(heroRuntimeType, "kmg"));
        changed += TryRuntimeCall("tz.keu", () => TryInvokeStaticMethod(heroRuntimeType, "keu"));
        changed += TryRuntimeCall("tz.cv", () => TryInvokeStaticMethod(heroRuntimeType, "cv"));
        changed += TryRuntimeCall("tz.dfl", () => TryInvokeStaticMethod(heroRuntimeType, "dfl"));
        changed += TryRuntimeCall("tz.gfa", () => TryInvokeStaticMethod(heroRuntimeType, "gfa"));
        changed += TryRuntimeCall("tz.iuj", () => TryInvokeStaticMethod(heroRuntimeType, "iuj"));
        return changed;
    }

    private static int RefreshRuntimePet(int petKey)
    {
        int changed = 0;
        var manager = GetPetManager();
        if (!IsValid(manager))
        {
            return changed;
        }

        changed += TryRuntimeCall("PetManager.kte", () => manager.kte(petKey));
        changed += TryRuntimeCall("PetManager.kti", () => manager.kti(petKey));
        changed += TryRuntimeCall("PetManager.OnPetUnlocked", () => manager.OnPetUnlocked?.Invoke(petKey));
        return changed;
    }

    private static int TryRuntimeCall(string label, Action action)
    {
        try
        {
            action();
            return 1;
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"{label} failed: {ex.Message}");
            return 0;
        }
    }

    private static string RequestSave()
    {
        try
        {
            if (!Plugin.EnableForcedSaveRequests)
            {
                string blocked = "Guardado forzado no solicitado: desactivado tras la update para proteger el save.";
                Plugin.FileLog(blocked);
                return blocked;
            }

            var manager = GetSaveManager();
            if (manager == null)
            {
                return "Save manager no listo; no se pudo guardar.";
            }

            ReapplyForcedHeroLevels(GetSaveDataOrNull());
            try
            {
                return ForceWriteSave(manager);
            }
            catch (Exception ex)
            {
                Plugin.FileLog("Direct save failed, falling back to async save: " + ex.Message);
                TryInvokeInstanceMethod(manager, "mio");
                TryInvokeInstanceMethod(manager, "lwn");
                TryInvokeInstanceMethod(manager, "mil");
            }

            string status = "Guardado async solicitado; espera unos segundos antes de cerrar.";
            Plugin.FileLog(status);
            return status;
        }
        catch (Exception ex)
        {
            return Fail("Autosave fallo", ex);
        }
    }

    private static string ForceWriteSave(global::bau manager)
    {
        var save = GetSaveDataOrNull();
        if (save == null || save.Pointer == IntPtr.Zero)
        {
            throw new InvalidOperationException("SaveData no cargado para escritura directa.");
        }

        TryInvokeInstanceMethod(manager, "mio");
        TryInvokeInstanceMethod(manager, "lwn");
        TryInvokeInstanceMethod(manager, "mil");

        string status = "Guardado solicitado al save manager.";
        Plugin.FileLog(status);
        return status;
    }

    private static string GetPrivateSaveKey(string methodName)
    {
        const string keyTypeName = "<PrivateImplementationDetails>{BDBA7524-0749-4342-84CF-86ABA0F0E14D}.a";
        var keyType = typeof(global::bau).Assembly.GetType(keyTypeName);
        var method = keyType?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        var value = method?.Invoke(null, null) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MissingMethodException(keyTypeName, methodName);
        }

        return value;
    }

    private static string BuildHeroLevelSummary(global::TaskbarHero.PlayerSaveData save)
    {
        var heroes = save?.heroSaveDatas;
        if (heroes == null || heroes.Count == 0)
        {
            return "sin heroes";
        }

        var builder = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < heroes.Count && shown < 10; i++)
        {
            var hero = heroes[i];
            if (hero == null)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(hero.heroKey)
                .Append('=')
                .Append(hero.HeroLevel);
            shown++;
        }

        return builder.Length == 0 ? "sin heroes" : builder.ToString();
    }

    private static string BuildHeroAbilitySummary(global::TaskbarHero.PlayerSaveData save)
    {
        var heroes = save?.heroSaveDatas;
        if (heroes == null || heroes.Count == 0)
        {
            return "sin heroes";
        }

        var builder = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < heroes.Count && shown < 10; i++)
        {
            var hero = heroes[i];
            if (hero == null)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(hero.heroKey)
                .Append(" ap=")
                .Append(hero.AbilityPoint)
                .Append(" used=")
                .Append(hero.AllocatedHeroAbilityPoint)
                .Append(" groups=")
                .Append(CountArray(hero.unlockedAttributeGroupKeys));
            shown++;
        }

        return builder.Length == 0 ? "sin heroes" : builder.ToString();
    }

    private static System.Collections.Generic.HashSet<ulong> ReadLoggedTrainerItemIds()
    {
        var ids = new System.Collections.Generic.HashSet<ulong>();
        string logPath = Path.Combine(Paths.PluginPath, "TaskbarHeroModMenu.runtime.log");
        if (!File.Exists(logPath))
        {
            return ids;
        }

        foreach (string line in File.ReadLines(logPath))
        {
            if (!line.Contains("Item creado:", StringComparison.Ordinal) &&
                !line.Contains("Cloned item", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in Regex.Matches(line, @"unique[= ]+(\d+)", RegexOptions.IgnoreCase))
            {
                if (ulong.TryParse(match.Groups[1].Value, out ulong uniqueId) && uniqueId != 0UL)
                {
                    ids.Add(uniqueId);
                }
            }
        }

        return ids;
    }

    private static System.Collections.Generic.List<CraftMaterialLogHint> ReadLastLoggedCraftingMaterialHints(out ulong maxUniqueBeforeCraftLine)
    {
        maxUniqueBeforeCraftLine = 0UL;
        var hints = new System.Collections.Generic.List<CraftMaterialLogHint>();
        string logPath = Path.Combine(Paths.PluginPath, "TaskbarHeroModMenu.runtime.log");
        if (!File.Exists(logPath))
        {
            return hints;
        }

        string[] lines = File.ReadAllLines(logPath);
        int detailLineIndex = -1;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].Contains("Craft mats detalles:", StringComparison.Ordinal))
            {
                detailLineIndex = i;
                break;
            }
        }

        if (detailLineIndex < 0)
        {
            return hints;
        }

        for (int i = 0; i < detailLineIndex; i++)
        {
            foreach (Match match in Regex.Matches(lines[i], @"unique[= ]+(\d+)", RegexOptions.IgnoreCase))
            {
                if (ulong.TryParse(match.Groups[1].Value, out ulong uniqueId) && uniqueId > maxUniqueBeforeCraftLine)
                {
                    maxUniqueBeforeCraftLine = uniqueId;
                }
            }
        }

        string details = lines[detailLineIndex];
        int markerIndex = details.IndexOf("Craft mats detalles:", StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            details = details.Substring(markerIndex + "Craft mats detalles:".Length);
        }

        foreach (Match match in Regex.Matches(details, @"(?<key>\d+):[^;]*:slot(?<slot>\d+)", RegexOptions.IgnoreCase))
        {
            if (!int.TryParse(match.Groups["key"].Value, out int itemKey) ||
                !int.TryParse(match.Groups["slot"].Value, out int slotIndex))
            {
                continue;
            }

            hints.Add(new CraftMaterialLogHint
            {
                ItemKey = itemKey,
                SlotIndex = slotIndex
            });
        }

        return hints;
    }

    private static ulong FindCraftingMaterialUniqueId(
        global::TaskbarHero.PlayerSaveData save,
        CraftMaterialLogHint hint,
        ulong maxUniqueBeforeCraftLine,
        out string source)
    {
        source = "none";
        if (save == null || hint.ItemKey <= 0)
        {
            return 0UL;
        }

        var inventory = save.inventorySaveDatas;
        if (inventory == null)
        {
            return 0UL;
        }

        for (int i = 0; i < inventory.Count; i++)
        {
            var slot = inventory[i];
            if (slot == null || slot.Index != hint.SlotIndex || slot.ItemUniqueId == 0UL)
            {
                continue;
            }

            var item = FindItemSaveDataByUniqueId(save, slot.ItemUniqueId);
            if (IsMatchingCraftingMaterial(item, hint.ItemKey))
            {
                source = "exact-slot";
                return item.UniqueId;
            }
        }

        for (int i = 0; i < inventory.Count; i++)
        {
            var slot = inventory[i];
            if (slot == null || slot.ItemUniqueId == 0UL)
            {
                continue;
            }

            if (maxUniqueBeforeCraftLine != 0UL && slot.ItemUniqueId <= maxUniqueBeforeCraftLine)
            {
                continue;
            }

            var item = FindItemSaveDataByUniqueId(save, slot.ItemUniqueId);
            if (IsMatchingCraftingMaterial(item, hint.ItemKey))
            {
                source = maxUniqueBeforeCraftLine == 0UL ? "inventory-key" : "inventory-after-log";
                return item.UniqueId;
            }
        }

        return 0UL;
    }

    private static bool IsMatchingCraftingMaterial(global::TaskbarHero.EasySaveData.ItemSaveData item, int itemKey)
    {
        if (item == null || item.ItemKey != itemKey)
        {
            return false;
        }

        var itemInfo = GetItemInfo(item.ItemKey);
        return IsValid(itemInfo) && itemInfo.ITEMTYPE == global::TaskbarHero.Data.EItemType.MATERIAL;
    }

    private static int ClearInventoryItems(global::TaskbarHero.PlayerSaveData save, System.Collections.Generic.HashSet<ulong> ids)
    {
        int changed = 0;
        var inventory = save?.inventorySaveDatas;
        if (inventory == null)
        {
            return changed;
        }

        for (int i = 0; i < inventory.Count; i++)
        {
            var slot = inventory[i];
            if (slot != null && ids.Contains(slot.ItemUniqueId))
            {
                Plugin.FileLog($"Clearing trainer item from inventory slot={slot.Index} unique={slot.ItemUniqueId}");
                slot.ItemUniqueId = 0UL;
                changed++;
            }
        }

        return changed;
    }

    private static int ClearStashItems(global::TaskbarHero.PlayerSaveData save, System.Collections.Generic.HashSet<ulong> ids)
    {
        int changed = 0;
        var stash = save?.stashSaveDatas;
        if (stash == null)
        {
            return changed;
        }

        for (int i = 0; i < stash.Count; i++)
        {
            var slot = stash[i];
            if (slot != null && ids.Contains(slot.ItemUniqueId))
            {
                Plugin.FileLog($"Clearing trainer item from stash slot={slot.Index} unique={slot.ItemUniqueId}");
                slot.ItemUniqueId = 0UL;
                changed++;
            }
        }

        return changed;
    }

    private static int ClearTradingStashItems(global::TaskbarHero.PlayerSaveData save, System.Collections.Generic.HashSet<ulong> ids)
    {
        int changed = 0;
        var stash = save?.remakeTradingStashSaveDatas;
        if (stash == null)
        {
            return changed;
        }

        for (int i = 0; i < stash.Count; i++)
        {
            var slot = stash[i];
            if (slot != null && ids.Contains(slot.ItemUniqueId))
            {
                Plugin.FileLog($"Clearing trainer item from trading stash slot={slot.Index} unique={slot.ItemUniqueId}");
                slot.ItemUniqueId = 0UL;
                changed++;
            }
        }

        return changed;
    }

    private static int RemoveItemSaves(global::TaskbarHero.PlayerSaveData save, System.Collections.Generic.HashSet<ulong> ids)
    {
        int removed = 0;
        var items = save?.itemSaveDatas;
        if (items == null)
        {
            return removed;
        }

        for (int i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (item != null && ids.Contains(item.UniqueId))
            {
                Plugin.FileLog($"Removing trainer item save key={item.ItemKey} unique={item.UniqueId}");
                items.RemoveAt(i);
                removed++;
            }
        }

        return removed;
    }

    private static bool SetRuntimeCurrency(int key, long target, out long oldValue, out long newValue)
    {
        oldValue = 0;
        newValue = 0;

        var currency = FindRuntimeCurrency(key);
        if (currency == null || currency.Pointer == IntPtr.Zero)
        {
            return false;
        }

        oldValue = ReadRuntimeCurrencyQuantity(currency, 0);
        long delta = target - oldValue;
        if (delta != 0)
        {
            try
            {
                object result = InvokeInstanceMethod(currency, "isd", delta, global::TaskbarHero.EGoldCurrencySource.OfflineReward);
                if (result != null)
                {
                    newValue = Convert.ToInt64(result, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                Plugin.FileLog($"Runtime currency isd failed key={key}: {ex.Message}");
                TryRefreshRuntimeCurrencyFromSave(currency, new global::TaskbarHero.EasySaveData.CurrencySaveData(key, target), key);
            }
        }

        if (newValue == 0)
        {
            newValue = ReadRuntimeCurrencyQuantity(currency, target);
        }

        return true;
    }

    private static bool AddRuntimeCurrency(int key, long amount, out long oldValue, out long newValue)
    {
        oldValue = 0;
        newValue = 0;

        var currency = FindRuntimeCurrency(key);
        if (currency == null || currency.Pointer == IntPtr.Zero)
        {
            return false;
        }

        oldValue = ReadRuntimeCurrencyQuantity(currency, 0);
        long target = AddSaturating(oldValue, amount);
        if (amount != 0)
        {
            try
            {
                object result = InvokeInstanceMethod(currency, "isd", amount, global::TaskbarHero.EGoldCurrencySource.OfflineReward);
                if (result != null)
                {
                    newValue = Convert.ToInt64(result, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                Plugin.FileLog($"Runtime currency add failed key={key}: {ex.Message}");
                TryRefreshRuntimeCurrencyFromSave(currency, new global::TaskbarHero.EasySaveData.CurrencySaveData(key, target), key);
            }
        }

        if (newValue == 0)
        {
            newValue = ReadRuntimeCurrencyQuantity(currency, target);
        }

        return true;
    }

    private static long ReadRuntimeCurrencyQuantity(object currency, long fallback)
    {
        return ReadRuntimeLongFlexible(currency, fallback, "bsmp", "isa", "irz");
    }

    private static void TryRefreshRuntimeCurrencyFromSave(object currency, global::TaskbarHero.EasySaveData.CurrencySaveData save, int key)
    {
        foreach (string methodName in new[] { "isc", "fnj", "ocr", "elz", "jek" })
        {
            try
            {
                InvokeInstanceMethod(currency, methodName, save);
                return;
            }
            catch
            {
                // Obfuscated method names differ between builds; try the next candidate.
            }
        }

        Plugin.FileLog($"Runtime currency save refresh failed key={key}: no compatible tq method.");
    }

    private static long AddSaturating(long value, long amount)
    {
        if (amount > 0 && value > long.MaxValue - amount)
        {
            return long.MaxValue;
        }

        if (amount < 0 && value < long.MinValue - amount)
        {
            return long.MinValue;
        }

        return value + amount;
    }

    private static Il2CppObjectBase FindRuntimeCurrency(int key)
    {
        AttachIl2CppThread();
        Il2CppObjectBase currency = null;

        var currencyRuntimeType = GameType("tp");
        try { currency = TryInvokeStaticMethod(currencyRuntimeType, "irw", key) as Il2CppObjectBase; } catch { }
        if (IsValid(currency)) { return currency; }

        try { currency = TryInvokeStaticMethod(currencyRuntimeType, "ibz", key) as Il2CppObjectBase; } catch { }
        if (IsValid(currency)) { return currency; }

        try { currency = TryInvokeStaticMethod(currencyRuntimeType, "jxs", key) as Il2CppObjectBase; } catch { }
        if (IsValid(currency)) { return currency; }

        try { currency = TryInvokeStaticMethod(currencyRuntimeType, "iuq", key) as Il2CppObjectBase; } catch { }
        if (IsValid(currency)) { return currency; }

        try { currency = TryInvokeStaticMethod(currencyRuntimeType, "irv", key) as Il2CppObjectBase; } catch { }
        if (IsValid(currency)) { return currency; }

        try { currency = TryInvokeStaticMethod(currencyRuntimeType, "exe", key) as Il2CppObjectBase; } catch { }
        if (IsValid(currency)) { return currency; }

        try { currency = TryInvokeStaticMethod(currencyRuntimeType, "lok", key) as Il2CppObjectBase; } catch { }
        if (IsValid(currency)) { return currency; }

        return null;
    }

    private static bool IsValid(Il2CppObjectBase obj)
    {
        return obj != null && obj.Pointer != IntPtr.Zero;
    }

    private static int CountUnlockedInventory(global::TaskbarHero.PlayerSaveData save)
    {
        int count = 0;
        var inventory = save?.inventorySaveDatas;
        if (inventory == null)
        {
            return count;
        }

        for (int i = 0; i < inventory.Count; i++)
        {
            var slot = inventory[i];
            if (slot != null && slot.IsUnlock)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountEmptyUnlockedInventory(global::TaskbarHero.PlayerSaveData save)
    {
        int count = 0;
        var inventory = save?.inventorySaveDatas;
        if (inventory == null)
        {
            return count;
        }

        for (int i = 0; i < inventory.Count; i++)
        {
            var slot = inventory[i];
            if (slot != null && slot.IsUnlock && slot.ItemUniqueId == 0UL)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountUnlockedStash(global::TaskbarHero.PlayerSaveData save)
    {
        int count = 0;
        var stash = save?.stashSaveDatas;
        if (stash == null)
        {
            return count;
        }

        for (int i = 0; i < stash.Count; i++)
        {
            var slot = stash[i];
            if (slot != null && slot.IsUnLock)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountUnlockedTradeShip(global::TaskbarHero.PlayerSaveData save)
    {
        int count = 0;
        var slots = save?.remakeTradingStashSaveDatas;
        if (slots == null)
        {
            return count;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot != null && slot.IsUnLock)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountTradeShipCatalogSlots(global::bas data)
    {
        return CollectTradeShipCatalogIndices(data).Count;
    }

    private static int CountUnlockedPets(global::TaskbarHero.PlayerSaveData save)
    {
        int count = 0;
        var pets = save?.PetSaveData;
        if (pets == null)
        {
            return count;
        }

        for (int i = 0; i < pets.Count; i++)
        {
            var pet = pets[i];
            if (pet != null && pet.IsUnlock)
            {
                count++;
            }
        }

        return count;
    }

    internal static int NormalizeGearCatalogForTrainer()
    {
        var list = GetDataManager()?.itemInfoData;
        if (list == null)
        {
            return -1;
        }

        int changed = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            if (!IsValid(item) || !IsTrainerLocalCatalogItemType(item))
            {
                continue;
            }

            if (NormalizeCatalogItemForTrainer(item))
            {
                changed++;
            }
        }

        return changed;
    }

    internal static bool NormalizeGearItemForTrainer(global::TaskbarHero.Data.ItemInfoData item)
    {
        if (!IsValid(item) || item.ITEMTYPE != global::TaskbarHero.Data.EItemType.GEAR)
        {
            return false;
        }

        return NormalizeCatalogItemForTrainer(item);
    }

    private static bool IsTrainerLocalCatalogItemType(global::TaskbarHero.Data.ItemInfoData item)
    {
        return item.ITEMTYPE == global::TaskbarHero.Data.EItemType.GEAR;
    }

    internal static bool NormalizeCatalogItemForTrainer(global::TaskbarHero.Data.ItemInfoData item)
    {
        if (!IsValid(item))
        {
            return false;
        }

        bool changed =
            item.IsSteamItem ||
            item.IsCanExchangeMarketable ||
            item.TemporaryBlockTradingStash ||
            item.IsDeletedInServer;

        item.IsSteamItem = false;
        item.IsCanExchangeMarketable = false;
        item.TemporaryBlockTradingStash = false;
        item.IsDeletedInServer = false;

        return changed;
    }

    internal static void TryNormalizeGearCatalogForTrainer(string reason)
    {
        try
        {
            int changed = NormalizeGearCatalogForTrainer();
            if (changed > 0)
            {
                Plugin.FileLog($"{reason}: trainer-local item catalog normalized changed={changed}.");
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"{reason}: item catalog normalization skipped: {ex.Message}");
        }
    }

    internal static bool AllowFilteredBackendInventoryRemoval(
        Il2CppSystem.Collections.Generic.List<ulong> itemIds,
        global::TaskbarHero.UI.EMovePivotTarget target,
        string reason)
    {
        try
        {
            if (itemIds == null || itemIds.Count == 0)
            {
                return true;
            }

            int before = itemIds.Count;
            int removed = FilterProtectedLocalItemIds(itemIds);
            if (removed > 0)
            {
                Plugin.FileLog($"{reason}: backend remove filtered {removed}/{before}, remaining={itemIds.Count}, target={target}, ids={FormatUlongList(itemIds, 12)}");
            }

            if (itemIds.Count == 0)
            {
                Plugin.FileLog($"{reason}: backend remove skipped; all ids are local trainer/runtime items.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"{reason}: backend remove blocked after filter failure: {ex}");
            return false;
        }
    }

    private static int FilterProtectedLocalItemIds(Il2CppSystem.Collections.Generic.List<ulong> itemIds)
    {
        int removed = 0;
        var save = GetSaveDataOrNull();
        for (int i = itemIds.Count - 1; i >= 0; i--)
        {
            ulong uniqueId = itemIds[i];
            if (!IsProtectedLocalItemId(save, uniqueId))
            {
                continue;
            }

            itemIds.RemoveAt(i);
            removed++;
        }

        return removed;
    }

    private static bool IsProtectedLocalItemId(global::TaskbarHero.PlayerSaveData save, ulong uniqueId)
    {
        if (save == null || uniqueId == 0UL)
        {
            return false;
        }

        var items = save.itemSaveDatas;
        if (items != null)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.UniqueId != uniqueId)
                {
                    continue;
                }

                var itemInfo = GetItemInfo(item.ItemKey);
                return !IsValid(itemInfo) || IsTrainerLocalCatalogItemType(itemInfo);
            }
        }

        return InventorySlotContains(save.inventorySaveDatas, uniqueId) ||
               InventorySlotContains(save.stashSaveDatas, uniqueId) ||
               InventorySlotContains(save.remakeTradingStashSaveDatas, uniqueId);
    }

    private static bool InventorySlotContains(Il2CppSystem.Collections.Generic.List<global::TaskbarHero.EasySaveData.InventorySaveData> slots, ulong uniqueId)
    {
        if (slots == null)
        {
            return false;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot != null && slot.ItemUniqueId == uniqueId)
            {
                return true;
            }
        }

        return false;
    }

    private static bool InventorySlotContains(Il2CppSystem.Collections.Generic.List<global::TaskbarHero.EasySaveData.StashSaveData> slots, ulong uniqueId)
    {
        if (slots == null)
        {
            return false;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot != null && slot.ItemUniqueId == uniqueId)
            {
                return true;
            }
        }

        return false;
    }

    private static bool InventorySlotContains(Il2CppSystem.Collections.Generic.List<global::TaskbarHero.EasySaveData.RemakeTradingStashSaveData> slots, ulong uniqueId)
    {
        if (slots == null)
        {
            return false;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot != null && slot.ItemUniqueId == uniqueId)
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatUlongList(Il2CppSystem.Collections.Generic.List<ulong> values, int max)
    {
        if (values == null || values.Count == 0)
        {
            return "none";
        }

        var builder = new StringBuilder();
        int count = Math.Min(values.Count, max);
        for (int i = 0; i < count; i++)
        {
            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            builder.Append(values[i]);
        }

        if (values.Count > count)
        {
            builder.Append("...");
        }

        return builder.ToString();
    }

    private static int EnsureAllHeroSaveData(global::TaskbarHero.PlayerSaveData save)
    {
        var data = GetDataManager();
        var heroInfos = data?.heroInfoData;
        if (save?.heroSaveDatas == null || heroInfos == null)
        {
            return 0;
        }

        int added = 0;
        int targetHeroLevel = GetTargetHeroLevel();
        for (int i = 0; i < heroInfos.Count; i++)
        {
            var info = heroInfos[i];
            if (!IsValid(info) || info.HeroKey <= 0 || FindHeroSaveData(save, info.HeroKey) != null)
            {
                continue;
            }

            var hero = new global::TaskbarHero.EasySaveData.HeroSaveData(info.HeroKey)
            {
                HeroLevel = targetHeroLevel,
                IsUnLock = true,
                HeroExp = 0f,
                AbilityPoint = DefaultHeroAbilityPoints,
                AllocatedHeroAbilityPoint = DefaultHeroAllocatedAbilityPoints
            };
            NormalizeHeroAbilityTree(hero);

            save.heroSaveDatas.Add(hero);
            added++;
            Plugin.FileLog($"Hero added key={info.HeroKey} name={info.HeroNameKey}");
        }

        return added;
    }

    private static int EnsureMissingHeroSaveDataUnlockedOnly(global::TaskbarHero.PlayerSaveData save)
    {
        var data = GetDataManager();
        var heroInfos = data?.heroInfoData;
        if (save?.heroSaveDatas == null || heroInfos == null)
        {
            return 0;
        }

        int added = 0;
        int targetHeroLevel = GetTargetHeroLevel();
        for (int i = 0; i < heroInfos.Count; i++)
        {
            var info = heroInfos[i];
            if (!IsValid(info) || info.HeroKey <= 0 || FindHeroSaveData(save, info.HeroKey) != null)
            {
                continue;
            }

            var hero = new global::TaskbarHero.EasySaveData.HeroSaveData(info.HeroKey)
            {
                HeroLevel = targetHeroLevel,
                IsUnLock = true,
                HeroExp = 0f,
                AbilityPoint = 0,
                AllocatedHeroAbilityPoint = 0
            };

            save.heroSaveDatas.Add(hero);
            added++;
            Plugin.FileLog($"Hero unlock-only added key={info.HeroKey} name={info.HeroNameKey} level={targetHeroLevel}");
        }

        return added;
    }

    private static int NormalizeHeroCatalogAvailability()
    {
        var heroInfos = GetDataManager()?.heroInfoData;
        if (heroInfos == null)
        {
            return 0;
        }

        int changed = 0;
        for (int i = 0; i < heroInfos.Count; i++)
        {
            var info = heroInfos[i];
            if (!IsValid(info))
            {
                continue;
            }

            bool shouldCount =
                !info.IsAvailable ||
                !info.IsFirstAvailable ||
                info.UnlockCost != 0 ||
                info.DLCAppId != 0 ||
                info.DLCBitIndex != 0;

            info.IsAvailable = true;
            info.IsFirstAvailable = true;
            info.UnlockCost = 0;
            info.DLCAppId = 0;
            info.DLCBitIndex = 0;

            if (shouldCount)
            {
                changed++;
                Plugin.FileLog($"Hero catalog normalized key={info.HeroKey} name={info.HeroNameKey} class={info.ClassType}");
            }
        }

        return changed;
    }

    private static string DescribeHeroCatalog()
    {
        var heroInfos = GetDataManager()?.heroInfoData;
        if (heroInfos == null)
        {
            return "Catalogo heroes no cargado.";
        }

        var builder = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < heroInfos.Count && shown < 10; i++)
        {
            var info = heroInfos[i];
            if (!IsValid(info))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(info.HeroKey)
                .Append(':')
                .Append(info.ClassType);

            string nameKey = info.HeroNameKey;
            if (!string.IsNullOrWhiteSpace(nameKey))
            {
                builder.Append(':').Append(nameKey);
            }

            shown++;
        }

        return builder.Length == 0
            ? $"Catalogo heroes {heroInfos.Count} sin entradas visibles."
            : $"Catalogo heroes {heroInfos.Count}: {builder}.";
    }

    private static global::TaskbarHero.EasySaveData.HeroSaveData FindHeroSaveData(global::TaskbarHero.PlayerSaveData save, int heroKey)
    {
        var heroes = save?.heroSaveDatas;
        if (heroes == null)
        {
            return null;
        }

        for (int i = 0; i < heroes.Count; i++)
        {
            var hero = heroes[i];
            if (hero != null && hero.heroKey == heroKey)
            {
                return hero;
            }
        }

        return null;
    }

    private static global::TaskbarHero.EasySaveData.PetSaveData FindPetSaveData(global::TaskbarHero.PlayerSaveData save, int petKey)
    {
        var pets = save?.PetSaveData;
        if (pets == null)
        {
            return null;
        }

        for (int i = 0; i < pets.Count; i++)
        {
            var pet = pets[i];
            if (pet != null && pet.PetKey == petKey)
            {
                return pet;
            }
        }

        return null;
    }

    private static void EnsureItemSaveData(
        global::TaskbarHero.PlayerSaveData save,
        global::TaskbarHero.EasySaveData.ItemSaveData itemSave)
    {
        var items = save?.itemSaveDatas;
        if (items == null || itemSave == null)
        {
            return;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item != null && item.UniqueId == itemSave.UniqueId)
            {
                return;
            }
        }

        items.Add(itemSave);
    }

    private static global::TaskbarHero.EasySaveData.ItemSaveData FindBestOwnedItemSave(
        global::TaskbarHero.PlayerSaveData save,
        int itemKey,
        global::TaskbarHero.EasySaveData.HeroSaveData preferredHero)
    {
        var items = save?.itemSaveDatas;
        if (items == null)
        {
            return null;
        }

        global::TaskbarHero.EasySaveData.ItemSaveData best = null;
        int bestScore = int.MinValue;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item == null || item.ItemKey != itemKey)
            {
                continue;
            }

            int score = GetOwnedItemSelectionScore(save, preferredHero, item);
            if (best == null || score > bestScore)
            {
                best = item;
                bestScore = score;
            }
        }

        return best;
    }

    private static System.Collections.Generic.List<EquippedSocketTarget> FindFallbackEquippedSetTargets(
        global::TaskbarHero.PlayerSaveData save,
        global::TaskbarHero.EasySaveData.HeroSaveData heroSave,
        string normalizedClass,
        int originalEquippedLength,
        out ulong[] fallbackEquippedIds,
        out string fallbackSummary)
    {
        fallbackEquippedIds = Array.Empty<ulong>();
        fallbackSummary = string.Empty;
        var targets = new System.Collections.Generic.List<EquippedSocketTarget>();
        int targetLevel = GetTargetHeroLevel();
        var plan = BuildBestClassGearSet(normalizedClass, targetLevel);
        if (plan.ItemKeys.Length == 0)
        {
            fallbackSummary = $"fallback-set:no-plan:{normalizedClass}";
            return targets;
        }

        int equippedLength = Math.Max(originalEquippedLength, plan.ItemKeys.Length);
        fallbackEquippedIds = new ulong[equippedLength];
        var used = new System.Collections.Generic.HashSet<ulong>();
        int missing = 0;
        int classSpecificPieces = 0;
        for (int slotIndex = 0; slotIndex < plan.ItemKeys.Length; slotIndex++)
        {
            int itemKey = plan.ItemKeys[slotIndex];
            var itemSave = FindFallbackClassSetItem(save, heroSave, itemKey, used);
            if (itemSave == null)
            {
                missing++;
                continue;
            }

            used.Add(itemSave.UniqueId);
            fallbackEquippedIds[slotIndex] = itemSave.UniqueId;
            targets.Add(new EquippedSocketTarget
            {
                SlotIndex = slotIndex,
                UniqueId = itemSave.UniqueId,
                ItemSave = itemSave,
                Source = "fallback-set"
            });

            var itemInfo = GetItemInfo(itemSave.ItemKey);
            if (IsValid(itemInfo) && !IsSharedClassGearType(itemInfo.GEARTYPE.ToString()))
            {
                classSpecificPieces++;
            }
        }

        if (targets.Count == 0)
        {
            fallbackEquippedIds = Array.Empty<ulong>();
            fallbackSummary = $"fallback-set:sin-piezas:{plan.ClassName}:faltas{missing}";
            return targets;
        }

        if (classSpecificPieces == 0)
        {
            fallbackEquippedIds = Array.Empty<ulong>();
            targets.Clear();
            fallbackSummary = $"fallback-set:bloqueado-sin-arma-clase:{plan.ClassName}:candidatos{used.Count}:faltas{missing}";
            return targets;
        }

        fallbackSummary = $"fallback-set:{targets.Count}/{plan.ItemKeys.Length}:armaClase{classSpecificPieces}:faltas{missing}";
        return targets;
    }

    private static int FillMissingEquippedSetTargets(
        global::TaskbarHero.PlayerSaveData save,
        global::TaskbarHero.EasySaveData.HeroSaveData heroSave,
        string normalizedClass,
        int originalEquippedLength,
        Il2CppStructArray<ulong> equipped,
        System.Collections.Generic.List<EquippedSocketTarget> targets,
        System.Collections.Generic.HashSet<ulong> usedUniqueIds,
        out string fallbackSummary)
    {
        fallbackSummary = string.Empty;
        int targetLevel = GetTargetHeroLevel();
        var plan = BuildBestClassGearSet(normalizedClass, targetLevel);
        if (plan.ItemKeys.Length == 0)
        {
            fallbackSummary = $"partial-fallback:no-plan:{normalizedClass}";
            return 0;
        }

        int equippedLength = Math.Max(originalEquippedLength, plan.ItemKeys.Length);
        var equippedIds = new ulong[equippedLength];
        if (equipped != null)
        {
            int copyLength = Math.Min(equipped.Length, equippedIds.Length);
            for (int i = 0; i < copyLength; i++)
            {
                equippedIds[i] = equipped[i];
            }
        }

        var occupiedSlots = new bool[equippedLength];
        int classSpecificPieces = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            if (target.SlotIndex >= 0 &&
                target.SlotIndex < occupiedSlots.Length &&
                target.UniqueId != 0UL &&
                equippedIds[target.SlotIndex] == target.UniqueId)
            {
                occupiedSlots[target.SlotIndex] = true;
            }

            var itemSave = target.ItemSave ?? FindItemSaveDataByUniqueId(save, target.UniqueId);
            var itemInfo = itemSave == null ? null : GetItemInfo(itemSave.ItemKey);
            if (IsValid(itemInfo) && !IsSharedClassGearType(itemInfo.GEARTYPE.ToString()))
            {
                classSpecificPieces++;
            }
        }

        var staged = new System.Collections.Generic.List<EquippedSocketTarget>();
        int missing = 0;
        for (int slotIndex = 0; slotIndex < plan.ItemKeys.Length; slotIndex++)
        {
            if (slotIndex < occupiedSlots.Length && occupiedSlots[slotIndex])
            {
                continue;
            }

            int itemKey = plan.ItemKeys[slotIndex];
            var itemSave = FindFallbackClassSetItem(save, heroSave, itemKey, usedUniqueIds);
            if (itemSave == null)
            {
                missing++;
                continue;
            }

            usedUniqueIds?.Add(itemSave.UniqueId);
            equippedIds[slotIndex] = itemSave.UniqueId;
            staged.Add(new EquippedSocketTarget
            {
                SlotIndex = slotIndex,
                UniqueId = itemSave.UniqueId,
                ItemSave = itemSave,
                Source = "partial-fallback"
            });

            var itemInfo = GetItemInfo(itemSave.ItemKey);
            if (IsValid(itemInfo) && !IsSharedClassGearType(itemInfo.GEARTYPE.ToString()))
            {
                classSpecificPieces++;
            }
        }

        if (staged.Count == 0)
        {
            if (missing > 0)
            {
                fallbackSummary = $"partial-fallback:none:faltas{missing}";
            }

            return 0;
        }

        if (classSpecificPieces == 0)
        {
            fallbackSummary = $"partial-fallback:bloqueado-sin-arma-clase:{plan.ClassName}:candidatos{staged.Count}:faltas{missing}";
            return 0;
        }

        for (int i = 0; i < staged.Count; i++)
        {
            targets.Add(staged[i]);
        }

        heroSave.equippedItemIds = BuildULongArray(equippedIds);
        fallbackSummary = $"partial-fallback:{staged.Count}/{plan.ItemKeys.Length}:faltas{missing}";
        return staged.Count;
    }

    private static global::TaskbarHero.EasySaveData.ItemSaveData FindFallbackClassSetItem(
        global::TaskbarHero.PlayerSaveData save,
        global::TaskbarHero.EasySaveData.HeroSaveData ownerHero,
        int itemKey,
        System.Collections.Generic.HashSet<ulong> usedUniqueIds)
    {
        var items = save?.itemSaveDatas;
        if (items == null || itemKey <= 0)
        {
            return null;
        }

        global::TaskbarHero.EasySaveData.ItemSaveData best = null;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item == null ||
                item.ItemKey != itemKey ||
                item.UniqueId == 0UL ||
                (usedUniqueIds != null && usedUniqueIds.Contains(item.UniqueId)))
            {
                continue;
            }

            var itemInfo = GetItemInfo(item.ItemKey);
            if (!IsValid(itemInfo) || itemInfo.ITEMTYPE != global::TaskbarHero.Data.EItemType.GEAR)
            {
                continue;
            }

            if (IsItemInAnySlotContainer(save, item.UniqueId) ||
                IsEquippedByOtherHero(save, ownerHero, item.UniqueId))
            {
                continue;
            }

            if (best == null || CompareFallbackClassSetItem(item, best) > 0)
            {
                best = item;
            }
        }

        return best;
    }

    private static int CompareFallbackClassSetItem(
        global::TaskbarHero.EasySaveData.ItemSaveData left,
        global::TaskbarHero.EasySaveData.ItemSaveData right)
    {
        int leftPenalty = GetFallbackItemPenalty(left);
        int rightPenalty = GetFallbackItemPenalty(right);
        int penalty = rightPenalty.CompareTo(leftPenalty);
        if (penalty != 0)
        {
            return penalty;
        }

        return left.UniqueId.CompareTo(right.UniqueId);
    }

    private static int GetFallbackItemPenalty(global::TaskbarHero.EasySaveData.ItemSaveData item)
    {
        if (item == null)
        {
            return int.MaxValue;
        }

        int penalty = CountValidMaterialKeys(item) * 1000;
        if (item.IsServerPendingItem)
        {
            penalty += 100000;
        }

        if (item.IsBlocked)
        {
            penalty += 100000;
        }

        return penalty;
    }

    private static bool IsItemInAnySlotContainer(global::TaskbarHero.PlayerSaveData save, ulong uniqueId)
    {
        return InventorySlotContains(save?.inventorySaveDatas, uniqueId) ||
               InventorySlotContains(save?.stashSaveDatas, uniqueId) ||
               InventorySlotContains(save?.remakeTradingStashSaveDatas, uniqueId);
    }

    private static bool IsEquippedByOtherHero(
        global::TaskbarHero.PlayerSaveData save,
        global::TaskbarHero.EasySaveData.HeroSaveData ownerHero,
        ulong uniqueId)
    {
        var heroes = save?.heroSaveDatas;
        if (heroes == null || uniqueId == 0UL)
        {
            return false;
        }

        int ownerHeroKey = ownerHero?.heroKey ?? 0;
        for (int i = 0; i < heroes.Count; i++)
        {
            var hero = heroes[i];
            if (hero == null || (ownerHeroKey != 0 && hero.heroKey == ownerHeroKey))
            {
                continue;
            }

            if (HeroHasEquippedItem(hero, uniqueId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSharedClassGearType(string gearType)
    {
        for (int i = 0; i < SharedClassGearTypes.Length; i++)
        {
            if (string.Equals(SharedClassGearTypes[i], gearType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static global::TaskbarHero.EasySaveData.ItemSaveData FindItemSaveDataByUniqueId(
        global::TaskbarHero.PlayerSaveData save,
        ulong uniqueId)
    {
        var items = save?.itemSaveDatas;
        if (items == null || uniqueId == 0UL)
        {
            return null;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item != null && item.UniqueId == uniqueId)
            {
                return item;
            }
        }

        return null;
    }

    private static int GetOwnedItemSelectionScore(
        global::TaskbarHero.PlayerSaveData save,
        global::TaskbarHero.EasySaveData.HeroSaveData preferredHero,
        global::TaskbarHero.EasySaveData.ItemSaveData item)
    {
        if (item == null)
        {
            return int.MinValue;
        }

        int score = 0;
        if (HeroHasEquippedItem(preferredHero, item.UniqueId))
        {
            score += 100000;
        }

        if (InventorySlotContains(save?.inventorySaveDatas, item.UniqueId))
        {
            score += 10000;
        }
        else if (InventorySlotContains(save?.stashSaveDatas, item.UniqueId))
        {
            score += 5000;
        }
        else if (InventorySlotContains(save?.remakeTradingStashSaveDatas, item.UniqueId))
        {
            score += 3000;
        }

        score -= CountValidMaterialKeys(item) * 100;
        if (item.UniqueId > 0UL && item.UniqueId < int.MaxValue)
        {
            score += (int)(item.UniqueId % 100);
        }

        return score;
    }

    private static bool HeroHasEquippedItem(global::TaskbarHero.EasySaveData.HeroSaveData hero, ulong uniqueId)
    {
        var equipped = hero?.equippedItemIds;
        if (equipped == null || uniqueId == 0UL)
        {
            return false;
        }

        for (int i = 0; i < equipped.Length; i++)
        {
            if (equipped[i] == uniqueId)
            {
                return true;
            }
        }

        return false;
    }

    private static int CountValidMaterialKeys(global::TaskbarHero.EasySaveData.ItemSaveData item)
    {
        int count = 0;
        var enchants = item?.EnchantData;
        if (enchants == null)
        {
            return count;
        }

        for (int i = 0; i < enchants.Length; i++)
        {
            if (enchants[i].MaterialKey > 0)
            {
                count++;
            }
        }

        return count;
    }

    private static bool ConsumeOneMaterialInstance(global::TaskbarHero.PlayerSaveData save, int materialKey)
    {
        var items = save?.itemSaveDatas;
        if (items == null || materialKey <= 0)
        {
            return false;
        }

        for (int i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (item == null || item.ItemKey != materialKey)
            {
                continue;
            }

            var itemInfo = GetItemInfo(item.ItemKey);
            if (!IsValid(itemInfo) || itemInfo.ITEMTYPE != global::TaskbarHero.Data.EItemType.MATERIAL)
            {
                continue;
            }

            ClearItemFromSlotContainers(save, item.UniqueId);
            items.RemoveAt(i);
            return true;
        }

        return false;
    }

    internal static int NormalizeEquippedItemContainerDuplicates(global::TaskbarHero.PlayerSaveData save)
    {
        if (IsSuspiciousFreshSave(save))
        {
            Plugin.FileLog("NormalizeEquippedItemContainerDuplicates blocked: suspicious fresh save " + DescribeSafetyShape(save));
            return 0;
        }

        int changed = ClearEquippedItemInventoryReferences(save, out int runtimeSteps, out string clearedSlots);
        if (changed > 0)
        {
            Plugin.FileLog($"NormalizeEquippedItemContainerDuplicates cleared inventory refs {changed} ({clearedSlots}), runtime {runtimeSteps}. Stash/trade untouched.");
        }

        return changed;
    }

    private static int ClearEquippedItemInventoryReferences(
        global::TaskbarHero.PlayerSaveData save,
        out int runtimeSteps,
        out string clearedSlots)
    {
        runtimeSteps = 0;
        var cleared = new StringBuilder();
        var inventory = save?.inventorySaveDatas;
        if (inventory == null)
        {
            clearedSlots = "none";
            return 0;
        }

        var equippedIds = CollectEquippedItemIds(save);
        if (equippedIds.Count == 0)
        {
            clearedSlots = "none";
            return 0;
        }

        int changed = 0;
        for (int i = 0; i < inventory.Count; i++)
        {
            var slot = inventory[i];
            if (slot == null)
            {
                continue;
            }

            ulong uniqueId = slot.ItemUniqueId;
            if (uniqueId == 0UL || !equippedIds.Contains(uniqueId))
            {
                continue;
            }

            runtimeSteps += ClearInventorySlotReference(slot, uniqueId, "equipped duplicate");
            changed++;
            AppendShortSummary(cleared, $"inv{slot.Index}:u{uniqueId}");
        }

        clearedSlots = cleared.Length == 0 ? "none" : cleared.ToString();
        return changed;
    }

    private static int ClearInventorySlotReference(
        global::TaskbarHero.EasySaveData.InventorySaveData slot,
        ulong uniqueId,
        string reason)
    {
        if (slot == null || uniqueId == 0UL)
        {
            return 0;
        }

        int steps = 0;
        AttachIl2CppThread();

        var inventoryManager = GetLocalInventoryManager();
        if (IsValid(inventoryManager))
        {
            steps += TryRuntimeCall("LocalInventoryManager clear slot " + reason, () => TryInvokeAnyInstanceMethod(inventoryManager, new object[] { slot.Index, 0UL }, "ksh", "ksi", "ksj"));
        }

        slot.ItemUniqueId = 0UL;
        steps += RefreshRuntimeInventorySlot(slot);
        return steps;
    }

    private static string BuildEquippedDuplicateReport(global::TaskbarHero.PlayerSaveData save)
    {
        if (IsSuspiciousFreshSave(save))
        {
            return "Save no estable para diagnostico (" + DescribeSafetyShape(save) + ").";
        }

        var equippedIds = CollectEquippedItemIds(save);
        if (equippedIds.Count == 0)
        {
            return "No hay items equipados con uniqueId legible.";
        }

        var summary = new StringBuilder();
        int duplicates = 0;
        foreach (ulong uniqueId in equippedIds)
        {
            var locations = new StringBuilder();
            int locationCount = 0;
            locationCount += AppendEquippedDuplicateLocations(save?.inventorySaveDatas, uniqueId, "inv", locations);
            locationCount += AppendEquippedDuplicateLocations(save?.remakeTradingStashSaveDatas, uniqueId, "trade", locations);
            if (locationCount == 0)
            {
                continue;
            }

            duplicates += locationCount;
            var itemSave = FindItemSaveDataByUniqueId(save, uniqueId);
            var itemInfo = itemSave == null ? null : GetItemInfo(itemSave.ItemKey);
            string itemLabel = itemSave == null
                ? "sin itemSave"
                : $"key={itemSave.ItemKey},type={(IsValid(itemInfo) ? itemInfo.ITEMTYPE.ToString() : "?")},gear={(IsValid(itemInfo) ? itemInfo.GEARTYPE.ToString() : "?")}";
            string equippedLocations = BuildEquippedLocations(save, uniqueId);
            int itemSaveCount = CountItemSavesByUniqueId(save, uniqueId);
            AppendShortSummary(summary, $"u{uniqueId}[{itemLabel},itemSaves={itemSaveCount},equip={equippedLocations},dup={locations}]");
        }

        return duplicates == 0
            ? "No detecte equipped uniqueIds tambien presentes en inventario/trade. No se toco stash."
            : $"Detectados {duplicates} equipped uniqueIds tambien en inventario/trade: {summary}. No se modifico nada.";
    }

    private static System.Collections.Generic.HashSet<ulong> CollectEquippedItemIds(global::TaskbarHero.PlayerSaveData save)
    {
        var ids = new System.Collections.Generic.HashSet<ulong>();
        var heroes = save?.heroSaveDatas;
        if (heroes == null)
        {
            return ids;
        }

        for (int heroIndex = 0; heroIndex < heroes.Count; heroIndex++)
        {
            var equipped = heroes[heroIndex]?.equippedItemIds;
            if (equipped == null)
            {
                continue;
            }

            for (int slotIndex = 0; slotIndex < equipped.Length; slotIndex++)
            {
                ulong uniqueId = equipped[slotIndex];
                if (uniqueId != 0UL)
                {
                    ids.Add(uniqueId);
                }
            }
        }

        return ids;
    }

    private static int AppendEquippedDuplicateLocations(
        Il2CppSystem.Collections.Generic.List<global::TaskbarHero.EasySaveData.InventorySaveData> slots,
        ulong uniqueId,
        string label,
        StringBuilder summary)
    {
        if (slots == null || uniqueId == 0UL)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot == null || slot.ItemUniqueId != uniqueId)
            {
                continue;
            }

            AppendShortSummary(summary, $"{label}{slot.Index}:u{slot.ItemUniqueId}");
            count++;
        }

        return count;
    }

    private static int AppendEquippedDuplicateLocations(
        Il2CppSystem.Collections.Generic.List<global::TaskbarHero.EasySaveData.RemakeTradingStashSaveData> slots,
        ulong uniqueId,
        string label,
        StringBuilder summary)
    {
        if (slots == null || uniqueId == 0UL)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot == null || slot.ItemUniqueId != uniqueId)
            {
                continue;
            }

            AppendShortSummary(summary, $"{label}{slot.Index}:u{slot.ItemUniqueId}");
            count++;
        }

        return count;
    }

    private static string BuildEquippedLocations(global::TaskbarHero.PlayerSaveData save, ulong uniqueId)
    {
        var builder = new StringBuilder();
        var heroes = save?.heroSaveDatas;
        if (heroes == null || uniqueId == 0UL)
        {
            return "none";
        }

        for (int heroIndex = 0; heroIndex < heroes.Count; heroIndex++)
        {
            var hero = heroes[heroIndex];
            var equipped = hero?.equippedItemIds;
            if (equipped == null)
            {
                continue;
            }

            for (int slotIndex = 0; slotIndex < equipped.Length; slotIndex++)
            {
                if (equipped[slotIndex] == uniqueId)
                {
                    AppendShortSummary(builder, $"hero{hero.heroKey}:slot{slotIndex}");
                }
            }
        }

        return builder.Length == 0 ? "none" : builder.ToString();
    }

    private static int CountItemSavesByUniqueId(global::TaskbarHero.PlayerSaveData save, ulong uniqueId)
    {
        var items = save?.itemSaveDatas;
        if (items == null || uniqueId == 0UL)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item != null && item.UniqueId == uniqueId)
            {
                count++;
            }
        }

        return count;
    }

    private static void ClearItemFromSlotContainers(global::TaskbarHero.PlayerSaveData save, ulong uniqueId)
    {
        if (save == null || uniqueId == 0UL)
        {
            return;
        }

        ClearInventorySlot(save.inventorySaveDatas, uniqueId);
        ClearStashSlot(save.stashSaveDatas, uniqueId);
        ClearTradingStashSlot(save.remakeTradingStashSaveDatas, uniqueId);
    }

    private static void ClearInventorySlot(Il2CppSystem.Collections.Generic.List<global::TaskbarHero.EasySaveData.InventorySaveData> slots, ulong uniqueId)
    {
        if (slots == null)
        {
            return;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot != null && slot.ItemUniqueId == uniqueId)
            {
                ClearInventorySlotReference(slot, uniqueId, "clear item container");
                return;
            }
        }
    }

    private static void ClearStashSlot(Il2CppSystem.Collections.Generic.List<global::TaskbarHero.EasySaveData.StashSaveData> slots, ulong uniqueId)
    {
        if (slots == null)
        {
            return;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot != null && slot.ItemUniqueId == uniqueId)
            {
                slot.ItemUniqueId = 0UL;
                return;
            }
        }
    }

    private static void ClearTradingStashSlot(Il2CppSystem.Collections.Generic.List<global::TaskbarHero.EasySaveData.RemakeTradingStashSaveData> slots, ulong uniqueId)
    {
        if (slots == null)
        {
            return;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot != null && slot.ItemUniqueId == uniqueId)
            {
                slot.ItemUniqueId = 0UL;
                return;
            }
        }
    }

    private static int RefreshRuntimeItemSave(global::TaskbarHero.EasySaveData.ItemSaveData itemSave)
    {
        if (itemSave == null || itemSave.UniqueId == 0UL)
        {
            return 0;
        }

        int changed = 0;
        var itemCache = FindRuntimeItem(itemSave.UniqueId);
        if (IsValid(itemCache))
        {
            changed += TryRuntimeCall("uc.iyp socket refresh", () => TryInvokeInstanceMethod(itemCache, "iyp", itemSave));
            changed += TryRuntimeCall("uc.fcy socket refresh", () => TryInvokeInstanceMethod(itemCache, "fcy", itemSave));
            var inventoryRuntimeType = GameType("ua");
            changed += TryRuntimeCall("ua.njw socket refresh", () => TryInvokeStaticMethod(inventoryRuntimeType, "njw", itemSave.UniqueId, itemCache));
            changed += TryRuntimeCall("ua.ivg socket refresh", () => TryInvokeStaticMethod(inventoryRuntimeType, "ivg", itemSave.UniqueId, itemCache));
        }

        return changed;
    }

    private static void AppendShortSummary(StringBuilder builder, string value)
    {
        if (builder.Length > 0)
        {
            builder.Append("; ");
        }

        builder.Append(value);
    }

    private static int AddItemKeysToInventory(
        global::TaskbarHero.PlayerSaveData save,
        int[] itemKeys,
        bool fillEnchantSlots,
        string className,
        out string createdSummary,
        out int enchantSlots)
    {
        var builder = new StringBuilder();
        createdSummary = string.Empty;
        enchantSlots = 0;
        var inventory = save?.inventorySaveDatas;
        var items = save?.itemSaveDatas;
        if (inventory == null || items == null || itemKeys == null || itemKeys.Length == 0)
        {
            return 0;
        }

        ulong uniqueId = FindMaxKnownUniqueId(save) + 1UL;
        int created = 0;
        for (int i = 0; i < itemKeys.Length; i++)
        {
            var slot = FindFirstEmptyUnlockedInventorySlot(save);
            if (slot == null)
            {
                break;
            }

            int itemKey = itemKeys[i];
            var itemInfo = GetItemInfo(itemKey);
            if (!IsValid(itemInfo))
            {
                continue;
            }
            NormalizeCatalogItemForTrainer(itemInfo);

            while (uniqueId == 0UL || UniqueIdExists(save, uniqueId))
            {
                uniqueId++;
            }

            var itemSave = new global::TaskbarHero.EasySaveData.ItemSaveData(itemKey, uniqueId, 1)
            {
                IsChaotic = false,
                IsBlocked = false,
                IsServerPendingItem = false
            };

            int itemEnchantSlots = fillEnchantSlots
                ? ApplyBestEnchantSlots(itemSave, itemInfo, className)
                : 0;
            enchantSlots += itemEnchantSlots;

            int runtimeSteps = RegisterRuntimeInventoryItem(save, itemInfo, itemSave, slot);

            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(itemKey)
                .Append(':')
                .Append(itemInfo.GRADE)
                .Append(':')
                .Append(itemInfo.GEARTYPE)
                .Append(":u")
                .Append(uniqueId)
                .Append(":lv")
                .Append(itemInfo.Level)
                .Append(":slot")
                .Append(slot.Index)
                .Append(":ench")
                .Append(itemEnchantSlots)
                .Append(":rt")
                .Append(runtimeSteps);

            created++;
            uniqueId++;
        }

        int reconciled = ReconcileInventorySlotUniqueReferences(save, out string reconcileSummary);
        if (reconciled > 0)
        {
            builder.Append("; reconcile:")
                .Append(reconciled)
                .Append('[')
                .Append(reconcileSummary)
                .Append(']');
        }

        createdSummary = builder.ToString();
        return created;
    }

    private static int ApplyBestEnchantSlots(
        global::TaskbarHero.EasySaveData.ItemSaveData itemSave,
        global::TaskbarHero.Data.ItemInfoData itemInfo,
        string className)
    {
        if (itemSave == null || !IsValid(itemInfo))
        {
            return 0;
        }

        var recipes = new[]
        {
            global::TaskbarHero.Data.ERecipeType.DECORATION,
            global::TaskbarHero.Data.ERecipeType.ENGRAVING,
            global::TaskbarHero.Data.ERecipeType.INSCRIPTION
        };
        var stats = BuildPreferredEnchantStats(className);
        if (stats.Length == 0)
        {
            return 0;
        }

        var enchantData = new Il2CppStructArray<global::TaskbarHero.EasySaveData.ItemEnchantSaveData>(FullSetEnchantSlotsPerItem);
        int index = 0;
        for (int recipeIndex = 0; recipeIndex < recipes.Length && index < FullSetEnchantSlotsPerItem; recipeIndex++)
        {
            for (int perRecipe = 0; perRecipe < FullSetEnchantSlotsPerRecipe && index < FullSetEnchantSlotsPerItem; perRecipe++)
            {
                var stat = stats[index % stats.Length];
                enchantData[index] = BuildEnchantSaveData(itemInfo, recipes[recipeIndex], stat, index);
                index++;
            }
        }

        var counts = BuildEnchantCountArray(FullSetEnchantSlotsPerRecipe, FullSetEnchantSlotsPerRecipe, FullSetEnchantSlotsPerRecipe);

        itemSave.EnchantCount = counts;
        itemSave.EnchantData = enchantData;
        itemSave.DecorationAppliedTotalCount = FullSetEnchantSlotsPerRecipe;
        itemSave.EngravingAppliedTotalCount = FullSetEnchantSlotsPerRecipe;
        itemSave.InscriptionAppliedTotalCount = FullSetEnchantSlotsPerRecipe;
        return index;
    }

    private static int ApplyBestCatalogGemSockets(
        global::TaskbarHero.PlayerSaveData save,
        global::TaskbarHero.EasySaveData.ItemSaveData itemSave,
        global::TaskbarHero.Data.ItemInfoData itemInfo,
        string className,
        bool consumeExistingMaterials,
        out int consumedMaterials)
    {
        consumedMaterials = 0;
        if (save == null || itemSave == null || !IsValid(itemInfo))
        {
            return 0;
        }

        var data = GetDataManager();
        if (data == null)
        {
            return 0;
        }

        var recipes = new[]
        {
            global::TaskbarHero.Data.ERecipeType.DECORATION,
            global::TaskbarHero.Data.ERecipeType.ENGRAVING,
            global::TaskbarHero.Data.ERecipeType.INSCRIPTION
        };
        var stats = BuildPreferredEnchantStats(className);
        if (stats.Length == 0)
        {
            return 0;
        }

        var selectedMaterials = new MaterialCandidate[FullSetEnchantSlotsPerItem];
        var selectedRecipes = new global::TaskbarHero.Data.ERecipeType[FullSetEnchantSlotsPerItem];
        var selectedStats = new global::TaskbarHero.StatType[FullSetEnchantSlotsPerItem];
        int index = 0;
        for (int recipeIndex = 0; recipeIndex < recipes.Length && index < FullSetEnchantSlotsPerItem; recipeIndex++)
        {
            var recipe = recipes[recipeIndex];
            var materialType = ToMaterialType(recipe);
            for (int perRecipe = 0; perRecipe < FullSetEnchantSlotsPerRecipe && index < FullSetEnchantSlotsPerItem; perRecipe++)
            {
                var stat = stats[index % stats.Length];
                var material = FindBestSocketMaterialCandidate(data, materialType, stat);
                if (material == null)
                {
                    return 0;
                }

                selectedMaterials[index] = material;
                selectedRecipes[index] = recipe;
                selectedStats[index] = stat;
                index++;
            }
        }

        if (index != FullSetEnchantSlotsPerItem)
        {
            return 0;
        }

        var enchantData = new Il2CppStructArray<global::TaskbarHero.EasySaveData.ItemEnchantSaveData>(FullSetEnchantSlotsPerItem);
        for (int i = 0; i < FullSetEnchantSlotsPerItem; i++)
        {
            enchantData[i] = BuildEnchantSaveDataFromMaterial(data, selectedMaterials[i], selectedRecipes[i], selectedStats[i]);
            if (consumeExistingMaterials && ConsumeOneMaterialInstance(save, selectedMaterials[i].ItemKey))
            {
                consumedMaterials++;
            }
        }

        var counts = BuildEnchantCountArray(FullSetEnchantSlotsPerRecipe, FullSetEnchantSlotsPerRecipe, FullSetEnchantSlotsPerRecipe);

        itemSave.EnchantCount = counts;
        itemSave.EnchantData = enchantData;
        itemSave.DecorationAppliedTotalCount = FullSetEnchantSlotsPerRecipe;
        itemSave.EngravingAppliedTotalCount = FullSetEnchantSlotsPerRecipe;
        itemSave.InscriptionAppliedTotalCount = FullSetEnchantSlotsPerRecipe;
        itemSave.IsServerPendingItem = false;
        itemSave.IsBlocked = false;
        return index;
    }

    private static global::TaskbarHero.EasySaveData.ItemEnchantSaveData BuildEnchantSaveData(
        global::TaskbarHero.Data.ItemInfoData itemInfo,
        global::TaskbarHero.Data.ERecipeType recipeType,
        global::TaskbarHero.StatType statType,
        int slotIndex)
    {
        var data = GetDataManager();
        var resolvedStatType = statType;
        var modType = GetBestEnchantModType(statType);
        int tier = 10;
        int statModKey = 0;
        if (TryFindBestStatMod(data, statType, out var statMod, out statModKey, out tier))
        {
            resolvedStatType = statMod.bgrk;
            modType = statMod.bgrl;
        }

        int value = GetBestEnchantValue(statType);
        return new global::TaskbarHero.EasySaveData.ItemEnchantSaveData
        {
            StatModKey = statModKey > 0 ? statModKey : BuildStatModKey(statType, tier),
            Tier = tier,
            Value = value,
            RecipeType = (int)recipeType,
            ModType = (int)modType,
            MaterialKey = 0,
            StatType = (int)resolvedStatType
        };
    }

    private static Il2CppStructArray<int> BuildEnchantCountArray(int decoration, int engraving, int inscription)
    {
        var counts = new Il2CppStructArray<int>(EnchantCountSlotCount);
        counts[0] = decoration;
        counts[1] = engraving;
        counts[2] = inscription;
        return counts;
    }

    private static global::TaskbarHero.EasySaveData.ItemEnchantSaveData BuildEnchantSaveDataFromMaterial(
        global::bas data,
        MaterialCandidate material,
        global::TaskbarHero.Data.ERecipeType recipeType,
        global::TaskbarHero.StatType preferredStat)
    {
        var statType = preferredStat;
        var modType = GetBestEnchantModType(statType);
        int tier = 10;
        int statModKey = 0;
        if (TryFindBestStatMod(data, statType, out var statMod, out statModKey, out tier))
        {
            statType = statMod.bgrk;
            modType = statMod.bgrl;
        }

        int value = GetBestEnchantValue(statType);

        return new global::TaskbarHero.EasySaveData.ItemEnchantSaveData
        {
            StatModKey = statModKey > 0 ? statModKey : BuildStatModKey(statType, tier),
            Tier = tier,
            Value = value,
            RecipeType = (int)recipeType,
            ModType = (int)modType,
            MaterialKey = material.ItemKey,
            StatType = (int)statType
        };
    }

    private static global::TaskbarHero.StatType[] BuildPreferredEnchantStats(string className)
    {
        string normalized = NormalizeClassType(className);
        return normalized.ToUpperInvariant() switch
        {
            "RANGER" or "HUNTER" => new[]
            {
                global::TaskbarHero.StatType.AttackDamage,
                global::TaskbarHero.StatType.PhysicalDamagePercent,
                global::TaskbarHero.StatType.IncreaseProjectileDamage,
                global::TaskbarHero.StatType.AttackSpeed,
                global::TaskbarHero.StatType.CriticalChance,
                global::TaskbarHero.StatType.CriticalDamage
            },
            "SORCERER" => new[]
            {
                global::TaskbarHero.StatType.AttackDamage,
                global::TaskbarHero.StatType.FireDamagePercent,
                global::TaskbarHero.StatType.LightningDamagePercent,
                global::TaskbarHero.StatType.CastSpeed,
                global::TaskbarHero.StatType.CooldownReduction,
                global::TaskbarHero.StatType.AddAllSkillLevel
            },
            "PRIEST" => new[]
            {
                global::TaskbarHero.StatType.AttackDamage,
                global::TaskbarHero.StatType.SkillHealIncrease,
                global::TaskbarHero.StatType.SkillDurationIncrease,
                global::TaskbarHero.StatType.CastSpeed,
                global::TaskbarHero.StatType.CooldownReduction,
                global::TaskbarHero.StatType.AddAllSkillLevel
            },
            _ => new[]
            {
                global::TaskbarHero.StatType.AttackDamage,
                global::TaskbarHero.StatType.PhysicalDamagePercent,
                global::TaskbarHero.StatType.IncreaseMeleeDamage,
                global::TaskbarHero.StatType.AttackSpeed,
                global::TaskbarHero.StatType.CriticalChance,
                global::TaskbarHero.StatType.CriticalDamage
            }
        };
    }

    private static global::TaskbarHero.MODTYPE GetBestEnchantModType(global::TaskbarHero.StatType statType)
    {
        return statType switch
        {
            global::TaskbarHero.StatType.AttackDamage or
            global::TaskbarHero.StatType.MaxHp or
            global::TaskbarHero.StatType.Armor or
            global::TaskbarHero.StatType.AddAllSkillLevel => global::TaskbarHero.MODTYPE.FLAT,
            _ => global::TaskbarHero.MODTYPE.ADDITIVE
        };
    }

    private static bool TryFindBestStatMod(
        global::bas data,
        global::TaskbarHero.StatType statType,
        out global::TaskbarHero.Data.StatModInfoData statMod,
        out int statModKey,
        out int tier)
    {
        statMod = null;
        statModKey = 0;
        tier = 10;
        if (data == null || statType == global::TaskbarHero.StatType.NONE)
        {
            return false;
        }

        for (int candidateTier = 10; candidateTier >= 1; candidateTier--)
        {
            int candidateKey = BuildStatModKey(statType, candidateTier);
            var candidate = GetStatModInfo(data, candidateKey, candidateTier);
            if (!IsValid(candidate))
            {
                continue;
            }

            statMod = candidate;
            statModKey = candidateKey;
            tier = candidateTier;
            return true;
        }

        return false;
    }

    private static int BuildStatModKey(global::TaskbarHero.StatType statType, int tier)
    {
        int safeTier = Math.Clamp(tier, 1, 10);
        return 100000 + ((int)statType * 100) + safeTier;
    }

    private static int GetBestEnchantValue(global::TaskbarHero.StatType statType)
    {
        return statType switch
        {
            global::TaskbarHero.StatType.AttackDamage => 999999,
            global::TaskbarHero.StatType.MaxHp => 999999,
            global::TaskbarHero.StatType.Armor => 999999,
            global::TaskbarHero.StatType.AddAllSkillLevel => 50,
            global::TaskbarHero.StatType.CriticalChance => 100,
            global::TaskbarHero.StatType.AttackSpeed => 200,
            global::TaskbarHero.StatType.CastSpeed => 200,
            global::TaskbarHero.StatType.CooldownReduction => 90,
            _ => 999
        };
    }

    private static int[] BuildClassGemPackItemKeys(string className)
    {
        var data = GetDataManager();
        var list = data?.itemInfoData;
        if (data == null || list == null)
        {
            return Array.Empty<int>();
        }

        int classOffset = GetClassGemOffset(className);
        var keys = new System.Collections.Generic.List<int>(ClassGemPackPerRecipe * 3);
        AppendMaterialPackKeys(data, list, global::TaskbarHero.Data.EMaterialType.DECORATION, ClassGemPackPerRecipe, classOffset, keys);
        AppendMaterialPackKeys(data, list, global::TaskbarHero.Data.EMaterialType.ENGRAVING, ClassGemPackPerRecipe, classOffset + 1, keys);
        AppendMaterialPackKeys(data, list, global::TaskbarHero.Data.EMaterialType.INSCRIPTION, ClassGemPackPerRecipe, classOffset + 2, keys);
        return keys.ToArray();
    }

    private static void AppendMaterialPackKeys(
        global::bas data,
        Il2CppSystem.Collections.Generic.List<global::TaskbarHero.Data.ItemInfoData> list,
        global::TaskbarHero.Data.EMaterialType materialType,
        int count,
        int offset,
        System.Collections.Generic.List<int> output)
    {
        var candidates = FindBestMaterialCandidates(data, list, materialType);
        if (candidates.Count == 0)
        {
            Plugin.FileLog($"No material candidates for {materialType}");
            return;
        }

        for (int i = 0; i < count; i++)
        {
            int index = (i + Math.Abs(offset)) % candidates.Count;
            output.Add(candidates[index].ItemKey);
        }
    }

    private static System.Collections.Generic.List<MaterialCandidate> FindBestMaterialCandidates(
        global::bas data,
        Il2CppSystem.Collections.Generic.List<global::TaskbarHero.Data.ItemInfoData> list,
        global::TaskbarHero.Data.EMaterialType materialType)
    {
        var candidates = new System.Collections.Generic.List<MaterialCandidate>();
        int bestGrade = -1;
        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            if (!IsValid(item) ||
                item.ITEMTYPE != global::TaskbarHero.Data.EItemType.MATERIAL ||
                item.ItemSynthesisType != global::TaskbarHero.Data.EItemSynthesisType.Material ||
                item.IsDeletedInServer)
            {
                continue;
            }

            var material = GetMaterialInfo(data, item.ItemKey);
            if (!IsValid(material) || material.bgpv != materialType)
            {
                continue;
            }

            var statMod = GetStatModInfo(data, material.bgpu, material.bgpw);
            bool hasStatMod = IsValid(statMod);

            int grade = GradeRank(item.GRADE.ToString());
            if (grade < bestGrade)
            {
                continue;
            }

            if (grade > bestGrade)
            {
                bestGrade = grade;
                candidates.Clear();
            }

            NormalizeCatalogItemForTrainer(item);
            candidates.Add(new MaterialCandidate
            {
                ItemKey = item.ItemKey,
                GradeRank = grade,
                MaterialTier = material.bgpw,
                StatModKey = material.bgpu,
                HasStatMod = hasStatMod,
                StatType = hasStatMod ? statMod.bgrk : global::TaskbarHero.StatType.NONE,
                ModType = hasStatMod ? statMod.bgrl : global::TaskbarHero.MODTYPE.ADDITIVE,
                Value = hasStatMod ? statMod.bgrm : 0
            });
        }

        candidates.Sort((left, right) =>
        {
            int tier = right.MaterialTier.CompareTo(left.MaterialTier);
            if (tier != 0)
            {
                return tier;
            }

            return left.ItemKey.CompareTo(right.ItemKey);
        });
        return candidates;
    }

    private static MaterialCandidate FindBestSocketMaterialCandidate(
        global::bas data,
        global::TaskbarHero.Data.EMaterialType materialType,
        global::TaskbarHero.StatType preferredStat)
    {
        var list = data?.itemInfoData;
        if (list == null)
        {
            return null;
        }

        var candidates = FindBestMaterialCandidates(data, list, materialType);
        MaterialCandidate fallback = null;
        MaterialCandidate best = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (fallback == null || CompareMaterialCandidate(candidate, fallback) > 0)
            {
                fallback = candidate;
            }

            if (candidate.StatType != preferredStat)
            {
                continue;
            }

            if (best == null || CompareMaterialCandidate(candidate, best) > 0)
            {
                best = candidate;
            }
        }

        return best ?? fallback;
    }

    private static int CompareMaterialCandidate(MaterialCandidate left, MaterialCandidate right)
    {
        int grade = left.GradeRank.CompareTo(right.GradeRank);
        if (grade != 0)
        {
            return grade;
        }

        int tier = left.MaterialTier.CompareTo(right.MaterialTier);
        if (tier != 0)
        {
            return tier;
        }

        int value = left.Value.CompareTo(right.Value);
        if (value != 0)
        {
            return value;
        }

        return right.ItemKey.CompareTo(left.ItemKey);
    }

    private static global::TaskbarHero.Data.MaterialInfoData GetMaterialInfo(global::bas data, int materialKey)
    {
        if (data == null)
        {
            return null;
        }

        try
        {
            return data.mgk(materialKey);
        }
        catch
        {
            // try next generated lookup name
        }

        try { return TryInvokeInstanceObject<global::TaskbarHero.Data.MaterialInfoData>(data, "mgk", materialKey); } catch { }
        return null;
    }

    private static global::TaskbarHero.Data.StatModInfoData GetStatModInfo(global::bas data, int statModKey, int tier)
    {
        if (data == null)
        {
            return null;
        }

        try
        {
            return data.mgh(statModKey, tier);
        }
        catch
        {
            // try next generated lookup name
        }

        foreach (string name in new[] { "vs", "mxs", "fpt", "ded" })
        {
            try { return TryInvokeInstanceObject<global::TaskbarHero.Data.StatModInfoData>(data, name, statModKey, tier); } catch { }
        }

        return null;
    }

    private static global::TaskbarHero.Data.LevelInfoData GetLevelInfo(global::bas data, int level)
    {
        if (data == null)
        {
            return null;
        }

        try { return data.jse(level); } catch { }
        try { return data.mwh(level); } catch { }
        try { return data.dqw(level); } catch { }
        try { return data.mia(level); } catch { }
        try { return data.hmi(level); } catch { }
        return null;
    }

    private static global::TaskbarHero.Data.PetInfoData GetPetInfo(global::bas data, int petKey)
    {
        if (data == null)
        {
            return null;
        }

        try { return data.mfg(petKey); } catch { }
        foreach (string name in new[] { "mfg", "beo" })
        {
            try { return TryInvokeInstanceObject<global::TaskbarHero.Data.PetInfoData>(data, name, petKey); } catch { }
        }

        return null;
    }

    private static global::TaskbarHero.Data.CraftingRecipeInfoData GetCraftingRecipeInfo(
        global::bas data,
        int tier,
        global::TaskbarHero.Data.EItemCraftingType craftingType)
    {
        if (data == null)
        {
            return null;
        }

        try { return data.mfk(tier, craftingType); } catch { }
        foreach (string name in new[] { "mfk", "geg", "egd", "dct", "esj" })
        {
            try { return TryInvokeInstanceObject<global::TaskbarHero.Data.CraftingRecipeInfoData>(data, name, tier, craftingType); } catch { }
        }

        return null;
    }

    private static global::TaskbarHero.Data.EMaterialType ToMaterialType(global::TaskbarHero.Data.ERecipeType recipeType)
    {
        return recipeType switch
        {
            global::TaskbarHero.Data.ERecipeType.DECORATION => global::TaskbarHero.Data.EMaterialType.DECORATION,
            global::TaskbarHero.Data.ERecipeType.ENGRAVING => global::TaskbarHero.Data.EMaterialType.ENGRAVING,
            global::TaskbarHero.Data.ERecipeType.INSCRIPTION => global::TaskbarHero.Data.EMaterialType.INSCRIPTION,
            _ => global::TaskbarHero.Data.EMaterialType.NONE
        };
    }

    private static int GetClassGemOffset(string className)
    {
        return NormalizeClassType(className).ToUpperInvariant() switch
        {
            "KNIGHT" => 0,
            "RANGER" => 1,
            "SORCERER" => 2,
            "PRIEST" => 3,
            "HUNTER" => 4,
            "SLAYER" => 5,
            _ => 0
        };
    }

    private sealed class MaterialCandidate
    {
        public int ItemKey;
        public int GradeRank;
        public int MaterialTier;
        public int StatModKey;
        public bool HasStatMod;
        public global::TaskbarHero.StatType StatType;
        public global::TaskbarHero.MODTYPE ModType;
        public int Value;
    }

    private sealed class CraftMaterialRequirement
    {
        public int ItemKey;
        public int Amount;
    }

    private sealed class CraftMaterialLogHint
    {
        public int ItemKey;
        public int SlotIndex;
    }

    private sealed class EquippedSocketTarget
    {
        public int SlotIndex;
        public ulong UniqueId;
        public string Source = string.Empty;
        public global::TaskbarHero.EasySaveData.ItemSaveData ItemSave;
    }

    private sealed class BestGearSetPlan
    {
        public string ClassName = string.Empty;
        public string WeaponSummary = string.Empty;
        public int[] ItemKeys = Array.Empty<int>();
        public string ItemSummary = string.Empty;
    }

    private static BestGearSetPlan BuildBestClassGearSet(string classType, int targetLevel)
    {
        var data = GetDataManager();
        var hero = FindHeroInfoForClass(data?.heroInfoData, classType);
        if (!IsValid(hero))
        {
            return new BestGearSetPlan { ClassName = string.IsNullOrWhiteSpace(classType) ? "desconocida" : classType };
        }

        var gearTypes = BuildClassGearTypes(hero);
        var keys = new System.Collections.Generic.List<int>();
        var itemSummary = new StringBuilder();
        var list = data?.itemInfoData;
        if (list == null)
        {
            return new BestGearSetPlan { ClassName = hero.ClassType.ToString() };
        }

        for (int i = 0; i < gearTypes.Count; i++)
        {
            string gearType = gearTypes[i];
            var best = FindBestGearItemForType(list, targetLevel, gearType);
            if (!IsValid(best))
            {
                continue;
            }

            keys.Add(best.ItemKey);
            AppendGearItemSummary(itemSummary, best);
        }

        return new BestGearSetPlan
        {
            ClassName = hero.ClassType.ToString(),
            WeaponSummary = $"{hero.MainWeaponGearType}/{hero.SubWeaponGearType}",
            ItemKeys = keys.ToArray(),
            ItemSummary = itemSummary.ToString()
        };
    }

    private static string BuildBestClassGearSetSummary(int targetLevel)
    {
        var heroInfos = GetDataManager()?.heroInfoData;
        if (heroInfos == null)
        {
            return string.Empty;
        }

        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        for (int i = 0; i < heroInfos.Count; i++)
        {
            var hero = heroInfos[i];
            if (!IsValid(hero))
            {
                continue;
            }

            string className = hero.ClassType.ToString();
            if (!seen.Add(className))
            {
                continue;
            }

            var plan = BuildBestClassGearSet(className, targetLevel);
            if (plan.ItemKeys.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(" | ");
            }

            builder.Append(plan.ClassName)
                .Append('[')
                .Append(plan.WeaponSummary)
                .Append("]: ")
                .Append(plan.ItemSummary);
        }

        return builder.ToString();
    }

    private static global::TaskbarHero.Data.HeroInfoData FindHeroInfoForClass(
        Il2CppSystem.Collections.Generic.List<global::TaskbarHero.Data.HeroInfoData> heroInfos,
        string classType)
    {
        if (heroInfos == null)
        {
            return null;
        }

        string normalized = NormalizeClassType(classType);
        for (int i = 0; i < heroInfos.Count; i++)
        {
            var hero = heroInfos[i];
            if (!IsValid(hero))
            {
                continue;
            }

            if (string.Equals(hero.ClassType.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return hero;
            }
        }

        return null;
    }

    private static string NormalizeClassType(string classType)
    {
        string normalized = (classType ?? string.Empty).Trim();
        return normalized.ToUpperInvariant() switch
        {
            "ARCHER" => "Ranger",
            "WIZARD" => "Sorcerer",
            "BARBARIAN" => "Slayer",
            _ => normalized
        };
    }

    private static System.Collections.Generic.List<string> BuildClassGearTypes(global::TaskbarHero.Data.HeroInfoData hero)
    {
        var gearTypes = new System.Collections.Generic.List<string>();
        AddGearType(gearTypes, hero.MainWeaponGearType.ToString());
        AddGearType(gearTypes, hero.SubWeaponGearType.ToString());
        for (int i = 0; i < SharedClassGearTypes.Length; i++)
        {
            AddGearType(gearTypes, SharedClassGearTypes[i]);
        }

        return gearTypes;
    }

    private static void AddGearType(System.Collections.Generic.List<string> gearTypes, string gearType)
    {
        if (string.IsNullOrWhiteSpace(gearType) || string.Equals(gearType, "NONE", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        for (int i = 0; i < gearTypes.Count; i++)
        {
            if (string.Equals(gearTypes[i], gearType, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        gearTypes.Add(gearType);
    }

    private static global::TaskbarHero.Data.ItemInfoData FindBestGearItemForType(
        Il2CppSystem.Collections.Generic.List<global::TaskbarHero.Data.ItemInfoData> list,
        int targetLevel,
        string gearType)
    {
        global::TaskbarHero.Data.ItemInfoData best = null;
        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            if (!IsBestGearCandidate(item, targetLevel, gearType))
            {
                continue;
            }

            if (!IsValid(best) || CompareGearPower(item, best) > 0)
            {
                best = item;
            }
        }

        return best;
    }

    private static bool IsBestGearCandidate(
        global::TaskbarHero.Data.ItemInfoData item,
        int targetLevel,
        string gearType)
    {
        return IsValid(item) &&
               item.ITEMTYPE == global::TaskbarHero.Data.EItemType.GEAR &&
               !item.IsDeletedInServer &&
               item.Level > 0 &&
               item.Level <= targetLevel &&
               GradeRank(item.GRADE.ToString()) >= 0 &&
               string.Equals(item.GEARTYPE.ToString(), gearType, StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendGearItemSummary(StringBuilder builder, global::TaskbarHero.Data.ItemInfoData item)
    {
        if (!IsValid(item))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append("; ");
        }

        builder.Append(item.ItemKey)
            .Append(':')
            .Append(item.GRADE)
            .Append(':')
            .Append(item.GEARTYPE)
            .Append(":lv")
            .Append(item.Level);
    }

    private static int CompareGearPower(
        global::TaskbarHero.Data.ItemInfoData left,
        global::TaskbarHero.Data.ItemInfoData right)
    {
        int grade = GradeRank(left.GRADE.ToString()).CompareTo(GradeRank(right.GRADE.ToString()));
        if (grade != 0)
        {
            return grade;
        }

        int level = left.Level.CompareTo(right.Level);
        if (level != 0)
        {
            return level;
        }

        return left.ItemKey.CompareTo(right.ItemKey);
    }

    private static int GradeRank(string grade)
    {
        return grade switch
        {
            "COMMON" => 0,
            "UNCOMMON" => 1,
            "RARE" => 2,
            "LEGENDARY" => 3,
            "IMMORTAL" => 4,
            "ARCANA" => 5,
            "BEYOND" => 6,
            "CELESTIAL" => 7,
            "DIVINE" => 8,
            "COSMIC" => 9,
            _ => -1
        };
    }

    private static global::TaskbarHero.EasySaveData.ItemSaveData FindCloneSourceItem(global::TaskbarHero.PlayerSaveData save)
    {
        var items = save?.itemSaveDatas;
        if (items == null)
        {
            return null;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item != null && item.ItemKey != 0 && item.UniqueId != 0UL)
            {
                return item;
            }
        }

        return null;
    }

    private static global::TaskbarHero.EasySaveData.InventorySaveData FindFirstEmptyUnlockedInventorySlot(global::TaskbarHero.PlayerSaveData save)
    {
        var inventory = save?.inventorySaveDatas;
        if (inventory == null)
        {
            return null;
        }

        for (int i = 0; i < inventory.Count; i++)
        {
            var slot = inventory[i];
            if (slot != null && slot.IsUnlock && slot.ItemUniqueId == 0UL)
            {
                return slot;
            }
        }

        return null;
    }

    private static ulong FindMaxKnownUniqueId(global::TaskbarHero.PlayerSaveData save)
    {
        ulong max = 0UL;
        var items = save?.itemSaveDatas;
        if (items != null)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item != null && item.UniqueId > max)
                {
                    max = item.UniqueId;
                }
            }
        }

        var inventory = save?.inventorySaveDatas;
        if (inventory != null)
        {
            for (int i = 0; i < inventory.Count; i++)
            {
                var slot = inventory[i];
                if (slot != null && slot.ItemUniqueId > max)
                {
                    max = slot.ItemUniqueId;
                }
            }
        }

        var stash = save?.stashSaveDatas;
        if (stash != null)
        {
            for (int i = 0; i < stash.Count; i++)
            {
                var slot = stash[i];
                if (slot != null && slot.ItemUniqueId > max)
                {
                    max = slot.ItemUniqueId;
                }
            }
        }

        return max;
    }

    private static bool UniqueIdExists(global::TaskbarHero.PlayerSaveData save, ulong uniqueId)
    {
        var items = save?.itemSaveDatas;
        if (items != null)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item != null && item.UniqueId == uniqueId)
                {
                    return true;
                }
            }
        }

        var inventory = save?.inventorySaveDatas;
        if (inventory != null)
        {
            for (int i = 0; i < inventory.Count; i++)
            {
                var slot = inventory[i];
                if (slot != null && slot.ItemUniqueId == uniqueId)
                {
                    return true;
                }
            }
        }

        var stash = save?.stashSaveDatas;
        if (stash != null)
        {
            for (int i = 0; i < stash.Count; i++)
            {
                var slot = stash[i];
                if (slot != null && slot.ItemUniqueId == uniqueId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string WriteSocketedItemDiagnosticsFile(global::TaskbarHero.PlayerSaveData save, global::bas data)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TaskbarHeroTrainer");
        Directory.CreateDirectory(directory);

        string filePath = Path.Combine(directory, $"TaskbarHeroSocketedItems_{DateTime.Now:yyyyMMdd_HHmmss}.tsv");
        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        WriteTsvRow(
            writer,
            "UniqueId",
            "ItemKey",
            "Location",
            "Type",
            "Grade",
            "Parts",
            "GearType",
            "Level",
            "NameKey",
            "ServerPending",
            "Blocked",
            "Chaotic",
            "EnchantCounts",
            "DecorationTotal",
            "EngravingTotal",
            "InscriptionTotal",
            "Slot",
            "RecipeType",
            "MaterialKey",
            "MaterialValid",
            "MaterialType",
            "MaterialTier",
            "MaterialStatModKey",
            "MaterialStatType",
            "MaterialModType",
            "MaterialValue",
            "EnchantStatModKey",
            "EnchantTier",
            "EnchantStatModValid",
            "EnchantStatType",
            "EnchantModType",
            "EnchantValue",
            "SaveStatType",
            "SaveModType",
            "SaveValue");

        var items = save?.itemSaveDatas;
        if (items == null)
        {
            return filePath;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item == null || !HasAnyEnchantData(item))
            {
                continue;
            }

            var itemInfo = GetItemInfo(item.ItemKey);
            string location = DescribeItemLocation(save, item.UniqueId);
            string enchantCounts = FormatEnchantCounts(item.EnchantCount);
            var enchants = item.EnchantData;
            if (enchants == null || enchants.Length == 0)
            {
                WriteSocketedItemDiagnosticRow(writer, data, item, itemInfo, location, enchantCounts, -1, default);
                continue;
            }

            for (int slot = 0; slot < enchants.Length; slot++)
            {
                WriteSocketedItemDiagnosticRow(writer, data, item, itemInfo, location, enchantCounts, slot, enchants[slot]);
            }
        }

        return filePath;
    }

    private static void WriteSocketedItemDiagnosticRow(
        StreamWriter writer,
        global::bas data,
        global::TaskbarHero.EasySaveData.ItemSaveData item,
        global::TaskbarHero.Data.ItemInfoData itemInfo,
        string location,
        string enchantCounts,
        int slot,
        global::TaskbarHero.EasySaveData.ItemEnchantSaveData enchant)
    {
        var material = enchant.MaterialKey > 0 ? GetMaterialInfo(data, enchant.MaterialKey) : null;
        bool materialValid = IsValid(material);
        var materialStatMod = materialValid ? GetStatModInfo(data, material.bgpu, material.bgpw) : null;
        bool materialStatModValid = IsValid(materialStatMod);
        var enchantStatMod = enchant.StatModKey > 0 ? GetStatModInfo(data, enchant.StatModKey, enchant.Tier) : null;
        bool enchantStatModValid = IsValid(enchantStatMod);

        WriteTsvRow(
            writer,
            item.UniqueId,
            item.ItemKey,
            location,
            IsValid(itemInfo) ? itemInfo.ITEMTYPE : string.Empty,
            IsValid(itemInfo) ? itemInfo.GRADE : string.Empty,
            IsValid(itemInfo) ? itemInfo.PARTS : string.Empty,
            IsValid(itemInfo) ? itemInfo.GEARTYPE : string.Empty,
            IsValid(itemInfo) ? itemInfo.Level : 0,
            IsValid(itemInfo) ? itemInfo.NameKey : string.Empty,
            item.IsServerPendingItem,
            item.IsBlocked,
            item.IsChaotic,
            enchantCounts,
            item.DecorationAppliedTotalCount,
            item.EngravingAppliedTotalCount,
            item.InscriptionAppliedTotalCount,
            slot,
            RecipeName(enchant.RecipeType),
            enchant.MaterialKey,
            materialValid,
            materialValid ? material.bgpu : global::TaskbarHero.Data.EMaterialType.NONE,
            materialValid ? material.bgpv : 0,
            materialValid ? material.bgpt : 0,
            materialStatModValid ? materialStatMod.bgrj : global::TaskbarHero.StatType.NONE,
            materialStatModValid ? materialStatMod.bgrk : global::TaskbarHero.MODTYPE.ADDITIVE,
            materialStatModValid ? materialStatMod.bgrl : 0,
            enchant.StatModKey,
            enchant.Tier,
            enchantStatModValid,
            enchantStatModValid ? enchantStatMod.bgrj : global::TaskbarHero.StatType.NONE,
            enchantStatModValid ? enchantStatMod.bgrk : global::TaskbarHero.MODTYPE.ADDITIVE,
            enchantStatModValid ? enchantStatMod.bgrl : 0,
            StatTypeName(enchant.StatType),
            ModTypeName(enchant.ModType),
            enchant.Value);
    }

    private static bool HasAnyEnchantData(global::TaskbarHero.EasySaveData.ItemSaveData item)
    {
        if (item == null)
        {
            return false;
        }

        if (item.DecorationAppliedTotalCount > 0 ||
            item.EngravingAppliedTotalCount > 0 ||
            item.InscriptionAppliedTotalCount > 0)
        {
            return true;
        }

        var counts = item.EnchantCount;
        if (counts != null)
        {
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] > 0)
                {
                    return true;
                }
            }
        }

        var enchants = item.EnchantData;
        if (enchants == null)
        {
            return false;
        }

        for (int i = 0; i < enchants.Length; i++)
        {
            var enchant = enchants[i];
            if (enchant.MaterialKey > 0 ||
                enchant.StatModKey > 0 ||
                enchant.Tier > 0 ||
                enchant.Value != 0 ||
                enchant.StatType != 0 ||
                enchant.ModType != 0 ||
                enchant.RecipeType != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatEnchantCounts(Il2CppStructArray<int> counts)
    {
        if (counts == null || counts.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (int i = 0; i < counts.Length; i++)
        {
            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            builder.Append(i).Append('=').Append(counts[i]);
        }

        return builder.ToString();
    }

    private static string DescribeItemLocation(global::TaskbarHero.PlayerSaveData save, ulong uniqueId)
    {
        if (uniqueId == 0UL)
        {
            return "none";
        }

        var heroes = save?.heroSaveDatas;
        if (heroes != null)
        {
            for (int i = 0; i < heroes.Count; i++)
            {
                var hero = heroes[i];
                var equipped = hero?.equippedItemIds;
                if (equipped == null)
                {
                    continue;
                }

                for (int slot = 0; slot < equipped.Length; slot++)
                {
                    if (equipped[slot] == uniqueId)
                    {
                        return $"equipped:hero={hero.heroKey}:slot={slot}";
                    }
                }
            }
        }

        string inventory = FindSlotLocation(save?.inventorySaveDatas, uniqueId, "inventory");
        if (!string.IsNullOrEmpty(inventory))
        {
            return inventory;
        }

        string stash = FindSlotLocation(save?.stashSaveDatas, uniqueId, "stash");
        if (!string.IsNullOrEmpty(stash))
        {
            return stash;
        }

        string trading = FindSlotLocation(save?.remakeTradingStashSaveDatas, uniqueId, "trading");
        return string.IsNullOrEmpty(trading) ? "save-only" : trading;
    }

    private static string FindSlotLocation(
        Il2CppSystem.Collections.Generic.List<global::TaskbarHero.EasySaveData.InventorySaveData> slots,
        ulong uniqueId,
        string label)
    {
        if (slots == null)
        {
            return string.Empty;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot != null && slot.ItemUniqueId == uniqueId)
            {
                return $"{label}:slot={slot.Index}";
            }
        }

        return string.Empty;
    }

    private static string FindSlotLocation(
        Il2CppSystem.Collections.Generic.List<global::TaskbarHero.EasySaveData.StashSaveData> slots,
        ulong uniqueId,
        string label)
    {
        if (slots == null)
        {
            return string.Empty;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot != null && slot.ItemUniqueId == uniqueId)
            {
                return $"{label}:slot={slot.Index}";
            }
        }

        return string.Empty;
    }

    private static string FindSlotLocation(
        Il2CppSystem.Collections.Generic.List<global::TaskbarHero.EasySaveData.RemakeTradingStashSaveData> slots,
        ulong uniqueId,
        string label)
    {
        if (slots == null)
        {
            return string.Empty;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot != null && slot.ItemUniqueId == uniqueId)
            {
                return $"{label}:slot={slot.Index}";
            }
        }

        return string.Empty;
    }

    private static string RecipeName(int value)
    {
        return ((global::TaskbarHero.Data.ERecipeType)value).ToString();
    }

    private static string StatTypeName(int value)
    {
        return ((global::TaskbarHero.StatType)value).ToString();
    }

    private static string ModTypeName(int value)
    {
        return ((global::TaskbarHero.MODTYPE)value).ToString();
    }

    private static void WriteTsvRow(StreamWriter writer, params object[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                writer.Write('\t');
            }

            writer.Write(CleanCell(ToInvariantString(values[i])));
        }

        writer.WriteLine();
    }

    private static string ToInvariantString(object value)
    {
        return value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static string WriteItemCatalogFile(Il2CppSystem.Collections.Generic.List<global::TaskbarHero.Data.ItemInfoData> list)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TaskbarHeroTrainer");
        Directory.CreateDirectory(directory);

        string filePath = Path.Combine(directory, "TaskbarHeroItemKeys.tsv");
        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        writer.WriteLine("ItemKey\tType\tGrade\tParts\tGearType\tGearGroup\tSynthesisType\tLevel\tSteamItem\tMarketable\tDeleted\tTemporaryBlockTradingStash\tBucketBox\tNameKey\tDescriptionKey\tIconPath");

        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            if (!IsValid(item))
            {
                continue;
            }

            writer.Write(item.ItemKey);
            writer.Write('\t');
            writer.Write(item.ITEMTYPE);
            writer.Write('\t');
            writer.Write(item.GRADE);
            writer.Write('\t');
            writer.Write(item.PARTS);
            writer.Write('\t');
            writer.Write(item.GEARTYPE);
            writer.Write('\t');
            writer.Write(item.GearGroup);
            writer.Write('\t');
            writer.Write(item.ItemSynthesisType);
            writer.Write('\t');
            writer.Write(item.Level);
            writer.Write('\t');
            writer.Write(item.IsSteamItem);
            writer.Write('\t');
            writer.Write(item.IsCanExchangeMarketable);
            writer.Write('\t');
            writer.Write(item.IsDeletedInServer);
            writer.Write('\t');
            writer.Write(item.TemporaryBlockTradingStash);
            writer.Write('\t');
            writer.Write(item.IsBucketBox);
            writer.Write('\t');
            writer.Write(CleanCell(item.NameKey));
            writer.Write('\t');
            writer.Write(CleanCell(item.DescriptionKey));
            writer.Write('\t');
            writer.WriteLine(CleanCell(item.IconPath));
        }

        return filePath;
    }

    private static string WriteCraftingRecipeFile(global::bas data)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TaskbarHeroTrainer");
        Directory.CreateDirectory(directory);

        string filePath = Path.Combine(directory, "TaskbarHeroCraftingRecipes.tsv");
        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        writer.WriteLine("CraftingRecipeKey\tItemCraftingType\tRecipeTier\tDropKey\tMaterialRaw\tMaterialIndexRaw\tResolvedMaterials");

        var recipes = CollectCraftingRecipes(data);
        for (int i = 0; i < recipes.Count; i++)
        {
            var recipe = recipes[i];
            if (!IsValid(recipe))
            {
                continue;
            }

            writer.Write(recipe.CraftingRecipeKey);
            writer.Write('\t');
            writer.Write(recipe.ItemCraftingType);
            writer.Write('\t');
            writer.Write(recipe.RecipeTier);
            writer.Write('\t');
            writer.Write(recipe.DropKey);
            writer.Write('\t');
            writer.Write(CleanCell(recipe.Material));
            writer.Write('\t');
            writer.Write(CleanCell(recipe.MaterialIndex));
            writer.Write('\t');
            writer.WriteLine(CleanCell(DescribeCraftingMaterials(data, recipe.Material, recipe.MaterialIndex)));
        }

        return filePath;
    }

    private static string BuildCraftingRecipeExamples(global::bas data)
    {
        var builder = new StringBuilder();
        var recipes = CollectCraftingRecipes(data);
        int shown = 0;
        for (int i = 0; i < recipes.Count; i++)
        {
            var recipe = recipes[i];
            if (!IsValid(recipe))
            {
                continue;
            }

            bool useful = recipe.ItemCraftingType == global::TaskbarHero.Data.EItemCraftingType.SubWeapon ||
                          recipe.ItemCraftingType == global::TaskbarHero.Data.EItemCraftingType.MainWeapon;
            if (!useful && shown >= 8)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(recipe.ItemCraftingType)
                .Append(":tier")
                .Append(recipe.RecipeTier)
                .Append(":key")
                .Append(recipe.CraftingRecipeKey)
                .Append(':')
                .Append(DescribeCraftingMaterials(data, recipe.Material, recipe.MaterialIndex));

            shown++;
            if (shown >= 16)
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static System.Collections.Generic.List<global::TaskbarHero.Data.CraftingRecipeInfoData> CollectCraftingRecipes(global::bas data)
    {
        var output = new System.Collections.Generic.List<global::TaskbarHero.Data.CraftingRecipeInfoData>();
        if (data == null)
        {
            return output;
        }

        var seen = new System.Collections.Generic.HashSet<int>();
        var types = new[]
        {
            global::TaskbarHero.Data.EItemCraftingType.MainWeapon,
            global::TaskbarHero.Data.EItemCraftingType.SubWeapon,
            global::TaskbarHero.Data.EItemCraftingType.Helmet,
            global::TaskbarHero.Data.EItemCraftingType.Armor,
            global::TaskbarHero.Data.EItemCraftingType.Gloves,
            global::TaskbarHero.Data.EItemCraftingType.Boots,
            global::TaskbarHero.Data.EItemCraftingType.Accessory
        };

        for (int tier = 0; tier <= 250; tier++)
        {
            for (int typeIndex = 0; typeIndex < types.Length; typeIndex++)
            {
                global::TaskbarHero.Data.CraftingRecipeInfoData recipe = null;
                try
                {
                    recipe = GetCraftingRecipeInfo(data, tier, types[typeIndex]);
                }
                catch
                {
                    recipe = null;
                }

                if (!IsValid(recipe))
                {
                    continue;
                }

                int key = recipe.CraftingRecipeKey != 0 ? recipe.CraftingRecipeKey : (recipe.RecipeTier * 10 + (int)recipe.ItemCraftingType);
                if (seen.Add(key))
                {
                    output.Add(recipe);
                }
            }
        }

        output.Sort((left, right) =>
        {
            int tier = left.RecipeTier.CompareTo(right.RecipeTier);
            if (tier != 0)
            {
                return tier;
            }

            return left.ItemCraftingType.CompareTo(right.ItemCraftingType);
        });
        return output;
    }

    private static bool TryParseCraftingType(string raw, out global::TaskbarHero.Data.EItemCraftingType craftingType)
    {
        string normalized = (raw ?? string.Empty)
            .Trim()
            .Replace(" ", string.Empty)
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .ToUpperInvariant();

        switch (normalized)
        {
            case "MAINWEAPON":
            case "ARMA":
            case "ARMAPRINCIPAL":
                craftingType = global::TaskbarHero.Data.EItemCraftingType.MainWeapon;
                return true;
            case "SUBWEAPON":
            case "OFFHAND":
            case "ARMASECUNDARIA":
            case "SECUNDARIA":
                craftingType = global::TaskbarHero.Data.EItemCraftingType.SubWeapon;
                return true;
            case "HELMET":
            case "CASCO":
                craftingType = global::TaskbarHero.Data.EItemCraftingType.Helmet;
                return true;
            case "ARMOR":
            case "ARMADURA":
                craftingType = global::TaskbarHero.Data.EItemCraftingType.Armor;
                return true;
            case "GLOVES":
            case "GUANTES":
                craftingType = global::TaskbarHero.Data.EItemCraftingType.Gloves;
                return true;
            case "BOOTS":
            case "BOTAS":
                craftingType = global::TaskbarHero.Data.EItemCraftingType.Boots;
                return true;
            case "ACCESSORY":
            case "ACCESORIO":
            case "ACCESORIOS":
                craftingType = global::TaskbarHero.Data.EItemCraftingType.Accessory;
                return true;
            default:
                craftingType = global::TaskbarHero.Data.EItemCraftingType.None;
                return false;
        }
    }

    private static global::TaskbarHero.Data.CraftingRecipeInfoData FindCraftingRecipe(
        global::bas data,
        global::TaskbarHero.Data.EItemCraftingType craftingType,
        int requestedTier)
    {
        if (data == null)
        {
            return null;
        }

        if (requestedTier > 0)
        {
            try
            {
                return GetCraftingRecipeInfo(data, requestedTier, craftingType);
            }
            catch
            {
                return null;
            }
        }

        var recipes = CollectCraftingRecipes(data);
        global::TaskbarHero.Data.CraftingRecipeInfoData best = null;
        for (int i = 0; i < recipes.Count; i++)
        {
            var recipe = recipes[i];
            if (!IsValid(recipe) || recipe.ItemCraftingType != craftingType)
            {
                continue;
            }

            if (!IsValid(best) || recipe.RecipeTier > best.RecipeTier)
            {
                best = recipe;
            }
        }

        return best;
    }

    private static string DescribeCraftingMaterials(global::bas data, string rawMaterial, string rawIndex)
    {
        var requirements = ParseCraftingMaterialRequirements(rawMaterial);
        if (requirements.Count == 0)
        {
            return string.IsNullOrWhiteSpace(rawMaterial) ? "none" : rawMaterial;
        }

        var builder = new StringBuilder();
        for (int i = 0; i < requirements.Count; i++)
        {
            int materialKey = requirements[i].ItemKey;
            int amount = requirements[i].Amount;
            var itemInfo = GetItemInfo(materialKey);
            var materialInfo = GetMaterialInfo(data, materialKey);

            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(materialKey)
                .Append('x')
                .Append(amount);

            if (IsValid(itemInfo))
            {
                builder.Append(':')
                    .Append(itemInfo.GRADE)
                    .Append(':')
                    .Append(itemInfo.ItemSynthesisType)
                    .Append(":steam")
                    .Append(itemInfo.IsSteamItem ? "Y" : "N");
            }

            if (IsValid(materialInfo))
            {
                builder.Append(":mat")
                    .Append(materialInfo.bgpu)
                    .Append(":tier")
                    .Append(materialInfo.bgpv);
            }
        }

        return builder.ToString();
    }

    private static System.Collections.Generic.List<CraftMaterialRequirement> ParseCraftingMaterialRequirements(string rawMaterial)
    {
        var requirements = new System.Collections.Generic.List<CraftMaterialRequirement>();
        if (string.IsNullOrWhiteSpace(rawMaterial))
        {
            return requirements;
        }

        var tokenMatches = Regex.Matches(rawMaterial, @"(?<key>\d+)\s*_\s*(?<amount>\d+)");
        for (int i = 0; i < tokenMatches.Count; i++)
        {
            var match = tokenMatches[i];
            if (!int.TryParse(match.Groups["key"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int key) ||
                !int.TryParse(match.Groups["amount"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) ||
                key <= 0)
            {
                continue;
            }

            requirements.Add(new CraftMaterialRequirement
            {
                ItemKey = key,
                Amount = Math.Max(1, amount)
            });
        }

        if (requirements.Count > 0)
        {
            return requirements;
        }

        var materialKeys = ExtractInts(rawMaterial);
        for (int i = 0; i < materialKeys.Count; i++)
        {
            if (materialKeys[i] <= 0)
            {
                continue;
            }

            requirements.Add(new CraftMaterialRequirement
            {
                ItemKey = materialKeys[i],
                Amount = 1
            });
        }

        return requirements;
    }

    private static System.Collections.Generic.List<int> ExtractInts(string value)
    {
        var numbers = new System.Collections.Generic.List<int>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return numbers;
        }

        var matches = Regex.Matches(value, @"\d+");
        for (int i = 0; i < matches.Count; i++)
        {
            if (int.TryParse(matches[i].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                numbers.Add(number);
            }
        }

        return numbers;
    }

    private static string CleanCell(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }

    private static bool TryParseSpeed(string rawSpeed, out float speed)
    {
        if (float.TryParse(rawSpeed, NumberStyles.Float, CultureInfo.InvariantCulture, out speed))
        {
            return true;
        }

        return float.TryParse(
            rawSpeed?.Replace(',', '.') ?? string.Empty,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out speed);
    }

    private static string BuildItemKeyExamples(Il2CppSystem.Collections.Generic.List<global::TaskbarHero.Data.ItemInfoData> list)
    {
        var builder = new StringBuilder();
        AppendBestClassGearExamples(builder, GetTargetHeroLevel());
        AppendItemExamples(builder, list, global::TaskbarHero.Data.EItemType.GEAR, 12, "GEAR");
        AppendItemExamples(builder, list, global::TaskbarHero.Data.EItemType.MATERIAL, 8, "MATERIAL");
        return builder.ToString();
    }

    private static void AppendBestClassGearExamples(StringBuilder builder, int targetLevel)
    {
        string summary = BuildBestClassGearSetSummary(targetLevel);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(" | ");
        }

        builder.Append("BEST_CLASSES<=lv")
            .Append(targetLevel)
            .Append(": ")
            .Append(summary);
    }

    private static void AppendItemExamples(
        StringBuilder builder,
        Il2CppSystem.Collections.Generic.List<global::TaskbarHero.Data.ItemInfoData> list,
        global::TaskbarHero.Data.EItemType itemType,
        int limit,
        string label)
    {
        int shown = 0;
        int total = 0;
        var local = new StringBuilder();

        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            if (!IsValid(item) || item.ITEMTYPE != itemType || item.IsDeletedInServer)
            {
                continue;
            }

            total++;
            if (shown >= limit)
            {
                continue;
            }

            if (shown > 0)
            {
                local.Append("; ");
            }

            local.Append(item.ItemKey)
                .Append(':')
                .Append(item.GRADE)
                .Append(':')
                .Append(item.PARTS)
                .Append(":lv")
                .Append(item.Level);

            string nameKey = item.NameKey;
            if (!string.IsNullOrWhiteSpace(nameKey))
            {
                local.Append(':').Append(nameKey);
            }

            shown++;
        }

        if (shown == 0)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append(" | ");
        }

        builder.Append(label)
            .Append(' ')
            .Append(total)
            .Append(": ")
            .Append(local);
    }

    private static global::bau GetSaveManager()
    {
        AttachIl2CppThread();
        var manager = global::nq<global::bau>.bsfi;
        return manager == null || manager.Pointer == IntPtr.Zero ? null : manager;
    }

    private static global::bas GetDataManager()
    {
        AttachIl2CppThread();
        var manager = global::nq<global::bas>.bsfi;
        return manager == null || manager.Pointer == IntPtr.Zero ? null : manager;
    }

    private static global::TaskbarHero.Manager.LocalInventoryManager GetLocalInventoryManager()
    {
        AttachIl2CppThread();
        try
        {
            var manager = global::TaskbarHero.Manager.LocalInventoryManager.bszh;
            return manager == null || manager.Pointer == IntPtr.Zero ? null : manager;
        }
        catch (Exception ex)
        {
            Plugin.FileLog("LocalInventoryManager lookup failed: " + ex.Message);
            return null;
        }
    }

    private static global::TaskbarHero.Manager.PetManager GetPetManager()
    {
        AttachIl2CppThread();
        var manager = global::nq<global::TaskbarHero.Manager.PetManager>.bsfi;
        return manager == null || manager.Pointer == IntPtr.Zero ? null : manager;
    }

    private static global::TaskbarHero.StageManager GetStageManager()
    {
        AttachIl2CppThread();
        var manager = global::nq<global::TaskbarHero.StageManager>.bsfi;
        return manager == null || manager.Pointer == IntPtr.Zero ? null : manager;
    }

    private static global::TaskbarHero.Data.ItemInfoData GetItemInfo(int itemKey)
    {
        var data = GetDataManager();
        if (data == null)
        {
            return null;
        }

        try
        {
            var item = data.mgm(itemKey);
            if (item != null && item.Pointer != IntPtr.Zero)
            {
                return item;
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"mgl item lookup failed for {itemKey}: {ex.Message}");
        }

        try
        {
            var item = data.gvd(itemKey);
            if (item != null && item.Pointer != IntPtr.Zero)
            {
                return item;
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"gvd item lookup failed for {itemKey}: {ex.Message}");
        }

        foreach (string name in new[] { "mjq", "gxm", "jno" })
        {
            try
            {
                var item = TryInvokeInstanceObject<global::TaskbarHero.Data.ItemInfoData>(data, name, itemKey);
                if (item != null && item.Pointer != IntPtr.Zero)
                {
                    return item;
                }
            }
            catch (Exception ex)
            {
                Plugin.FileLog($"{name} item lookup failed for {itemKey}: {ex.Message}");
            }
        }

        return null;
    }

    private static global::zb GetRuntimeAccountStatusManager(out string detail)
    {
        detail = string.Empty;
        var attempts = new StringBuilder();

        try
        {
            var singleton = GetAccountStatusManagerFromSingleton(attempts);
            if (IsAccountStatusManagerReady(singleton, attempts, "singleton"))
            {
                detail = "Encontrado via singleton.";
                return singleton;
            }

            var sceneManager = GetAccountStatusManagerFromScene(attempts);
            if (IsAccountStatusManagerReady(sceneManager, attempts, "escena"))
            {
                detail = "Encontrado via escena.";
                return sceneManager;
            }

            detail = attempts.ToString();
            if (Interlocked.Increment(ref _accountStatusLookupLogCount) <= 8)
            {
                Plugin.FileLog("AccountStatus lookup not ready: " + detail);
            }

            return null;
        }
        catch (Exception ex)
        {
            Plugin.FileLog("AccountStatus lookup failed: " + ex.Message);
            detail = "Lookup fallo: " + ex.Message + ".";
            return null;
        }
    }

    private static global::zb GetAccountStatusManagerFromSingleton(StringBuilder attempts)
    {
        try
        {
            var getter = AccountStatusManagerGetterMethod.Value;
            if (getter == null)
            {
                attempts.Append("singleton sin getter; ");
                return null;
            }

            var manager = getter.Invoke(null, Array.Empty<object>()) as global::zb;
            attempts.Append(IsValid(manager) ? "singleton ok; " : "singleton null; ");
            return manager;
        }
        catch (Exception ex)
        {
            attempts.Append("singleton error ").Append(ex.Message).Append("; ");
            return null;
        }
    }

    private static global::zb GetAccountStatusManagerFromScene(StringBuilder attempts)
    {
        try
        {
            var manager = global::UnityEngine.Object.FindObjectOfType<global::zb>();
            if (IsValid(manager))
            {
                attempts.Append("FindObjectOfType manager ok; ");
                return manager;
            }

            attempts.Append("FindObjectOfType manager null; ");
        }
        catch (Exception ex)
        {
            attempts.Append("FindObjectOfType error ").Append(ex.Message).Append("; ");
        }

        try
        {
            var managers = global::UnityEngine.Resources.FindObjectsOfTypeAll<global::zb>();
            int count = managers?.Length ?? 0;
            attempts.Append("Resources zb=").Append(count).Append("; ");
            for (int i = 0; i < count; i++)
            {
                if (IsValid(managers[i]))
                {
                    attempts.Append("Resources[").Append(i).Append("] manager ok; ");
                    return managers[i];
                }
            }
        }
        catch (Exception ex)
        {
            attempts.Append("Resources error ").Append(ex.Message).Append("; ");
        }

        return null;
    }

    private static bool IsAccountStatusManagerReady(global::zb manager, StringBuilder attempts, string source)
    {
        if (!IsValid(manager))
        {
            attempts.Append(source).Append(" manager null; ");
            return false;
        }

        try
        {
            _ = manager.kqq(global::TaskbarHero.StatusSystem.EAccountStatus.AllHeroAttackSpeed);
            attempts.Append(source).Append(" status ok; ");
            return true;
        }
        catch (Exception ex)
        {
            attempts.Append(source).Append(" status read error ").Append(ex.Message).Append("; ");
            return false;
        }
    }

    private static int ReadAccountStatus(global::zb manager, global::TaskbarHero.StatusSystem.EAccountStatus status)
    {
        return manager.kqq(status);
    }

    private static void SetAccountStatusContribution(global::zb manager, global::TaskbarHero.StatusSystem.EAccountStatus status, int value, int source)
    {
        try
        {
            manager.kqw(source, status, value);
            return;
        }
        catch
        {
            foreach (string methodName in new[] { "hpm", "kqx", "nit", "cic", "jqv", "cgm", "mkr", "kqz", "fql", "bfx" })
            {
                if (TryInvokeInstanceMethod(manager, methodName, source, status, value))
                {
                    return;
                }
            }
        }
    }

    private static global::TaskbarHero.PlayerSaveData GetSaveData()
    {
        var save = GetSaveDataOrNull();
        if (save == null || save.Pointer == IntPtr.Zero)
        {
            throw new InvalidOperationException("PlayerSaveData no cargado.");
        }

        return save;
    }

    private static global::TaskbarHero.PlayerSaveData GetSaveDataOrNull()
    {
        var manager = GetSaveManager();
        var save = manager == null ? null : GetSaveDataFromManager(manager);
        if (save != null && save.Pointer != IntPtr.Zero)
        {
            _lastKnownSaveData = save;
            return save;
        }

        return _lastKnownSaveData == null || _lastKnownSaveData.Pointer == IntPtr.Zero ? null : _lastKnownSaveData;
    }

    private static global::TaskbarHero.PlayerSaveData GetSaveDataFromManager(global::bau manager)
    {
        if (!IsValid(manager))
        {
            return null;
        }

        try
        {
            return InvokeInstanceMethod(manager, "mii") as global::TaskbarHero.PlayerSaveData;
        }
        catch (Exception ex)
        {
            Plugin.FileLog("PlayerSaveData lookup failed: " + ex.Message);
            return null;
        }
    }

    private static void AttachIl2CppThread()
    {
        if (_il2cppAttached)
        {
            return;
        }

        IntPtr domain = IL2CPP.il2cpp_domain_get();
        if (domain != IntPtr.Zero)
        {
            IL2CPP.il2cpp_thread_attach(domain);
            _il2cppAttached = true;
        }
    }

    private static string Fail(string label, Exception ex)
    {
        string status = $"{label}: {ex.Message}";
        Plugin.FileLog($"{label}: {ex}");
        return status;
    }
}

[HarmonyPatch(typeof(global::TaskbarHero.PlayerSaveData), nameof(global::TaskbarHero.PlayerSaveData.PreSave))]
internal static class TrainerPlayerSaveDataPreSavePatch
{
    internal static void Postfix(global::TaskbarHero.PlayerSaveData __instance)
    {
        ModActions.ApplySaveLifecycleUnlocks(__instance, "PreSave");
        ModActions.ReapplyHeroUnlocks(__instance);
        ModActions.ReapplyForcedHeroLevels(__instance);
        ModActions.NormalizeEquippedItemContainerDuplicates(__instance);
    }
}

[HarmonyPatch(typeof(global::TaskbarHero.PlayerSaveData), nameof(global::TaskbarHero.PlayerSaveData.PostLoad))]
internal static class TrainerPlayerSaveDataPostLoadPatch
{
    internal static void Postfix(global::TaskbarHero.PlayerSaveData __instance)
    {
        ModActions.ApplySaveLifecycleUnlocks(__instance, "PostLoad");
    }
}

internal static class TrainerStashUiPageUnlockPatch
{
    internal static void Postfix(global::TaskbarHero.UI.UI_RemakeStash __instance)
    {
        ModActions.ApplyStashPageUnlockForUi(__instance, "UI_RemakeStash");
    }
}

internal static class TrainerTradeShipUiUnlockPatch
{
    internal static void Postfix(global::TaskbarHero.UI.UI_TradingStash __instance)
    {
        ModActions.ApplyTradeShipUnlockForUi(__instance, "UI_TradingStash");
    }
}

[HarmonyPatch]
internal static class OneHitKillMonsterPatch
{
    private const float OneHitDamage = 1_000_000_000f;

    private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
    {
        return TrainerPatchDiscovery.FindDamageReceiverMethods(typeof(global::TaskbarHero.Monster));
    }

    private static void Prefix(ref global::TaskbarHero.DamageInfo __0)
    {
        if (Plugin.IsShuttingDown || !ModActions.OneHitKillEnabled || __0.OriginDamage <= 0f)
        {
            return;
        }

        __0.OriginDamage = OneHitDamage;
        __0.IsCritical = true;
        __0.FloatingDamageText = true;
        __0.PlayHitFeedBack = true;
    }
}

[HarmonyPatch]
internal static class GodModeHeroDamagePatch
{
    private static long _lastBlockedDamageLogTick;

    private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
    {
        return TrainerPatchDiscovery.FindDamageReceiverMethods(typeof(global::TaskbarHero.Hero));
    }

    private static bool Prefix(ref global::TaskbarHero.DamageInfo __0)
    {
        if (Plugin.IsShuttingDown || !ModActions.GodModeEnabled || __0.OriginDamage <= 0f)
        {
            return true;
        }

        LogBlockedDamage(__0.OriginDamage);
        __0.OriginDamage = 0f;
        __0.IsCritical = false;
        __0.FloatingDamageText = false;
        __0.PlayHitFeedBack = false;
        __0.PlayHitSound = false;
        return true;
    }

    private static void LogBlockedDamage(float damage)
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref _lastBlockedDamageLogTick);
        if (now - previous < 3000)
        {
            return;
        }

        Interlocked.Exchange(ref _lastBlockedDamageLogTick, now);
        Plugin.FileLog($"God mode bloqueo dano de heroe: {damage.ToString("0.##", CultureInfo.InvariantCulture)}.");
    }
}

[HarmonyPatch(typeof(global::UnityEngine.Time), "set_timeScale")]
internal static class TrainerTimeScalePatch
{
    private static void Prefix(ref float value)
    {
        ModActions.FilterRequestedTimeScale(ref value);
    }
}

[HarmonyPatch(typeof(global::UnityEngine.Time), "set_fixedDeltaTime")]
internal static class TrainerFixedDeltaTimePatch
{
    private static void Prefix(ref float value)
    {
        ModActions.FilterRequestedFixedDeltaTime(ref value);
    }
}

[HarmonyPatch(typeof(global::TaskbarHero.StageManager), "ifx")]
internal static class TrainerStageEnterPatch
{
    private static void Postfix()
    {
        ModActions.ReapplyGameSpeedIfNeeded("StageManager.ifx");
    }
}

[HarmonyPatch(typeof(global::TaskbarHero.StageManager), "ihc")]
internal static class TrainerStageChangePatch
{
    private static void Postfix()
    {
        ModActions.ReapplyGameSpeedIfNeeded("StageManager.ihc");
    }
}

[HarmonyPatch(typeof(global::TaskbarHero.StageManager), "ige")]
internal static class TrainerStageSpawnPatch
{
    private static void Postfix()
    {
        ModActions.ReapplyGameSpeedIfNeeded("StageManager.ige");
    }
}

[HarmonyPatch(typeof(global::TaskbarHero.StageManager), "iic")]
internal static class TrainerStageResetPatch
{
    private static void Postfix()
    {
        ModActions.ReapplyGameSpeedIfNeeded("StageManager.iic");
    }
}

[HarmonyPatch]
internal static class TrainerDlcOwnedPatch
{
    private static readonly object LogSync = new();
    private static readonly System.Collections.Generic.HashSet<uint> LoggedAppIds = new();

    private static bool Prepare()
    {
        Plugin.FileLog("DLC ownership runtime patch disabled for updated build; avoiding Steam/DLC stack overflow.");
        return false;
    }

    private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in typeof(global::TaskbarHero.DLCManager).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var parameters = method.GetParameters();
            if (method.ReturnType == typeof(bool) &&
                parameters.Length == 1 &&
                (parameters[0].ParameterType == typeof(uint) || parameters[0].ParameterType == typeof(int)))
            {
                yield return method;
            }
        }
    }

    private static void Postfix(object[] __args, ref bool __result)
    {
        if (Plugin.IsShuttingDown || !ModActions.ForceDlcOwnershipChecks)
        {
            return;
        }

        uint appId = 0;
        if (__args != null && __args.Length > 0 && __args[0] != null)
        {
            try { appId = Convert.ToUInt32(__args[0], CultureInfo.InvariantCulture); } catch { }
        }

        if (!__result && ShouldLog(appId))
        {
            Plugin.FileLog($"DLC ownership bypass appId={appId}");
        }

        __result = true;
    }

    private static bool ShouldLog(uint appId)
    {
        lock (LogSync)
        {
            return LoggedAppIds.Add(appId);
        }
    }
}

[HarmonyPatch]
internal static class TrainerHeroBoolBypassPatch
{
    private static readonly object LogSync = new();
    private static readonly System.Collections.Generic.HashSet<string> LoggedMethods = new();

    private static bool Prepare()
    {
        Plugin.FileLog("Hero runtime bool bypass disabled for updated build; using DLC/save persistence paths only.");
        return false;
    }

    private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in typeof(global::vd).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!method.IsSpecialName && method.ReturnType == typeof(bool) && method.GetParameters().Length == 0)
            {
                yield return method;
            }
        }
    }

    private static void Postfix(MethodBase __originalMethod, ref bool __result)
    {
        if (Plugin.IsShuttingDown || !ModActions.ForceHeroUnlockChecks)
        {
            return;
        }

        if (!__result && ShouldLog(__originalMethod?.Name ?? "unknown"))
        {
            Plugin.FileLog($"Hero runtime bool bypass method={__originalMethod?.Name}");
        }

        __result = true;
    }

    private static bool ShouldLog(string methodName)
    {
        lock (LogSync)
        {
            return LoggedMethods.Add(methodName);
        }
    }
}

[HarmonyPatch]
internal static class TrainerServerPendingItemValidationPatch
{
    private static bool Prepare()
    {
        return TrainerPatchDiscovery.HasOptionalPatchMethods("wz", "kai");
    }

    private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
    {
        return TrainerPatchDiscovery.FindOptionalPatchMethods("wz", "kai");
    }

    private static void Prefix()
    {
        if (!Plugin.IsShuttingDown)
        {
            ModActions.TryNormalizeGearCatalogForTrainer("Steam/server pending item validation");
        }
    }
}

[HarmonyPatch]
internal static class TrainerLocalSteamItemValidationPatch
{
    private static bool Prepare()
    {
        return TrainerPatchDiscovery.HasOptionalPatchMethods("wz", "kaj");
    }

    private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
    {
        return TrainerPatchDiscovery.FindOptionalPatchMethods("wz", "kaj");
    }

    private static void Prefix()
    {
        if (!Plugin.IsShuttingDown)
        {
            ModActions.TryNormalizeGearCatalogForTrainer("Steam/local item validation");
        }
    }
}

[HarmonyPatch]
internal static class TrainerSteamValidationWorkerPatch
{
    private static bool Prepare()
    {
        return TrainerPatchDiscovery.HasOptionalPatchMethods("wz", "kak");
    }

    private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
    {
        return TrainerPatchDiscovery.FindOptionalPatchMethods("wz", "kak");
    }

    private static void Prefix()
    {
        if (!Plugin.IsShuttingDown)
        {
            ModActions.TryNormalizeGearCatalogForTrainer("Steam validation worker");
        }
    }
}

[HarmonyPatch]
internal static class TrainerBackendInventoryRemovePatch
{
    private static bool Prepare()
    {
        Plugin.FileLog("Backend inventory remove patch disabled for updated build; avoiding recursive offline-reward crash.");
        return false;
    }

    private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
    {
        foreach (string name in new[] { "hfm", "kcd" })
        {
            var method = AccessTools.Method(typeof(global::qj), name);
            if (method != null)
            {
                yield return method;
            }
        }
    }

    private static bool Prefix(Il2CppSystem.Collections.Generic.List<ulong> a, global::TaskbarHero.UI.EMovePivotTarget b)
    {
        if (Plugin.IsShuttingDown)
        {
            return true;
        }

        return ModActions.AllowFilteredBackendInventoryRemoval(a, b, "qi.het backend inventory remove");
    }
}

[HarmonyPatch]
internal static class TrainerServerDeletedItemCleanupPatch
{
    private static bool Prepare()
    {
        return TrainerPatchDiscovery.HasOptionalPatchMethods("ws", "jzh");
    }

    private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
    {
        return TrainerPatchDiscovery.FindOptionalPatchMethods("ws", "jzh");
    }

    private static bool Prefix()
    {
        if (Plugin.IsShuttingDown)
        {
            return true;
        }

        ModActions.TryNormalizeGearCatalogForTrainer("Steam/server deleted item cleanup skipped");
        Plugin.FileLog("Steam/server deleted item cleanup skipped by trainer.");
        return false;
    }
}

internal static class TrainerPatchDiscovery
{
    internal static System.Collections.Generic.IEnumerable<MethodBase> FindDamageReceiverMethods(Type type)
    {
        if (type == null)
        {
            yield break;
        }

        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.ReturnType != typeof(void))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 2 &&
                parameters[0].ParameterType == typeof(global::TaskbarHero.DamageInfo) &&
                parameters[1].ParameterType == typeof(bool))
            {
                yield return method;
            }
        }
    }

    internal static System.Collections.Generic.IEnumerable<MethodBase> FindOptionalPatchMethods(string typeName, params string[] methodNames)
    {
        var type = FindGameType(typeName);
        if (type == null)
        {
            yield break;
        }

        for (int i = 0; i < methodNames.Length; i++)
        {
            var method = FindMethod(type, methodNames[i]);
            if (method != null)
            {
                yield return method;
            }
        }
    }

    internal static bool HasOptionalPatchMethods(string typeName, params string[] methodNames)
    {
        var type = FindGameType(typeName);
        if (type == null)
        {
            return false;
        }

        for (int i = 0; i < methodNames.Length; i++)
        {
            if (FindMethod(type, methodNames[i]) != null)
            {
                return true;
            }
        }

        return false;
    }

    private static Type FindGameType(string typeName)
    {
        var assembly = typeof(global::TaskbarHero.PlayerSaveData).Assembly;
        try
        {
            var direct = assembly.GetType(typeName);
            if (direct != null)
            {
                return direct;
            }

            foreach (var type in assembly.GetTypes())
            {
                if (type.FullName == typeName || type.Name == typeName)
                {
                    return type;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static MethodInfo FindMethod(Type type, string methodName)
    {
        if (type == null || string.IsNullOrWhiteSpace(methodName))
        {
            return null;
        }

        return type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    }
}
