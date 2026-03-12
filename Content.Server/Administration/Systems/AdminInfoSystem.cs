// SPDX-FileCopyrightText: 2024 Conchelle <mary@thughunt.ing>
// SPDX-FileCopyrightText: 2024 Piras314 <p1r4s@proton.me>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 GoobBot <uristmchands@proton.me>
// SPDX-FileCopyrightText: 2025 SolsticeOfTheWinter <solsticeofthewinter@gmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Server.Discord;
using Content.Shared.Administration.Events;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Robust.Shared.Configuration;

namespace Content.Server.Administration.Systems;

public sealed class AdminInfoSystem : EntitySystem
{
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly IPlayerLocator _locator = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!; // Reserve edit
    [Dependency] private readonly DiscordWebhook _discord = default!; // Reserve edit

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<AdminInfoEvent>(OnAdminInfoEvent);
    }

    private async void OnAdminInfoEvent(AdminInfoEvent ev, EntitySessionEventArgs eventArgs)
    {
        var name = eventArgs.SenderSession.Name;
        if (ev.user == eventArgs.SenderSession.UserId)
            return;

        // Try to get original account for this session
        var main = await _locator.LookupIdAsync(ev.user);

        // We don't have a player like that, ignore.
        if (main == null)
            return;

        _adminLog.Add(LogType.AdminMessage, LogImpact.High, $"{name} is attempting to connect with a userid from {main.Username}");
        _chatManager.SendAdminAlert($"{name} is attempting to connect with a userid from {main.Username}");

        // Reserve edit begin
        var webhookUrl = _cfg.GetCVar(CCVars.DiscordAdminchatWebhook);
        if (!string.IsNullOrEmpty(webhookUrl))
        {
            if (await _discord.GetWebhook(webhookUrl) is not { } webhookData)
                return;

            var payload = new WebhookPayload
            {
                Content = $"{name} is attempting to connect with a userid from {main.Username}",
            };
            var identifier = webhookData.ToIdentifier();
            await _discord.CreateMessage(identifier, payload);
        }
        // Reserve edit end
    }
}
