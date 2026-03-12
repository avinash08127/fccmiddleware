package com.fccmiddleware.edge.config

fun canonicalEdgeConfig(
    configVersion: Int = 5,
    schemaVersion: String = "1.0",
    cloudBaseUrl: String = "https://api.fccmiddleware.io",
    connectionProtocol: String = "TCP",
    hostAddress: String = "192.168.1.100",
    port: Int = 8080,
    pullIntervalSeconds: Int = 30,
): EdgeAgentConfigDto = EdgeAgentConfigDto(
    schemaVersion = schemaVersion,
    configVersion = configVersion,
    configId = "00000000-0000-0000-0000-000000000001",
    issuedAtUtc = "2025-01-01T00:00:00Z",
    effectiveAtUtc = "2025-01-01T00:00:00Z",
    sourceRevision = SourceRevisionDto(
        databricksSyncAtUtc = "2025-01-01T00:00:00Z",
        siteMasterRevision = "site-rev-001",
        fccConfigRevision = "fcc-rev-001",
    ),
    identity = IdentityDto(
        legalEntityId = "22222222-2222-2222-2222-222222222222",
        legalEntityCode = "LE-001",
        siteId = "33333333-3333-3333-3333-333333333333",
        siteCode = "SITE-001",
        siteName = "Site 001",
        timezone = "Africa/Johannesburg",
        currencyCode = "ZAR",
        deviceId = "11111111-1111-1111-1111-111111111111",
        isPrimaryAgent = true,
    ),
    site = SiteDto(
        isActive = true,
        operatingModel = "COCO",
        siteUsesPreAuth = true,
        connectivityMode = "CONNECTED",
        odooSiteId = "ODOO-SITE-001",
        companyTaxPayerId = "TAX-001",
    ),
    fcc = FccDto(
        enabled = true,
        vendor = "DOMS",
        connectionProtocol = connectionProtocol,
        hostAddress = hostAddress,
        port = port,
        credentialRef = "fcc/site-001",
        transactionMode = "PULL",
        ingestionMode = "RELAY",
        pullIntervalSeconds = pullIntervalSeconds,
        heartbeatIntervalSeconds = 15,
        heartbeatTimeoutSeconds = 45,
    ),
    sync = SyncDto(
        cloudBaseUrl = cloudBaseUrl,
        uploadBatchSize = 50,
        uploadIntervalSeconds = 30,
        syncedStatusPollIntervalSeconds = 30,
        configPollIntervalSeconds = 60,
    ),
    buffer = BufferDto(
        retentionDays = 30,
        stalePendingDays = 3,
        maxRecords = 50000,
        cleanupIntervalHours = 24,
    ),
    localApi = LocalApiDto(
        localhostPort = 8585,
        enableLanApi = false,
        rateLimitPerMinute = 120,
    ),
    telemetry = TelemetryDto(
        telemetryIntervalSeconds = 60,
        logLevel = "INFO",
    ),
    fiscalization = FiscalizationDto(
        mode = "NONE",
        requireCustomerTaxId = false,
        fiscalReceiptRequired = false,
    ),
    mappings = MappingsDto(
        pumpNumberOffset = 0,
        priceDecimalPlaces = 2,
        volumeUnit = "LITRE",
        products = listOf(
            ProductMappingDto(
                fccProductCode = "001",
                canonicalProductCode = "PMS",
                displayName = "Petrol",
                active = true,
            ),
        ),
        nozzles = emptyList(),
    ),
    rollout = RolloutDto(
        minAgentVersion = "1.0.0",
        configTtlHours = 24,
    ),
)

fun canonicalEdgeConfigJson(
    configVersion: Int = 5,
    schemaVersion: String = "1.0",
    cloudBaseUrl: String = "https://api.fccmiddleware.io",
    connectionProtocol: String = "TCP",
    hostAddress: String = "192.168.1.100",
    port: Int = 8080,
    pullIntervalSeconds: Int = 30,
): String = EdgeAgentConfigJson.encode(
    canonicalEdgeConfig(
        configVersion = configVersion,
        schemaVersion = schemaVersion,
        cloudBaseUrl = cloudBaseUrl,
        connectionProtocol = connectionProtocol,
        hostAddress = hostAddress,
        port = port,
        pullIntervalSeconds = pullIntervalSeconds,
    ),
)
