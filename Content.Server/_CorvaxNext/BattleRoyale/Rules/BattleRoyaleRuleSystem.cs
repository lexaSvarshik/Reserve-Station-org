// SPDX-FileCopyrightText: 2025 ReserveBot <211949879+ReserveBot@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Svarshik <96281939+lexaSvarshik@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using Content.Server.Antag;
using Content.Server.Chat.Managers;
using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Events;
using Content.Server.GameTicking.Rules;
using Content.Server.KillTracking;
using Content.Server.Mind;
//using Content.Server.Traits.Assorted; //Reserve port BattleRoyale
//using Content.Server._CorvaxNext.Traits.Assorted; //Reserve port BattleRoyale
using Content.Server.Points;
using Content.Server.RoundEnd;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Spawners.Components;
using Content.Server.Station.Systems;
using Content.Server._CorvaxNext.BattleRoyale.Rules.Components;
using Content.Server._Goobstation.Ghostbar.Components; //Reserve port BattleRoyale
using Robust.Shared.Audio;
using Content.Shared.Bed.Sleep;
using Content.Shared.CombatMode.Pacification;
using Content.Shared.Chat;
using Content.Shared.Eye.Blinding.Components;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Players;
using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Robust.Shared.Enums;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using Content.Shared.Administration;
using Content.Server.Administration.Logs;
using Content.Server.Administration.Commands;
using Content.Server.Players;
using Robust.Server.Player;
using Robust.Shared.Network;
using Content.Shared.Points;


namespace Content.Server._CorvaxNext.BattleRoyale.Rules
{
    /// <summary>
    /// Battle Royale game mode: “последний выживший”,
    /// реализует логику спавна игроков по образцу NukeOps.
    /// </summary>
    public sealed class BattleRoyaleRuleSystem : GameRuleSystem<BattleRoyaleRuleComponent>
    {
        [Dependency] private readonly IPlayerManager _player = default!;
        [Dependency] private readonly MindSystem _mind = default!;
        [Dependency] private readonly PointSystem _point = default!;
        [Dependency] private readonly RoundEndSystem _roundEnd = default!;
        [Dependency] private readonly StationSpawningSystem _stationSpawning = default!;
        [Dependency] private readonly TransformSystem _transforms = default!;
        [Dependency] private readonly MobStateSystem _mobState = default!;
        [Dependency] private readonly KillTrackingSystem _killTracking = default!;
        [Dependency] private readonly IAdminLogManager _adminLogger = default!;
        [Dependency] private readonly IChatManager _chatManager = default!;
        [Dependency] private readonly ChatSystem _chatSystem = default!;
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly IRobustRandom _random = default!;
        [Dependency] private readonly AntagSelectionSystem _antag = default!;

        private const int MaxNormalCallouts = 60;
        private const int MaxEnvironmentalCallouts = 10;

        public override void Initialize()
        {
            base.Initialize();

            // 1) Сбор точек через AntagSelectLocationEvent
            SubscribeLocalEvent<BattleRoyaleRuleComponent, AntagSelectLocationEvent>(OnAntagSelectLocation);

            // 2) Перехват спавна до его выполнения (по образцу NukeOps)
            SubscribeLocalEvent<BattleRoyaleRuleComponent, PlayerBeforeSpawnEvent>(OnBeforeSpawn, before: new[] { typeof(ArrivalsSystem) });

            // остальные события
            SubscribeLocalEvent<BattleRoyaleRuleComponent, AfterAntagEntitySelectedEvent>(OnAfterAntagSelected);
            SubscribeLocalEvent<BattleRoyaleRuleComponent, RuleLoadedGridsEvent>(OnRuleLoadedGrids);
            SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);
            SubscribeLocalEvent<MobStateChangedEvent>(OnMobStateChanged);
            SubscribeLocalEvent<KillReportedEvent>(OnKillReported);
            SubscribeLocalEvent<RefreshLateJoinAllowedEvent>(OnRefreshLateJoinAllowed);
            SubscribeLocalEvent<PlayerDetachedEvent>(OnPlayerDetached);
        }

        private void OnRefreshLateJoinAllowed(RefreshLateJoinAllowedEvent ev)
        {
            if (CheckBattleRoyaleActive())
                ev.Disallow();
        }

        private void OnRuleLoadedGrids(EntityUid uid, BattleRoyaleRuleComponent br, ref RuleLoadedGridsEvent args)
        {
            br.MapId = args.Map;
            Log.Info($"BattleRoyaleRule: Map loaded = {args.Map}");
        }

        /// <summary>
        /// 1) Собираем все SpawnPoint(Type=Job, ID=SpawnPointBattleRoyale) на нашей карте
        ///    и кладём их UID в args.SpawnPoints.
        /// </summary>
        private void OnAntagSelectLocation(EntityUid uid, BattleRoyaleRuleComponent br, ref AntagSelectLocationEvent args)
        {
            if (args.Handled || br.MapId == null)
                return;

            var query = EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();
            var found = 0;
            while (query.MoveNext(out var spUid, out var spawnPoint, out var xform))
            {
                if (xform.MapID != br.MapId)
                    continue;

                if (spawnPoint.SpawnType != SpawnPointType.Job)
                    continue;

                // Фильтруем только точки с prototype ID = "SpawnPointBattleRoyale"
                if (MetaData(spUid).EntityPrototype?.ID != "SpawnPointBattleRoyale")
                    continue;

                args.SpawnPoints.Add(spUid);
                found++;
                Log.Info($"[BattleRoyale] Added spawner {spUid} → {_transforms.GetMapCoordinates(xform)}");
            }

            if (found == 0)
            {
                Log.Error($"BattleRoyaleRule: не найдено ни одной SpawnPointBattleRoyale на карте {br.MapId}");
                return;
            }

            Log.Info($"[BattleRoyale] Total Job Spawners: {found} on Map {br.MapId}");
        }

        /// <summary>
        /// Перехват события до спавна игрока: выбираем из ev.SpawnPoints точку,
        /// выставляем ev.SpawnResult и создаём тело вручную.
        /// </summary>
        private void OnBeforeSpawn(EntityUid uid, BattleRoyaleRuleComponent br, PlayerBeforeSpawnEvent ev)
        {
            // 1) Проверяем, что правило активно и карта задана
            if (!CheckBattleRoyaleActive() || br.MapId == null)
                return;

            // 2) Если спавн уже «присвоен» (ev.SpawnResult != null) или был уже перехвачен, выходим
            if (ev.Handled || ev.SpawnResult != null)
                return;

            // 3) Блокируем спавн через arrival-станцию (если ev.Station указывает на Arrival)
            if (ev.Station != EntityUid.Invalid && HasComp<StationArrivalsComponent>(ev.Station))
            {
                ev.Handled = true;
                return;
            }

            // 4) Если нет ни одной точки в ev.SpawnPoints, выходим без спавна
            if (!ev.SpawnPoints.Any())
            {
                Log.Warning($"[BattleRoyale] No SpawnPoints in BeforeSpawn on Map {br.MapId} — player will not spawn.");
                return;
            }

            // 5) Выбираем случайную точку
            var chosen = _random.Pick(ev.SpawnPoints);
            ev.SpawnResult = chosen;
            ev.Handled = true;
            Log.Info($"[BattleRoyale] Player {ev.Player.Name} will spawn at {chosen}");

            // 6) Ручной спавн тела на выбранной точке
            var xform = Transform(chosen);
            var coords = xform.Coordinates;
            var mobMaybe = _stationSpawning.SpawnPlayerCharacterAtCoords(coords, ev.Profile);
            DebugTools.AssertNotNull(mobMaybe);
            var mob = mobMaybe!.Value;

            // 7) Переносим mind на созданное тело
            var newMind = _mind.CreateMind(ev.Player.UserId, ev.Profile.Name);
            _mind.SetUserId(newMind, ev.Player.UserId);
            _mind.TransferTo(newMind, mob);

            // 8) Присваиваем loadout (если необходимо)
            if (!string.IsNullOrEmpty(br.Gear))
            {
                // Предположим, что у вас есть метод ApplyLoadout, аналогичен NukeOps
                SetOutfitCommand.SetOutfit(mob, br.Gear, false, EntityManager);
            }

            // 9) Добавляем игровые компоненты: KillTracker, Pacified и т.д.
            EnsureComp<KillTrackerComponent>(mob);
            EnsureComp<SleepingComponent>(mob);
            var pacified = EnsureComp<Content.Shared.CombatMode.Pacification.PacifiedComponent>(mob);
            Timer.Spawn(TimeSpan.FromMinutes(2), () =>
            {
                if (!Deleted(mob) && HasComp<Content.Shared.CombatMode.Pacification.PacifiedComponent>(mob))
                    RemComp<Content.Shared.CombatMode.Pacification.PacifiedComponent>(mob);
            });
            var blurry = EnsureComp<Content.Shared.Eye.Blinding.Components.BlurryVisionComponent>(mob);
            Timer.Spawn(TimeSpan.FromSeconds(15), () =>
            {
                if (!Deleted(mob) && HasComp<Content.Shared.Eye.Blinding.Components.BlurryVisionComponent>(mob))
                    RemComp<Content.Shared.Eye.Blinding.Components.BlurryVisionComponent>(mob);
            });
        }

        private bool CheckBattleRoyaleActive()
        {
            var query = EntityQueryEnumerator<BattleRoyaleRuleComponent, ActiveGameRuleComponent>();
            return query.MoveNext(out _, out _, out _);
        }

        private void OnAfterAntagSelected(EntityUid uid, BattleRoyaleRuleComponent br, AfterAntagEntitySelectedEvent args)
        {
            if (args.Session == null)
                return;

            Log.Info($"[BattleRoyale] AfterAntagSelected: {args.Session.Name}");
            _antag.SendBriefing(args.Session, "Вы участник Battle Royale. Сражайтесь, чтобы остаться последним!", Color.Red, null);
        }

        private void OnPlayerAttached(PlayerAttachedEvent ev)
        {
            var entity = ev.Entity;
            if (!HasComp<BattleRoyaleMemberComponent>(entity))
                return;

            var coords = _transforms.GetMapCoordinates(entity);
            Log.Info($"Player {ev.Player.Name} spawned at {coords}");

            // Теперь можно добавить любую логику, например:
            // - добавить очки
            // - удалить или добавить компоненты
        }

        private void OnMobStateChanged(MobStateChangedEvent args)
        {
            if (args.NewMobState != MobState.Dead)
                return;

            var query = EntityQueryEnumerator<BattleRoyaleRuleComponent, GameRuleComponent>();
            while (query.MoveNext(out var uid, out var br, out var gameRule))
            {
                if (!GameTicker.IsGameRuleActive(uid, gameRule))
                    continue;

                CheckLastManStanding(uid, br);
            }
        }

        private void OnKillReported(ref KillReportedEvent ev)
        {
            var query = EntityQueryEnumerator<BattleRoyaleRuleComponent, PointManagerComponent, GameRuleComponent>();
            while (query.MoveNext(out var uid, out var br, out var point, out var gameRule))
            {
                if (!GameTicker.IsGameRuleActive(uid, gameRule))
                    continue;

                if (ev.Primary is KillPlayerSource player)
                    _point.AdjustPointValue(player.PlayerId, 1, uid, point);
                if (ev.Assist is KillPlayerSource assist)
                    _point.AdjustPointValue(assist.PlayerId, 0.5f, uid, point);

                SendKillCallout(uid, ref ev);
            }
        }

        private void SendKillCallout(EntityUid uid, ref KillReportedEvent ev)
        {
            if (ev.Primary is KillEnvironmentSource || ev.Suicide)
            {
                var calloutNumber = _random.Next(0, MaxEnvironmentalCallouts + 1);
                var calloutId = $"death-match-kill-callout-env-{calloutNumber}";
                var victimName = GetEntityName(ev.Entity);
                var message = Loc.GetString(calloutId, ("victim", victimName));
                _chatManager.ChatMessageToAll(ChatChannel.Server, message, message, uid, false, true, Color.OrangeRed);
                return;
            }

            if (ev.Primary is KillPlayerSource primarySource)
            {
                string killerString;
                var primaryName = GetPlayerName(primarySource.PlayerId);
                if (ev.Assist is KillPlayerSource assistSource)
                {
                    var assistName = GetPlayerName(assistSource.PlayerId);
                    killerString = Loc.GetString("death-match-assist", ("primary", primaryName), ("secondary", assistName));
                }
                else
                {
                    killerString = primaryName;
                }

                var calloutNumber = _random.Next(0, MaxNormalCallouts + 1);
                var calloutId = $"death-match-kill-callout-{calloutNumber}";
                var victimName = GetEntityName(ev.Entity);
                var message = Loc.GetString(calloutId, ("killer", killerString), ("victim", victimName));
                _chatManager.ChatMessageToAll(ChatChannel.Server, message, message, uid, false, true, Color.OrangeRed);
            }
            else if (ev.Primary is KillNpcSource npcSource)
            {
                var killerString = GetEntityName(npcSource.NpcEnt);
                var calloutNumber = _random.Next(0, MaxNormalCallouts + 1);
                var calloutId = $"death-match-kill-callout-{calloutNumber}";
                var victimName = GetEntityName(ev.Entity);
                var message = Loc.GetString(calloutId, ("killer", killerString), ("victim", victimName));
                _chatManager.ChatMessageToAll(ChatChannel.Server, message, message, uid, false, true, Color.OrangeRed);
            }
        }

        private string GetPlayerName(NetUserId userId)
        {
            if (!_player.TryGetSessionById(userId, out var session))
                return "Unknown";
            if (session.AttachedEntity == null)
                return session.Name;
            return Loc.GetString("death-match-name-player",
                ("name", MetaData(session.AttachedEntity.Value).EntityName),
                ("username", session.Name));
        }

        private string GetEntityName(EntityUid entity)
        {
            if (TryComp<ActorComponent>(entity, out var actor))
                return Loc.GetString("death-match-name-player",
                    ("name", MetaData(entity).EntityName),
                    ("username", actor.PlayerSession.Name));
            return Loc.GetString("death-match-name-npc",
                ("name", MetaData(entity).EntityName));
        }

        private void CheckLastManStanding(EntityUid uid, BattleRoyaleRuleComponent component)
        {
            var alivePlayers = GetAlivePlayers();
            if (alivePlayers.Count == 1)
            {
                if (!component.WinnerAnnounced || component.Victor == null || component.Victor.Value != alivePlayers.First())
                {
                    component.Victor = alivePlayers.First();
                    if (!component.WinnerAnnounced && _mind.TryGetMind(component.Victor.Value, out _, out var mind))
                    {
                        component.WinnerAnnounced = true;
                        var victorName = MetaData(component.Victor.Value).EntityName;
                        var playerName = mind.Session?.Name ?? victorName;
                        if (_timing.CurTime < TimeSpan.FromSeconds(10))
                        {
                            _chatManager.DispatchServerAnnouncement(
                                Loc.GetString("battle-royale-single-player", ("player", playerName)));
                        }
                        else
                        {
                            _chatManager.DispatchServerAnnouncement(
                                Loc.GetString("battle-royale-winner-announcement", ("player", playerName)));
                        }
                        Timer.Spawn(component.RoundEndDelay, () =>
                        {
                            if (GameTicker.RunLevel == GameRunLevel.InRound)
                                _roundEnd.EndRound();
                        });
                    }
                }
            }
            else if (alivePlayers.Count == 0)
            {
                component.Victor = null;
                _roundEnd.EndRound();
            }
        }

        private void OnPlayerDetached(PlayerDetachedEvent ev)
        {
            var query = EntityQueryEnumerator<BattleRoyaleRuleComponent, GameRuleComponent>();
            while (query.MoveNext(out var uid, out var component, out var gameRule))
            {
                if (!GameTicker.IsGameRuleActive(uid, gameRule))
                    continue;
                CheckLastManStanding(uid, component);
            }
        }

        private List<EntityUid> GetAlivePlayers()
        {
            var result = new List<EntityUid>();
            var mobQuery = EntityQueryEnumerator<MobStateComponent, ActorComponent>();
            while (mobQuery.MoveNext(out var uid, out var mobState, out var actor))
            {
                if ( HasComp<IsDeadICComponent>(uid))
                    continue;
                var session = actor.PlayerSession;
                if (session == null || (session.Status != SessionStatus.Connected && session.Status != SessionStatus.InGame))
                    continue;
                if (_mobState.IsAlive(uid, mobState))
                    result.Add(uid);
            }
            return result;
        }

        protected override void AppendRoundEndText(EntityUid uid, BattleRoyaleRuleComponent component, GameRuleComponent gameRule, ref RoundEndTextAppendEvent args)
        {
            if (!TryComp<PointManagerComponent>(uid, out var point))
                return;

            if (component.Victor != null && _mind.TryGetMind(component.Victor.Value, out var mindId, out var victorMind))
            {
                var victorName = MetaData(component.Victor.Value).EntityName;
                var victorPlayerName = victorMind.Session?.Name ?? victorName;
                args.AddLine(Loc.GetString("battle-royale-winner", ("player", victorPlayerName)));
                args.AddLine("");
            }

            args.AddLine(Loc.GetString("battle-royale-scoreboard-header"));
            args.AddLine(new FormattedMessage(point.Scoreboard).ToMarkup());
        }
    }
}
