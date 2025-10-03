using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Generic;
using System.Windows.Forms;

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

    public static readonly Dictionary<ushort, string> Routes = new()
    {
        { 18, LuminaWrapper.GetContentRouletteName(18) }, // 荒野大道
        { 19, LuminaWrapper.GetContentRouletteName(19) }, // 太阳海岸
        { 20, LuminaWrapper.GetContentRouletteName(20) }, // 恬静小路
        { 21, LuminaWrapper.GetContentRouletteName(21) }  // 随机赛道
    };

    private static Config ModuleConfig = null!;

    private static readonly ContentsFinderOption ContentsFinderOption = DefaultOption.Clone();

    private static byte Rank => RaceChocoboManager.Instance()->Rank;
    private static byte AbilityHereditary => RaceChocoboManager.Instance()->AbilityHereditary;
    private static byte AbilityLearned => RaceChocoboManager.Instance()->AbilityLearned;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        
        DService.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RaceChocoboResult", OnRaceResult);
        DService.ClientState.Login += OnLogin;
        DService.Condition.ConditionChange += OnConditionChanged;
    }

    protected override void ConfigUI()
    {
        var notHereditaryOptimised = AbilityHereditary != 58; // 超级冲刺
        var notLearnedOptimised = AbilityLearned != 30;     // 体力消耗降低III
        var notRankMax = Rank != 50;                           //满级
        if (notHereditaryOptimised || notLearnedOptimised || notRankMax)
        {
            ImGui.Text(GetLoc("AutoChocoboRacing-OptimisedRacingWarning"));

            var unmet = new List<string>();
            if (notHereditaryOptimised)
                unmet.Add(GetLoc($"AutoChocoboRacing-NeedHereditary{LuminaWrapper.GetChocoboRaceAbilityName(58)}"));
            if (notLearnedOptimised)
                unmet.Add(GetLoc($"AutoChocoboRacing-NeedLearned{LuminaWrapper.GetChocoboRaceAbilityName(30)}"));
            if (notRankMax)
                unmet.Add(GetLoc("AutoChocoboRacing-NeedMaxRank"));
            
            ImGui.TextColored(KnownColor.Red.ToVector4(), string.Join(", ", unmet));
        }
        else
        {
            if (ImGui.Checkbox(GetLoc("AutoChocoboRacing-OptimisedRacing"), ref ModuleConfig.OptimisedRacing))
                SaveConfig(ModuleConfig);
        }

        ImGui.NewLine();

        ImGui.Text(GetLoc("AutoChocoboRacing-RouteSelection"));
        using (var combo = ImRaii.Combo("##RouteSelection", 
                                        LuminaWrapper.GetContentRouletteName(ModuleConfig.Route)))
        {
            if (combo)
            {
                foreach (var route in Routes)
                {
                    if (ImGui.Selectable(route.Value, ModuleConfig.Route == route.Key))
                    {
                        ModuleConfig.Route = route.Key;
                        SaveConfig(ModuleConfig);
                    }
                }
            }
        }

        if (ImGui.Checkbox(GetLoc("AutoChocoboRacing-AutoExit"), ref ModuleConfig.AutoExit))
            SaveConfig(ModuleConfig);
        if (ImGui.Checkbox(GetLoc("AutoChocoboRacing-StopAtMaxRank"), ref ModuleConfig.StopAtRetireRank))
            SaveConfig(ModuleConfig);
        if (ImGui.Checkbox(GetLoc("AutoChocoboRacing-AlwaysRun"), ref ModuleConfig.AlwaysRun))
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

    private void OnLogin() => 
        ExecuteCommandManager.ExecuteCommand(ExecuteCommandFlag.RequestGSChocobo);

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.ChocoboRacing ||
            !ModuleConfig.IsEnabled) return;

        if (value)
        {
            FrameworkManager.Unregister(OnUpdate);
            FrameworkManager.Register(OnUpdate, throttleMS: 1500);
        }
        else
        {
            FrameworkManager.Unregister(OnUpdate);

            SetMoving(false);
            SlowDown(false);

            if (ModuleConfig.StopAtRetireRank && 
                !ModuleConfig.OptimisedRacing &&
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

        if (ModuleConfig.OptimisedRacing)
            OptimisedRacing();
        else
            HandleRacing(RaceChocoboParameter);
    }

    private static void RequestDuty()
    {
        if (!DService.Condition.Any(ConditionFlag.WaitingForDuty, ConditionFlag.InDutyQueue) &&
            Throttler.Throttle("AutoChocoboRacing-RequestDuty", 1500))
        
            // 毕业只跑荒野大道
            RequestDutyRoulette(ModuleConfig.OptimisedRacing ? 
                                    (ushort)18 : ModuleConfig.Route, ContentsFinderOption); 
    }

    private void HandleRacing(AtkUnitBase* raceChocoboParameter)
    {
        var lathered = raceChocoboParameter->GetImageNodeById(3)->IsVisible();
        var stamina = raceChocoboParameter->GetNodeById(5)->GetAsAtkCounterNode()->NodeText.ToString();
        var hasStamina = !string.Equals(stamina, "0.00%");

        SetMoving(ModuleConfig.AlwaysRun || (!lathered && hasStamina));
        SlowDown(!ModuleConfig.AlwaysRun && lathered);
    }

    private void OptimisedRacing()
    {
        SendKeypressLongPressAsync(Keys.A, 5000);
        
        var localPlayer = DService.ObjectTable.LocalPlayer;
        if (!localPlayer.StatusList.HasStatus(1058))
            UseActionManager.UseAction(ActionType.ChocoboRaceAbility, 58);
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
        DService.ClientState.Login -= OnLogin;
        DService.Condition.ConditionChange -= OnConditionChanged;
    }

    private class Config : ModuleConfiguration
    {
        public bool IsEnabled;
        public bool AutoExit = true;
        public bool AlwaysRun = true;
        public bool StopAtRetireRank = true;
        public bool OptimisedRacing;

        public ushort Route = 19;
    }
}
