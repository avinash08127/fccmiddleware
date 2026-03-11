using FluentAssertions;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Models.Adapter;

namespace FccMiddleware.Adapter.Doms.Tests;

public class DomsValidationTests
{
    private readonly DomsCloudAdapter _adapter = TestHelpers.CreateAdapter();

    private static RawPayloadEnvelope Wrap(string json,
        FccVendor vendor = FccVendor.DOMS,
        string contentType = "application/json") =>
        new()
        {
            Vendor = vendor,
            SiteCode = "MW-001",
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            ContentType = contentType,
            Payload = json
        };

    // -------------------------------------------------------------------------
    // Valid payload
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidatePayload_ValidFixture_ReturnsIsValidTrue()
    {
        var json = TestHelpers.ReadFixture("doms-transaction.json");
        var result = _adapter.ValidatePayload(Wrap(json));
        result.IsValid.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // Null / empty payload
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidatePayload_NullOrEmptyPayload_ReturnsNullPayloadError(string? payload)
    {
        var envelope = new RawPayloadEnvelope
        {
            Vendor = FccVendor.DOMS,
            SiteCode = "MW-001",
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            ContentType = "application/json",
            Payload = payload!
        };

        var result = _adapter.ValidatePayload(envelope);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("NULL_PAYLOAD");
        result.Recoverable.Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Vendor mismatch
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidatePayload_WrongVendor_ReturnsVendorMismatchError()
    {
        var json = TestHelpers.ReadFixture("doms-transaction.json");
        var envelope = Wrap(json, vendor: FccVendor.RADIX);

        var result = _adapter.ValidatePayload(envelope);

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("VENDOR_MISMATCH");
    }

    // -------------------------------------------------------------------------
    // Invalid JSON
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidatePayload_InvalidJson_ReturnsInvalidJsonError()
    {
        var result = _adapter.ValidatePayload(Wrap("not-json{{{{"));

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("INVALID_JSON");
        result.Recoverable.Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Wrong content type
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidatePayload_XmlContentType_ReturnsUnsupportedMessageTypeError()
    {
        var result = _adapter.ValidatePayload(Wrap("<xml/>", contentType: "text/xml"));

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("UNSUPPORTED_MESSAGE_TYPE");
    }

    // -------------------------------------------------------------------------
    // Missing required fields
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidatePayload_MissingTransactionId_ReturnsMissingFieldError()
    {
        var json = """
            {
              "pumpNumber": 1, "nozzleNumber": 1, "productCode": "PMS",
              "volumeMicrolitres": 1000000, "amountMinorUnits": 1500,
              "unitPriceMinorPerLitre": 1500,
              "startTime": "2024-01-15T08:00:00+00:00",
              "endTime": "2024-01-15T08:05:00+00:00"
            }
            """;

        var result = _adapter.ValidatePayload(Wrap(json));

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("MISSING_REQUIRED_FIELD");
        result.Message.Should().Contain("transactionId");
    }

    [Fact]
    public void ValidatePayload_ZeroVolume_ReturnsMissingFieldError()
    {
        var json = """
            {
              "transactionId": "TX-001",
              "pumpNumber": 1, "nozzleNumber": 1, "productCode": "PMS",
              "volumeMicrolitres": 0,
              "amountMinorUnits": 1500, "unitPriceMinorPerLitre": 1500,
              "startTime": "2024-01-15T08:00:00+00:00",
              "endTime": "2024-01-15T08:05:00+00:00"
            }
            """;

        var result = _adapter.ValidatePayload(Wrap(json));

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("MISSING_REQUIRED_FIELD");
        result.Message.Should().Contain("volumeMicrolitres");
    }

    [Fact]
    public void ValidatePayload_ZeroAmount_ReturnsMissingFieldError()
    {
        var json = """
            {
              "transactionId": "TX-001",
              "pumpNumber": 1, "nozzleNumber": 1, "productCode": "PMS",
              "volumeMicrolitres": 1000000, "amountMinorUnits": 0,
              "unitPriceMinorPerLitre": 1500,
              "startTime": "2024-01-15T08:00:00+00:00",
              "endTime": "2024-01-15T08:05:00+00:00"
            }
            """;

        var result = _adapter.ValidatePayload(Wrap(json));

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("MISSING_REQUIRED_FIELD");
        result.Message.Should().Contain("amountMinorUnits");
    }

    [Fact]
    public void ValidatePayload_MissingProductCode_ReturnsMissingFieldError()
    {
        var json = """
            {
              "transactionId": "TX-001",
              "pumpNumber": 1, "nozzleNumber": 1,
              "volumeMicrolitres": 1000000, "amountMinorUnits": 1500,
              "unitPriceMinorPerLitre": 1500,
              "startTime": "2024-01-15T08:00:00+00:00",
              "endTime": "2024-01-15T08:05:00+00:00"
            }
            """;

        var result = _adapter.ValidatePayload(Wrap(json));

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("MISSING_REQUIRED_FIELD");
        result.Message.Should().Contain("productCode");
    }

    [Fact]
    public void ValidatePayload_EndTimeBeforeStartTime_ReturnsMissingFieldError()
    {
        var json = """
            {
              "transactionId": "TX-001",
              "pumpNumber": 1, "nozzleNumber": 1, "productCode": "PMS",
              "volumeMicrolitres": 1000000, "amountMinorUnits": 1500,
              "unitPriceMinorPerLitre": 1500,
              "startTime": "2024-01-15T08:05:00+00:00",
              "endTime": "2024-01-15T08:00:00+00:00"
            }
            """;

        var result = _adapter.ValidatePayload(Wrap(json));

        result.IsValid.Should().BeFalse();
        result.ErrorCode.Should().Be("MISSING_REQUIRED_FIELD");
        result.Message.Should().Contain("endTime");
    }
}
