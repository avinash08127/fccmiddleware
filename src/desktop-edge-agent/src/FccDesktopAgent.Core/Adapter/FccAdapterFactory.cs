using FccDesktopAgent.Core.Adapter.Advatec;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Adapter.Doms;
using FccDesktopAgent.Core.Adapter.Petronite;
using FccDesktopAgent.Core.Adapter.Radix;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Adapter;

/// <summary>
/// Concrete factory that creates FCC adapter instances by vendor type.
/// For DOMS, selects TCP/JPL or REST based on <see cref="FccConnectionConfig.ConnectionProtocol"/>.
/// Adapters are lightweight — a new instance is created per use.
/// </summary>
public sealed class FccAdapterFactory : IFccAdapterFactory
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory _loggerFactory;

    // Petronite adapter is stateful (webhook listener + pre-auth map + queue).
    // Cache a single instance per config fingerprint so state survives across poll cycles.
    private PetroniteAdapter? _cachedPetroniteAdapter;
    private string? _cachedPetroniteFingerprint;
    private readonly object _petroniteLock = new();

    // Advatec adapter is stateful (webhook listener for Receipt callbacks).
    // Cache a single instance per config fingerprint so state survives across poll cycles.
    private AdvatecAdapter? _cachedAdvatecAdapter;
    private string? _cachedAdvatecFingerprint;
    private readonly object _advatecLock = new();

    public FccAdapterFactory(IHttpClientFactory httpFactory, ILoggerFactory loggerFactory)
    {
        _httpFactory = httpFactory;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc/>
    public IFccAdapter Create(FccVendor vendor, FccConnectionConfig config) => vendor switch
    {
        FccVendor.Doms => config.ConnectionProtocol?.Equals("TCP", StringComparison.OrdinalIgnoreCase) == true
            ? CreateDomsJplAdapter(config)
            : new DomsAdapter(
                _httpFactory,
                config,
                _loggerFactory.CreateLogger<DomsAdapter>()),
        FccVendor.Radix => new RadixAdapter(
            _httpFactory,
            config,
            _loggerFactory.CreateLogger<RadixAdapter>(),
            siteCode: config.SiteCode,
            legalEntityId: config.LegalEntityId ?? config.SiteCode,
            currencyCode: config.CurrencyCode ?? "TZS",
            timezone: config.Timezone ?? "Africa/Dar_es_Salaam",
            pumpNumberOffset: config.PumpNumberOffset,
            productCodeMapping: config.ProductCodeMapping),
        FccVendor.Petronite => GetOrCreatePetroniteAdapter(config),
        FccVendor.Advatec => GetOrCreateAdvatecAdapter(config),
        _ => throw new ArgumentException($"Unknown FCC vendor: {vendor}", nameof(vendor))
    };

    /// <summary>
    /// Returns a cached Petronite adapter if the config hasn't changed, or creates
    /// a new one (disposing the old). Petronite adapters are stateful — they own the
    /// webhook listener, pre-auth map, and webhook queue — so they must survive across
    /// poll cycles.
    /// </summary>
    private PetroniteAdapter GetOrCreatePetroniteAdapter(FccConnectionConfig config)
    {
        var fingerprint = $"{config.BaseUrl}|{config.ClientId}|{config.WebhookSecret}|{config.WebhookListenerPort}";

        lock (_petroniteLock)
        {
            if (_cachedPetroniteAdapter is not null && _cachedPetroniteFingerprint == fingerprint)
                return _cachedPetroniteAdapter;

            // Config changed or first creation — dispose old and create new.
            _cachedPetroniteAdapter?.DisposeAsync().AsTask().GetAwaiter().GetResult();

            _cachedPetroniteAdapter = CreatePetroniteAdapter(config);
            _cachedPetroniteFingerprint = fingerprint;
            return _cachedPetroniteAdapter;
        }
    }

    private PetroniteAdapter CreatePetroniteAdapter(FccConnectionConfig config)
    {
        var oauthClient = new PetroniteOAuthClient(
            _httpFactory,
            config,
            _loggerFactory.CreateLogger<PetroniteOAuthClient>());

        var nozzleResolver = new PetroniteNozzleResolver(
            _httpFactory,
            config,
            oauthClient,
            _loggerFactory.CreateLogger<PetroniteNozzleResolver>());

        return new PetroniteAdapter(
            _httpFactory,
            config,
            oauthClient,
            nozzleResolver,
            _loggerFactory);
    }

    /// <summary>
    /// Returns a cached Advatec adapter if the config hasn't changed, or creates
    /// a new one. Advatec adapters are stateful — they will own the webhook listener
    /// for Receipt callbacks — so they must survive across poll cycles.
    /// </summary>
    private AdvatecAdapter GetOrCreateAdvatecAdapter(FccConnectionConfig config)
    {
        var fingerprint = $"{config.AdvatecDeviceAddress}|{config.AdvatecDevicePort}|{config.AdvatecWebhookToken}|{config.AdvatecWebhookListenerPort}";

        lock (_advatecLock)
        {
            if (_cachedAdvatecAdapter is not null && _cachedAdvatecFingerprint == fingerprint)
                return _cachedAdvatecAdapter;

            _cachedAdvatecAdapter = new AdvatecAdapter(
                config,
                _loggerFactory);
            _cachedAdvatecFingerprint = fingerprint;
            return _cachedAdvatecAdapter;
        }
    }

    private DomsJplAdapter CreateDomsJplAdapter(FccConnectionConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.SiteCode)
            || string.IsNullOrWhiteSpace(config.LegalEntityId)
            || string.IsNullOrWhiteSpace(config.CurrencyCode)
            || string.IsNullOrWhiteSpace(config.Timezone))
        {
            throw new InvalidOperationException(
                "DOMS TCP adapter requires siteCode, legalEntityId, currencyCode, and timezone from site config.");
        }

        return new DomsJplAdapter(
            config: config,
            siteCode: config.SiteCode,
            legalEntityId: config.LegalEntityId,
            currencyCode: config.CurrencyCode,
            timezone: config.Timezone,
            pumpNumberOffset: config.PumpNumberOffset,
            productCodeMapping: config.ProductCodeMapping,
            logger: _loggerFactory.CreateLogger<DomsJplAdapter>());
    }
}
