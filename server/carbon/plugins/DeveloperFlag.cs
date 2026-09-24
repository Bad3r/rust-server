using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Developer Flag", "local", "1.0.0")]
    [Description("Gives players with developerflag.use Rust's IsDeveloper player flag with every skin unlocked")]
    public class DeveloperFlag : RustPlugin
    {
        // BasePlayer.PlayerInit resets IsDeveloper from Facepunch's DeveloperList on every connect, before
        // OnPlayerConnected runs. For a developer, BasePlayer.GetSkinsAccessLevel returns the connection's
        // client.skins_access, which every skin and DLC ownership check reads first: 1 unlocks all, -1 locks all.
        // The client reports that convar on connect and through global.setinfo, and its cfg/client.cfg does not keep
        // it, so the flag turning on sets it to 1 here.
        private const string PermissionUse = "developerflag.use";
        private const string SkinsAccess = "client.skins_access";

        private void Init() => permission.RegisterPermission(PermissionUse, this);

        private void OnServerInitialized() => ApplyAll();

        private void OnPlayerConnected(BasePlayer player) => Apply(player);

        private void OnUserPermissionGranted(string id, string perm) => ApplyAll();

        private void OnUserPermissionRevoked(string id, string perm) => ApplyAll();

        private void OnGroupPermissionGranted(string group, string perm) => ApplyAll();

        private void OnGroupPermissionRevoked(string group, string perm) => ApplyAll();

        private void OnUserGroupAdded(string id, string group) => ApplyAll();

        private void OnUserGroupRemoved(string id, string group) => ApplyAll();

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
                player.SetPlayerFlag(BasePlayer.PlayerFlags.IsDeveloper, DeveloperList.IsDeveloper(player));
        }

        [ConsoleCommand("developerflag.status")]
        private void StatusCommand(ConsoleSystem.Arg arg)
        {
            var caller = arg.Player();
            if (caller != null && !caller.IsAdmin && !caller.IsDeveloper)
                return;

            var lines = new List<string>();
            foreach (var player in BasePlayer.activePlayerList)
                lines.Add($"{player.displayName} [{player.UserIDString}] IsDeveloper={player.IsDeveloper} " +
                          $"{SkinsAccess}={player.GetInfoInt(SkinsAccess, 0)} " +
                          $"effective={player.GetSkinsAccessLevel()}");
            arg.ReplyWith(lines.Count == 0 ? "no players connected" : string.Join("\n", lines));
        }

        private void ApplyAll()
        {
            foreach (var player in BasePlayer.activePlayerList)
                Apply(player);
        }

        private void Apply(BasePlayer player)
        {
            var developer = DeveloperList.IsDeveloper(player) ||
                            permission.UserHasPermission(player.UserIDString, PermissionUse);
            if (developer && !player.IsDeveloper)
                player.SetInfo(SkinsAccess, "1");
            player.SetPlayerFlag(BasePlayer.PlayerFlags.IsDeveloper, developer);
        }
    }
}
