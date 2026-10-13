// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.AspNetCore.Routing;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Community.HomeAssistant;

/// <summary>
/// Thin shell between Zeus and <see cref="HaService"/>: lifecycle and route
/// registration only. Everything else is SDK-free and unit tested.
/// </summary>
public sealed class HomeAssistantPlugin : IZeusPlugin, IBackendPlugin
{
    private HaService? _service;

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        var service = new HaService(
            context.Logger,
            HostBridge.CreateSettingsStore(context),
            HostBridge.CreateRadioStateSource(context));
        await service.StartAsync(ct);
        _service = service;
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        var service = _service;
        _service = null;
        if (service is not null) await service.DisposeAsync();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        HaEndpoints.Map(endpoints, () => _service);
}
