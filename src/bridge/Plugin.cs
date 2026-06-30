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
    public const string PluginVersion = "1.5";
    public const string PipeName = "TaskbarHeroTrainerPipe";

    internal static ManualLogSource LogSource;
    private static ModActions _actions;
    private static TrainerPipeServer _pipeServer;
    private static int _shutdownStarted;

    internal static bool IsShuttingDown => _shutdownStarted != 0;

    public override void Load()
    {
        LogSource = Log;
        _actions = new ModActions();
        _pipeServer = new TrainerPipeServer(_actions);

        LogSource.LogInfo($"{PluginName} {PluginVersion} loading");
        FileLog($"{PluginName} {PluginVersion} Load()");

        try
        {
            new Harmony(PluginGuid).PatchAll(typeof(Plugin).Assembly);
            FileLog("Harmony patches loaded");
        }
        catch (Exception ex)
        {
            FileLog("Harmony patch load failed: " + ex);
        }

        _pipeServer.Start();
    }

    public override bool Unload()
    {
        BeginShutdown("BepInEx unload");
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
    private const int ManualChestAddAmount = 25;
    private const int FastChestDropIntervalSeconds = 10;
    private const int FastChestChanceTarget = 100_000_000;
    private const int FastChestChancePercentTarget = 10_000;
    private const int FastChestMaxNormalChests = 99;
    private const int FastChestStatusBoostSource = 941414;
    private const float FastChestCooldownExpireSlackSeconds = 0.25f;
    private const float FastChestDropChanceInput = 100f;
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
    internal static volatile bool FastChestDropsEnabled;
    internal static volatile bool FastChestStatBoostEnabled;
    internal static volatile bool ForceHeroUnlockChecks = true;
    internal static volatile bool ForceDlcOwnershipChecks = true;
    private static readonly int[] DefaultPersistentHeroFormation = { 101, 601, 501 };
    private static readonly object SessionChestSync = new();
    private static readonly HashSet<ulong> SessionChestIds = new();
    private static readonly object ChestStatusBoostSync = new();
    private static readonly object FastChestCooldownSync = new();
    private static readonly Lazy<MethodInfo> StageManagerSetBoxCooldownMethod = new(() => AccessTools.Method(typeof(global::TaskbarHero.StageManager), "ihw"));
    private static readonly Lazy<MethodInfo> StageManagerBoxCooldownsGetter = new(ResolveStageManagerBoxCooldownsGetter);
    private static readonly Lazy<FieldInfo> StageManagerBoxCooldownsField = new(ResolveStageManagerBoxCooldownsField);
    private static readonly Lazy<MethodInfo> StageBoxManagerMethod = new(() => AccessTools.Method(typeof(global::wk), "jxg") ?? AccessTools.PropertyGetter(typeof(global::wk), "bsqk"));
    private static readonly Lazy<MethodInfo> StageBoxFindByUniqueIdMethod = new(() => AccessTools.Method(typeof(global::vy), "jvk"));
    private static readonly Lazy<MethodInfo> AccountStatusManagerGetterMethod = new(() => AccessTools.PropertyGetter(typeof(global::yw), "bfpw") ?? AccessTools.Method(typeof(global::yw), "knu"));
    private static int _accountStatusLookupLogCount;
    private static long _nextFastChestDropUtcTicks;
    private static long _nextFastChestStatusSyncUtcTicks;
    private readonly object _sync = new();
    private bool _fastChestDropsEnabled;
    private static int _fastChestDropForces;
    private static int _fastChestCooldownExpirations;
    private static int _fastChestCooldownUpdates;
    private static int _fastChestChanceInputUpdates;
    private static int _fastChestStageBoxKeyFixes;
    private static int _fastChestStatusMainThreadUpdates;
    private static int _fastChestNoStageBoxKeySkips;
    private static int _fastChestEligibleStageBoxAttempts;
    private static int _fastChestChanceResultObservations;
    private static int _fastChestRequestObservations;
    private static float _nextFastChestCooldownUnscaled;
    private static volatile bool _fastChestStatusContributionApplied;
    private static float _desiredGameSpeed = 1f;
    private static long _lastGameSpeedReapplyLogTick;

    [ThreadStatic]
    private static bool _il2cppAttached;

    public void StopBackgroundActions()
    {
        lock (_sync)
        {
            StopChestTimerLocked();
        }
    }

    internal static void BeginShutdown()
    {
        IsGameQuitting = true;
        OneHitKillEnabled = false;
        FastChestDropsEnabled = false;
        FastChestStatBoostEnabled = false;
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

        if (normalized.StartsWith("FAST_CHESTS:", StringComparison.Ordinal))
        {
            string state = normalized.Substring("FAST_CHESTS:".Length);
            return SetFastChestDrops(state == "ON" || state == "TRUE" || state == "1");
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
            "FAST_CHESTS_ON" => SetFastChestDrops(true),
            "FAST_CHESTS_OFF" => SetFastChestDrops(false),
            "CHEST_DROP_DIAG" => BuildChestDropBoostDiagnostics(),
            "FIX_EQUIPPED_DUPES" => FixEquippedItemDuplicates(),
            "REPAIR_EQUIPPED_DUPES" => RepairEquippedItemDuplicates(),
            "RESTORE_HEROES" => RestorePersistentHeroes(),
            "UNLOCK_HEROES_ONLY" => UnlockHeroesOnly(),
            "SKILL_POINTS_0" => SetHeroAbilityPoints(0),
            "SKILL_POINTS_999" => SetHeroAbilityPoints(999),
            "UNLOCK_SLOTS" => UnlockInventoryAndStash(),
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
                var save = manager?.bggy;
                int currencyCount = save?.currenySaveDatas?.Count ?? 0;
                int heroCount = save?.heroSaveDatas?.Count ?? 0;
                int inventoryCount = save?.inventorySaveDatas?.Count ?? 0;
                int inventoryUnlocked = CountUnlockedInventory(save);
                int inventoryEmpty = CountEmptyUnlockedInventory(save);
                int stashCount = save?.stashSaveDatas?.Count ?? 0;
                int stashUnlocked = CountUnlockedStash(save);
                int itemCount = save?.itemSaveDatas?.Count ?? 0;
                int petCount = save?.PetSaveData?.Count ?? 0;
                int petUnlocked = CountUnlockedPets(save);
                int heroCatalogCount = GetDataManager()?.heroInfoData?.Count ?? 0;
                string heroLevels = BuildHeroLevelSummary(save);
                string boxSummary = BuildBoxSummary(save);
                string status = manager == null
                    ? "Save manager no listo. Entra en partida y pulsa Refresh."
                    : $"Runtime listo. Monedas {currencyCount}, heroes save {heroCount}/catalogo {heroCatalogCount} [{heroLevels}], inv {inventoryUnlocked}/{inventoryCount} ({inventoryEmpty} libres), alijo {stashUnlocked}/{stashCount}, items {itemCount}, mascotas {petUnlocked}/{petCount}, {boxSummary}, hero bypass {(ForceHeroUnlockChecks ? "ON" : "OFF")}, chest boost {(_fastChestDropsEnabled ? "ON" : "OFF")}.";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Refresh fallo", ex);
            }
        }
    }

    public string AddLocalChests(string rawType, int amount, string source)
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                if (IsSuspiciousFreshSave(save))
                {
                    string blocked = "Bloqueado cofres: el save cargado parece partida nueva/vacia (" + DescribeSafetyShape(save) + "). No se guardo nada.";
                    Plugin.FileLog(blocked);
                    return blocked;
                }

                if (IsUnsafeForChestMutation(save, out string unsafeReason))
                {
                    string blocked = "Bloqueado cofres: save no estable para modificar (" + unsafeReason + "). Entra en partida, espera a que cargue todo y pulsa Refresh.";
                    Plugin.FileLog(blocked);
                    return blocked;
                }

                if (!TryParseBoxType(rawType, out var boxType))
                {
                    return $"Tipo de cofre invalido: {rawType}. Usa NORMAL, BOSS o ACTBOSS.";
                }

                if (!AreRuntimeBoxesSyncedWithSave(save, out string syncReason))
                {
                    int pruned = PruneStaleSaveChestsLocked(save, out int prunedItemSaves, out int prunedManager, out int pruneRefreshes);
                    if (pruned > 0)
                    {
                        string pruneSaveStatus = RequestSave();
                        Plugin.FileLog($"Cofres stale autolimpiados antes de crear: ids {pruned}, item saves {prunedItemSaves}, manager {prunedManager}, UI {pruneRefreshes}. {BuildBoxSummary(save)}. {pruneSaveStatus}");
                    }

                    if (!AreRuntimeBoxesSyncedWithSave(save, out syncReason))
                    {
                        string blocked = "Bloqueado cofres: runtime de cofres no sincronizado (" + syncReason + "). Espera unos segundos en partida y pulsa Refresh antes de intentarlo.";
                        Plugin.FileLog(blocked);
                        return blocked;
                    }
                }

                amount = Math.Clamp(amount, 1, 9999);
                int beforeRuntime = GetRuntimeBoxCount(boxType);
                string details = AddLocalChestsLocked(save, boxType, amount, source, out int runtimeChanged, out int uiRefreshes, out var createdIds);
                int afterRuntime = GetRuntimeBoxCount(boxType);
                if (afterRuntime < beforeRuntime + createdIds.Count)
                {
                    RemoveSaveBoxEntries(save, createdIds);
                    RemoveRuntimeBoxEntries(boxType, createdIds);
                    string blocked = $"{details} Bloqueado: el runtime no acepto los cofres ({beforeRuntime}->{afterRuntime}, esperados {beforeRuntime + createdIds.Count}). No se guardo.";
                    Plugin.FileLog(blocked);
                    return blocked;
                }

                MarkSessionChestIds(createdIds);
                int saveIdsRemoved = RemoveSessionChestSaveData(save, createdIds, out int itemSavesRemoved);
                string status = $"{details} Runtime {runtimeChanged}, UI {uiRefreshes}. Sesion runtime; no se guardo para evitar cofres invalidos persistentes. Save limpio ids {saveIdsRemoved}, item saves {itemSavesRemoved}. {BuildBoxSummary(save)}.";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Add chests fallo", ex);
            }
        }
    }

    public string SetFastChestDrops(bool enabled)
    {
        lock (_sync)
        {
            try
            {
                if (enabled)
                {
                    _fastChestDropForces = 0;
                    _fastChestCooldownExpirations = 0;
                    _fastChestCooldownUpdates = 0;
                    _fastChestChanceInputUpdates = 0;
                    _fastChestStageBoxKeyFixes = 0;
                    _fastChestStatusMainThreadUpdates = 0;
                    _fastChestNoStageBoxKeySkips = 0;
                    _fastChestEligibleStageBoxAttempts = 0;
                    _fastChestChanceResultObservations = 0;
                    _fastChestRequestObservations = 0;
                    ResetFastChestCooldownWindow();
                    _fastChestDropsEnabled = true;
                    FastChestDropsEnabled = true;
                    FastChestStatBoostEnabled = true;
                    string status = ApplyChestDropAccountStatusBoost(true);
                    status += $" Bonus de runas/cofres se sincroniza en hilo Unity durante StageManager.ihu; cooldown NORMAL objetivo {FastChestDropIntervalSeconds}s via StageManager.bdlr/unscaledTime; peticion de cofre por flujo de stage del juego; probabilidad de entrada NORMAL {FastChestDropChanceInput.ToString("0", CultureInfo.InvariantCulture)}%; sin crear cofres ni rewards locales falsos.";
                    Plugin.FileLog(status);
                    return status;
                }

                string restoreStatus = ApplyChestDropAccountStatusBoost(false);
                StopChestTimerLocked();
                string stopped = $"Chest drop boost OFF. {restoreStatus} Cooldowns acelerados: expirados {_fastChestCooldownExpirations}, fijados {_fastChestCooldownUpdates}; probabilidad subida {_fastChestChanceInputUpdates}; chance obs {_fastChestChanceResultObservations}; requests obs {_fastChestRequestObservations}; status sync {_fastChestStatusMainThreadUpdates}; stagebox key elegibles {_fastChestEligibleStageBoxAttempts}, sin key {_fastChestNoStageBoxKeySkips}, rellenados legacy {_fastChestStageBoxKeyFixes}. Forced reward path apagado; cofres forzados {_fastChestDropForces}.";
                Plugin.FileLog(stopped);
                return stopped;
            }
            catch (Exception ex)
            {
                return Fail("Chest drop boost fallo", ex);
            }
        }
    }

    private static string ChestRewardsDisabledMessage()
    {
        return "Cofres/rewards forzados desactivados: la apertura genera rewardUid local y Steam/backend lo marca como no emitido por servidor. Usa solo cofres obtenidos por el juego; el trainer puede limpiar cofres rotos con CHESTS_PRUNE_STALE.";
    }

    private static string ApplyChestDropAccountStatusBoost(bool enabled)
    {
        try
        {
            var accountStatusManager = GetRuntimeAccountStatusManager(out string lookupDetail);
            if (!IsValid(accountStatusManager))
            {
                return "AccountStatus aun no esta listo para diagnostico. " + lookupDetail + " ";
            }

            var targets = new (global::TaskbarHero.StatusSystem.EAccountStatus Status, int Minimum)[]
            {
                (global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceNormalChest, FastChestChanceTarget),
                (global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceNormalChestPercent, FastChestChancePercentTarget),
                (global::TaskbarHero.StatusSystem.EAccountStatus.MaxAmountNormalChest, FastChestMaxNormalChests),
                (global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceStageBossChest, FastChestChanceTarget),
                (global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceStageBossChestPercent, FastChestChancePercentTarget),
                (global::TaskbarHero.StatusSystem.EAccountStatus.MaxAmountStageBossChest, FastChestMaxNormalChests)
            };

            lock (ChestStatusBoostSync)
            {
                var details = new StringBuilder();
                foreach (var target in targets)
                {
                    int before = ReadAccountStatus(accountStatusManager, target.Status);
                    details.Append(target.Status).Append('=').Append(before);
                    if (before < target.Minimum)
                    {
                        details.Append(" (<").Append(target.Minimum).Append(')');
                    }

                    details.Append("; ");
                }

                string mode = enabled ? "ON" : "OFF";
                return $"Chest drop boost {mode}: stats cuenta leidos {details}La mutacion AccountStatus se difiere al hilo Unity para evitar crash IL2CPP. ";
            }
        }
        catch (Exception ex)
        {
            return Fail("Chest drop boost runtime", ex);
        }
    }

    public string BuildChestDropBoostDiagnostics()
    {
        lock (_sync)
        {
            try
            {
                var details = new StringBuilder();
                details.Append("Chest boost diag: flag ")
                    .Append(FastChestDropsEnabled ? "ON" : "OFF")
                    .Append("; interval ").Append(FastChestDropIntervalSeconds).Append("s; ");

                var accountStatusManager = GetRuntimeAccountStatusManager(out string lookupDetail);
                if (IsValid(accountStatusManager))
                {
                    AppendChestStatusDiagnostic(details, accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceNormalChest);
                    AppendChestStatusDiagnostic(details, accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceNormalChestPercent);
                    AppendChestStatusDiagnostic(details, accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.MaxAmountNormalChest);
                    AppendChestStatusDiagnostic(details, accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceStageBossChest);
                    AppendChestStatusDiagnostic(details, accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceStageBossChestPercent);
                    AppendChestStatusDiagnostic(details, accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.MaxAmountStageBossChest);
                }
                else
                {
                    details.Append("AccountStatus no listo: ").Append(lookupDetail);
                }

                var stageManager = GetStageManager();
                if (IsValid(stageManager))
                {
                    if (TryReadStageBoxCooldownValue(stageManager, global::TaskbarHero.EBoxType.NORMAL, out float value, out string cooldownDetail))
                    {
                        details.Append("bdlr NORMAL=")
                            .Append(value.ToString("0.00", CultureInfo.InvariantCulture))
                            .Append(", now=")
                            .Append(GetStageBoxCooldownClock().ToString("0.00", CultureInfo.InvariantCulture))
                            .Append("; ");
                    }
                    else
                    {
                        details.Append("bdlr no legible: ").Append(cooldownDetail).Append("; ");
                    }
                }
                else
                {
                    details.Append("StageManager no listo; ");
                }

                details.Append("runtime NORMAL ").Append(GetRuntimeBoxCount(global::TaskbarHero.EBoxType.NORMAL))
                    .Append(", BOSS ").Append(GetRuntimeBoxCount(global::TaskbarHero.EBoxType.BOSS))
                    .Append("; stagebox elegibles ").Append(_fastChestEligibleStageBoxAttempts)
                    .Append(", sin key ").Append(_fastChestNoStageBoxKeySkips)
                    .Append("; chance obs ").Append(_fastChestChanceResultObservations)
                    .Append(", request obs ").Append(_fastChestRequestObservations)
                    .Append(".");
                string status = details.ToString();
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Chest boost diag fallo", ex);
            }
        }
    }

    private static void AppendChestStatusDiagnostic(StringBuilder details, global::yw accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus status)
    {
        details.Append(status).Append('=').Append(ReadAccountStatus(accountStatusManager, status)).Append("; ");
    }

    public string PruneStaleSaveChests()
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                string unsafeReason = "save vacio/sospechoso";
                if (IsSuspiciousFreshSave(save) || IsUnsafeForChestMutation(save, out unsafeReason))
                {
                    string blocked = "Bloqueado limpiar cofres: el save no esta cargado/estable (" + DescribeSafetyShape(save) + "; " + unsafeReason + "). Entra en partida, espera a que cargue inventario/heroes y pulsa Refresh antes de limpiar.";
                    Plugin.FileLog(blocked);
                    return blocked;
                }

                int pruned = PruneStaleSaveChestsLocked(save, out int itemSavesRemoved, out int managerRemoved, out int uiRefreshes);
                if (pruned == 0)
                {
                    string clean = "No hay cofres stale en el save. " + BuildBoxSummary(save);
                    Plugin.FileLog(clean);
                    return clean;
                }

                string saveStatus = RequestSave();
                string status = $"Cofres stale limpiados: ids {pruned}, item saves {itemSavesRemoved}, manager {managerRemoved}, UI {uiRefreshes}. {BuildBoxSummary(save)}. {saveStatus}";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Prune stale chests fallo", ex);
            }
        }
    }

    private static int PruneStaleSaveChestsLocked(global::TaskbarHero.PlayerSaveData save, out int itemSavesRemoved, out int managerRemoved, out int uiRefreshes)
    {
        itemSavesRemoved = 0;
        managerRemoved = 0;
        uiRefreshes = 0;
        EnsureSerializedBoxData(save);
        var boxData = save.BoxData;
        var staleIds = new HashSet<ulong>();

        int count = Math.Min(boxData.BoxTypes.Count, Math.Min(boxData.BoxUniqueId.Count, boxData.BoxQuantity.Count));
        for (int i = 0; i < count; i++)
        {
            ulong uniqueId = boxData.BoxUniqueId[i];
            if (uniqueId != 0UL && GetRuntimeBoxQuantity(uniqueId) <= 0)
            {
                staleIds.Add(uniqueId);
                continue;
            }

            if (uniqueId != 0UL && !HasOpenableStageBoxData(uniqueId, out string reason))
            {
                Plugin.FileLog($"Prune invalid saved chest uid={uniqueId}: {reason}");
                staleIds.Add(uniqueId);
            }
        }

        CollectInvalidRuntimeStageBoxIds(staleIds);
        CollectOrphanStageBoxItemIds(save, staleIds);
        if (staleIds.Count == 0)
        {
            return 0;
        }

        var staleList = new List<ulong>(staleIds);
        RemoveSaveBoxEntries(save, staleList);
        RemoveRuntimeBoxEntries(global::TaskbarHero.EBoxType.NORMAL, staleList);
        RemoveRuntimeBoxEntries(global::TaskbarHero.EBoxType.BOSS, staleList);
        RemoveRuntimeBoxEntries(global::TaskbarHero.EBoxType.ACTBOSS, staleList);
        managerRemoved = RemoveStageBoxManagerEntries(staleList);
        itemSavesRemoved = RemoveItemSaves(save, staleIds);
        uiRefreshes = RefreshRuntimeBoxes();
        return staleIds.Count;
    }

    private static void CollectOrphanStageBoxItemIds(global::TaskbarHero.PlayerSaveData save, HashSet<ulong> staleIds)
    {
        var items = save?.itemSaveDatas;
        if (items == null)
        {
            return;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item == null || item.UniqueId == 0UL)
            {
                continue;
            }

            if (SaveBoxUniqueIdExists(save, item.UniqueId) || GetRuntimeBoxQuantity(item.UniqueId) > 0)
            {
                continue;
            }

            var itemInfo = GetItemInfo(item.ItemKey);
            if (IsValid(itemInfo) && itemInfo.ITEMTYPE == global::TaskbarHero.Data.EItemType.STAGEBOX)
            {
                staleIds.Add(item.UniqueId);
            }
        }
    }

    private static void CollectInvalidRuntimeStageBoxIds(HashSet<ulong> staleIds)
    {
        if (staleIds == null)
        {
            return;
        }

        try
        {
            var store = global::uz.ty.bshg;
            if (store == null)
            {
                return;
            }

            foreach (var boxType in new[]
                     {
                         global::TaskbarHero.EBoxType.NORMAL,
                         global::TaskbarHero.EBoxType.BOSS,
                         global::TaskbarHero.EBoxType.ACTBOSS
                     })
            {
                if (!store.TryGetValue(boxType, out var perType) || perType == null)
                {
                    continue;
                }

                var runtimeIds = new List<ulong>();
                foreach (ulong uniqueId in perType.Keys)
                {
                    runtimeIds.Add(uniqueId);
                }

                foreach (ulong uniqueId in runtimeIds)
                {
                    if (uniqueId == 0UL || GetRuntimeBoxQuantity(uniqueId) <= 0)
                    {
                        continue;
                    }

                    if (!HasOpenableStageBoxData(uniqueId, out string reason))
                    {
                        Plugin.FileLog($"Prune invalid runtime chest {boxType} uid={uniqueId}: {reason}");
                        staleIds.Add(uniqueId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog("CollectInvalidRuntimeStageBoxIds failed: " + ex.Message);
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
                int runtimeChanged = TryRuntimeCall("uz.tx.isj", () => global::uz.tx.isj());
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

                runtimeChanged += TryRuntimeCall("uz.tx.itn", () => global::uz.tx.itn());
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

        return $"heroes={save.heroSaveDatas?.Count ?? 0},items={save.itemSaveDatas?.Count ?? 0},inv={CountEmptyUnlockedInventory(save)}/{CountUnlockedInventory(save)} empty/unlocked,stash={CountUnlockedStash(save)}";
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
        string status = enabled ? "One hit kill activado." : "One hit kill desactivado.";
        Plugin.FileLog(status);
        return status;
    }

    public string SetHeroBypass(bool enabled)
    {
        ForceHeroUnlockChecks = enabled;
        ForceDlcOwnershipChecks = enabled;
        string status = enabled ? "Bypass heroes/DLC activado." : "Bypass heroes/DLC desactivado.";
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
                string status = $"Velocidad juego: {speed.ToString("0.0", CultureInfo.InvariantCulture)}x persistente.";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Set game speed fallo", ex);
            }
        }
    }

    public string UnlockInventoryAndStash()
    {
        lock (_sync)
        {
            try
            {
                var save = GetSaveData();
                int inventoryChanged = 0;
                int stashChanged = 0;
                int tradingStashChanged = 0;

                var inventory = save.inventorySaveDatas;
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
                        inventoryChanged++;
                    }
                }

                var stash = save.stashSaveDatas;
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
                        stashChanged++;
                    }
                }

                var tradingStash = save.remakeTradingStashSaveDatas;
                if (tradingStash != null)
                {
                    for (int i = 0; i < tradingStash.Count; i++)
                    {
                        var slot = tradingStash[i];
                        if (slot == null || slot.IsUnLock)
                        {
                            continue;
                        }

                        slot.IsUnLock = true;
                        tradingStashChanged++;
                    }
                }

                int runtimeInventory = RefreshRuntimeInventorySlots();
                int runtimeStash = RefreshRuntimeStashSlots();
                int runtimeTradingStash = RefreshRuntimeTradingStashSlots();
                string saveStatus = RequestSave();
                string status = $"Slots desbloqueados: inv {inventoryChanged}, alijo {stashChanged}, trade {tradingStashChanged}; runtime inv {runtimeInventory}, alijo {runtimeStash}, trade {runtimeTradingStash}. {saveStatus}";
                Plugin.FileLog(status);
                return status;
            }
            catch (Exception ex)
            {
                return Fail("Unlock inventory fallo", ex);
            }
        }
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

                    var clone = new global::TaskbarHero.EasySaveData.ItemSaveData(source.ItemKey, nextUniqueId)
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

                var newItem = new global::TaskbarHero.EasySaveData.ItemSaveData(itemKey, uniqueId)
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
                var recipes = data?.mco();
                if (recipes == null)
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
                var petInfos = GetDataManager()?.bsxk;
                if (petInfos == null)
                {
                    return "Catalogo de mascotas no cargado todavia.";
                }

                if (save.PetSaveData == null)
                {
                    save.PetSaveData = new Il2CppSystem.Collections.Generic.List<global::TaskbarHero.EasySaveData.PetSaveData>();
                }

                int changed = 0;
                int runtimeChanged = 0;
                for (int i = 0; i < petInfos.Count; i++)
                {
                    var info = petInfos[i];
                    if (!IsValid(info) || info.PetKey <= 0)
                    {
                        continue;
                    }

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
                string status = $"Mascotas desbloqueadas: save {changed}/{petInfos.Count}, runtime {runtimeChanged}. {saveStatus}";
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
                itemCache = new global::uz.uc.ua(itemInfo, itemSave, false);
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
            steps += TryRuntimeCall("ua.ixr item save refresh", () => InvokeInstanceMethod(itemCache, "ixr", itemSave));
            steps += TryRuntimeCall("uz.ty.iul", () => global::uz.ty.iul(itemSave.UniqueId, itemCache));
            steps += TryRuntimeCall("uz.ty.miv", () => global::uz.ty.miv(itemSave.UniqueId, itemCache));

            var slotCache = FindRuntimeInventorySlot(slot.Index);
            if (IsValid(slotCache))
            {
                steps += TryRuntimeCall("ve.jjz", () => slotCache.jjz(itemSave.UniqueId));
                steps += TryRuntimeCall("ve.bfae", () => slotCache.bfae?.Invoke());
                steps += TryRuntimeCall("uz.ty.iuj", () => global::uz.ty.iuj(itemSave.UniqueId, slotCache));
            }

            var inventoryManager = GetLocalInventoryManager();
            if (IsValid(inventoryManager))
            {
                steps += TryRuntimeCall("LocalInventoryManager.kpx", () => inventoryManager.kpx(slot.Index, itemSave.UniqueId));
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

        changed += TryRuntimeCall("ve.jjz reconcile", () => slotCache.jjz(slot.ItemUniqueId));
        changed += TryRuntimeCall("ve.bfae reconcile", () => slotCache.bfae?.Invoke());
        return changed;
    }

    private static int TryAddRuntimeItemByGameApi(int itemKey, ulong uniqueId)
    {
        int steps = 0;
        bool success = false;
        success |= TryAddRuntimeItemByGameApi("uz.uc.fzn", () => global::uz.uc.fzn(itemKey, uniqueId, 1, false), ref steps);
        if (!success)
        {
            success |= TryAddRuntimeItemByGameApi("uz.uc.c", () => global::uz.uc.c(itemKey, uniqueId, 1, false), ref steps);
        }

        if (!success)
        {
            success |= TryAddRuntimeItemByGameApi("uz.uc.ize", () => global::uz.uc.ize(itemKey, uniqueId, 1, false), ref steps);
        }

        if (!success)
        {
            TryAddRuntimeItemByGameApi("uz.uc.ouh", () => global::uz.uc.ouh(itemKey, uniqueId, 1, false), ref steps);
        }

        return steps;
    }

    private static bool TryAddRuntimeItemByGameApi(string label, Func<global::TaskbarHero.AddItemResult> action, ref int steps)
    {
        try
        {
            var result = action();
            steps++;
            Plugin.FileLog($"{label} result type={result.ItemType} addResult={result.AddResult}");
            return result.ItemType != global::TaskbarHero.Data.EItemType.NONE && result.AddResult >= 0;
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"{label} failed: {ex.Message}");
            return false;
        }
    }

    private static global::uz.uc.ua FindRuntimeItem(ulong uniqueId)
    {
        global::uz.uc.ua item = null;

        try { item = global::uz.uc.izb(uniqueId); } catch { }
        if (IsValid(item)) { return item; }

        try { item = global::uz.uc.ogk(uniqueId); } catch { }
        if (IsValid(item)) { return item; }

        try { item = global::uz.uc.htm(uniqueId); } catch { }
        if (IsValid(item)) { return item; }

        try { item = global::uz.uc.bbe(uniqueId); } catch { }
        if (IsValid(item)) { return item; }

        try { item = global::uz.uc.izf(uniqueId); } catch { }
        return IsValid(item) ? item : null;
    }

    private static global::ve FindRuntimeInventorySlot(int index)
    {
        try
        {
            var slots = global::uz.ty.bshf;
            if (slots != null && slots.ContainsKey(index))
            {
                return slots[index];
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"FindRuntimeInventorySlot failed: {ex.Message}");
        }

        return null;
    }

    private static global::uz.StashCache FindRuntimeStashSlot(int index)
    {
        try
        {
            var slot = global::uz.Stash.jjd(index);
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

        try { slot = global::uz.uy.hge(index); } catch { }
        if (IsValid(slot)) { return slot; }

        try { slot = global::uz.uy.emj(index); } catch { }
        if (IsValid(slot)) { return slot; }

        try { slot = global::uz.uy.jkn(index); } catch { }
        if (IsValid(slot)) { return slot; }

        try { slot = global::uz.uy.jth(index); } catch { }
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

                changed += TryRuntimeCall("ve.jqs", () => slot.jqs());
                changed += TryRuntimeCall("ve.bfae", () => slot.bfae?.Invoke());
            }

            changed += TryRuntimeCall("uz.ty.gjh", () => global::uz.ty.gjh());
            changed += TryRuntimeCall("uz.ty.jwz", () => global::uz.ty.jwz());
            changed += TryRuntimeCall("uz.ty.berw", () => global::uz.ty.berw?.Invoke());
            changed += TryRuntimeCall("uz.ty.berx", () => global::uz.ty.berx?.Invoke());
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"RefreshRuntimeInventorySlots failed: {ex.Message}");
        }

        return changed;
    }

    private void ChestTimerTick(object state)
    {
        // Legacy timer callback kept so older builds can unload cleanly after upgrade.
    }

    private void StopChestTimerLocked()
    {
        FastChestDropsEnabled = false;
        FastChestStatBoostEnabled = false;
        _fastChestDropsEnabled = false;
        ResetFastChestCooldownWindow();
    }

    private static void ResetFastChestCooldownWindow()
    {
        lock (FastChestCooldownSync)
        {
            _nextFastChestCooldownUnscaled = 0f;
        }

        Interlocked.Exchange(ref _nextFastChestDropUtcTicks, 0);
        Interlocked.Exchange(ref _nextFastChestStatusSyncUtcTicks, 0);
    }

    internal static void SyncFastChestAccountStatusOnMainThread()
    {
        if (Plugin.IsShuttingDown || IsGameQuitting)
        {
            return;
        }

        bool shouldApply = FastChestStatBoostEnabled;
        if (!shouldApply && !_fastChestStatusContributionApplied)
        {
            return;
        }

        long now = DateTime.UtcNow.Ticks;
        long next = Interlocked.Read(ref _nextFastChestStatusSyncUtcTicks);
        if (shouldApply && _fastChestStatusContributionApplied && now < next)
        {
            return;
        }

        try
        {
            var accountStatusManager = GetRuntimeAccountStatusManager(out string lookupDetail);
            if (!IsValid(accountStatusManager))
            {
                if (shouldApply)
                {
                    Plugin.FileLog("Fast chest status: AccountStatus no listo en hilo Unity. " + lookupDetail);
                }

                Interlocked.Exchange(ref _nextFastChestStatusSyncUtcTicks, DateTime.UtcNow.AddSeconds(2).Ticks);
                return;
            }

            if (shouldApply)
            {
                int changed = 0;
                var details = new StringBuilder();
                changed += SetFastChestAccountStatusContribution(accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceNormalChest, FastChestChanceTarget, details);
                changed += SetFastChestAccountStatusContribution(accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceNormalChestPercent, FastChestChancePercentTarget, details);
                changed += SetFastChestAccountStatusContribution(accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.MaxAmountNormalChest, FastChestMaxNormalChests, details);
                changed += SetFastChestAccountStatusContribution(accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceStageBossChest, FastChestChanceTarget, details);
                changed += SetFastChestAccountStatusContribution(accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceStageBossChestPercent, FastChestChancePercentTarget, details);
                changed += SetFastChestAccountStatusContribution(accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.MaxAmountStageBossChest, FastChestMaxNormalChests, details);

                _fastChestStatusContributionApplied = true;
                Interlocked.Exchange(ref _nextFastChestStatusSyncUtcTicks, DateTime.UtcNow.AddSeconds(10).Ticks);
                int updates = Interlocked.Increment(ref _fastChestStatusMainThreadUpdates);
                if (changed > 0 || updates <= 3 || updates % 12 == 0)
                {
                    Plugin.FileLog($"Fast chest status: aplicado en hilo Unity, cambios={changed}; {details}");
                }

                return;
            }

            var clearDetails = new StringBuilder();
            SetFastChestAccountStatusContribution(accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceNormalChest, 0, clearDetails);
            SetFastChestAccountStatusContribution(accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceNormalChestPercent, 0, clearDetails);
            SetFastChestAccountStatusContribution(accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.MaxAmountNormalChest, 0, clearDetails);
            SetFastChestAccountStatusContribution(accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceStageBossChest, 0, clearDetails);
            SetFastChestAccountStatusContribution(accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceStageBossChestPercent, 0, clearDetails);
            SetFastChestAccountStatusContribution(accountStatusManager, global::TaskbarHero.StatusSystem.EAccountStatus.MaxAmountStageBossChest, 0, clearDetails);
            _fastChestStatusContributionApplied = false;
            Interlocked.Exchange(ref _nextFastChestStatusSyncUtcTicks, 0);
            Plugin.FileLog("Fast chest status: contribuciones retiradas en hilo Unity; " + clearDetails);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _nextFastChestStatusSyncUtcTicks, DateTime.UtcNow.AddSeconds(2).Ticks);
            Plugin.FileLog("Fast chest status: sync fallo en hilo Unity: " + ex.Message);
        }
    }

    private static int SetFastChestAccountStatusContribution(
        global::yw accountStatusManager,
        global::TaskbarHero.StatusSystem.EAccountStatus status,
        int value,
        StringBuilder details)
    {
        int before = ReadAccountStatus(accountStatusManager, status);
        SetAccountStatusContribution(accountStatusManager, status, value);
        int after = ReadAccountStatus(accountStatusManager, status);
        if (details != null)
        {
            details.Append(status)
                .Append(' ')
                .Append(before)
                .Append("->")
                .Append(after)
                .Append("; ");
        }

        return before == after ? 0 : 1;
    }

    internal static void PrepareFastChestDropBeforeStageDrop(
        global::TaskbarHero.StageManager stageManager,
        int monsterKey,
        global::TaskbarHero.Data.EStageType stageType,
        global::TaskbarHero.Data.EMonsterType monsterType,
        ref int stageBoxItemKey,
        ref float dropChanceInput)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting || !FastChestDropsEnabled || !IsValid(stageManager))
        {
            return;
        }

        if (monsterType != global::TaskbarHero.Data.EMonsterType.MONSTER &&
            monsterType != global::TaskbarHero.Data.EMonsterType.BOSS)
        {
            return;
        }

        var boxType = monsterType == global::TaskbarHero.Data.EMonsterType.BOSS
            ? global::TaskbarHero.EBoxType.BOSS
            : global::TaskbarHero.EBoxType.NORMAL;

        if (stageBoxItemKey <= 0)
        {
            int skips = Interlocked.Increment(ref _fastChestNoStageBoxKeySkips);
            if (skips <= 5 || skips % 50 == 0)
            {
                Plugin.FileLog($"Fast chest drops: skip sin stageBoxItemKey real stage={stageType}, monsterType={monsterType}, monster={monsterKey}.");
            }

            return;
        }

        int runtimeCount = GetRuntimeBoxCount(boxType);
        if (runtimeCount >= FastChestMaxNormalChests)
        {
            return;
        }

        int eligible = Interlocked.Increment(ref _fastChestEligibleStageBoxAttempts);
        if (eligible <= 5 || eligible % 25 == 0)
        {
            Plugin.FileLog($"Fast chest drops: elegible {boxType} itemKey={stageBoxItemKey}, stage={stageType}, monsterType={monsterType}, monster={monsterKey}, runtime={runtimeCount}.");
        }

        if (dropChanceInput < FastChestDropChanceInput)
        {
            float before = dropChanceInput;
            dropChanceInput = FastChestDropChanceInput;
            int updates = Interlocked.Increment(ref _fastChestChanceInputUpdates);
            if (updates <= 5 || updates % 25 == 0)
            {
                Plugin.FileLog($"Fast chest drops: StageManager.ihu drop chance input {before.ToString("0.###", CultureInfo.InvariantCulture)}->{dropChanceInput.ToString("0.###", CultureInfo.InvariantCulture)}.");
            }
        }

        try
        {
            float now = GetStageBoxCooldownClock();
            lock (FastChestCooldownSync)
            {
                if (_nextFastChestCooldownUnscaled > 0f && now + 0.05f < _nextFastChestCooldownUnscaled)
                {
                    return;
                }

                float expiredAt = now - FastChestCooldownExpireSlackSeconds;
                if (!TrySetStageBoxCooldownValue(stageManager, boxType, expiredAt, out string detail))
                {
                    Plugin.FileLog($"Fast chest cooldown: no pude expirar bdlr {boxType} antes de StageManager.ihu: " + detail);
                    _nextFastChestCooldownUnscaled = now + 2f;
                    return;
                }

                _nextFastChestCooldownUnscaled = now + 1f;
                int expirations = Interlocked.Increment(ref _fastChestCooldownExpirations);
                if (expirations <= 5 || expirations % 25 == 0)
                {
                    Plugin.FileLog($"Fast chest cooldown: bdlr {boxType} vencido antes de ihu ({detail}); runtime {boxType}={runtimeCount}.");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Fast chest cooldown: PrepareFastChestDropBeforeStageDrop fallo: " + ex.Message);
        }
    }

    internal static void OnStageBoxCooldownSet(global::TaskbarHero.StageManager stageManager, global::TaskbarHero.EBoxType boxType, int itemKey)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting || !FastChestDropsEnabled || !IsFastChestBoxType(boxType) || !IsValid(stageManager))
        {
            return;
        }

        try
        {
            float now = GetStageBoxCooldownClock();
            float next = now + FastChestDropIntervalSeconds;
            lock (FastChestCooldownSync)
            {
                if (!TrySetStageBoxCooldownValue(stageManager, boxType, next, out string detail))
                {
                    Plugin.FileLog($"Fast chest cooldown: no pude fijar bdlr {boxType} despues de StageManager.ihw: " + detail);
                    return;
                }

                _nextFastChestCooldownUnscaled = next;
                int updates = Interlocked.Increment(ref _fastChestCooldownUpdates);
                if (updates <= 10 || updates % 25 == 0)
                {
                    Plugin.FileLog($"Fast chest cooldown: StageManager.ihw {boxType} itemKey={itemKey}, siguiente intento en {FastChestDropIntervalSeconds}s ({detail}).");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Fast chest cooldown: OnStageBoxCooldownSet fallo: " + ex.Message);
        }
    }

    private static bool IsFastChestBoxType(global::TaskbarHero.EBoxType boxType)
    {
        return boxType == global::TaskbarHero.EBoxType.NORMAL ||
               boxType == global::TaskbarHero.EBoxType.BOSS;
    }

    private static bool TrySetStageBoxCooldownValue(
        global::TaskbarHero.StageManager stageManager,
        global::TaskbarHero.EBoxType boxType,
        float value,
        out string detail)
    {
        detail = string.Empty;
        try
        {
            if (!TryGetStageBoxCooldownDictionary(stageManager, out object raw, out string source, out detail))
            {
                return false;
            }

            var cooldowns = raw as Il2CppSystem.Collections.Generic.Dictionary<global::TaskbarHero.EBoxType, float>;
            if (cooldowns != null)
            {
                bool hadPrevious = cooldowns.TryGetValue(boxType, out float previous);
                cooldowns[boxType] = value;
                string before = hadPrevious ? previous.ToString("0.00", CultureInfo.InvariantCulture) : "sin valor";
                detail = source + " " + before + "->" + value.ToString("0.00", CultureInfo.InvariantCulture);
                return true;
            }

            if (raw == null)
            {
                detail = source + " null";
                return false;
            }

            return TrySetDictionaryValueByReflection(raw, boxType, value, source, out detail);
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    private static bool TryReadStageBoxCooldownValue(
        global::TaskbarHero.StageManager stageManager,
        global::TaskbarHero.EBoxType boxType,
        out float value,
        out string detail)
    {
        value = 0f;
        detail = string.Empty;
        try
        {
            if (!TryGetStageBoxCooldownDictionary(stageManager, out object raw, out string source, out detail))
            {
                return false;
            }

            var cooldowns = raw as Il2CppSystem.Collections.Generic.Dictionary<global::TaskbarHero.EBoxType, float>;
            if (cooldowns != null)
            {
                if (cooldowns.TryGetValue(boxType, out value))
                {
                    return true;
                }

                detail = source + " sin clave " + boxType;
                return false;
            }

            if (raw == null)
            {
                detail = source + " null";
                return false;
            }

            return TryReadDictionaryValueByReflection(raw, boxType, out value, out detail);
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    private static bool TryGetStageBoxCooldownDictionary(
        global::TaskbarHero.StageManager stageManager,
        out object dictionary,
        out string source,
        out string detail)
    {
        dictionary = null;
        source = "bdlr";
        detail = string.Empty;
        try
        {
            var getter = StageManagerBoxCooldownsGetter.Value;
            if (getter != null)
            {
                dictionary = getter.Invoke(stageManager, Array.Empty<object>());
                source = getter.Name;
                return true;
            }

            var field = StageManagerBoxCooldownsField.Value;
            if (field != null)
            {
                dictionary = field.GetValue(stageManager);
                source = field.Name;
                return true;
            }

            detail = "getter/campo bdlr no encontrado";
            return false;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    private static float GetStageBoxCooldownClock()
    {
        return global::UnityEngine.Time.unscaledTime;
    }

    private static bool TrySetDictionaryValueByReflection(object dictionary, global::TaskbarHero.EBoxType boxType, float value, string fieldName, out string detail)
    {
        detail = string.Empty;
        try
        {
            var type = dictionary.GetType();
            float previous = 0f;
            bool hadPrevious = TryReadDictionaryValueByReflection(dictionary, boxType, out previous, out _);
            var setItem = type.GetMethod("set_Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (setItem == null)
            {
                detail = fieldName + " tipo " + type.FullName + " sin set_Item";
                return false;
            }

            setItem.Invoke(dictionary, new object[] { boxType, value });
            string before = hadPrevious ? previous.ToString("0.00", CultureInfo.InvariantCulture) : "sin valor";
            detail = fieldName + " " + before + "->" + value.ToString("0.00", CultureInfo.InvariantCulture) + " via reflection";
            return true;
        }
        catch (Exception ex)
        {
            detail = fieldName + " reflection " + ex.Message;
            return false;
        }
    }

    private static bool TryReadDictionaryValueByReflection(object dictionary, global::TaskbarHero.EBoxType boxType, out float value, out string detail)
    {
        value = 0f;
        detail = string.Empty;
        try
        {
            var type = dictionary.GetType();
            MethodInfo tryGetValue = null;
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!string.Equals(method.Name, "TryGetValue", StringComparison.Ordinal))
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length == 2)
                {
                    tryGetValue = method;
                    break;
                }
            }

            if (tryGetValue != null)
            {
                object[] args = { boxType, 0f };
                bool ok = tryGetValue.Invoke(dictionary, args) is bool result && result;
                if (ok && args[1] != null)
                {
                    value = Convert.ToSingle(args[1], CultureInfo.InvariantCulture);
                }

                return ok;
            }

            var getItem = type.GetMethod("get_Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (getItem != null)
            {
                object raw = getItem.Invoke(dictionary, new object[] { boxType });
                value = Convert.ToSingle(raw, CultureInfo.InvariantCulture);
                return true;
            }

            detail = "tipo " + type.FullName + " sin TryGetValue/get_Item";
            return false;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    internal static void ApplyFastChestDropCheck(global::TaskbarHero.EBoxType boxType, ref bool result)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting || !FastChestDropsEnabled || !IsFastChestBoxType(boxType))
        {
            return;
        }

        if (result)
        {
            return;
        }

        if (!TryClaimFastChestWindow())
        {
            return;
        }

        int runtimeCount = GetRuntimeBoxCount(boxType);
        if (runtimeCount >= FastChestMaxNormalChests)
        {
            Plugin.FileLog($"Fast chest drops: saltado, ya hay {runtimeCount} cofres {boxType} en runtime.");
            return;
        }

        result = true;
        Interlocked.Increment(ref _fastChestDropForces);
        Plugin.FileLog($"Fast chest drops: StageManager.ihv permitio drop real {boxType}; runtime antes={runtimeCount}, siguiente ventana {FastChestDropIntervalSeconds}s.");
    }

    internal static void TryForceFastChestDropFromStage(global::TaskbarHero.StageManager stageManager, object[] args)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting || !FastChestDropsEnabled || !IsValid(stageManager) || args == null || args.Length < 5)
        {
            return;
        }

        int monsterType = ReadIntArg(args, 4, 0);
        if (monsterType != 0)
        {
            return;
        }

        if (!TryClaimFastChestWindow())
        {
            return;
        }

        var boxType = global::TaskbarHero.EBoxType.NORMAL;
        int runtimeNormal = GetRuntimeBoxCount(global::TaskbarHero.EBoxType.NORMAL);
        if (runtimeNormal >= FastChestMaxNormalChests)
        {
            Plugin.FileLog($"Fast chest drops: saltado, ya hay {runtimeNormal} cofres NORMAL en runtime.");
            return;
        }

        int stageBoxItemKey = ReadIntArg(args, 2, 0);
        if (stageBoxItemKey <= 0)
        {
            stageBoxItemKey = FindStageBoxItemKey(boxType);
        }

        if (stageBoxItemKey <= 0)
        {
            Plugin.FileLog("Fast chest drops: no encontre itemKey de cofre NORMAL en StageManager.ihu.");
            return;
        }

        int monsterKey = ReadIntArg(args, 0, 0);
        bool callbackSucceeded = false;
        bool callbackSeen = false;

        try
        {
            global::uz.uc.izd(stageBoxItemKey, new Action<int>(callbackValue =>
            {
                callbackSeen = true;
                if (Plugin.IsShuttingDown || IsGameQuitting)
                {
                    return;
                }

                if (callbackValue <= 0)
                {
                    Plugin.FileLog($"Fast chest drops: uz.uc.izd no acepto cofre NORMAL itemKey={stageBoxItemKey}, callback={callbackValue}.");
                    return;
                }

                int cooldownUpdates = TrySetStageBoxCooldown(stageManager, boxType, stageBoxItemKey);
                int eventUpdates = TryNotifyStageBoxCreated(stageManager, boxType, callbackValue);
                int refreshes = RefreshRuntimeBoxes();
                int afterRuntime = GetRuntimeBoxCount(boxType);
                callbackSucceeded = true;
                Interlocked.Increment(ref _fastChestDropForces);
                Plugin.FileLog($"Fast chest drops: cofre NORMAL solicitado por ruta real itemKey={stageBoxItemKey}, callback={callbackValue}, monster={monsterKey}, runtime {runtimeNormal}->{afterRuntime}, cooldown {cooldownUpdates}, eventos {eventUpdates}, UI {refreshes}. No se modifica claimableAt ni reward local.");
            }));

            if (!callbackSeen)
            {
                int afterRuntime = GetRuntimeBoxCount(boxType);
                if (afterRuntime > runtimeNormal)
                {
                    callbackSucceeded = true;
                    RefreshRuntimeBoxes();
                    Interlocked.Increment(ref _fastChestDropForces);
                    Plugin.FileLog($"Fast chest drops: cofre NORMAL real creado sin callback itemKey={stageBoxItemKey}, monster={monsterKey}, runtime {runtimeNormal}->{afterRuntime}.");
                }
            }

            if (!callbackSucceeded && !callbackSeen)
            {
                Plugin.FileLog($"Fast chest drops: uz.uc.izd no devolvio callback para itemKey={stageBoxItemKey}, monster={monsterKey}.");
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _nextFastChestDropUtcTicks, DateTime.UtcNow.AddSeconds(2).Ticks);
            Plugin.FileLog("Fast chest drops: fallo al solicitar drop real en StageManager.ihu: " + ex.Message);
        }
    }

    private static int MakeActiveNormalChestsClaimable()
    {
        try
        {
            var manager = GetStageBoxManager();
            if (!IsValid(manager))
            {
                return 0;
            }

            string pastClaimableAt = FormatPastClaimableAt();
            int changed = 0;
            changed += MakeStageBoxesClaimableInStore("bspy", manager.bspy, global::TaskbarHero.EBoxType.NORMAL, pastClaimableAt);
            changed += MakeStageBoxesClaimableInStore("bspz", manager.bspz, global::TaskbarHero.EBoxType.NORMAL, pastClaimableAt);
            if (changed > 0)
            {
                int refreshes = RefreshRuntimeBoxes();
                Plugin.FileLog($"Fast chest drops: timers activos NORMAL vencidos={changed}, UI={refreshes}.");
            }

            return changed;
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Fast chest drops: MakeActiveNormalChestsClaimable fallo: " + ex.Message);
            return 0;
        }
    }

    private static int MakeStageBoxesClaimableInStore(
        string storeName,
        Il2CppSystem.Collections.Generic.IReadOnlyDictionary<global::TaskbarHero.EBoxType, Il2CppSystem.Collections.Generic.List<global::TaskbarHero.BoxData>> store,
        global::TaskbarHero.EBoxType boxType,
        string pastClaimableAt)
    {
        if (store == null || !store.TryGetValue(boxType, out var boxes) || boxes == null)
        {
            return 0;
        }

        int changed = 0;
        for (int i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            if (!IsValid(box) || !HasValidBoxReward(box))
            {
                continue;
            }

            int originalItemId = ReadBoxItemId(box);
            string originalUniqueKey = ReadBoxUniqueKey(box);
            int originalRewardItemId = ReadBoxRewardItemId(box);
            string originalRewardUniqueKey = ReadBoxRewardUniqueKey(box);
            bool originalIsGet = ReadBoxIsGet(box);

            box.itemId = originalItemId;
            box.itemKey = originalUniqueKey;
            box.rewardItemId = originalRewardItemId;
            box.rewardItemKey = originalRewardUniqueKey;
            box.claimableAt = pastClaimableAt;
            box.isGet = originalIsGet;
            box.CopyToObscured();
            changed++;

            if (changed <= 3)
            {
                Plugin.FileLog($"Fast chest drops: active claimable {storeName} -> [{DescribeBoxData(box)}].");
            }
        }

        return changed;
    }

    private static int TryMakeStageBoxClaimable(
        global::TaskbarHero.EBoxType boxType,
        int stageBoxItemKey,
        out global::TaskbarHero.BoxData preparedBox,
        out string summary)
    {
        preparedBox = null;
        summary = string.Empty;

        try
        {
            var manager = GetStageBoxManager();
            if (!IsValid(manager))
            {
                summary = "vy manager no disponible.";
                return 0;
            }

            string pastClaimableAt = FormatPastClaimableAt();
            var details = new StringBuilder();
            int changed = TryMakeStageBoxClaimableInStore("bspx", manager.bspx, boxType, stageBoxItemKey, pastClaimableAt, details, out preparedBox);
            if (changed <= 0)
            {
                changed = TryRecycleStageBoxToSource(manager.bspx, "bspy", manager.bspy, boxType, stageBoxItemKey, pastClaimableAt, details, out preparedBox);
            }

            if (changed <= 0)
            {
                changed = TryRecycleStageBoxToSource(manager.bspx, "bspz", manager.bspz, boxType, stageBoxItemKey, pastClaimableAt, details, out preparedBox);
            }

            if (changed > 0 && IsValid(preparedBox))
            {
                summary = $"timers vencidos={changed}, box={DescribeBoxData(preparedBox)}";
            }
            else
            {
                summary = details.Length > 0 ? details.ToString() : "sin datos en bspx/bspy/bspz.";
            }

            return changed;
        }
        catch (Exception ex)
        {
            preparedBox = null;
            summary = "excepcion venciendo timer local: " + ex.Message;
            return 0;
        }
    }

    private static int TryMakeStageBoxClaimableInStore(
        string storeName,
        Il2CppSystem.Collections.Generic.IReadOnlyDictionary<global::TaskbarHero.EBoxType, Il2CppSystem.Collections.Generic.List<global::TaskbarHero.BoxData>> store,
        global::TaskbarHero.EBoxType boxType,
        int stageBoxItemKey,
        string pastClaimableAt,
        StringBuilder details,
        out global::TaskbarHero.BoxData preparedBox)
    {
        preparedBox = null;
        try
        {
            if (store == null || !store.TryGetValue(boxType, out var boxes) || boxes == null)
            {
                AppendFastChestStoreSummary(details, $"{storeName}=sin lista {boxType}");
                return 0;
            }

            string stageBoxKey = stageBoxItemKey.ToString(CultureInfo.InvariantCulture);
            int valid = 0;
            int rewardless = 0;
            int shown = 0;
            var samples = new StringBuilder();

            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (!IsValid(box))
                {
                    continue;
                }

                valid++;
                int itemId = ReadBoxItemId(box);
                string uniqueKey = ReadBoxUniqueKey(box);
                string publicKey = box.itemKey ?? string.Empty;
                bool isMatch = itemId == stageBoxItemKey
                               || string.Equals(uniqueKey, stageBoxKey, StringComparison.Ordinal)
                               || string.Equals(publicKey, stageBoxKey, StringComparison.Ordinal);

                if (!isMatch)
                {
                    if (shown < 3)
                    {
                        if (samples.Length > 0)
                        {
                            samples.Append("; ");
                        }

                        samples.Append(itemId)
                            .Append(':')
                            .Append(string.IsNullOrWhiteSpace(uniqueKey) ? publicKey : uniqueKey)
                            .Append(":get=")
                            .Append(ReadBoxIsGet(box) ? "1" : "0")
                            .Append(":at=")
                            .Append(ReadBoxClaimableAt(box));
                        shown++;
                    }

                    continue;
                }

                if (!HasReadableBoxUniqueId(box))
                {
                    AppendFastChestStoreSummary(details, $"{storeName}=match sin unique legible [{DescribeBoxData(box)}]");
                    continue;
                }

                if (!HasValidBoxReward(box))
                {
                    rewardless++;
                    if (shown < 3)
                    {
                        if (samples.Length > 0)
                        {
                            samples.Append("; ");
                        }

                        samples.Append("sinReward:")
                            .Append(DescribeBoxData(box));
                        shown++;
                    }

                    continue;
                }

                int originalItemId = ReadBoxItemId(box);
                string originalUniqueKey = ReadBoxUniqueKey(box);
                int originalRewardItemId = ReadBoxRewardItemId(box);
                string originalRewardUniqueKey = ReadBoxRewardUniqueKey(box);
                bool originalIsGet = ReadBoxIsGet(box);

                box.itemId = originalItemId;
                box.itemKey = originalUniqueKey;
                box.rewardItemId = originalRewardItemId;
                box.rewardItemKey = originalRewardUniqueKey;
                box.claimableAt = pastClaimableAt;
                box.isGet = originalIsGet;
                box.CopyToObscured();
                preparedBox = box;
                Plugin.FileLog($"Fast chest drops: claimable {storeName} originalIsGet={(originalIsGet ? "1" : "0")} -> [{DescribeBoxData(box)}].");
                return 1;
            }

            AppendFastChestStoreSummary(details, $"{storeName}={valid} boxes, sinReward={rewardless}, ejemplos [{samples}]");
            return 0;
        }
        catch (Exception ex)
        {
            AppendFastChestStoreSummary(details, $"{storeName}=error {ex.Message}");
            return 0;
        }
    }

    private static int TryRecycleStageBoxToSource(
        Il2CppSystem.Collections.Generic.IReadOnlyDictionary<global::TaskbarHero.EBoxType, Il2CppSystem.Collections.Generic.List<global::TaskbarHero.BoxData>> sourceStore,
        string activeStoreName,
        Il2CppSystem.Collections.Generic.IReadOnlyDictionary<global::TaskbarHero.EBoxType, Il2CppSystem.Collections.Generic.List<global::TaskbarHero.BoxData>> activeStore,
        global::TaskbarHero.EBoxType boxType,
        int stageBoxItemKey,
        string pastClaimableAt,
        StringBuilder details,
        out global::TaskbarHero.BoxData preparedBox)
    {
        preparedBox = null;
        try
        {
            if (sourceStore == null || !sourceStore.TryGetValue(boxType, out var sourceBoxes) || sourceBoxes == null)
            {
                AppendFastChestStoreSummary(details, $"bspx=sin lista fuente {boxType}");
                return 0;
            }

            if (activeStore == null || !activeStore.TryGetValue(boxType, out var activeBoxes) || activeBoxes == null)
            {
                AppendFastChestStoreSummary(details, $"{activeStoreName}=sin lista {boxType}");
                return 0;
            }

            string stageBoxKey = stageBoxItemKey.ToString(CultureInfo.InvariantCulture);
            int scanned = 0;
            for (int i = activeBoxes.Count - 1; i >= 0; i--)
            {
                var box = activeBoxes[i];
                if (!IsValid(box))
                {
                    continue;
                }

                scanned++;
                int itemId = ReadBoxItemId(box);
                string uniqueKey = ReadBoxUniqueKey(box);
                string publicKey = box.itemKey ?? string.Empty;
                bool isMatch = itemId == stageBoxItemKey
                               || string.Equals(uniqueKey, stageBoxKey, StringComparison.Ordinal)
                               || string.Equals(publicKey, stageBoxKey, StringComparison.Ordinal);

                if (!isMatch || !TryReadBoxUniqueId(box, out ulong uniqueId) || !HasValidBoxReward(box))
                {
                    continue;
                }

                int originalItemId = ReadBoxItemId(box);
                string originalUniqueKey = ReadBoxUniqueKey(box);
                int originalRewardItemId = ReadBoxRewardItemId(box);
                string originalRewardUniqueKey = ReadBoxRewardUniqueKey(box);
                bool originalIsGet = ReadBoxIsGet(box);

                box.itemId = originalItemId;
                box.itemKey = originalUniqueKey;
                box.rewardItemId = originalRewardItemId;
                box.rewardItemKey = originalRewardUniqueKey;
                box.claimableAt = pastClaimableAt;
                box.isGet = originalIsGet;
                box.CopyToObscured();

                activeBoxes.RemoveAt(i);
                if (!StageBoxListContainsUniqueId(sourceBoxes, uniqueId))
                {
                    sourceBoxes.Add(box);
                }

                RemoveRuntimeBoxEntries(boxType, new List<ulong> { uniqueId });
                try
                {
                    var save = GetSaveData();
                    if (IsValid(save))
                    {
                        var ids = new List<ulong> { uniqueId };
                        RemoveSaveBoxEntries(save, ids);
                        RemoveItemSaves(save, new HashSet<ulong>(ids));
                    }
                }
                catch (Exception saveEx)
                {
                    Plugin.FileLog($"Fast chest drops: reciclado save cleanup fallo uid={uniqueId}: {saveEx.Message}");
                }

                preparedBox = box;
                Plugin.FileLog($"Fast chest drops: reciclado {activeStoreName}->bspx uid={uniqueId}, originalIsGet={(originalIsGet ? "1" : "0")} -> [{DescribeBoxData(box)}].");
                return 1;
            }

            AppendFastChestStoreSummary(details, $"{activeStoreName}={scanned} activos sin reciclable");
            return 0;
        }
        catch (Exception ex)
        {
            AppendFastChestStoreSummary(details, $"{activeStoreName}=reciclado error {ex.Message}");
            return 0;
        }
    }

    internal static void TraceStageBoxOpen(global::TaskbarHero.EBoxType boxType, global::uz.ty.WillRemoveBoxData willRemoveBoxData, global::uz.uc.ua itemCache)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting || boxType != global::TaskbarHero.EBoxType.NORMAL)
        {
            return;
        }

        try
        {
            ulong uniqueId = willRemoveBoxData.BoxUniqueId;
            var box = TryFindStageBoxByUniqueId(uniqueId);
            Plugin.FileLog($"Fast chest open: type={boxType}, uid={uniqueId}, runtimeQty={GetRuntimeBoxQuantity(uniqueId)}, box=[{DescribeBoxData(box)}], item=[{DescribeRuntimeItem(itemCache)}].");
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Fast chest open trace failed: " + ex.Message);
        }
    }

    internal static void TraceSteamChestConsume(ulong uniqueId, int itemId, long quantity, bool result)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting || uniqueId == 0UL)
        {
            return;
        }

        try
        {
            var box = TryFindStageBoxByUniqueId(uniqueId);
            if (!IsValid(box))
            {
                return;
            }

            Plugin.FileLog($"Fast chest consume: uid={uniqueId}, itemId={itemId}, qty={quantity}, steamConsumeStarted={result}, box=[{DescribeBoxData(box)}].");
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Fast chest consume trace failed: " + ex.Message);
        }
    }

    internal static void TraceStageBoxClaimLookup(int itemKey, global::TaskbarHero.BoxData result)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting)
        {
            return;
        }

        try
        {
            if (itemKey == FindStageBoxItemKey(global::TaskbarHero.EBoxType.NORMAL))
            {
                Plugin.FileLog($"Fast chest claim lookup: itemKey={itemKey}, box=[{DescribeBoxData(result)}].");
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Fast chest claim lookup trace failed: " + ex.Message);
        }
    }

    internal static void TraceStageBoxAddItem(int itemKey, ulong uniqueId, int quantity, bool silent, global::TaskbarHero.AddItemResult result)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting)
        {
            return;
        }

        try
        {
            bool isStageBoxItem = itemKey == FindStageBoxItemKey(global::TaskbarHero.EBoxType.NORMAL);
            var rewardBox = isStageBoxItem ? null : TryFindStageBoxByRewardUniqueId(uniqueId);
            if (isStageBoxItem || IsValid(rewardBox))
            {
                var box = isStageBoxItem ? TryFindStageBoxByUniqueId(uniqueId) : rewardBox;
                string kind = isStageBoxItem ? "stagebox" : "reward";
                Plugin.FileLog($"Fast chest add item {kind}: itemKey={itemKey}, uid={uniqueId}, qty={quantity}, silent={silent}, resultType={result.ItemType}, addResult={result.AddResult}, runtimeQty={GetRuntimeBoxQuantity(uniqueId)}, box=[{DescribeBoxData(box)}].");
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Fast chest add item trace failed: " + ex.Message);
        }
    }

    internal static void TraceStageBoxExchangeResult(
        global::TaskbarHero.EBoxType boxType,
        global::uz.ty.WillRemoveBoxData willRemoveBoxData,
        global::TaskbarHero.InventoryExchangeResult result)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting || boxType != global::TaskbarHero.EBoxType.NORMAL)
        {
            return;
        }

        try
        {
            ulong uniqueId = willRemoveBoxData.BoxUniqueId;
            var box = TryFindStageBoxByUniqueId(uniqueId);
            Plugin.FileLog($"Fast chest exchange result: type={boxType}, uid={uniqueId}, success={SafeExchangeSuccess(result)}, error={SafeExchangeError(result)}, box=[{DescribeBoxData(box)}], added=[{DescribeExchangeAdded(result)}], removed=[{DescribeExchangeRemoved(result)}], invalid=[{DescribeExchangeInvalid(result)}].");
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Fast chest exchange result trace failed: " + ex.Message);
        }
    }

    internal static void TraceInventoryExchangeResult(global::TaskbarHero.InventoryExchangeResult result)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting)
        {
            return;
        }

        try
        {
            Plugin.FileLog($"Fast chest inventory exchange callback: success={SafeExchangeSuccess(result)}, error={SafeExchangeError(result)}, added=[{DescribeExchangeAdded(result)}], removed=[{DescribeExchangeRemoved(result)}], invalid=[{DescribeExchangeInvalid(result)}].");
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Fast chest inventory exchange callback trace failed: " + ex.Message);
        }
    }

    internal static void TraceFastChestRequest(int itemKey)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting || !FastChestDropsEnabled)
        {
            return;
        }

        try
        {
            int observations = Interlocked.Increment(ref _fastChestRequestObservations);
            int normalStageBoxItemKey = FindStageBoxItemKey(global::TaskbarHero.EBoxType.NORMAL);
            bool isNormalStageBox = normalStageBoxItemKey <= 0 || itemKey == normalStageBoxItemKey;
            if (isNormalStageBox && (observations <= 10 || observations % 25 == 0))
            {
                Plugin.FileLog($"Fast chest request: uz.uc.izd original itemKey={itemKey}, runtime NORMAL={GetRuntimeBoxCount(global::TaskbarHero.EBoxType.NORMAL)}, BOSS={GetRuntimeBoxCount(global::TaskbarHero.EBoxType.BOSS)}.");
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Fast chest request trace failed: " + ex.Message);
        }
    }

    private static void AppendFastChestStoreSummary(StringBuilder details, string text)
    {
        if (details == null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (details.Length > 0)
        {
            details.Append(" | ");
        }

        details.Append(text);
    }

    private static string FormatPastClaimableAt()
    {
        DateTime past = DateTime.UtcNow.AddMinutes(-5);
        return past.ToString("o", CultureInfo.InvariantCulture);
    }

    private static int ReadBoxItemId(global::TaskbarHero.BoxData box)
    {
        if (!IsValid(box))
        {
            return 0;
        }

        try
        {
            return box.ItemId;
        }
        catch
        {
            return box.itemId;
        }
    }

    private static string ReadBoxUniqueKey(global::TaskbarHero.BoxData box)
    {
        if (!IsValid(box))
        {
            return string.Empty;
        }

        try
        {
            return box.UniqueKey ?? string.Empty;
        }
        catch
        {
            return box.itemKey ?? string.Empty;
        }
    }

    private static string ReadBoxClaimableAt(global::TaskbarHero.BoxData box)
    {
        if (!IsValid(box))
        {
            return string.Empty;
        }

        try
        {
            return box.ClaimableAt ?? string.Empty;
        }
        catch
        {
            return box.claimableAt ?? string.Empty;
        }
    }

    private static int ReadBoxRewardItemId(global::TaskbarHero.BoxData box)
    {
        if (!IsValid(box))
        {
            return 0;
        }

        try
        {
            return box.RewardItemId;
        }
        catch
        {
            return box.rewardItemId;
        }
    }

    private static string ReadBoxRewardUniqueKey(global::TaskbarHero.BoxData box)
    {
        if (!IsValid(box))
        {
            return string.Empty;
        }

        try
        {
            return box.RewardItemUniqueID ?? string.Empty;
        }
        catch
        {
            return box.rewardItemKey ?? string.Empty;
        }
    }

    private static bool HasReadableBoxUniqueId(global::TaskbarHero.BoxData box)
    {
        string uniqueKey = ReadBoxUniqueKey(box);
        return !string.IsNullOrWhiteSpace(uniqueKey) &&
               ulong.TryParse(uniqueKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong uniqueId) &&
               uniqueId != 0UL;
    }

    private static bool TryReadBoxUniqueId(global::TaskbarHero.BoxData box, out ulong uniqueId)
    {
        uniqueId = 0UL;
        string uniqueKey = ReadBoxUniqueKey(box);
        return !string.IsNullOrWhiteSpace(uniqueKey) &&
               ulong.TryParse(uniqueKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out uniqueId) &&
               uniqueId != 0UL;
    }

    private static bool StageBoxListContainsUniqueId(Il2CppSystem.Collections.Generic.List<global::TaskbarHero.BoxData> boxes, ulong uniqueId)
    {
        if (boxes == null || uniqueId == 0UL)
        {
            return false;
        }

        for (int i = 0; i < boxes.Count; i++)
        {
            if (TryReadBoxUniqueId(boxes[i], out ulong existing) && existing == uniqueId)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasValidBoxReward(global::TaskbarHero.BoxData box)
    {
        if (!IsValid(box) || ReadBoxRewardItemId(box) <= 0)
        {
            return false;
        }

        string rewardUniqueKey = ReadBoxRewardUniqueKey(box);
        return !string.IsNullOrWhiteSpace(rewardUniqueKey) &&
               ulong.TryParse(rewardUniqueKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong rewardUniqueId) &&
               rewardUniqueId != 0UL;
    }

    private static bool HasOpenableStageBoxData(ulong uniqueId, out string reason)
    {
        var box = TryFindStageBoxByUniqueId(uniqueId);
        if (!IsValid(box))
        {
            reason = "BoxData no existe";
            return false;
        }

        if (!HasValidBoxReward(box))
        {
            reason = "BoxData sin recompensa: " + DescribeBoxData(box);
            return false;
        }

        reason = "ok: " + DescribeBoxData(box);
        return true;
    }

    private static int RemoveDeadStageBoxRuntime(global::TaskbarHero.EBoxType boxType, ulong uniqueId, string reason)
    {
        if (uniqueId == 0UL)
        {
            return 0;
        }

        try
        {
            var ids = new List<ulong> { uniqueId };
            RemoveRuntimeBoxEntries(boxType, ids);

            int removedSave = 0;
            int removedItemSaves = 0;
            try
            {
                var save = GetSaveManager()?.bggy;
                if (IsValid(save))
                {
                    RemoveSaveBoxEntries(save, ids);
                    removedSave = 1;
                    removedItemSaves = RemoveItemSaves(save, new HashSet<ulong>(ids));
                }
            }
            catch (Exception saveEx)
            {
                Plugin.FileLog($"Dead chest save cleanup failed uid={uniqueId}: {saveEx.Message}");
            }

            int refreshes = RefreshRuntimeBoxes();
            Plugin.FileLog($"Dead chest limpiado {boxType} uid={uniqueId}: {reason}, save={removedSave}, itemSaves={removedItemSaves}, UI={refreshes}.");
            return 1;
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"RemoveDeadStageBoxRuntime failed uid={uniqueId}: {ex.Message}");
            return 0;
        }
    }

    private static string DescribeBoxData(global::TaskbarHero.BoxData box)
    {
        if (!IsValid(box))
        {
            return "null";
        }

        return "itemId=" + ReadBoxItemId(box).ToString(CultureInfo.InvariantCulture) +
               ",uid=" + ReadBoxUniqueKey(box) +
               ",claimableAt=" + ReadBoxClaimableAt(box) +
               ",rewardItemId=" + ReadBoxRewardItemId(box).ToString(CultureInfo.InvariantCulture) +
               ",rewardUid=" + ReadBoxRewardUniqueKey(box) +
               ",isGet=" + (ReadBoxIsGet(box) ? "1" : "0");
    }

    private static string DescribeRuntimeItem(global::uz.uc.ua item)
    {
        if (!IsValid(item))
        {
            return "null";
        }

        try
        {
            return "itemKey=" + item.bsic.ToString(CultureInfo.InvariantCulture) +
                   ",uid=" + item.bsie.ToString(CultureInfo.InvariantCulture) +
                   ",type=" + item.bsht +
                   ",grade=" + item.bshu;
        }
        catch (Exception ex)
        {
            return "error " + ex.Message;
        }
    }

    private static string SafeExchangeSuccess(global::TaskbarHero.InventoryExchangeResult result)
    {
        if (!IsValid(result))
        {
            return "null";
        }

        try
        {
            return result.IsSuccess ? "1" : "0";
        }
        catch (Exception ex)
        {
            return "error:" + ex.Message;
        }
    }

    private static string SafeExchangeError(global::TaskbarHero.InventoryExchangeResult result)
    {
        if (!IsValid(result))
        {
            return "null";
        }

        try
        {
            return result.Error ?? string.Empty;
        }
        catch (Exception ex)
        {
            return "error:" + ex.Message;
        }
    }

    private static string DescribeExchangeAdded(global::TaskbarHero.InventoryExchangeResult result)
    {
        if (!IsValid(result) || result.added == null)
        {
            return "null";
        }

        try
        {
            var builder = new StringBuilder();
            int count = result.added.Length;
            int limit = Math.Min(count, 6);
            for (int i = 0; i < limit; i++)
            {
                var item = result.added[i];
                if (item == null)
                {
                    AppendShortSummary(builder, "null");
                    continue;
                }

                string uniqueKey;
                int itemId;
                try
                {
                    uniqueKey = item.UniqueKey;
                }
                catch
                {
                    uniqueKey = item.itemKey;
                }

                try
                {
                    itemId = item.ItemID;
                }
                catch
                {
                    itemId = item.itemId;
                }

                AppendShortSummary(builder, itemId.ToString(CultureInfo.InvariantCulture) + ":" + uniqueKey);
            }

            if (count > limit)
            {
                AppendShortSummary(builder, "+" + (count - limit).ToString(CultureInfo.InvariantCulture));
            }

            return builder.Length == 0 ? "empty" : builder.ToString();
        }
        catch (Exception ex)
        {
            return "error:" + ex.Message;
        }
    }

    private static string DescribeExchangeRemoved(global::TaskbarHero.InventoryExchangeResult result)
    {
        if (!IsValid(result) || result.removed == null)
        {
            return "null";
        }

        try
        {
            var builder = new StringBuilder();
            int count = result.removed.Length;
            int limit = Math.Min(count, 8);
            for (int i = 0; i < limit; i++)
            {
                AppendShortSummary(builder, result.removed[i] ?? "null");
            }

            if (count > limit)
            {
                AppendShortSummary(builder, "+" + (count - limit).ToString(CultureInfo.InvariantCulture));
            }

            return builder.Length == 0 ? "empty" : builder.ToString();
        }
        catch (Exception ex)
        {
            return "error:" + ex.Message;
        }
    }

    private static string DescribeExchangeInvalid(global::TaskbarHero.InventoryExchangeResult result)
    {
        if (!IsValid(result))
        {
            return "null";
        }

        try
        {
            var invalid = result.InvalidUniqueIds;
            if (invalid == null)
            {
                return "null";
            }

            var builder = new StringBuilder();
            int count = invalid.Length;
            int limit = Math.Min(count, 8);
            for (int i = 0; i < limit; i++)
            {
                AppendShortSummary(builder, invalid[i].ToString(CultureInfo.InvariantCulture));
            }

            if (count > limit)
            {
                AppendShortSummary(builder, "+" + (count - limit).ToString(CultureInfo.InvariantCulture));
            }

            return builder.Length == 0 ? "empty" : builder.ToString();
        }
        catch (Exception ex)
        {
            return "error:" + ex.Message;
        }
    }

    private static bool ReadBoxIsGet(global::TaskbarHero.BoxData box)
    {
        if (!IsValid(box))
        {
            return false;
        }

        try
        {
            return box.IsGet;
        }
        catch
        {
            return box.isGet;
        }
    }

    private static bool TryClaimFastChestWindow()
    {
        long now = DateTime.UtcNow.Ticks;
        long next = Interlocked.Read(ref _nextFastChestDropUtcTicks);
        if (now < next)
        {
            return false;
        }

        long newNext = now + TimeSpan.FromSeconds(FastChestDropIntervalSeconds).Ticks;
        long previous = Interlocked.CompareExchange(ref _nextFastChestDropUtcTicks, newNext, next);
        return previous == next || now >= previous;
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

    private static int TrySetStageBoxCooldown(global::TaskbarHero.StageManager stageManager, global::TaskbarHero.EBoxType boxType, int stageBoxItemKey)
    {
        try
        {
            var method = StageManagerSetBoxCooldownMethod.Value;
            if (method == null)
            {
                return 0;
            }

            method.Invoke(stageManager, new object[] { boxType, stageBoxItemKey });
            return 1;
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Fast chest drops: no pude actualizar cooldown de StageManager.ihw: " + ex.Message);
            return 0;
        }
    }

    private static int TryNotifyStageBoxCreated(global::TaskbarHero.StageManager stageManager, global::TaskbarHero.EBoxType boxType, int slot)
    {
        int updates = 0;
        try
        {
            stageManager.OnGetBox?.Invoke(slot);
            updates++;
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Fast chest drops: OnGetBox fallo: " + ex.Message);
        }

        try
        {
            global::on.gnk(boxType);
            updates++;
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Fast chest drops: on.gnk fallo: " + ex.Message);
        }

        return updates;
    }

    internal static void ApplyFastChestAccountStatus(global::TaskbarHero.StatusSystem.EAccountStatus status, ref int result)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting || !FastChestStatBoostEnabled)
        {
            return;
        }

        switch (status)
        {
            case global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceNormalChest:
                result = Math.Max(result, FastChestChanceTarget);
                break;
            case global::TaskbarHero.StatusSystem.EAccountStatus.DropChanceNormalChestPercent:
                result = Math.Max(result, FastChestChancePercentTarget);
                break;
            case global::TaskbarHero.StatusSystem.EAccountStatus.MaxAmountNormalChest:
                result = Math.Max(result, FastChestMaxNormalChests);
                break;
        }
    }

    internal static void ApplyFastChestChanceResult(ref float result, string source)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting || !FastChestDropsEnabled)
        {
            return;
        }

        int observations = Interlocked.Increment(ref _fastChestChanceResultObservations);
        if (result >= FastChestDropChanceInput)
        {
            if (observations <= 10 || observations % 50 == 0)
            {
                Plugin.FileLog($"Fast chest drops: {source} chance result observado {result.ToString("0.###", CultureInfo.InvariantCulture)}.");
            }

            return;
        }

        float before = result;
        result = FastChestDropChanceInput;
        int updates = Interlocked.Increment(ref _fastChestChanceInputUpdates);
        if (updates <= 5 || updates % 25 == 0)
        {
            Plugin.FileLog($"Fast chest drops: {source} chance result {before.ToString("0.###", CultureInfo.InvariantCulture)}->{result.ToString("0.###", CultureInfo.InvariantCulture)}.");
        }
    }

    internal static void ApplyFastChestMaxBoxAmount(global::TaskbarHero.EBoxType boxType, ref int result)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting || !FastChestDropsEnabled || !IsFastChestBoxType(boxType))
        {
            return;
        }

        result = Math.Max(result, FastChestMaxNormalChests);
    }

    internal static void ApplyFastChestCanAddBox(global::TaskbarHero.EBoxType boxType, ref bool result)
    {
        if (Plugin.IsShuttingDown || IsGameQuitting || !FastChestDropsEnabled || !IsFastChestBoxType(boxType))
        {
            return;
        }

        int runtimeCount = GetRuntimeBoxCount(boxType);
        if (runtimeCount < FastChestMaxNormalChests)
        {
            if (!result)
            {
                Plugin.FileLog($"Fast chest drops: uz.ty.iuf permitira {boxType}; runtime={runtimeCount}, limite={FastChestMaxNormalChests}.");
            }

            result = true;
        }
    }

    private static string AddLocalChestsLocked(
        global::TaskbarHero.PlayerSaveData save,
        global::TaskbarHero.EBoxType boxType,
        int amount,
        string source,
        out int runtimeChanged,
        out int uiRefreshes,
        out List<ulong> createdIds)
    {
        EnsureSerializedBoxData(save);
        var boxData = save.BoxData;
        ulong firstUniqueId = 0UL;
        ulong lastUniqueId = 0UL;
        int created = 0;
        runtimeChanged = 0;
        uiRefreshes = 0;
        createdIds = new List<ulong>();

        for (int i = 0; i < amount; i++)
        {
            ulong uniqueId = FindNextBoxUniqueId(save);
            while (createdIds.Contains(uniqueId))
            {
                uniqueId++;
            }

            if (firstUniqueId == 0UL)
            {
                firstUniqueId = uniqueId;
            }

            lastUniqueId = uniqueId;

            int beforeRuntime = GetRuntimeBoxCount(boxType);
            if (!TryAddRuntimeBoxByGameApi(boxType, uniqueId, out int stageBoxItemKey, out string runtimeDetail))
            {
                RemoveSaveBoxEntries(save, createdIds);
                RemoveRuntimeBoxEntries(boxType, createdIds);
                return $"No pude crear cofres {boxType}: {runtimeDetail}. Creados antes del fallo {created}.";
            }

            int afterRuntime = GetRuntimeBoxCount(boxType);
            if (afterRuntime <= beforeRuntime && GetRuntimeBoxQuantity(uniqueId) <= 0)
            {
                RemoveRuntimeBoxEntries(boxType, new List<ulong> { uniqueId });
                RemoveSaveBoxEntries(save, new List<ulong> { uniqueId });
                return $"No pude crear cofres {boxType}: el runtime no acepto itemKey {stageBoxItemKey}, uid {uniqueId} ({beforeRuntime}->{afterRuntime}).";
            }

            if (!SaveBoxUniqueIdExists(save, uniqueId))
            {
                boxData.BoxTypes.Add(boxType);
                boxData.BoxUniqueId.Add(uniqueId);
                boxData.BoxQuantity.Add(1);
            }

            createdIds.Add(uniqueId);
            runtimeChanged++;
            created++;
        }

        uiRefreshes = RefreshRuntimeBoxes();
        return $"Cofres {boxType} creados ({source}): +{created}, uid {firstUniqueId}->{lastUniqueId}.";
    }

    private static int RemoveSessionChestSaveData(global::TaskbarHero.PlayerSaveData save, List<ulong> uniqueIds, out int itemSavesRemoved)
    {
        itemSavesRemoved = 0;
        if (uniqueIds == null || uniqueIds.Count == 0)
        {
            return 0;
        }

        RemoveSaveBoxEntries(save, uniqueIds);
        itemSavesRemoved = RemoveItemSaves(save, new HashSet<ulong>(uniqueIds));
        return uniqueIds.Count;
    }

    private static void MarkSessionChestIds(List<ulong> uniqueIds)
    {
        if (uniqueIds == null || uniqueIds.Count == 0)
        {
            return;
        }

        lock (SessionChestSync)
        {
            for (int i = 0; i < uniqueIds.Count; i++)
            {
                if (uniqueIds[i] != 0UL)
                {
                    SessionChestIds.Add(uniqueIds[i]);
                }
            }
        }
    }

    internal static int PruneSessionChestsForSave(global::TaskbarHero.PlayerSaveData save)
    {
        List<ulong> ids;
        lock (SessionChestSync)
        {
            if (SessionChestIds.Count == 0)
            {
                return 0;
            }

            ids = new List<ulong>(SessionChestIds);
        }

        RemoveRuntimeBoxEntries(global::TaskbarHero.EBoxType.NORMAL, ids);
        RemoveRuntimeBoxEntries(global::TaskbarHero.EBoxType.BOSS, ids);
        RemoveRuntimeBoxEntries(global::TaskbarHero.EBoxType.ACTBOSS, ids);
        RemoveSaveBoxEntries(save, ids);
        int itemSavesRemoved = RemoveItemSaves(save, new HashSet<ulong>(ids));
        int changed = ids.Count + itemSavesRemoved;
        if (changed > 0)
        {
            Plugin.FileLog($"PreSave limpio cofres de sesion: ids {ids.Count}, item saves {itemSavesRemoved}. {BuildBoxSummary(save)}");
        }

        return changed;
    }

    private static bool IsUnsafeForChestMutation(global::TaskbarHero.PlayerSaveData save, out string reason)
    {
        int heroCount = save?.heroSaveDatas?.Count ?? 0;
        int itemCount = save?.itemSaveDatas?.Count ?? 0;
        int inventoryCount = save?.inventorySaveDatas?.Count ?? 0;
        int stashCount = save?.stashSaveDatas?.Count ?? 0;
        int petCount = save?.PetSaveData?.Count ?? 0;

        if (save == null || heroCount < 3 || itemCount < 1 || inventoryCount < 10 || stashCount < 10 || petCount < 1)
        {
            reason = $"heroes={heroCount},items={itemCount},inv={inventoryCount},stash={stashCount},pets={petCount}";
            return true;
        }

        reason = "ok";
        return false;
    }

    private static bool AreRuntimeBoxesSyncedWithSave(global::TaskbarHero.PlayerSaveData save, out string reason)
    {
        int saveNormal = GetSaveBoxCount(save, global::TaskbarHero.EBoxType.NORMAL);
        int saveBoss = GetSaveBoxCount(save, global::TaskbarHero.EBoxType.BOSS);
        int saveAct = GetSaveBoxCount(save, global::TaskbarHero.EBoxType.ACTBOSS);
        int runtimeNormal = GetRuntimeBoxCount(global::TaskbarHero.EBoxType.NORMAL);
        int runtimeBoss = GetRuntimeBoxCount(global::TaskbarHero.EBoxType.BOSS);
        int runtimeAct = GetRuntimeBoxCount(global::TaskbarHero.EBoxType.ACTBOSS);

        if (saveNormal > runtimeNormal || saveBoss > runtimeBoss || saveAct > runtimeAct)
        {
            reason = $"save N/B/A={saveNormal}/{saveBoss}/{saveAct}, runtime N/B/A={runtimeNormal}/{runtimeBoss}/{runtimeAct}";
            return false;
        }

        reason = $"save/runtime N/B/A={saveNormal}/{saveBoss}/{saveAct}";
        return true;
    }

    private static void RemoveSaveBoxEntries(global::TaskbarHero.PlayerSaveData save, List<ulong> uniqueIds)
    {
        if (uniqueIds == null || uniqueIds.Count == 0 || !IsValid(save?.BoxData))
        {
            return;
        }

        var boxData = save.BoxData;
        if (boxData.BoxTypes == null || boxData.BoxUniqueId == null || boxData.BoxQuantity == null)
        {
            return;
        }

        for (int i = Math.Min(boxData.BoxTypes.Count, Math.Min(boxData.BoxUniqueId.Count, boxData.BoxQuantity.Count)) - 1; i >= 0; i--)
        {
            if (!uniqueIds.Contains(boxData.BoxUniqueId[i]))
            {
                continue;
            }

            boxData.BoxTypes.RemoveAt(i);
            boxData.BoxUniqueId.RemoveAt(i);
            boxData.BoxQuantity.RemoveAt(i);
        }
    }

    private static void RemoveRuntimeBoxEntries(global::TaskbarHero.EBoxType boxType, List<ulong> uniqueIds)
    {
        if (uniqueIds == null || uniqueIds.Count == 0)
        {
            return;
        }

        try
        {
            var store = global::uz.ty.bshg;
            if (store == null || !store.TryGetValue(boxType, out var perType) || perType == null)
            {
                return;
            }

            foreach (ulong uniqueId in uniqueIds)
            {
                perType.Remove(uniqueId);
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog("RemoveRuntimeBoxEntries failed: " + ex.Message);
        }
    }

    private static int RemoveStageBoxManagerEntries(List<ulong> uniqueIds)
    {
        if (uniqueIds == null || uniqueIds.Count == 0)
        {
            return 0;
        }

        try
        {
            var manager = GetStageBoxManager();
            if (!IsValid(manager))
            {
                return 0;
            }

            var ids = new HashSet<ulong>(uniqueIds);
            int removed = 0;
            removed += RemoveStageBoxManagerEntriesFromStore("bspx", manager.bspx, ids);
            removed += RemoveStageBoxManagerEntriesFromStore("bspy", manager.bspy, ids);
            removed += RemoveStageBoxManagerEntriesFromStore("bspz", manager.bspz, ids);
            if (removed > 0)
            {
                Plugin.FileLog($"StageBox manager stale limpiados: {removed} entradas.");
            }

            return removed;
        }
        catch (Exception ex)
        {
            Plugin.FileLog("RemoveStageBoxManagerEntries failed: " + ex.Message);
            return 0;
        }
    }

    private static int RemoveStageBoxManagerEntriesFromStore(
        string storeName,
        Il2CppSystem.Collections.Generic.IReadOnlyDictionary<global::TaskbarHero.EBoxType, Il2CppSystem.Collections.Generic.List<global::TaskbarHero.BoxData>> store,
        HashSet<ulong> uniqueIds)
    {
        if (store == null || uniqueIds == null || uniqueIds.Count == 0)
        {
            return 0;
        }

        int removed = 0;
        foreach (var boxType in new[]
                 {
                     global::TaskbarHero.EBoxType.NORMAL,
                     global::TaskbarHero.EBoxType.BOSS,
                     global::TaskbarHero.EBoxType.ACTBOSS
                 })
        {
            if (!store.TryGetValue(boxType, out var boxes) || boxes == null)
            {
                continue;
            }

            for (int i = boxes.Count - 1; i >= 0; i--)
            {
                if (TryReadBoxUniqueId(boxes[i], out ulong uniqueId) && uniqueIds.Contains(uniqueId))
                {
                    boxes.RemoveAt(i);
                    removed++;
                }
            }
        }

        if (removed > 0)
        {
            Plugin.FileLog($"StageBox manager {storeName} limpio {removed} entradas.");
        }

        return removed;
    }

    private static void EnsureSerializedBoxData(global::TaskbarHero.PlayerSaveData save)
    {
        if (!IsValid(save.BoxData))
        {
            save.BoxData = new global::TaskbarHero.SerializedBoxData();
        }

        if (save.BoxData.BoxTypes == null)
        {
            save.BoxData.BoxTypes = new Il2CppSystem.Collections.Generic.List<global::TaskbarHero.EBoxType>();
        }

        if (save.BoxData.BoxUniqueId == null)
        {
            save.BoxData.BoxUniqueId = new Il2CppSystem.Collections.Generic.List<ulong>();
        }

        if (save.BoxData.BoxQuantity == null)
        {
            save.BoxData.BoxQuantity = new Il2CppSystem.Collections.Generic.List<int>();
        }

        int count = Math.Min(save.BoxData.BoxTypes.Count, Math.Min(save.BoxData.BoxUniqueId.Count, save.BoxData.BoxQuantity.Count));
        while (save.BoxData.BoxTypes.Count > count)
        {
            save.BoxData.BoxTypes.RemoveAt(save.BoxData.BoxTypes.Count - 1);
        }

        while (save.BoxData.BoxUniqueId.Count > count)
        {
            save.BoxData.BoxUniqueId.RemoveAt(save.BoxData.BoxUniqueId.Count - 1);
        }

        while (save.BoxData.BoxQuantity.Count > count)
        {
            save.BoxData.BoxQuantity.RemoveAt(save.BoxData.BoxQuantity.Count - 1);
        }
    }

    private static bool TryParseBoxType(string rawType, out global::TaskbarHero.EBoxType boxType)
    {
        string normalized = (rawType ?? string.Empty).Trim().ToUpperInvariant();
        switch (normalized)
        {
            case "":
            case "NORMAL":
            case "NORMALBOX":
                boxType = global::TaskbarHero.EBoxType.NORMAL;
                return true;
            case "BOSS":
            case "BOSSBOX":
                boxType = global::TaskbarHero.EBoxType.BOSS;
                return true;
            case "ACT":
            case "ACTBOSS":
            case "ACT_BOSS":
                boxType = global::TaskbarHero.EBoxType.ACTBOSS;
                return true;
            default:
                return Enum.TryParse(normalized, ignoreCase: true, out boxType);
        }
    }

    private static int FindSaveBoxEntry(global::TaskbarHero.SerializedBoxData boxData, global::TaskbarHero.EBoxType boxType)
    {
        if (!IsValid(boxData) || boxData.BoxTypes == null || boxData.BoxUniqueId == null || boxData.BoxQuantity == null)
        {
            return -1;
        }

        int count = Math.Min(boxData.BoxTypes.Count, Math.Min(boxData.BoxUniqueId.Count, boxData.BoxQuantity.Count));
        for (int i = 0; i < count; i++)
        {
            if (boxData.BoxTypes[i] == boxType && boxData.BoxUniqueId[i] != 0UL)
            {
                return i;
            }
        }

        return -1;
    }

    private static ulong FindNextBoxUniqueId(global::TaskbarHero.PlayerSaveData save)
    {
        ulong candidate = Math.Max(FindMaxKnownUniqueId(save), FindMaxKnownBoxUniqueId(save)) + 1UL;
        while (candidate == 0UL || UniqueIdExists(save, candidate) || BoxUniqueIdExists(save, candidate))
        {
            candidate++;
        }

        return candidate;
    }

    private static ulong FindMaxKnownBoxUniqueId(global::TaskbarHero.PlayerSaveData save)
    {
        ulong max = 0UL;
        var boxIds = save?.BoxData?.BoxUniqueId;
        if (boxIds != null)
        {
            for (int i = 0; i < boxIds.Count; i++)
            {
                if (boxIds[i] > max)
                {
                    max = boxIds[i];
                }
            }
        }

        try
        {
            var store = global::uz.ty.bshg;
            if (store != null)
            {
                foreach (var boxType in new[]
                         {
                             global::TaskbarHero.EBoxType.NORMAL,
                             global::TaskbarHero.EBoxType.BOSS,
                             global::TaskbarHero.EBoxType.ACTBOSS
                         })
                {
                    if (!store.TryGetValue(boxType, out var perType))
                    {
                        continue;
                    }

                    if (perType == null)
                    {
                        continue;
                    }

                    foreach (ulong uniqueId in perType.Keys)
                    {
                        if (uniqueId > max)
                        {
                            max = uniqueId;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog("FindMaxKnownBoxUniqueId runtime failed: " + ex.Message);
        }

        return max;
    }

    private static bool BoxUniqueIdExists(global::TaskbarHero.PlayerSaveData save, ulong uniqueId)
    {
        var boxIds = save?.BoxData?.BoxUniqueId;
        if (boxIds != null)
        {
            for (int i = 0; i < boxIds.Count; i++)
            {
                if (boxIds[i] == uniqueId)
                {
                    return true;
                }
            }
        }

        var store = global::uz.ty.bshg;
        if (store == null)
        {
            return false;
        }

        foreach (var boxType in new[]
                 {
                     global::TaskbarHero.EBoxType.NORMAL,
                     global::TaskbarHero.EBoxType.BOSS,
                     global::TaskbarHero.EBoxType.ACTBOSS
                 })
        {
            if (!store.TryGetValue(boxType, out var perType))
            {
                continue;
            }

            if (perType != null && perType.ContainsKey(uniqueId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SaveBoxUniqueIdExists(global::TaskbarHero.PlayerSaveData save, ulong uniqueId)
    {
        var boxIds = save?.BoxData?.BoxUniqueId;
        if (boxIds == null)
        {
            return false;
        }

        for (int i = 0; i < boxIds.Count; i++)
        {
            if (boxIds[i] == uniqueId)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryAddRuntimeBoxByGameApi(
        global::TaskbarHero.EBoxType boxType,
        ulong uniqueId,
        out int stageBoxItemKey,
        out string detail)
    {
        stageBoxItemKey = FindStageBoxItemKey(boxType);
        if (stageBoxItemKey <= 0)
        {
            detail = $"no encontre itemKey STAGEBOX para {boxType}";
            return false;
        }

        int beforeCount = GetRuntimeBoxCount(boxType);
        int beforeQuantity = GetRuntimeBoxQuantity(uniqueId);
        bool processed = TryRegisterStageBoxItem(stageBoxItemKey, uniqueId, boxType, out string processDetail);

        RefreshRuntimeBoxes();
        int afterCount = GetRuntimeBoxCount(boxType);
        int afterQuantity = GetRuntimeBoxQuantity(uniqueId);

        detail = $"itemKey {stageBoxItemKey}, {processDetail}, count {beforeCount}->{afterCount}, qty {beforeQuantity}->{afterQuantity}";
        Plugin.FileLog("TryAddRuntimeBoxByGameApi: " + detail);
        return processed && (afterCount > beforeCount || afterQuantity > beforeQuantity);
    }

    private static bool TryProcessBoxInventory(int stageBoxItemKey, ulong uniqueId, int quantity, out string detail)
    {
        try
        {
            var manager = global::np<global::qi>.brzs;
            if (!IsValid(manager))
            {
                detail = "qi no cargado";
                return false;
            }

            var list = new Il2CppSystem.Collections.Generic.List<global::wz.BoxInventoryData>();
            var box = new global::wz.BoxInventoryData
            {
                ItemId = stageBoxItemKey,
                UniqueId = uniqueId,
                Quantity = quantity
            };
            list.Add(box);
            manager.hew(list);
            detail = "qi.hew ok";
            return true;
        }
        catch (Exception ex)
        {
            detail = "qi.hew fallo: " + ex.Message;
            Plugin.FileLog(detail);
            return false;
        }
    }

    private static bool TryRegisterStageBoxItem(
        int stageBoxItemKey,
        ulong uniqueId,
        global::TaskbarHero.EBoxType boxType,
        out string detail)
    {
        try
        {
            int beforeCount = GetRuntimeBoxCount(boxType);
            int beforeQuantity = GetRuntimeBoxQuantity(uniqueId);
            var result = global::uz.uc.ize(stageBoxItemKey, uniqueId, 1, false);
            RefreshRuntimeBoxes();
            int afterCreateCount = GetRuntimeBoxCount(boxType);
            int afterCreateQuantity = GetRuntimeBoxQuantity(uniqueId);
            if (afterCreateCount > beforeCount || afterCreateQuantity > beforeQuantity)
            {
                detail = $"uz.uc.ize type={result.ItemType} addResult={result.AddResult}, target={boxType}, count {beforeCount}->{afterCreateCount}, qty {beforeQuantity}->{afterCreateQuantity}";
                return true;
            }

            var runtimeItem = FindRuntimeItem(uniqueId);
            bool boxAdded = IsValid(runtimeItem) && global::uz.ty.ivg(runtimeItem, 1);
            RefreshRuntimeBoxes();
            int afterFallbackCount = GetRuntimeBoxCount(boxType);
            int afterFallbackQuantity = GetRuntimeBoxQuantity(uniqueId);
            detail = $"uz.uc.ize type={result.ItemType} addResult={result.AddResult}, ty.ivg={boxAdded}, target={boxType}, count {beforeCount}->{afterFallbackCount}, qty {beforeQuantity}->{afterFallbackQuantity}";
            return boxAdded || afterFallbackCount > beforeCount || afterFallbackQuantity > beforeQuantity;
        }
        catch (Exception ex)
        {
            detail = "uz.uc.ize/ty.ivg fallo: " + ex.Message;
            Plugin.FileLog(detail);
            return false;
        }
    }

    private static int FindStageBoxItemKey(global::TaskbarHero.EBoxType boxType)
    {
        try
        {
            var data = GetDataManager();
            var list = data?.itemInfoData;
            if (list == null)
            {
                return 0;
            }

            int fallbackNormal = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (!IsValid(item) ||
                    item.ITEMTYPE != global::TaskbarHero.Data.EItemType.STAGEBOX ||
                    item.IsDeletedInServer)
                {
                    continue;
                }

                int itemKey = item.ItemKey;
                if (fallbackNormal == 0)
                {
                    fallbackNormal = itemKey;
                }

                try
                {
                    if (global::uz.ty.ive(itemKey) == boxType)
                    {
                        return itemKey;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.FileLog($"StageBox map failed key={itemKey}: {ex.Message}");
                }
            }

            return boxType == global::TaskbarHero.EBoxType.NORMAL ? fallbackNormal : 0;
        }
        catch (Exception ex)
        {
            Plugin.FileLog("FindStageBoxItemKey failed: " + ex.Message);
            return 0;
        }
    }

    private static int GetRuntimeBoxQuantity(ulong uniqueId)
    {
        try
        {
            return global::uz.ty.ivm(uniqueId);
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"Runtime box quantity failed uid={uniqueId}: {ex.Message}");
            return 0;
        }
    }

    private static int SetRuntimeBoxQuantity(global::TaskbarHero.EBoxType boxType, ulong uniqueId, int quantity)
    {
        try
        {
            var store = global::uz.ty.bshg;
            if (store == null || !store.TryGetValue(boxType, out var perType) || perType == null)
            {
                Plugin.FileLog($"Runtime box map no disponible para {boxType}.");
                return 0;
            }

            int oldQuantity = perType.TryGetValue(uniqueId, out int existing) ? existing : 0;
            perType[uniqueId] = quantity;
            Plugin.FileLog($"Runtime box {boxType} uid={uniqueId} {oldQuantity}->{quantity}");
            return oldQuantity == quantity ? 0 : 1;
        }
        catch (Exception ex)
        {
            Plugin.FileLog("SetRuntimeBoxQuantity failed: " + ex.Message);
            return 0;
        }
    }

    private static int RefreshRuntimeBoxes()
    {
        int changed = 0;
        AttachIl2CppThread();
        changed += TryRuntimeCall("uz.ty.berw boxes", () => global::uz.ty.berw?.Invoke());
        changed += TryRuntimeCall("uz.ty.berx boxes", () => global::uz.ty.berx?.Invoke());
        return changed;
    }

    private static string BuildBoxSummary(global::TaskbarHero.PlayerSaveData save)
    {
        return "cofres runtime " +
               FormatBoxCounts(GetRuntimeBoxCount) +
               ", save " +
               FormatBoxCounts(type => GetSaveBoxCount(save, type));
    }

    private static string FormatBoxCounts(Func<global::TaskbarHero.EBoxType, int> counter)
    {
        return $"N={counter(global::TaskbarHero.EBoxType.NORMAL)}, B={counter(global::TaskbarHero.EBoxType.BOSS)}, A={counter(global::TaskbarHero.EBoxType.ACTBOSS)}";
    }

    private static int GetRuntimeBoxCount(global::TaskbarHero.EBoxType boxType)
    {
        try
        {
            return global::uz.ty.ivl(boxType);
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"Runtime box count failed {boxType}: {ex.Message}");
            return 0;
        }
    }

    private static int GetSaveBoxCount(global::TaskbarHero.PlayerSaveData save, global::TaskbarHero.EBoxType boxType)
    {
        var boxData = save?.BoxData;
        if (!IsValid(boxData) || boxData.BoxTypes == null || boxData.BoxQuantity == null || boxData.BoxUniqueId == null)
        {
            return 0;
        }

        int total = 0;
        int count = Math.Min(boxData.BoxTypes.Count, Math.Min(boxData.BoxUniqueId.Count, boxData.BoxQuantity.Count));
        for (int i = 0; i < count; i++)
        {
            if (boxData.BoxTypes[i] == boxType && boxData.BoxUniqueId[i] != 0UL)
            {
                total += Math.Max(0, boxData.BoxQuantity[i]);
            }
        }

        return total;
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

                changed += TryRuntimeCall("StashCache.OnStashChanged", () => slot.OnStashChanged?.Invoke());
            }

            changed += TryRuntimeCall("uz.Stash.jjb", () => global::uz.Stash.jjb());
            changed += TryRuntimeCall("uz.Stash.bexb", () => global::uz.Stash.bexb?.Invoke());
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"RefreshRuntimeStashSlots failed: {ex.Message}");
        }

        return changed;
    }

    private static int RefreshRuntimeTradingStashSlots()
    {
        int changed = 0;
        AttachIl2CppThread();
        try
        {
            for (int i = 0; i < 420; i++)
            {
                var slot = FindRuntimeTradingStashSlot(i);
                if (!IsValid(slot))
                {
                    continue;
                }

                changed += TryRuntimeCall("TradingStashCache.jrx", () => slot.jrx(true));
                changed += TryRuntimeCall("TradingStashCache.OnTradingStashSlotChanged", () => slot.OnTradingStashSlotChanged?.Invoke());
            }

            changed += TryRuntimeCall("uz.uy.jkm", () => global::uz.uy.jkm());
            changed += TryRuntimeCall("uz.uy.bext", () => global::uz.uy.bext?.Invoke(true));
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"RefreshRuntimeTradingStashSlots failed: {ex.Message}");
        }

        return changed;
    }

    private static int RefreshRuntimeHero(global::TaskbarHero.EasySaveData.HeroSaveData hero)
    {
        int changed = 0;
        AttachIl2CppThread();
        try
        {
            var cache = global::uz.tx.isk(hero.heroKey);
            if (!IsValid(cache))
            {
                return changed;
            }

            changed += TryRuntimeCall("vb.egz", () => cache.egz(hero.HeroLevel));
            changed += TryRuntimeCall("vb.jpd", () => InvokeInstanceMethod(cache, "jpd", hero));
            changed += RefreshRuntimeHeroAbilityTree(cache, hero);
            changed += TryRuntimeCall("vb.jpu", () => cache.jpu());
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
            var cache = global::uz.tx.isk(hero.heroKey);
            if (!IsValid(cache))
            {
                return changed;
            }

            changed += TryRuntimeCall("vb.jpd", () =>
            {
                InvokeInstanceMethod(cache, "jpd", hero);
            });
            changed += TryRuntimeCall("vb.jpu", () => cache.jpu());
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
                var info = data.mfb(level);
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

    private static int RefreshRuntimeHeroAbilityTree(global::vb cache, global::TaskbarHero.EasySaveData.HeroSaveData hero)
    {
        int changed = 0;
        if (!IsValid(cache) || hero == null)
        {
            return changed;
        }

        int desiredAllocated = Math.Max(hero.AllocatedHeroAbilityPoint, DefaultHeroAllocatedAbilityPoints);
        int currentAllocated = ReadRuntimeInt("vb.bsoi", () => cache.bsoi, -1);
        if (currentAllocated >= 0 && currentAllocated < desiredAllocated)
        {
            int delta = desiredAllocated - currentAllocated;
            changed += EnsureRuntimeHeroAbilityPoints(cache, Math.Max(hero.AbilityPoint, delta));
            changed += TryRuntimeCall("vb.jqh allocate", () =>
            {
                if (!cache.jqh(-delta))
                {
                    throw new InvalidOperationException($"jqh no acepto delta {-delta}");
                }
            });
            changed += SetRuntimeHeroAbilityPointsExact(cache, hero.AbilityPoint);
        }
        else
        {
            changed += SetRuntimeHeroAbilityPointsExact(cache, hero.AbilityPoint);
        }

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
                    if (!cache.jof(groupKey))
                    {
                        cache.jog(groupKey);
                    }
                });
            }
        }

        return changed;
    }

    private static int SetRuntimeHeroAbilityPointsExact(global::vb cache, int desired)
    {
        desired = Math.Max(0, desired);
        int current = ReadRuntimeInt("vb.bsoh", () => cache.bsoh, -1);
        if (current < 0 || current == desired)
        {
            return 0;
        }

        int delta = desired - current;
        return TryRuntimeCall("vb.jqg ability points exact", () => InvokeInstanceMethod(cache, "jqg", delta));
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
            var cache = global::uz.tx.isk(hero.heroKey);
            if (!IsValid(cache))
            {
                return 0;
            }

            changed += SetRuntimeHeroAbilityPointsExact(cache, hero.AbilityPoint);
            changed += TryRuntimeCall("vb.jpu ability points refresh", () => cache.jpu());
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"SetRuntimeHeroAbilityPointsExact failed key={hero.heroKey}: {ex.Message}");
        }

        return changed;
    }

    private static int EnsureRuntimeHeroAbilityPoints(global::vb cache, int minimum)
    {
        if (minimum <= 0)
        {
            return 0;
        }

        int current = ReadRuntimeInt("vb.bsoh", () => cache.bsoh, -1);
        if (current >= minimum || current < 0)
        {
            return 0;
        }

        int delta = minimum - current;
        return TryRuntimeCall("vb.jqg ability points", () => InvokeInstanceMethod(cache, "jqg", delta));
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
                if (method.Name == methodName && method.GetParameters().Length == args.Length)
                {
                    return method.Invoke(target, args);
                }
            }

            type = type.BaseType;
        }

        throw new MissingMethodException(target.GetType().FullName, methodName);
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
        changed += ClearHeroRuntimeDictionary("berc");
        changed += ClearHeroRuntimeDictionary("berd");
        changed += TryRuntimeCall("uz.tx.isj", () => global::uz.tx.isj());
        changed += TryRuntimeCall("uz.tx.isn", () => global::uz.tx.isn());
        changed += TryRuntimeCall("uz.tx.itn", () => global::uz.tx.itn());
        changed += TryRuntimeCall("uz.tx.beqy", () => global::uz.tx.beqy?.Invoke());
        changed += TryRuntimeCall("uz.tx.beqz", () => global::uz.tx.beqz?.Invoke());
        changed += TryRuntimeCall("uz.tx.bera", () => global::uz.tx.bera?.Invoke());
        return changed;
    }

    private static int ClearHeroRuntimeDictionary(string fieldName)
    {
        try
        {
            FieldInfo field = typeof(global::uz.tx).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
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

            var cache = global::uz.tx.isk(hero.heroKey);
            if (!IsValid(cache))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(cache.bsok)
                .Append(" used=")
                .Append(cache.bsoi)
                .Append(" ap=")
                .Append(cache.bsoh);
            shown++;
        }

        return builder.Length == 0 ? "sin heroes" : builder.ToString();
    }

    private static int RefreshHeroManagerRuntime()
    {
        int changed = 0;
        AttachIl2CppThread();
        changed += TryRuntimeCall("uz.tx.isi", () => global::uz.tx.isi());
        changed += TryRuntimeCall("uz.tx.isj", () => global::uz.tx.isj());
        changed += TryRuntimeCall("uz.tx.isn", () => global::uz.tx.isn());
        changed += TryRuntimeCall("uz.tx.itn", () => global::uz.tx.itn());
        changed += TryRuntimeCall("uz.tx.beqy", () => global::uz.tx.beqy?.Invoke());
        changed += TryRuntimeCall("uz.tx.beqz", () => global::uz.tx.beqz?.Invoke());
        changed += TryRuntimeCall("uz.tx.bera", () => global::uz.tx.bera?.Invoke());
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

        changed += TryRuntimeCall("PetManager.kqu", () => manager.kqu(petKey));
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
            var manager = GetSaveManager();
            if (manager == null)
            {
                return "Save manager no listo; no se pudo guardar.";
            }

            ReapplyForcedHeroLevels(manager.bggy);
            try
            {
                return ForceWriteSave(manager);
            }
            catch (Exception ex)
            {
                Plugin.FileLog("Direct save failed, falling back to async save: " + ex.Message);
                manager.mfp();
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

    private static string ForceWriteSave(global::bao manager)
    {
        var account = manager.bggx;
        var save = manager.bggy;
        if (account == null || account.Pointer == IntPtr.Zero || save == null || save.Pointer == IntPtr.Zero)
        {
            throw new InvalidOperationException("SaveData no cargado para escritura directa.");
        }

        bool timestampDirty = true;
        try
        {
            timestampDirty = manager.mgf();
            manager.mfu(global::UnityEngine.Application.version, global::UnityEngine.Time.timeSinceLevelLoad);
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Save metadata update failed: " + ex.Message);
        }

        string accountJson = global::Newtonsoft.Json.JsonConvert.SerializeObject(account);
        string playerJson = global::Newtonsoft.Json.JsonConvert.SerializeObject(save);
        string signature = manager.mfv(accountJson, playerJson, account.ownerSteamId);
        string accountKey = GetPrivateSaveKey("fhg");
        string playerKey = GetPrivateSaveKey("fhi");
        string signatureKey = GetPrivateSaveKey("fhk");
        var settings = new global::ES3Settings(manager.bghg, (global::ES3Settings)null);
        global::bfj.naz<string>(accountKey, accountJson, settings);
        global::bfj.naz<string>(playerKey, playerJson, settings);
        global::bfj.naz<string>(signatureKey, signature, settings);

        string status = "Guardado directo ES3 escrito.";
        Plugin.FileLog(status);
        return status;
    }

    private static string GetPrivateSaveKey(string methodName)
    {
        const string keyTypeName = "<PrivateImplementationDetails>{BDBA7524-0749-4342-84CF-86ABA0F0E14D}.a";
        var keyType = typeof(global::bao).Assembly.GetType(keyTypeName);
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

        oldValue = currency.bsgz;
        long delta = target - oldValue;
        if (delta > 0)
        {
            try
            {
                currency.irg(delta, global::TaskbarHero.EGoldCurrencySource.OfflineReward);
            }
            catch (Exception ex)
            {
                Plugin.FileLog($"Runtime currency irg failed key={key}: {ex.Message}");
            }
        }

        currency.beqo = target;
        newValue = currency.bsgz;

        try
        {
            currency.beqm?.Invoke(newValue);
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"Runtime currency callback failed key={key}: {ex.Message}");
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

        oldValue = currency.bsgz;
        long target = AddSaturating(oldValue, amount);
        if (amount > 0)
        {
            try
            {
                currency.irg(amount, global::TaskbarHero.EGoldCurrencySource.OfflineReward);
            }
            catch (Exception ex)
            {
                Plugin.FileLog($"Runtime currency add failed key={key}: {ex.Message}");
            }
        }

        currency.beqo = target;
        newValue = currency.bsgz;

        try
        {
            currency.beqm?.Invoke(newValue);
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"Runtime currency callback failed key={key}: {ex.Message}");
        }

        return true;
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

    private static global::uz.tn FindRuntimeCurrency(int key)
    {
        AttachIl2CppThread();
        global::uz.tn currency = null;

        try { currency = global::uz.tm.hrb(key); } catch { }
        if (IsValid(currency)) { return currency; }

        try { currency = global::uz.tm.fmp(key); } catch { }
        if (IsValid(currency)) { return currency; }

        try { currency = global::uz.tm.iqz(key); } catch { }
        if (IsValid(currency)) { return currency; }

        try { currency = global::uz.tm.iqn(key); } catch { }
        if (IsValid(currency)) { return currency; }

        try { currency = global::uz.tm.czg(key); } catch { }
        if (IsValid(currency)) { return currency; }

        var currencies = global::uz.tm.beqj;
        if (currencies != null)
        {
            for (int i = 0; i < currencies.Count; i++)
            {
                currency = currencies[i];
                if (IsValid(currency) && currency.beql != null && currency.beql.CurrencyKey == key)
                {
                    return currency;
                }
            }
        }

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
        var save = GetSaveManager()?.bggy;
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
            steps += TryRuntimeCall("LocalInventoryManager.kpy " + reason, () => inventoryManager.kpy(slot.Index, uniqueId));
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
            changed += TryRuntimeCall("ua.ixr socket refresh", () => InvokeInstanceMethod(itemCache, "ixr", itemSave));
            changed += TryRuntimeCall("uz.ty.iul socket refresh", () => global::uz.ty.iul(itemSave.UniqueId, itemCache));
            changed += TryRuntimeCall("uz.ty.miv socket refresh", () => global::uz.ty.miv(itemSave.UniqueId, itemCache));
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

            var itemSave = new global::TaskbarHero.EasySaveData.ItemSaveData(itemKey, uniqueId)
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
            resolvedStatType = statMod.bgma;
            modType = statMod.bgmb;
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
        global::bam data,
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
            statType = statMod.bgma;
            modType = statMod.bgmb;
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
        global::bam data,
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
        global::bam data,
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
        global::bam data,
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
            if (!IsValid(material) || material.bgkl != materialType)
            {
                continue;
            }

            var statMod = GetStatModInfo(data, material.bgkk, material.bgkm);
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
                MaterialTier = material.bgkm,
                StatModKey = material.bgkk,
                HasStatMod = hasStatMod,
                StatType = hasStatMod ? statMod.bgma : global::TaskbarHero.StatType.NONE,
                ModType = hasStatMod ? statMod.bgmb : global::TaskbarHero.MODTYPE.ADDITIVE,
                Value = hasStatMod ? statMod.bgmc : 0
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
        global::bam data,
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

    private static global::TaskbarHero.Data.MaterialInfoData GetMaterialInfo(global::bam data, int materialKey)
    {
        try
        {
            return data?.mdm(materialKey);
        }
        catch
        {
            return null;
        }
    }

    private static global::TaskbarHero.Data.StatModInfoData GetStatModInfo(global::bam data, int statModKey, int tier)
    {
        try
        {
            return data?.mdj(statModKey, tier);
        }
        catch
        {
            return null;
        }
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

    private static string WriteSocketedItemDiagnosticsFile(global::TaskbarHero.PlayerSaveData save, global::bam data)
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
        global::bam data,
        global::TaskbarHero.EasySaveData.ItemSaveData item,
        global::TaskbarHero.Data.ItemInfoData itemInfo,
        string location,
        string enchantCounts,
        int slot,
        global::TaskbarHero.EasySaveData.ItemEnchantSaveData enchant)
    {
        var material = enchant.MaterialKey > 0 ? GetMaterialInfo(data, enchant.MaterialKey) : null;
        bool materialValid = IsValid(material);
        var materialStatMod = materialValid ? GetStatModInfo(data, material.bgkk, material.bgkm) : null;
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
            materialValid ? material.bgkl : string.Empty,
            materialValid ? material.bgkm : 0,
            materialValid ? material.bgkk : 0,
            materialStatModValid ? materialStatMod.bgma : string.Empty,
            materialStatModValid ? materialStatMod.bgmb : string.Empty,
            materialStatModValid ? materialStatMod.bgmc : 0,
            enchant.StatModKey,
            enchant.Tier,
            enchantStatModValid,
            enchantStatModValid ? enchantStatMod.bgma : string.Empty,
            enchantStatModValid ? enchantStatMod.bgmb : string.Empty,
            enchantStatModValid ? enchantStatMod.bgmc : 0,
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

    private static string WriteCraftingRecipeFile(global::bam data)
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

    private static string BuildCraftingRecipeExamples(global::bam data)
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

    private static System.Collections.Generic.List<global::TaskbarHero.Data.CraftingRecipeInfoData> CollectCraftingRecipes(global::bam data)
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
                    recipe = data.mcm(tier, types[typeIndex]);
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
        global::bam data,
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
                return data.mcm(requestedTier, craftingType);
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

    private static string DescribeCraftingMaterials(global::bam data, string rawMaterial, string rawIndex)
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
                    .Append(materialInfo.bgkl)
                    .Append(":tier")
                    .Append(materialInfo.bgkm);
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
        AppendItemExamples(builder, list, global::TaskbarHero.Data.EItemType.STAGEBOX, 4, "STAGEBOX");
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

    private static global::bao GetSaveManager()
    {
        AttachIl2CppThread();
        var manager = global::np<global::bao>.brzs;
        return manager == null || manager.Pointer == IntPtr.Zero ? null : manager;
    }

    private static global::bam GetDataManager()
    {
        AttachIl2CppThread();
        var manager = global::np<global::bam>.brzs;
        return manager == null || manager.Pointer == IntPtr.Zero ? null : manager;
    }

    private static global::TaskbarHero.Manager.LocalInventoryManager GetLocalInventoryManager()
    {
        AttachIl2CppThread();
        try
        {
            var manager = global::TaskbarHero.Manager.LocalInventoryManager.bstp;
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
        var manager = global::np<global::TaskbarHero.Manager.PetManager>.brzs;
        return manager == null || manager.Pointer == IntPtr.Zero ? null : manager;
    }

    private static MethodInfo ResolveStageManagerBoxCooldownsGetter()
    {
        try
        {
            var getter = AccessTools.PropertyGetter(typeof(global::TaskbarHero.StageManager), "bdlr") ??
                         AccessTools.Method(typeof(global::TaskbarHero.StageManager), "get_bdlr");
            if (getter != null)
            {
                string returnType = getter.ReturnType?.FullName ?? getter.ReturnType?.Name ?? string.Empty;
                Plugin.FileLog("Fast chest cooldown getter resolved: " + getter.Name + " " + returnType);
                return getter;
            }

            Plugin.FileLog("Fast chest cooldown getter not found.");
            return null;
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Fast chest cooldown getter resolve failed: " + ex.Message);
            return null;
        }
    }

    private static FieldInfo ResolveStageManagerBoxCooldownsField()
    {
        try
        {
            var namedField = AccessTools.Field(typeof(global::TaskbarHero.StageManager), "bdlr");
            if (namedField != null)
            {
                string namedType = namedField.FieldType?.FullName ?? namedField.FieldType?.Name ?? string.Empty;
                Plugin.FileLog("Fast chest cooldown field resolved by name: " + namedField.Name + " " + namedType);
                return namedField;
            }

            var fields = AccessTools.GetDeclaredFields(typeof(global::TaskbarHero.StageManager));
            var candidates = new StringBuilder();
            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                if (field == null || field.IsStatic)
                {
                    continue;
                }

                string typeName = field.FieldType?.FullName ?? field.FieldType?.Name ?? string.Empty;
                if (typeName.IndexOf("Dictionary", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    AppendShortSummary(candidates, field.Name + ":" + typeName);
                }

                if (IsStageManagerBoxCooldownFieldType(field.FieldType))
                {
                    Plugin.FileLog("Fast chest cooldown field resolved: " + field.Name + " " + typeName);
                    return field;
                }
            }

            Plugin.FileLog("Fast chest cooldown field not found. Dictionary candidates: " + candidates);
            return null;
        }
        catch (Exception ex)
        {
            Plugin.FileLog("Fast chest cooldown field resolve failed: " + ex.Message);
            return null;
        }
    }

    private static bool IsStageManagerBoxCooldownFieldType(Type fieldType)
    {
        if (fieldType == null)
        {
            return false;
        }

        if (fieldType == typeof(Il2CppSystem.Collections.Generic.Dictionary<global::TaskbarHero.EBoxType, float>))
        {
            return true;
        }

        try
        {
            if (fieldType.IsGenericType)
            {
                var args = fieldType.GetGenericArguments();
                if (args.Length == 2 &&
                    args[0] == typeof(global::TaskbarHero.EBoxType) &&
                    args[1] == typeof(float))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Fall through to string matching for IL2CPP generated types.
        }

        string fullName = fieldType.FullName ?? fieldType.Name ?? string.Empty;
        return fullName.IndexOf("Dictionary", StringComparison.OrdinalIgnoreCase) >= 0 &&
               fullName.IndexOf("EBoxType", StringComparison.OrdinalIgnoreCase) >= 0 &&
               (fullName.IndexOf("Single", StringComparison.OrdinalIgnoreCase) >= 0 ||
                fullName.IndexOf("System.Single", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static global::TaskbarHero.StageManager GetStageManager()
    {
        AttachIl2CppThread();
        var manager = global::np<global::TaskbarHero.StageManager>.brzs;
        return manager == null || manager.Pointer == IntPtr.Zero ? null : manager;
    }

    private static global::vy GetStageBoxManager()
    {
        AttachIl2CppThread();
        try
        {
            var method = StageBoxManagerMethod.Value;
            var manager = method?.Invoke(null, null) as global::vy;
            return manager == null || manager.Pointer == IntPtr.Zero ? null : manager;
        }
        catch (Exception ex)
        {
            Plugin.FileLog("StageBoxManager lookup failed: " + ex.Message);
            return null;
        }
    }

    private static global::TaskbarHero.BoxData TryFindStageBoxByUniqueId(ulong uniqueId)
    {
        if (uniqueId == 0UL)
        {
            return null;
        }

        try
        {
            var manager = GetStageBoxManager();
            if (!IsValid(manager))
            {
                return null;
            }

            var method = StageBoxFindByUniqueIdMethod.Value;
            if (method != null)
            {
                return method.Invoke(manager, new object[] { uniqueId }) as global::TaskbarHero.BoxData;
            }

            return manager.jvk(uniqueId);
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"StageBox lookup by uid failed uid={uniqueId}: {ex.Message}");
            return null;
        }
    }

    private static global::TaskbarHero.BoxData TryFindStageBoxByRewardUniqueId(ulong rewardUniqueId)
    {
        if (rewardUniqueId == 0UL)
        {
            return null;
        }

        try
        {
            var manager = GetStageBoxManager();
            if (!IsValid(manager))
            {
                return null;
            }

            var box = TryFindStageBoxByRewardUniqueIdInStore(manager.bspx, rewardUniqueId);
            if (IsValid(box))
            {
                return box;
            }

            box = TryFindStageBoxByRewardUniqueIdInStore(manager.bspy, rewardUniqueId);
            if (IsValid(box))
            {
                return box;
            }

            return TryFindStageBoxByRewardUniqueIdInStore(manager.bspz, rewardUniqueId);
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"StageBox reward lookup failed rewardUid={rewardUniqueId}: {ex.Message}");
            return null;
        }
    }

    private static global::TaskbarHero.BoxData TryFindStageBoxByRewardUniqueIdInStore(
        Il2CppSystem.Collections.Generic.IReadOnlyDictionary<global::TaskbarHero.EBoxType, Il2CppSystem.Collections.Generic.List<global::TaskbarHero.BoxData>> store,
        ulong rewardUniqueId)
    {
        if (store == null)
        {
            return null;
        }

        foreach (var boxType in new[]
                 {
                     global::TaskbarHero.EBoxType.NORMAL,
                     global::TaskbarHero.EBoxType.BOSS,
                     global::TaskbarHero.EBoxType.ACTBOSS
                 })
        {
            if (!store.TryGetValue(boxType, out var boxes) || boxes == null)
            {
                continue;
            }

            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                string rewardKey = ReadBoxRewardUniqueKey(box);
                if (ulong.TryParse(rewardKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong existing) &&
                    existing == rewardUniqueId)
                {
                    return box;
                }
            }
        }

        return null;
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
            var item = data.mdo(itemKey);
            if (item != null && item.Pointer != IntPtr.Zero)
            {
                return item;
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"mdo item lookup failed for {itemKey}: {ex.Message}");
        }

        try
        {
            var item = data.gnk(itemKey);
            if (item != null && item.Pointer != IntPtr.Zero)
            {
                return item;
            }
        }
        catch (Exception ex)
        {
            Plugin.FileLog($"gnk item lookup failed for {itemKey}: {ex.Message}");
        }

        return null;
    }

    private static global::yw GetRuntimeAccountStatusManager(out string detail)
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

    private static global::yw GetAccountStatusManagerFromSingleton(StringBuilder attempts)
    {
        try
        {
            var getter = AccountStatusManagerGetterMethod.Value;
            if (getter == null)
            {
                attempts.Append("singleton sin getter; ");
                return null;
            }

            var manager = getter.Invoke(null, Array.Empty<object>()) as global::yw;
            attempts.Append(IsValid(manager) ? "singleton ok; " : "singleton null; ");
            return manager;
        }
        catch (Exception ex)
        {
            attempts.Append("singleton error ").Append(ex.Message).Append("; ");
            return null;
        }
    }

    private static global::yw GetAccountStatusManagerFromScene(StringBuilder attempts)
    {
        try
        {
            var manager = global::UnityEngine.Object.FindObjectOfType<global::yw>();
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
            var managers = global::UnityEngine.Resources.FindObjectsOfTypeAll<global::yw>();
            int count = managers?.Length ?? 0;
            attempts.Append("Resources yw=").Append(count).Append("; ");
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

    private static bool IsAccountStatusManagerReady(global::yw manager, StringBuilder attempts, string source)
    {
        if (!IsValid(manager))
        {
            attempts.Append(source).Append(" manager null; ");
            return false;
        }

        try
        {
            _ = manager.fhq(global::TaskbarHero.StatusSystem.EAccountStatus.MaxAmountNormalChest);
            attempts.Append(source).Append(" status ok; ");
            return true;
        }
        catch (Exception ex)
        {
            attempts.Append(source).Append(" status read error ").Append(ex.Message).Append("; ");
            return false;
        }
    }

    private static int ReadAccountStatus(global::yw manager, global::TaskbarHero.StatusSystem.EAccountStatus status)
    {
        return manager.fhq(status);
    }

    private static void SetAccountStatusContribution(global::yw manager, global::TaskbarHero.StatusSystem.EAccountStatus status, int value)
    {
        manager.ikw(FastChestStatusBoostSource, status, value);
    }

    private static global::TaskbarHero.PlayerSaveData GetSaveData()
    {
        var manager = GetSaveManager();
        if (manager == null)
        {
            throw new InvalidOperationException("Save manager no listo.");
        }

        var save = manager.bggy;
        if (save == null || save.Pointer == IntPtr.Zero)
        {
            throw new InvalidOperationException("PlayerSaveData no cargado.");
        }

        return save;
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
    private static void Prefix(global::TaskbarHero.PlayerSaveData __instance)
    {
        ModActions.PruneSessionChestsForSave(__instance);
    }

    private static void Postfix(global::TaskbarHero.PlayerSaveData __instance)
    {
        ModActions.ReapplyHeroUnlocks(__instance);
        ModActions.ReapplyForcedHeroLevels(__instance);
        ModActions.NormalizeEquippedItemContainerDuplicates(__instance);
    }
}

[HarmonyPatch(typeof(global::TaskbarHero.Monster), nameof(global::TaskbarHero.Monster.gqm))]
internal static class OneHitKillMonsterPatch
{
    private const float OneHitDamage = 1_000_000_000f;

    private static void Prefix(ref global::TaskbarHero.DamageInfo a)
    {
        if (Plugin.IsShuttingDown || !ModActions.OneHitKillEnabled || a.OriginDamage <= 0f)
        {
            return;
        }

        a.OriginDamage = OneHitDamage;
        a.IsCritical = true;
        a.FloatingDamageText = true;
        a.PlayHitFeedBack = true;
    }
}

[HarmonyPatch]
internal static class TrainerFastNormalChestDropPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var method = AccessTools.Method(
            typeof(global::TaskbarHero.StageManager),
            "ihv",
            new[] { typeof(global::TaskbarHero.EBoxType) });

        if (method != null)
        {
            Plugin.FileLog("Fast chest ihv patch target: " + method.FullDescription());
            yield return method;
        }
    }

    private static void Postfix(global::TaskbarHero.EBoxType a, ref bool __result)
    {
        ModActions.ApplyFastChestDropCheck(a, ref __result);
    }
}

[HarmonyPatch]
internal static class TrainerFastStageChestDropPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var method = AccessTools.Method(
            typeof(global::TaskbarHero.StageManager),
            "ihu",
            new[]
            {
                typeof(int),
                typeof(global::TaskbarHero.Data.EStageType),
                typeof(int),
                typeof(float),
                typeof(global::TaskbarHero.Data.EMonsterType)
            });

        if (method != null)
        {
            yield return method;
        }
    }

    private static void Prefix(
        global::TaskbarHero.StageManager __instance,
        int a,
        global::TaskbarHero.Data.EStageType b,
        ref int c,
        ref float d,
        global::TaskbarHero.Data.EMonsterType e)
    {
        ModActions.ReapplyGameSpeedIfNeeded("StageManager.ihu prefix");
        ModActions.SyncFastChestAccountStatusOnMainThread();
        ModActions.PrepareFastChestDropBeforeStageDrop(__instance, a, b, e, ref c, ref d);
        ModActions.TryForceFastChestDropFromStage(__instance, new object[] { a, b, c, d, e });
    }
}

[HarmonyPatch]
internal static class TrainerFastStageChestCooldownSetPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var method = AccessTools.Method(
            typeof(global::TaskbarHero.StageManager),
            "ihw",
            new[] { typeof(global::TaskbarHero.EBoxType), typeof(int) });

        if (method != null)
        {
            yield return method;
        }
    }

    private static void Postfix(global::TaskbarHero.StageManager __instance, global::TaskbarHero.EBoxType a, int b)
    {
        ModActions.OnStageBoxCooldownSet(__instance, a, b);
        ModActions.ReapplyGameSpeedIfNeeded("StageManager.ihw postfix");
    }
}

[HarmonyPatch]
internal static class TrainerTimeScaleSetterPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var method = AccessTools.PropertySetter(typeof(global::UnityEngine.Time), nameof(global::UnityEngine.Time.timeScale)) ??
                     AccessTools.Method(typeof(global::UnityEngine.Time), "set_timeScale");
        if (method != null)
        {
            yield return method;
        }
    }

    private static void Prefix(ref float value)
    {
        ModActions.FilterRequestedTimeScale(ref value);
    }
}

[HarmonyPatch]
internal static class TrainerFixedDeltaTimeSetterPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var method = AccessTools.PropertySetter(typeof(global::UnityEngine.Time), nameof(global::UnityEngine.Time.fixedDeltaTime)) ??
                     AccessTools.Method(typeof(global::UnityEngine.Time), "set_fixedDeltaTime");
        if (method != null)
        {
            yield return method;
        }
    }

    private static void Prefix(ref float value)
    {
        ModActions.FilterRequestedFixedDeltaTime(ref value);
    }
}

[HarmonyPatch]
internal static class TrainerStageBoxOpenTracePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (string name in new[] { "irs", "cqn", "dxb", "kml" })
        {
            var method = AccessTools.Method(
                typeof(global::uz.tv),
                name,
                new[]
                {
                    typeof(global::TaskbarHero.EBoxType),
                    typeof(Il2CppSystem.Action<global::TaskbarHero.BoxData>),
                    typeof(global::uz.ty.WillRemoveBoxData),
                    typeof(global::uz.uc.ua)
                });

            if (method != null)
            {
                Plugin.FileLog("Fast chest open trace patch target: " + method.FullDescription());
                yield return method;
            }
            else
            {
                Plugin.FileLog("Fast chest open trace patch target not found: uz.tv." + name);
            }
        }
    }

    private static void Prefix(global::TaskbarHero.EBoxType a, Il2CppSystem.Action<global::TaskbarHero.BoxData> b, global::uz.ty.WillRemoveBoxData c, global::uz.uc.ua d)
    {
        ModActions.TraceStageBoxOpen(a, c, d);
    }
}

internal static class TrainerStageBoxSteamConsumeTracePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield break;
    }

    private static void Postfix(ulong a, int b, long c, ref bool __result)
    {
        ModActions.TraceSteamChestConsume(a, b, c, __result);
    }
}

internal static class TrainerStageBoxClaimLookupTracePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield break;
    }

    private static void Postfix(int a, ref global::TaskbarHero.BoxData __result)
    {
        ModActions.TraceStageBoxClaimLookup(a, __result);
    }
}

[HarmonyPatch]
internal static class TrainerStageBoxAddItemTracePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var method = AccessTools.Method(
            typeof(global::uz.uc),
            "fzn",
            new[] { typeof(int), typeof(ulong), typeof(int), typeof(bool) });

        if (method != null)
        {
            Plugin.FileLog("Fast chest add item trace patch target: " + method.FullDescription());
            yield return method;
        }
        else
        {
            Plugin.FileLog("Fast chest add item trace patch target not found: uz.uc.fzn");
        }
    }

    private static void Postfix(int a, ulong b, int c, bool d, ref global::TaskbarHero.AddItemResult __result)
    {
        ModActions.TraceStageBoxAddItem(a, b, c, d, __result);
    }
}

[HarmonyPatch]
internal static class TrainerStageBoxExchangeResultTracePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(global::uz.tv)))
        {
            if (method == null || method.ReturnType != typeof(void))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(global::TaskbarHero.InventoryExchangeResult))
            {
                Plugin.FileLog("Fast chest exchange result trace patch target: " + method.FullDescription());
                yield return method;
            }
        }
    }

    private static void Prefix(global::TaskbarHero.InventoryExchangeResult a)
    {
        ModActions.TraceInventoryExchangeResult(a);
    }
}

[HarmonyPatch]
internal static class TrainerFastChestRequestTracePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(global::uz.uc)))
        {
            if (!string.Equals(method.Name, "izd", StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length != 2 || parameters[0].ParameterType != typeof(int))
            {
                continue;
            }

            Plugin.FileLog("Fast chest request trace patch target: " + method.FullDescription());
            yield return method;
        }
    }

    private static void Prefix(int a)
    {
        ModActions.TraceFastChestRequest(a);
    }
}

[HarmonyPatch]
internal static class TrainerFastChestCanAddBoxPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var method = AccessTools.Method(
            typeof(global::uz.ty),
            "iuf",
            new[] { typeof(global::TaskbarHero.EBoxType) });

        if (method != null)
        {
            Plugin.FileLog("Fast chest can-add patch target: " + method.FullDescription());
            yield return method;
        }
    }

    private static void Postfix(global::TaskbarHero.EBoxType a, ref bool __result)
    {
        ModActions.ApplyFastChestCanAddBox(a, ref __result);
    }
}

internal static class TrainerFastChestAccountStatusPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        // Harmony detours on these obfuscated yw account-status readers recurse
        // under IL2CPP/Unity 6 during startup. The trainer applies the boost by
        // mutating AccountStatus once from the command path instead.
        yield break;
    }

    private static void Postfix(global::TaskbarHero.StatusSystem.EAccountStatus a, ref int __result)
    {
        ModActions.ApplyFastChestAccountStatus(a, ref __result);
    }
}

[HarmonyPatch]
internal static class TrainerFastChestChanceResultPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var normal = AccessTools.Method(typeof(global::yw), "kow", new[] { typeof(float) });
        if (normal != null)
        {
            Plugin.FileLog("Fast chest chance patch target: " + normal.FullDescription());
            yield return normal;
        }
        else
        {
            Plugin.FileLog("Fast chest chance patch target yw.kow not found.");
        }

        var boss = AccessTools.Method(typeof(global::yw), "kox", new[] { typeof(float) });
        if (boss != null)
        {
            Plugin.FileLog("Fast chest chance patch target: " + boss.FullDescription());
            yield return boss;
        }
        else
        {
            Plugin.FileLog("Fast chest chance patch target yw.kox not found.");
        }
    }

    private static void Postfix(MethodBase __originalMethod, ref float __result)
    {
        ModActions.ApplyFastChestChanceResult(ref __result, __originalMethod?.Name ?? "yw chance");
    }
}

internal static class TrainerFastChestBoxAmountPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        // These obfuscated EBoxType limit methods recurse through the IL2CPP
        // Harmony detour on Unity 6, causing a stack overflow during startup.
        yield break;
    }

    private static void Postfix(global::TaskbarHero.EBoxType a, ref int __result)
    {
        ModActions.ApplyFastChestMaxBoxAmount(a, ref __result);
    }
}

[HarmonyPatch(typeof(global::TaskbarHero.DLCManager), nameof(global::TaskbarHero.DLCManager.hbo))]
internal static class TrainerDlcOwnedPatch
{
    private static readonly object LogSync = new();
    private static readonly System.Collections.Generic.HashSet<uint> LoggedAppIds = new();

    private static void Postfix(uint a, ref bool __result)
    {
        if (Plugin.IsShuttingDown || !ModActions.ForceDlcOwnershipChecks)
        {
            return;
        }

        if (!__result && ShouldLog(a))
        {
            Plugin.FileLog($"DLC ownership bypass appId={a}");
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

[HarmonyPatch(typeof(global::vb), nameof(global::vb.bsol), MethodType.Getter)]
internal static class TrainerHeroUnlockedPatch
{
    private static readonly object LogSync = new();
    private static readonly System.Collections.Generic.HashSet<int> LoggedHeroKeys = new();

    private static void Postfix(global::vb __instance, ref bool __result)
    {
        if (Plugin.IsShuttingDown || !ModActions.ForceHeroUnlockChecks)
        {
            return;
        }

        int heroKey = TryHeroKey(__instance);
        if (!__result && ShouldLog(heroKey))
        {
            Plugin.FileLog($"Hero unlock bypass key={heroKey}");
        }

        __result = true;
    }

    private static int TryHeroKey(global::vb hero)
    {
        try
        {
            return hero?.bsok ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool ShouldLog(int heroKey)
    {
        lock (LogSync)
        {
            return LoggedHeroKeys.Add(heroKey);
        }
    }
}

[HarmonyPatch(typeof(global::vb), nameof(global::vb.bsoe), MethodType.Getter)]
internal static class TrainerHeroAvailablePatch
{
    private static void Postfix(ref bool __result)
    {
        if (!Plugin.IsShuttingDown && ModActions.ForceHeroUnlockChecks)
        {
            __result = true;
        }
    }
}

[HarmonyPatch(typeof(global::wz), "kai")]
internal static class TrainerServerPendingItemValidationPatch
{
    private static void Prefix()
    {
        if (!Plugin.IsShuttingDown)
        {
            ModActions.TryNormalizeGearCatalogForTrainer("Steam/server pending item validation");
        }
    }
}

[HarmonyPatch(typeof(global::wz), "kaj")]
internal static class TrainerLocalSteamItemValidationPatch
{
    private static void Prefix()
    {
        if (!Plugin.IsShuttingDown)
        {
            ModActions.TryNormalizeGearCatalogForTrainer("Steam/local item validation");
        }
    }
}

[HarmonyPatch(typeof(global::wz), "kak")]
internal static class TrainerSteamValidationWorkerPatch
{
    private static void Prefix()
    {
        if (!Plugin.IsShuttingDown)
        {
            ModActions.TryNormalizeGearCatalogForTrainer("Steam validation worker");
        }
    }
}

[HarmonyPatch(typeof(global::qi), "het")]
internal static class TrainerBackendInventoryRemovePatch
{
    private static bool Prefix(Il2CppSystem.Collections.Generic.List<ulong> a, global::TaskbarHero.UI.EMovePivotTarget b)
    {
        if (Plugin.IsShuttingDown)
        {
            return true;
        }

        return ModActions.AllowFilteredBackendInventoryRemoval(a, b, "qi.het backend inventory remove");
    }
}

[HarmonyPatch(typeof(global::ws), "jzh")]
internal static class TrainerServerDeletedItemCleanupPatch
{
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
