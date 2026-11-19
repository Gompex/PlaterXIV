using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using KamiToolKit;
using PlaterXIV.Debug;
using PlaterXIV.Game;
using PlaterXIV.Render;
using PlaterXIV.UI;
using PlaterXIV.Util;

namespace PlaterXIV;

public sealed class Plugin : IAsyncDalamudPlugin
{
    /// <summary>Changes with every build zip. Checked by Select-String after extracting.</summary>
    public const string BuildTag = "PLX-101-7f3c";

    private const string CommandName = "/plater";

    private Configuration config = null!;
    private PerfMeter perf = null!;
    private NameCache names = null!;
    private PlateScanner? scanner;
    private PlateRenderer? renderer;
    private NativePlateHider? hider;
    private AggroResolver? aggro;
    private WindowSystem? windows;
    private DebugWindow? debugWindow;
    private SettingsWindow? settingsWindow;
    private bool kamiInitialized;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        // Constructor runs off the framework thread: only service injection here.
        pluginInterface.Create<Services>();
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        config = Services.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize();
        perf = new PerfMeter(config);
        names = new NameCache();

        await KamiToolKitLibrary.InitializeAsync(Services.PluginInterface);
        kamiInitialized = true;
        cancellationToken.ThrowIfCancellationRequested();

        await Services.Framework.RunOnFrameworkThread(() =>
        {
            scanner = new PlateScanner(perf);
            hider = new NativePlateHider();
            aggro = new AggroResolver(names);
            renderer = new PlateRenderer(config, scanner, perf, hider, names, aggro);
            scanner.Enable();

            windows = new WindowSystem("PlaterXIV");
            debugWindow = new DebugWindow(scanner, names, config, renderer, hider, aggro) { IsOpen = config.OpenDebugOnLoad };
            windows.AddWindow(debugWindow);

            settingsWindow = new SettingsWindow(config, names, hider)
            {
                InternalName = "PlaterXIVSettings",
                Title = "Plater for FF14",
                Size = new Vector2(800f, 580f),
            };

            Services.PluginInterface.UiBuilder.Draw += windows.Draw;
            Services.PluginInterface.UiBuilder.OpenMainUi += ToggleSettings;
            Services.PluginInterface.UiBuilder.OpenConfigUi += ToggleSettings;

            Services.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Plater for FF14 settings. /plater debug = data probe · /plater on|off · /plater native (game names)",
            });
        });

        Services.Log.Information($"Plater for FF14 loaded ({BuildTag})");
    }

    public async ValueTask DisposeAsync()
    {
        await Services.Framework.RunOnFrameworkThread(() =>
        {
            Services.CommandManager.RemoveHandler(CommandName);

            if (windows != null)
            {
                Services.PluginInterface.UiBuilder.Draw -= windows.Draw;
                Services.PluginInterface.UiBuilder.OpenMainUi -= ToggleSettings;
                Services.PluginInterface.UiBuilder.OpenConfigUi -= ToggleSettings;
                windows.RemoveAllWindows();
            }

            settingsWindow?.Dispose(); // closes it; its preview plates never load icons
            if (config.Dirty) config.Save();

            scanner?.Dispose(); // stop per-frame work first
            renderer?.Stop();   // hide our plates
            hider?.Dispose();   // give the game its own names back
        });

        // Never free icon nodes while a texture is still loading (that crashed before inside texture hooks).
        // Wait up to ~2 seconds of frames for every DoT icon to finish loading.
        var r = renderer;
        if (r != null)
        {
            for (var i = 0; i < 120; i++)
            {
                var busy = await Services.Framework.RunOnFrameworkThread(() => r.AnyIconLoading());
                if (!busy) break;
                await Services.Framework.DelayTicks(1);
            }

            await Services.Framework.RunOnFrameworkThread(() => r.Dispose());
        }

        if (kamiInitialized)
            await KamiToolKitLibrary.DisposeAsync();
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "settings";

        switch (verb)
        {
            case "settings":
            case "config":
                ToggleSettings();
                break;
            case "debug":
                ToggleDebug();
                break;
            case "on":
            case "off":
                config.Enabled = verb == "on";
                config.Save();
                hider?.Refresh();
                Services.Log.Information($"[Plater] plates {(config.Enabled ? "on" : "off")}");
                break;
            case "native":
                config.HideNativeEnemyPlates = !config.HideNativeEnemyPlates;
                config.Save();
                hider?.Refresh();
                Services.Log.Information($"[Plater] game enemy name/gauge {(config.HideNativeEnemyPlates ? "hidden" : "shown")}");
                break;
            case "pos" when parts.Length > 1 && int.TryParse(parts[1], out var source) && source is >= 0 and <= 2:
                config.PositionSource = (PositionSource)source;
                config.Save();
                Services.Log.Information($"[Plater] position source {config.PositionSource}");
                break;
            default:
                ToggleSettings();
                break;
        }
    }

    private void ToggleDebug() => debugWindow?.Toggle();

    // Native windows must be opened on the game's main thread; UI callbacks may come from elsewhere.
    private void ToggleSettings() => Services.Framework.RunOnFrameworkThread(() => settingsWindow?.Toggle());
}
