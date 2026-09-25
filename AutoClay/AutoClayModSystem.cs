using AutoClay.Client;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace AutoClay;

public sealed class AutoClayModSystem : ModSystem
{
    private Harmony? harmony;
    private ClayController? controller;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        var version = typeof(GameVersion).GetField(nameof(GameVersion.ShortGameVersion))?.GetRawConstantValue() as string;
        if (version != "1.22.7")
        {
            api.Logger.Error("Auto Clay supports Vintage Story 1.22.7. Found {0}; automation is disabled.", version);
            return;
        }
        var config = api.LoadModConfig<AutoClayConfig>("autoclay.json") ?? new AutoClayConfig();
        config.Validate();
        api.StoreModConfig(config, "autoclay.json");
        controller = new ClayController(api, config);
        ClayPatches.Initialize(api, controller);
        harmony = new Harmony("autoclay.client");
        harmony.PatchAll(typeof(AutoClayModSystem).Assembly);
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll("autoclay.client");
        controller?.Dispose();
        ClayPatches.Dispose();
        base.Dispose();
    }
}
