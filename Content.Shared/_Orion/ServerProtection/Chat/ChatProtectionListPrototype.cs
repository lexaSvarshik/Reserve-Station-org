// SPDX-FileCopyrightText: 2025 Space Station 14 Contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Prototypes;

namespace Content.Shared._Orion.ServerProtection.Chat;

//
// License-Identifier: AGPL-3.0-or-later
//

[Prototype("chatProtectionList")]
public sealed class ChatProtectionListPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public List<string> Words { get; private set; } = new();
}
