using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DppMiddleWareService.Models
{
    public class PumpTransactions
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("transaction_id")]
        public string TransactionId { get; set; }

        [JsonPropertyName("pump_id")]
        public int PumpId { get; set; }

        [JsonPropertyName("nozzle_id")]
        public int NozzleId { get; set; }

        [JsonPropertyName("attendant")]
        public string Attendant { get; set; }

        [JsonPropertyName("product_id")]
        public string ProductId { get; set; }

        [JsonPropertyName("qty")]
        public decimal Quantity { get; set; }

        [JsonPropertyName("unit_price")]
        public decimal UnitPrice { get; set; }

        [JsonPropertyName("total")]
        public decimal Total { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; }

        [JsonPropertyName("start_time")]
        public DateTime StartTime { get; set; }

        [JsonPropertyName("end_time")]
        public DateTime EndTime { get; set; }

        [JsonPropertyName("order_uuid")]
        public string OrderUuid { get; set; }

        [JsonPropertyName("sync_status")]
        public int SyncStatus { get; set; }

        [JsonPropertyName("odoo_order_id")]
        public string OdooOrderId { get; set; }

        [JsonPropertyName("add_to_cart")]
        public bool AddToCart { get; set; }

        [JsonPropertyName("payment_id")]
        public string? PaymentId { get; set; }
    }
}
