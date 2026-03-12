using FccDesktopAgent.Core.Adapter.Radix;
using FluentAssertions;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Adapter.Radix;

/// <summary>
/// Unit tests for <see cref="RadixSignatureHelper"/>.
///
/// Covers:
///   - SHA-1 computation with known input/output pairs
///   - Transaction management signing: SHA1(&lt;REQ&gt;...&lt;/REQ&gt; + password)
///   - Pre-auth signing: SHA1(&lt;AUTH_DATA&gt;...&lt;/AUTH_DATA&gt; + password)
///   - Signature validation (match and mismatch)
///   - Whitespace sensitivity
///   - Empty content edge case
///   - Special characters (Turkish, Arabic)
///   - Secret appended immediately after closing tag with no separator
///
/// Test vectors match the Kotlin RadixSignatureHelperTests to ensure cross-platform parity.
/// </summary>
public sealed class RadixSignatureHelperTests
{
    private const string Secret = "MySecretPassword";

    // -----------------------------------------------------------------------
    // Transaction signing (port P+1): SHA1(<REQ>...</REQ> + SECRET)
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeTransactionSignature_ProducesCorrectSha1ForBasicReqElement()
    {
        var req = "<REQ><CMD_CODE>10</CMD_CODE><CMD_NAME>TRN_REQ</CMD_NAME><TOKEN>12345</TOKEN></REQ>";

        var result = RadixSignatureHelper.ComputeTransactionSignature(req, Secret);

        result.Should().Be("d1d97346980b45e69560a799a86a9707cb56b01f");
    }

    [Fact]
    public void ComputeTransactionSignature_ProducesLowercaseHexStringOf40Characters()
    {
        var req = "<REQ><CMD_CODE>10</CMD_CODE></REQ>";

        var result = RadixSignatureHelper.ComputeTransactionSignature(req, Secret);

        result.Should().HaveLength(40);
        result.Should().MatchRegex("^[0-9a-f]{40}$");
    }

    [Fact]
    public void ComputeTransactionSignature_ForAckRequest()
    {
        var req = "<REQ><CMD_CODE>201</CMD_CODE></REQ>";

        var result = RadixSignatureHelper.ComputeTransactionSignature(req, Secret);

        result.Should().Be("8802249d78f9e0eb6e600079efa8284d611027fa");
    }

    // -----------------------------------------------------------------------
    // Auth signing (port P): SHA1(<AUTH_DATA>...</AUTH_DATA> + SECRET)
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeAuthSignature_ProducesCorrectSha1ForBasicAuthDataElement()
    {
        var authData = "<AUTH_DATA><PUMP>3</PUMP><FP>1</FP><AUTH>TRUE</AUTH><TOKEN>123456</TOKEN></AUTH_DATA>";

        var result = RadixSignatureHelper.ComputeAuthSignature(authData, Secret);

        result.Should().Be("d6302e39898c55fe0c2f15c398d27beab0a9f7bf");
    }

    [Fact]
    public void ComputeAuthSignature_ProducesLowercaseHexStringOf40Characters()
    {
        var authData = "<AUTH_DATA><PUMP>1</PUMP></AUTH_DATA>";

        var result = RadixSignatureHelper.ComputeAuthSignature(authData, Secret);

        result.Should().HaveLength(40);
        result.Should().MatchRegex("^[0-9a-f]{40}$");
    }

    // -----------------------------------------------------------------------
    // Both signing paths produce different results for different content
    // -----------------------------------------------------------------------

    [Fact]
    public void TransactionAndAuthSignatures_DifferForDifferentContentStructures()
    {
        var reqContent = "<REQ><CMD_CODE>10</CMD_CODE></REQ>";
        var authContent = "<AUTH_DATA><PUMP>1</PUMP></AUTH_DATA>";

        var txSig = RadixSignatureHelper.ComputeTransactionSignature(reqContent, Secret);
        var authSig = RadixSignatureHelper.ComputeAuthSignature(authContent, Secret);

        txSig.Should().NotBe(authSig, "different XML content must produce different signatures");
    }

    // -----------------------------------------------------------------------
    // Signature validation
    // -----------------------------------------------------------------------

    [Fact]
    public void ValidateSignature_ReturnsTrueForMatchingSignature()
    {
        var table = """<TABLE VERSION="1.0"><ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="12345" /><TRN AMO="30000.0" /></TABLE>""";
        var expectedSig = "8080da0a07ddcd78cf5aeec5bf9595e537046142";

        RadixSignatureHelper.ValidateSignature(table, expectedSig, Secret).Should().BeTrue();
    }

    [Fact]
    public void ValidateSignature_ReturnsFalseForMismatchedSignature()
    {
        var table = """<TABLE VERSION="1.0"><ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="12345" /><TRN AMO="30000.0" /></TABLE>""";
        var wrongSig = "0000000000000000000000000000000000000000";

        RadixSignatureHelper.ValidateSignature(table, wrongSig, Secret).Should().BeFalse();
    }

    [Fact]
    public void ValidateSignature_ReturnsFalseWhenWrongSecretIsUsed()
    {
        var table = """<TABLE VERSION="1.0"><ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="12345" /><TRN AMO="30000.0" /></TABLE>""";
        var sigWithCorrectSecret = "8080da0a07ddcd78cf5aeec5bf9595e537046142";

        RadixSignatureHelper.ValidateSignature(table, sigWithCorrectSecret, "WrongPassword").Should().BeFalse();
    }

    [Fact]
    public void ValidateSignature_IsCaseInsensitiveForHexComparison()
    {
        var table = """<TABLE VERSION="1.0"><ANS RESP_CODE="201" RESP_MSG="SUCCESS" TOKEN="12345" /><TRN AMO="30000.0" /></TABLE>""";
        var upperSig = "8080DA0A07DDCD78CF5AEEC5BF9595E537046142";

        RadixSignatureHelper.ValidateSignature(table, upperSig, Secret)
            .Should().BeTrue("validation should accept uppercase hex from FDC responses");
    }

    // -----------------------------------------------------------------------
    // Whitespace sensitivity
    // -----------------------------------------------------------------------

    [Fact]
    public void WhitespaceDifferencesInXml_ProduceDifferentSignatures()
    {
        var compact = "<REQ><CMD_CODE>10</CMD_CODE><CMD_NAME>TRN_REQ</CMD_NAME><TOKEN>12345</TOKEN></REQ>";
        var formatted = "<REQ>\n    <CMD_CODE>10</CMD_CODE>\n    <CMD_NAME>TRN_REQ</CMD_NAME>\n    <TOKEN>12345</TOKEN>\n</REQ>";

        var sigCompact = RadixSignatureHelper.ComputeTransactionSignature(compact, Secret);
        var sigFormatted = RadixSignatureHelper.ComputeTransactionSignature(formatted, Secret);

        sigCompact.Should().Be("d1d97346980b45e69560a799a86a9707cb56b01f");
        sigFormatted.Should().Be("1f37f81292cd10a5143366e65d3ee1376dda0c37");
        sigCompact.Should().NotBe(sigFormatted, "whitespace must affect signature");
    }

    // -----------------------------------------------------------------------
    // Secret appended immediately (no separator)
    // -----------------------------------------------------------------------

    [Fact]
    public void Secret_IsAppendedImmediatelyAfterClosingTagWithNoSeparator()
    {
        var req = "<REQ><CMD_CODE>201</CMD_CODE></REQ>";

        var sigNoSpace = RadixSignatureHelper.ComputeTransactionSignature(req, Secret);
        // If there were a space between </REQ> and the secret, the hash would differ
        var sigWithSpace = RadixSignatureHelper.ComputeTransactionSignature(req + " ", Secret[1..]);

        sigNoSpace.Should().Be("8802249d78f9e0eb6e600079efa8284d611027fa");
        sigNoSpace.Should().NotBe(sigWithSpace,
            "adding a space before the secret must produce a different signature");
    }

    // -----------------------------------------------------------------------
    // Empty content
    // -----------------------------------------------------------------------

    [Fact]
    public void EmptyContent_WithSecretProducesValidSha1()
    {
        var result = RadixSignatureHelper.ComputeTransactionSignature("", Secret);

        result.Should().Be("952729c61cab7e01e4b5f5ba7b95830d2075f74b");
        result.Should().HaveLength(40);
    }

    // -----------------------------------------------------------------------
    // Special characters (Unicode)
    // -----------------------------------------------------------------------

    [Fact]
    public void SpecialCharacters_TurkishInXmlContent()
    {
        var req = "<REQ><CMD_NAME>T\u00fcrk\u00e7e \u0130\u015flem</CMD_NAME></REQ>";

        var result = RadixSignatureHelper.ComputeTransactionSignature(req, Secret);

        result.Should().Be("5ba8c2bc9a0dc9eb660ae99ff5e17bf8adf9d47c");
    }

    [Fact]
    public void SpecialCharacters_ArabicInXmlContent()
    {
        var req = "<REQ><CMD_NAME>\u0639\u0645\u0644\u064a\u0629</CMD_NAME></REQ>";

        var result = RadixSignatureHelper.ComputeTransactionSignature(req, Secret);

        result.Should().Be("bc8137396e91fc2838ae9cdcca8e3caf65e9ea58");
    }

    // -----------------------------------------------------------------------
    // Deterministic output
    // -----------------------------------------------------------------------

    [Fact]
    public void RepeatedCalls_WithSameInputProduceIdenticalSignatures()
    {
        var req = "<REQ><CMD_CODE>10</CMD_CODE></REQ>";

        var sig1 = RadixSignatureHelper.ComputeTransactionSignature(req, Secret);
        var sig2 = RadixSignatureHelper.ComputeTransactionSignature(req, Secret);

        sig1.Should().Be(sig2);
    }
}
