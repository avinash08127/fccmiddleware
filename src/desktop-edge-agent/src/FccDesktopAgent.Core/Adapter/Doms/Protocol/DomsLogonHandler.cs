using FccDesktopAgent.Core.Adapter.Doms.Jpl;

namespace FccDesktopAgent.Core.Adapter.Doms.Protocol;

/// <summary>
/// Handles the DOMS FcLogon handshake sequence.
///
/// FcLogon must complete successfully before any other JPL operations.
/// Sequence: send FcLogon_req -> receive FcLogon_resp -> validate response code.
/// </summary>
public static class DomsLogonHandler
{
    /// <summary>JPL message name for FcLogon request.</summary>
    public const string LogonRequest = "FcLogon_req";

    /// <summary>JPL message name for FcLogon response.</summary>
    public const string LogonResponse = "FcLogon_resp";

    /// <summary>Response code indicating successful logon.</summary>
    public const string LogonOk = "0";

    /// <summary>
    /// Build an FcLogon request message.
    /// </summary>
    /// <param name="fcAccessCode">Access code credential for authentication.</param>
    /// <param name="posVersionId">POS version identifier.</param>
    /// <param name="countryCode">DOMS country code.</param>
    /// <returns>JPL message ready to send via JplTcpClient.</returns>
    public static JplMessage BuildLogonRequest(
        string fcAccessCode,
        string posVersionId,
        string countryCode)
    {
        return new JplMessage(
            Name: LogonRequest,
            Data: new Dictionary<string, string>
            {
                ["FcAccessCode"] = fcAccessCode,
                ["PosVersionId"] = posVersionId,
                ["CountryCode"] = countryCode,
            });
    }

    /// <summary>
    /// Validate an FcLogon response.
    /// </summary>
    /// <param name="response">The JPL response message.</param>
    /// <returns>true if logon was successful.</returns>
    /// <exception cref="DomsProtocolException">If the response indicates an error.</exception>
    public static bool ValidateLogonResponse(JplMessage response)
    {
        if (response.Name != LogonResponse)
        {
            throw new DomsProtocolException(
                $"Expected {LogonResponse} but received {response.Name}");
        }

        var data = response.Data;
        if (data is null || !data.TryGetValue("ResultCode", out var resultCode))
        {
            throw new DomsProtocolException("FcLogon response missing ResultCode");
        }

        if (resultCode != LogonOk)
        {
            var errorText = data.TryGetValue("ErrorText", out var et) ? et : "Unknown error";
            throw new DomsProtocolException(
                $"FcLogon failed with code {resultCode}: {errorText}");
        }

        return true;
    }
}
