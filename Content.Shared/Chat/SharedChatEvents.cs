// SPDX-FileCopyrightText: 2024 beck-thompson <107373427+beck-thompson@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Speech;
using Robust.Shared.Prototypes;
using Content.Shared.Inventory;
using Content.Shared.Radio;

namespace Content.Shared.Chat;

/// <summary>
///     This event should be sent everytime an entity talks (Radio, local chat, etc...).
///     The event is sent to both the entity itself, and all clothing (For stuff like voice masks).
/// </summary>
public sealed class TransformSpeakerNameEvent : EntityEventArgs, IInventoryRelayEvent
{
    public SlotFlags TargetSlots { get; } = SlotFlags.WITHOUT_POCKET;
    public EntityUid Sender;
    public string VoiceName;
    public ProtoId<SpeechVerbPrototype>? SpeechVerb;
    public readonly RadioChannelPrototype? Channel; // Reserve edit: Port from WD

    public TransformSpeakerNameEvent(EntityUid sender, string name, RadioChannelPrototype? channel = null) // Reserve edit: Port from WD
    {
        Sender = sender;
        VoiceName = name;
        SpeechVerb = null;
        Channel = channel;
    }
}
