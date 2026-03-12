using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DppMiddleWareService.Models
{
    public class AttendantPumpCountUpdate
    {
        [JsonPropertyName("session_id")]
        public int SessionId { get; set; }

        [JsonPropertyName("emp_tag_no")]
        public string EmpTagNo { get; set; }

        [JsonPropertyName("new_max_transaction")]
        public int NewMaxTransaction { get; set; }
        [JsonPropertyName("pump_number")]
        public int PumpNumber { get; set; }
    }
}
