using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DPPMiddleware.Models
{
    public class DppMessage
    {
        public string? Name { get; set; }
        public string? CallType { get; set; }
        public string? EXTC { get; set; }        
        public string? SubCode { get; set; }
        public bool Solicited { get; set; }
        public object? Data { get; set; }
    }
    public class GradePriceResponseDto
    {
        public List<GradeDto> Grades { get; set; } = new();
    }

    public class GradeDto
    {
        public string GradeId { get; set; }
        public decimal UnitPrice { get; set; }
    }
}
