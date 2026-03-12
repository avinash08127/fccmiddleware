using FccDesktopAgent.Core.Adapter.Doms.Jpl;

namespace FccDesktopAgent.Core.Adapter.Doms.Protocol;

/// <summary>
/// Handles DOMS pre-authorization (authorize_Fp) operations.
///
/// Flow: send authorize_Fp_req -> receive authorize_Fp_resp -> map result code.
/// </summary>
public static class DomsPreAuthHandler
{
    /// <summary>JPL message name for pre-auth request.</summary>
    public const string AuthRequest = "authorize_Fp_req";

    /// <summary>JPL message name for pre-auth response.</summary>
    public const string AuthResponse = "authorize_Fp_resp";

    // -- Result codes ------------------------------------------------------------

    public const string AuthOk = "0";
    public const string AuthPumpNotFound = "1";
    public const string AuthPumpNotIdle = "2";
    public const string AuthAlreadyAuthorized = "3";
    public const string AuthLimitExceeded = "4";
    public const string AuthSystemError = "99";

    /// <summary>
    /// Build an authorize_Fp_req message.
    /// </summary>
    /// <param name="fpId">Fuelling point to authorize.</param>
    /// <param name="nozzleId">Nozzle to authorize (0 = any nozzle).</param>
    /// <param name="amountMinorUnits">Maximum authorized amount in minor currency units.</param>
    /// <param name="currencyCode">ISO 4217 currency code.</param>
    /// <returns>JPL message ready to send.</returns>
    public static JplMessage BuildAuthRequest(
        int fpId,
        int nozzleId,
        long amountMinorUnits,
        string currencyCode)
    {
        // Convert minor units back to DOMS x10 format for the protocol.
        var domsAmount = amountMinorUnits / 10L;

        return new JplMessage(
            Name: AuthRequest,
            Data: new Dictionary<string, string>
            {
                ["FpId"] = fpId.ToString(),
                ["NozzleId"] = nozzleId.ToString(),
                ["Amount"] = domsAmount.ToString(),
                ["CurrencyCode"] = currencyCode,
            });
    }

    /// <summary>
    /// Parse an authorize_Fp_resp into an AuthResponseResult.
    /// </summary>
    /// <param name="response">JPL response message.</param>
    /// <returns>Parsed result with status, authorization code, and message.</returns>
    public static AuthResponseResult ParseAuthResponse(JplMessage response)
    {
        if (response.Name != AuthResponse)
        {
            return new AuthResponseResult(
                Accepted: false,
                Message: $"Unexpected response: {response.Name}");
        }

        var data = response.Data;
        if (data is null || !data.TryGetValue("ResultCode", out var resultCode))
        {
            return new AuthResponseResult(
                Accepted: false,
                Message: "Missing ResultCode in authorize_Fp response");
        }

        return resultCode switch
        {
            AuthOk => new AuthResponseResult(
                Accepted: true,
                AuthorizationCode: data.TryGetValue("AuthCode", out var ac) ? ac : null,
                ExpiresAtUtc: data.TryGetValue("ExpiresAt", out var ea) ? ea : null,
                CorrelationId: data.TryGetValue("CorrelationId", out var ci) ? ci : null),

            AuthPumpNotFound => new AuthResponseResult(
                Accepted: false,
                Message: $"Pump not found (FpId={GetField(data, "FpId")})"),

            AuthPumpNotIdle => new AuthResponseResult(
                Accepted: false,
                Message: "Pump not in idle state"),

            AuthAlreadyAuthorized => new AuthResponseResult(
                Accepted: false,
                Message: "Pump already has an active authorization"),

            AuthLimitExceeded => new AuthResponseResult(
                Accepted: false,
                Message: "Authorization amount exceeds configured limit"),

            _ => new AuthResponseResult(
                Accepted: false,
                Message: $"Unknown auth result code: {resultCode} ({GetField(data, "ErrorText")})")
        };
    }

    /// <summary>
    /// Build a deauthorize request (cancel pre-auth).
    /// </summary>
    public static JplMessage BuildDeauthRequest(int fpId)
    {
        return new JplMessage(
            Name: "deauthorize_Fp_req",
            Data: new Dictionary<string, string>
            {
                ["FpId"] = fpId.ToString(),
            });
    }

    private static string GetField(IReadOnlyDictionary<string, string> data, string key)
        => data.TryGetValue(key, out var value) ? value : string.Empty;
}

/// <summary>Parsed result of a DOMS pre-auth response.</summary>
public sealed record AuthResponseResult(
    bool Accepted,
    string? AuthorizationCode = null,
    string? ExpiresAtUtc = null,
    string? CorrelationId = null,
    string? Message = null);
