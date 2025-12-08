// SPDX-FileCopyrightText: 2025 RedFoxIV <38788538+RedFoxIV@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 ReserveBot <211949879+ReserveBot@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.DeviceNetwork;
using Content.Server.DeviceNetwork.Components;
using Content.Server.DeviceNetwork.Systems;
using Content.Shared._White.CustomGhostSystem;
using Content.Shared.DeviceNetwork;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._White
{
    [TestFixture]
    public sealed class CustomGhostDefaultTest
    {
        [Test]
        public async Task CustomGhostDefaultPrototypePresent()
        {
            await using var pair = await PoolManager.GetServerClient();
            var server = pair.Server;
            var prototypeManager = server.ResolveDependency<IPrototypeManager>();
            Assert.That(prototypeManager.HasIndex<CustomGhostPrototype>("default"));
            await pair.CleanReturnAsync();
        }
    }
}
