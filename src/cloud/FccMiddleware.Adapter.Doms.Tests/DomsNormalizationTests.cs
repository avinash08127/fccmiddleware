using FluentAssertions;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Models.Adapter;

namespace FccMiddleware.Adapter.Doms.Tests;

public class DomsNormalizationTests
{
    // -------------------------------------------------------------------------
    // Bare transaction object (push shape)
    // -------------------------------------------------------------------------

    [Fact]
    public void NormalizeTransaction_BareObject_MapsAllFields()
    {
        var adapter = TestHelpers.CreateAdapter();
        var json = TestHelpers.ReadFixture("doms-transaction.json");
        var envelope = TestHelpers.WrapPayload(json);

        var result = adapter.NormalizeTransaction(envelope);

        result.FccTransactionId.Should().Be("DOMS-TX-20240115-001");
        result.SiteCode.Should().Be("MW-001");
        result.PumpNumber.Should().Be(2);
        result.NozzleNumber.Should().Be(1);
        result.ProductCode.Should().Be("PMS");   // mapped from "01"
        result.VolumeMicrolitres.Should().Be(25_500_000);
        result.AmountMinorUnits.Should().Be(38_250);
        result.UnitPriceMinorPerLitre.Should().Be(1_500);
        result.CurrencyCode.Should().Be("MWK");
        result.StartedAt.Should().Be(DateTimeOffset.Parse("2024-01-15T08:00:00+00:00"));
        result.CompletedAt.Should().Be(DateTimeOffset.Parse("2024-01-15T08:03:47+00:00"));
        result.FccVendor.Should().Be(FccVendor.DOMS);
        result.FiscalReceiptNumber.Should().Be("R-2024-001");
        result.AttendantId.Should().Be("ATT-007");
    }

    [Fact]
    public void NormalizeTransaction_SingleItemWrappedArray_MapsCorrectly()
    {
        var adapter = TestHelpers.CreateAdapter();
        var json = TestHelpers.ReadFixture("doms-transaction-wrapped-single.json");
        var envelope = TestHelpers.WrapPayload(json);

        var result = adapter.NormalizeTransaction(envelope);

        result.FccTransactionId.Should().Be("DOMS-TX-20240115-003");
        result.ProductCode.Should().Be("IK");   // mapped from "03"
    }

    [Fact]
    public void NormalizeTransaction_AppliesPumpNumberOffset()
    {
        var config = TestHelpers.DefaultConfig(pumpNumberOffset: 1);
        var adapter = TestHelpers.CreateAdapter(config);
        var json = TestHelpers.ReadFixture("doms-transaction.json");  // pumpNumber=2
        var envelope = TestHelpers.WrapPayload(json);

        var result = adapter.NormalizeTransaction(envelope);

        result.PumpNumber.Should().Be(1);  // 2 - offset(1) = 1
    }

    [Fact]
    public void NormalizeTransaction_UnmappedProductCode_PreservesRawCode()
    {
        var config = TestHelpers.DefaultConfig(productMapping: new Dictionary<string, string>());
        var adapter = TestHelpers.CreateAdapter(config);
        var json = TestHelpers.ReadFixture("doms-transaction.json");  // productCode="01"
        var envelope = TestHelpers.WrapPayload(json);

        var result = adapter.NormalizeTransaction(envelope);

        result.ProductCode.Should().Be("01");
    }

    [Fact]
    public void NormalizeTransaction_NullOptionalFields_AreNull()
    {
        var adapter = TestHelpers.CreateAdapter();
        // Use second transaction from list (no attendantId / receiptNumber)
        var bareJson = """
            {
              "transactionId": "TX-NO-OPTIONAL",
              "pumpNumber": 1,
              "nozzleNumber": 1,
              "productCode": "01",
              "volumeMicrolitres": 10000000,
              "amountMinorUnits": 15000,
              "unitPriceMinorPerLitre": 1500,
              "startTime": "2024-01-15T09:00:00+00:00",
              "endTime": "2024-01-15T09:05:00+00:00"
            }
            """;
        var envelope = TestHelpers.WrapPayload(bareJson);

        var result = adapter.NormalizeTransaction(envelope);

        result.AttendantId.Should().BeNull();
        result.FiscalReceiptNumber.Should().BeNull();
    }

    [Fact]
    public void NormalizeTransaction_MultiItemArray_ThrowsInvalidOperation()
    {
        var adapter = TestHelpers.CreateAdapter();
        var json = TestHelpers.ReadFixture("doms-transaction-list.json");  // 2 transactions
        var envelope = TestHelpers.WrapPayload(json);

        var act = () => adapter.NormalizeTransaction(envelope);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*UNSUPPORTED_MESSAGE_TYPE*");
    }

    // -------------------------------------------------------------------------
    // GetAdapterMetadata
    // -------------------------------------------------------------------------

    [Fact]
    public void GetAdapterMetadata_ReturnsCorrectStaticInfo()
    {
        var adapter = TestHelpers.CreateAdapter();

        var info = adapter.GetAdapterMetadata();

        info.Vendor.Should().Be(FccVendor.DOMS);
        info.Protocol.Should().Be("REST");
        info.SupportsPreAuth.Should().BeFalse();
        info.SupportsPumpStatus.Should().BeFalse();
        info.SupportedIngestionMethods.Should().Contain(IngestionMethod.PUSH);
        info.SupportedIngestionMethods.Should().Contain(IngestionMethod.PULL);
        info.AdapterVersion.Should().NotBeNullOrEmpty();
    }
}
