using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Convars;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;

namespace Warden;

public partial class Warden : BasePlugin
{
    private int? wardenUserId = null;
    private System.Threading.CancellationTokenSource? incentiveTimerToken = null;
    private IConVar<bool>? wardenEnabledConvar;
    private bool wasPluginEnabled = true;
    private Color? savedWardenColor = null; // Guardar el color original del warden

    private enum LogLevel { Debug, Info, Warning, Error }

    public Warden(ISwiftlyCore core) : base(core)
    {
    }

    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager)
    {
    }

    public override void UseSharedInterface(IInterfaceManager interfaceManager)
    {
    }

    public override void Load(bool hotReload)
    {
        // Use CreateOrFind to handle both initial load and hot reload scenarios
        wardenEnabledConvar = Core.ConVar.CreateOrFind<bool>("warden_enabled", "Enable/Disable Warden plugin", false);
        Log($"Warden plugin loaded. warden_enabled = {wardenEnabledConvar.Value}", LogLevel.Info);

        // Register Commands
        Core.Command.RegisterCommand("w", OnWarden);
        Core.Command.RegisterCommand("uw", OnUnwarden);
        Core.Command.RegisterCommand("rw", OnRemoveWarden, false, "warden.command.remove");
        Core.Command.RegisterCommand("sw", OnSetWarden, false, "warden.command.set");

        // Register Event Hooks
        Core.GameEvent.HookPre<EventRoundStart>(OnRoundStart);
        Core.GameEvent.HookPre<EventRoundEnd>(OnRoundEnd);
        Core.GameEvent.HookPre<EventPlayerDeath>(OnPlayerDeath);
        Core.GameEvent.HookPre<EventPlayerDisconnect>(OnPlayerDisconnect);
        Core.GameEvent.HookPre<EventPlayerTeam>(OnPlayerTeam);

        // Reset warden state on plugin load
        wardenUserId = null;
    }

    public override void Unload()
    {
        wardenUserId = null;
        incentiveTimerToken?.Cancel();
        incentiveTimerToken = null;
    }

    private bool IsPluginEnabled()
    {
        bool isEnabled = wardenEnabledConvar?.Value ?? false;

        // Detectar si el plugin se acaba de desactivar
        if (wasPluginEnabled && !isEnabled)
        {
            Log("Plugin disabled via warden_enabled, cleaning up state...", LogLevel.Info);
            CleanupPluginState();
        }

        wasPluginEnabled = isEnabled;
        return isEnabled;
    }

    private void CleanupPluginState()
    {
        wardenUserId = null;
        incentiveTimerToken?.Cancel();
        incentiveTimerToken = null;
        savedWardenColor = null;
    }

    // Helper Methods
    /// <summary>
    /// Logs a message to console with log level prefix
    /// </summary>
    private void Log(string message, LogLevel level = LogLevel.Debug)
    {
        switch (level)
        {
            case LogLevel.Debug:
                Core.Logger.LogDebug($"[Warden:{level}] {message}");
                break;
            case LogLevel.Info:
                Core.Logger.LogInformation($"[Warden:{level}] {message}");
                break;
            case LogLevel.Warning:
                Core.Logger.LogWarning($"[Warden:{level}] {message}");
                break;
            case LogLevel.Error:
                Core.Logger.LogError($"[Warden:{level}] {message}");
                break;
        }
    }

    private void AnnounceWardenChange(string translationKey, params object[] args)
    {
        foreach (var player in Core.PlayerManager.GetAllPlayers())
        {
            if (player == null || !player.IsValid) continue;
            var localizer = Core.Translation.GetPlayerLocalizer(player);
            string message = localizer[translationKey, args];
            player.SendCenterHTML(message, 5000);
        }
    }

    private string? GetWardenName()
    {
        if (wardenUserId == null) return null;
        var warden = Core.PlayerManager.GetAllPlayers()
            .FirstOrDefault(p => p != null && p.IsValid && p.PlayerID == wardenUserId);
        return warden?.Controller.PlayerName;
    }

    private void SetWardenColor(int playerId, bool isWarden)
    {
        Core.Scheduler.NextTick(() =>
        {
            var player = Core.PlayerManager.GetPlayer(playerId);
            if (player == null || !player.IsValid) return;

            var pawn = player.PlayerPawn;
            if (pawn == null || !pawn.IsValid) return;

            var currentColor = pawn.Render;
            if (isWarden)
            {
                // Guardar el color actual antes de cambiar a azul
                savedWardenColor = currentColor;

                // Cambiar solo a azul, manteniendo el alpha actual
                pawn.Render = new Color((byte)0, (byte)0, (byte)255, currentColor.A);
            }
            else
            {
                // Restaurar el color original guardado si existe
                if (savedWardenColor.HasValue)
                {
                    pawn.Render = savedWardenColor.Value;
                    savedWardenColor = null;
                }
                else
                {
                    // Fallback: restaurar a blanco si no hay color guardado
                    pawn.Render = new Color((byte)255, (byte)255, (byte)255, currentColor.A);
                }
            }
            pawn.RenderUpdated();
        });
    }

    // Commands
    public void OnWarden(ICommandContext context)
    {
        if (!IsPluginEnabled()) return;
        if (context.Sender == null) return;
        var localizer = Core.Translation.GetPlayerLocalizer(context.Sender);

        if (wardenUserId != null)
        {
            // If non-CT, show current warden
            if (context.Sender.Controller.TeamNum != (byte)Team.CT)
            {
                var wardenName = GetWardenName();
                if (wardenName != null)
                {
                    context.Reply(localizer["warden.info.current", wardenName]);
                }
                else
                {
                    context.Reply(localizer["warden.error.active"]);
                }
            }
            else
            {
                context.Reply(localizer["warden.error.active"]);
            }
            return;
        }

        if (context.Sender.Controller.TeamNum != (byte)Team.CT)
        {
            context.Reply(localizer["warden.error.team_ct"]);
            return;
        }

        wardenUserId = context.Sender.PlayerID;
        context.Reply(localizer["warden.success.become"]);

        // Cancel incentive timer since we now have a warden
        incentiveTimerToken?.Cancel();
        incentiveTimerToken = null;

        // Pintar al warden de azul
        SetWardenColor(context.Sender.PlayerID, true);

        AnnounceWardenChange("warden.log.become", context.Sender.Controller.PlayerName);
        Log($"Player {context.Sender.Controller.PlayerName} became warden.", LogLevel.Info);
    }

    public void OnUnwarden(ICommandContext context)
    {
        if (!IsPluginEnabled()) return;
        if (context.Sender == null) return;
        var localizer = Core.Translation.GetPlayerLocalizer(context.Sender);

        if (wardenUserId != context.Sender.PlayerID)
        {
            context.Reply(localizer["warden.error.not_warden"]);
            return;
        }

        var playerName = context.Sender.Controller.PlayerName;
        var playerId = context.Sender.PlayerID;

        // Restaurar color antes de quitar el rol
        SetWardenColor(playerId, false);

        wardenUserId = null;
        context.Reply(localizer["warden.success.unwarden"]);
        AnnounceWardenChange("warden.log.unwarden", playerName);
        Log($"Player {playerName} is no longer warden.", LogLevel.Info);
    }

    public void OnRemoveWarden(ICommandContext context)
    {
        if (!IsPluginEnabled()) return;
        if (context.Sender != null)
        {
            var localizer = Core.Translation.GetPlayerLocalizer(context.Sender);
            if (!Core.Permission.PlayerHasPermission(context.Sender.SteamID, "warden.command.remove"))
            {
                context.Reply(localizer["command.error.permission"]);
                return;
            }
        }

        string adminName = context.Sender != null ? context.Sender.Controller.PlayerName : "Console";

        // Restaurar color del warden anterior si existe
        if (wardenUserId != null)
        {
            SetWardenColor(wardenUserId.Value, false);
        }

        wardenUserId = null;

        if (context.Sender != null)
        {
            var localizer = Core.Translation.GetPlayerLocalizer(context.Sender);
            context.Reply(localizer["warden.admin.removed", adminName]);
        }
        else
        {
            context.Reply($"Admin {adminName} removed the warden.");
        }
        AnnounceWardenChange("warden.admin.removed", adminName);
        Log($"Admin {adminName} removed the warden.", LogLevel.Info);
    }

    public void OnSetWarden(ICommandContext context)
    {
        if (!IsPluginEnabled()) return;
        if (context.Sender != null)
        {
            var localizer = Core.Translation.GetPlayerLocalizer(context.Sender);
            if (!Core.Permission.PlayerHasPermission(context.Sender.SteamID, "warden.command.set"))
            {
                context.Reply(localizer["command.error.permission"]);
                return;
            }
        }

        if (context.Args.Length == 0)
        {
            if (context.Sender != null)
            {
                var localizer = Core.Translation.GetPlayerLocalizer(context.Sender);
                context.Reply(localizer["warden.command.usage.sw"]);
            }
            else
            {
                context.Reply("Usage: !sw <player>");
            }
            return;
        }

        var targetName = context.Args[0];
        // Simple search by name substring (first match) - keeping it simple for now
        var target = Core.PlayerManager.GetAllPlayers()
            .FirstOrDefault(p => p.Controller.PlayerName.Contains(targetName, StringComparison.OrdinalIgnoreCase));

        if (target == null)
        {
            if (context.Sender != null)
            {
                var localizer = Core.Translation.GetPlayerLocalizer(context.Sender);
                context.Reply(localizer["command.error.player_not_found", targetName]);
            }
            else
            {
                context.Reply($"Player '{targetName}' not found.");
            }
            return;
        }

        if (target.Controller.TeamNum != (byte)Team.CT)
        {
            if (context.Sender != null)
            {
                var localizer = Core.Translation.GetPlayerLocalizer(context.Sender);
                context.Reply(localizer["warden.error.target_not_ct", target.Controller.PlayerName]);
            }
            else
            {
                context.Reply($"{target.Controller.PlayerName} is not on the CT team.");
            }
            return;
        }

        string adminName = context.Sender != null ? context.Sender.Controller.PlayerName : "Console";

        // Restaurar color del warden anterior si existe
        if (wardenUserId != null)
        {
            SetWardenColor(wardenUserId.Value, false);
        }

        wardenUserId = target.PlayerID;

        // Cancel incentive timer since we now have a warden
        incentiveTimerToken?.Cancel();
        incentiveTimerToken = null;

        // Pintar al nuevo warden de azul
        SetWardenColor(target.PlayerID, true);

        if (context.Sender != null)
        {
            var localizer = Core.Translation.GetPlayerLocalizer(context.Sender);
            context.Reply(localizer["warden.admin.set", adminName, target.Controller.PlayerName]);
        }
        else
        {
            context.Reply($"Admin {adminName} set {target.Controller.PlayerName} as warden.");
        }
        AnnounceWardenChange("warden.admin.set", adminName, target.Controller.PlayerName);
        Log($"Admin {adminName} set {target.Controller.PlayerName} as warden.", LogLevel.Info);
    }

    // Hooks
    [ClientCommandHookHandler]
    public HookResult OnClientCommand(int playerId, string command)
    {
        if (!IsPluginEnabled()) return HookResult.Continue;
        if (command.StartsWith("jointeam 3")) // Attempting to join CT
        {
            // Calculate ratio O(N)
            int tCount = 0;
            int ctCount = 0;

            foreach (var p in Core.PlayerManager.GetAllPlayers())
            {
                if (p == null || !p.IsValid) continue; // Basic validation
                if (p.Controller.TeamNum == (byte)Team.T) tCount++;
                else if (p.Controller.TeamNum == (byte)Team.CT) ctCount++;
            }

            // Formula: Always allow at least 1 CT. Then 1 CT per 2 Ts.
            int allowedCts = Math.Max(1, tCount / 2);

            if (ctCount >= allowedCts)
            {
                var player = Core.PlayerManager.GetPlayer(playerId);
                if (player != null)
                {
                    var localizer = Core.Translation.GetPlayerLocalizer(player);
                    player.SendCenterHTML(localizer["warden.error.ratio_full"], 5000);
                }
                return HookResult.Stop;
            }
        }

        return HookResult.Continue;
    }

    // Events
    public HookResult OnRoundStart(EventRoundStart @event)
    {
        if (!IsPluginEnabled()) return HookResult.Continue;
        // Cancel any previous timer
        incentiveTimerToken?.Cancel();

        // Start 5-second timer to check for warden
        incentiveTimerToken = Core.Scheduler.DelayBySeconds(5f, () =>
        {
            if (wardenUserId == null)
            {
                foreach (var player in Core.PlayerManager.GetAllPlayers())
                {
                    if (player == null || !player.IsValid) continue;
                    var localizer = Core.Translation.GetPlayerLocalizer(player);
                    player.SendChat(localizer["warden.incentive.none"]);
                }
            }
            incentiveTimerToken = null;
        });
        return HookResult.Continue;
    }

    public HookResult OnRoundEnd(EventRoundEnd @event)
    {
        if (!IsPluginEnabled()) return HookResult.Continue;

        // Restaurar color del warden al final de la ronda
        if (wardenUserId != null)
        {
            SetWardenColor(wardenUserId.Value, false);
        }

        wardenUserId = null;
        incentiveTimerToken?.Cancel();
        incentiveTimerToken = null;
        return HookResult.Continue;
    }

    public HookResult OnPlayerDeath(EventPlayerDeath @event)
    {
        if (!IsPluginEnabled()) return HookResult.Continue;
        var victimId = @event.UserId;
        if (victimId == wardenUserId)
        {
            var wardenName = GetWardenName();

            // Restaurar color
            SetWardenColor(victimId, false);

            wardenUserId = null;
            if (wardenName != null)
            {
                AnnounceWardenChange("warden.log.died", wardenName);
            }
            Log("Warden died.", LogLevel.Info);
        }
        return HookResult.Continue;
    }

    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event)
    {
        if (!IsPluginEnabled()) return HookResult.Continue;
        var playerId = @event.UserId;
        if (playerId == wardenUserId)
        {
            var wardenName = GetWardenName();

            // Restaurar color
            SetWardenColor(playerId, false);

            wardenUserId = null;
            if (wardenName != null)
            {
                AnnounceWardenChange("warden.log.disconnected", wardenName);
            }
            Log("Warden disconnected.", LogLevel.Info);
        }
        return HookResult.Continue;
    }

    public HookResult OnPlayerTeam(EventPlayerTeam @event)
    {
        if (!IsPluginEnabled()) return HookResult.Continue;
        var playerId = @event.UserId;
        if (playerId == wardenUserId)
        {
            // Check if they left CT
            if (@event.Team != (byte)Team.CT)
            {
                var wardenName = GetWardenName();

                // Restaurar color
                SetWardenColor(playerId, false);

                wardenUserId = null;
                if (wardenName != null)
                {
                    AnnounceWardenChange("warden.log.team_change", wardenName);
                }
                Log("Warden changed team.", LogLevel.Info);
            }
        }
        return HookResult.Continue;
    }
}
