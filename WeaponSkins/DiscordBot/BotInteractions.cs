using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using WeaponSkinsBot.Database;

namespace WeaponSkinsBot;

public sealed partial class BotApp
{
    private Task DispatchReady() => dispatcher.Dispatch(OnReady);
    private Task DispatchJoined(SocketGuild guild) => dispatcher.Dispatch(() => OnJoinedGuild(guild));
    private Task DispatchLeft(SocketGuild guild) => dispatcher.Dispatch(() => OnLeftGuild(guild));
    private Task DispatchMessage(SocketMessage message) => message.Author.IsBot || !message.Content.StartsWith('!')
        ? Task.CompletedTask : dispatcher.Dispatch(() => OnMessage(message));
    private Task DispatchSlash(SocketSlashCommand command) => DispatchInteraction(command, () => OnSlash(command));
    private Task DispatchButton(SocketMessageComponent component) => DispatchInteraction(component, () => OnButton(component));
    private Task DispatchSelect(SocketMessageComponent component) => DispatchInteraction(component, () => picker.HandleSelectAsync(component));
    private Task DispatchModal(SocketModal modal) => DispatchInteraction(modal, () => OnModal(modal));
    private Task DispatchAutocomplete(SocketAutocompleteInteraction interaction) =>
        dispatcher.Dispatch(() => OnAutocomplete(interaction), () => interaction.RespondAsync(Array.Empty<AutocompleteResult>()));

    private Task DispatchInteraction(SocketInteraction interaction, Func<Task> action) => dispatcher.Dispatch(async () =>
    {
        if (interaction.GuildId == null || !IsActiveGuild(interaction.GuildId.Value))
        {
            await interaction.RespondAsync("Use this command in the Discord server assigned to WeaponSkins.", ephemeral: true);
            return;
        }
        try { await action(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "WeaponSkinsBOT interaction failed");
            var message = ex is InvalidOperationException or ArgumentException ? Safe(ex.Message) : "Unable to complete this request. Please try again.";
            if (interaction.HasResponded) await interaction.FollowupAsync(message, ephemeral: true, allowedMentions: AllowedMentions.None);
            else await interaction.RespondAsync(message, ephemeral: true, allowedMentions: AllowedMentions.None);
        }
    }, () => interaction.RespondAsync("WeaponSkins is busy. Please try again in a moment.", ephemeral: true));

    private OwnedWeaponCache ownedWeapons = null!;
    private static string SearchKey(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private async Task OnAutocomplete(SocketAutocompleteInteraction interaction)
    {
        if (interaction.GuildId == null || !IsActiveGuild(interaction.GuildId.Value))
        {
            await interaction.RespondAsync(Array.Empty<AutocompleteResult>()); return;
        }
        AutocompleteResult[] choices;
        try
        {
            var owned = await ownedWeapons.GetAsync(interaction.User.Id, TimeSpan.FromSeconds(2));
            var query = SearchKey(Convert.ToString(interaction.Data.Current.Value) ?? "");
            var kind = interaction.Data.CommandName;
            choices = (owned?.Defs ?? []).Where(def =>
                    (kind != "stickers" || !catalog.Knives.Any(x => x.DefIndex == def)) &&
                    (kind is "wear" or "seed" || !catalog.Gloves.Any(x => x.DefIndex == def)))
                .Where(def => SearchKey(catalog.WeaponName(def)).Contains(query) || def.ToString() == query)
                .OrderBy(catalog.WeaponName).Take(25)
                .Select(def => new AutocompleteResult(catalog.WeaponName(def), catalog.WeaponName(def))).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "WeaponSkinsBOT could not load weapon suggestions");
            choices = [];
        }
        await interaction.RespondAsync(choices);
    }

    private void OnLoadoutChanged(ulong steamId)
    {
        ownedWeapons.Invalidate(steamId);
        loadoutChanged(steamId);
    }
}
