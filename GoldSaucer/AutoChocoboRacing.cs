using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Windows.Forms;
using static FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Delegates;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoChocoboRacing : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = GetLoc("AutoChocoboRacingTitle"),
        Description = GetLoc("AutoChocoboRacingDescription"),
        Category = ModuleCategories.GoldSaucer,
        Author = ["Bill"],
        ModulesPrerequisite = ["AutoCommenceDuty"]
    };

    private enum RaceRoute : ushort
    {
        Sagolii = 18,  // 荒野大道
        Costa = 19,    // 太阳海岸
        Tranquil = 20, // 恬静小路
        Random = 21,   // 随机赛道
    }

    private static Config ModuleConfig = null!;

    private static ContentsFinderOption ContentsFinderOption = ContentsFinderHelper.DefaultOption.Clone();

    private static byte Rank => RaceChocoboManager.Instance()->Rank;
    private static byte AbilityHereditary => RaceChocoboManager.Instance()->AbilityHereditary;
    private static byte AbilityLearned => RaceChocoboManager.Instance()->AbilityLearned;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RaceChocoboResult", OnRaceResult);
        DService.Condition.ConditionChange += OnConditionChanged;
    }

    protected override void ConfigUI()
    {
        ImGui.Text(GetLoc($"AutoChocoboRacing-Rank{Rank}"));
        ImGui.Text(GetLoc($"AutoChocoboRacing-AbilityHereditary{AbilityHereditary}"));
        ImGui.Text(GetLoc($"AutoChocoboRacing-AbilityLearned{AbilityLearned}"));

        ImGui.NewLine();

        ImGui.Text(GetLoc("AutoChocoboRacing-RouteSelection"));
        var currentRoute = (RaceRoute)ModuleConfig.Route;
        using (var combo = ImRaii.Combo("##RouteSelection", LuminaWrapper.GetContentRouletteName((ushort)currentRoute)))
        {
            if (combo)
            {
                foreach (var route in Enum.GetValues<RaceRoute>())
                {
                    if (ImGui.Selectable(LuminaWrapper.GetContentRouletteName((ushort)route), currentRoute == route))
                    {
                        ModuleConfig.Route = (ushort)route;
                        SaveConfig(ModuleConfig);
                    }
                }
            }
        }

        if (ImGui.Checkbox(GetLoc("AutoChocoboRacing-AutoExit"), ref ModuleConfig.AutoExit))
            SaveConfig(ModuleConfig);
        if (ImGui.Checkbox(GetLoc("AutoChocoboRacing-StopAtMaxRank"), ref ModuleConfig.StopAtMaxRank))
            SaveConfig(ModuleConfig);
        if (ImGui.Checkbox(GetLoc("AutoChocoboRacing-UseAbility"), ref ModuleConfig.UseAbility))
            SaveConfig(ModuleConfig);
        if (ImGui.Checkbox(GetLoc("AutoChocoboRacing-AlwaysRun"), ref ModuleConfig.AlwaysRun))
            SaveConfig(ModuleConfig);
        if (ImGui.Checkbox(GetLoc("AutoChocoboRacing-OptimisedRace"), ref ModuleConfig.OptimisedRace))
            SaveConfig(ModuleConfig);

        ImGui.NewLine();

        if (ImGui.Button(GetLoc(ModuleConfig.IsEnabled ? "Stop" : "Start")))
        {
            ModuleConfig.IsEnabled ^= true;
            SaveConfig(ModuleConfig);

            if (ModuleConfig.IsEnabled)
                RequestDuty();
            if (!ModuleConfig.IsEnabled && DService.Condition[ConditionFlag.InDutyQueue])
                CancelDutyApply();
            if (!ModuleConfig.IsEnabled && DService.Condition[ConditionFlag.ChocoboRacing])
            {
                FrameworkManager.Unregister(OnUpdate);

                SetMoving(false);
                SlowDown(false);
            }
        }
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.ChocoboRacing ||
            !ModuleConfig.IsEnabled) return;

        if (value)
            FrameworkManager.Register(OnUpdate, throttleMS: 1500);
        else
        {
            FrameworkManager.Unregister(OnUpdate);

            SetMoving(false);
            SlowDown(false);

            if (ModuleConfig.StopAtMaxRank && 
                !ModuleConfig.OptimisedRace &&
                Rank >= 40)
            {
                ModuleConfig.IsEnabled = false;
                SaveConfig(ModuleConfig);
                Chat(GetLoc("AutoChocoboRacing-FinishLeveling"));
                return;
            }

            RequestDuty();
        }
    }

    private void OnUpdate(IFramework _)
    {
        if (!ModuleConfig.IsEnabled || !IsScreenReady()) return;

        if (ModuleConfig.OptimisedRace)
            OptimisedRacing(RaceChocoboParameter);
        else
            HandleRacing(RaceChocoboParameter);
    }

    private static void RequestDuty()
    {
        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.RequestGSChocobo, 0, 0, 0, 0);
        
        if (!DService.Condition.Any(ConditionFlag.WaitingForDuty, ConditionFlag.InDutyQueue) &&
            Throttler.Throttle("AutoChocoboRacing-RequestDuty", 1500))
        
            RequestDutyRoulette(ModuleConfig.OptimisedRace ? 
                                    (ushort)18 : ModuleConfig.Route, ContentsFinderOption);
        
    }

    private void HandleRacing(AtkUnitBase* raceChocoboParameter)
    {
        var lathered = raceChocoboParameter->GetImageNodeById(3)->IsVisible();
        var stamina = raceChocoboParameter->GetNodeById(5)->GetAsAtkCounterNode()->NodeText.ToString();
        var hasStamina = !string.Equals(stamina, "0.00%");

        SetMoving(ModuleConfig.AlwaysRun || (!lathered && hasStamina));
        SlowDown(!ModuleConfig.AlwaysRun && lathered);

        // 使用技能，但是不一定更优
        if (ModuleConfig.UseAbility)
        {
            if (UseActionManager.IsActionOffCooldown(ActionType.ChocoboRaceAbility, AbilityLearned))
                UseActionManager.UseAction(ActionType.ChocoboRaceAbility, AbilityLearned);
            if (UseActionManager.IsActionOffCooldown(ActionType.ChocoboRaceAbility, AbilityHereditary))
                UseActionManager.UseAction(ActionType.ChocoboRaceAbility, AbilityHereditary);
        }
    }

    private void OptimisedRacing(AtkUnitBase* raceChocoboParameter)
    {
        if (AbilityHereditary != 58 && // 超级冲刺
            AbilityLearned != 30 &&    // 体力消耗降低III
            Rank != 50)        //满级
        {
            Chat(GetLoc("AutoChocoboRacing-NotOptimised"));
            ModuleConfig.OptimisedRace = false;
            SaveConfig(ModuleConfig);

            HandleRacing(raceChocoboParameter);
            return;
        }

        SendKeypressLongPressAsync(Keys.A, 3500);

        if (UseActionManager.IsActionOffCooldown(ActionType.ChocoboRaceAbility, AbilityHereditary))
            UseActionManager.UseAction(ActionType.ChocoboRaceAbility, AbilityHereditary);
    }

    private void SetMoving(bool value)
    {
        if (value) 
            SendKeyDown(Keys.W);
        else 
            SendKeyUp(Keys.W);
    }

    private void SlowDown(bool value)
    {
        if (value)
            SendKeyDown(Keys.S);
        else
            SendKeyUp(Keys.S);
    }

    private void OnRaceResult(AddonEvent type, AddonArgs args)
    {
        if (!ModuleConfig.AutoExit) return;

        var addon = RaceChocoboResult;
        if (!IsAddonAndNodesReady(addon)) return;

        Callback(addon, true, 1);
    }

    protected override void Uninit()
    {
        if (DService.Condition[ConditionFlag.ChocoboRacing])
        {
            SetMoving(false);
            SlowDown(false);
        }

        ModuleConfig.IsEnabled = false;
        SaveConfig(ModuleConfig);

        FrameworkManager.Unregister(OnUpdate);
        DService.AddonLifecycle.UnregisterListener(OnRaceResult);
        DService.Condition.ConditionChange -= OnConditionChanged;
    }

    private class Config : ModuleConfiguration
    {
        public bool IsEnabled;
        public bool AutoExit = true;
        public bool AlwaysRun = true;
        public bool UseAbility;
        public bool StopAtMaxRank = true;
        public bool OptimisedRace;

        public ushort Route = 19;
    }
}
