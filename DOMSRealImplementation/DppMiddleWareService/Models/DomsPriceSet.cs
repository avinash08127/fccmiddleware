using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DppMiddleWareService.Models
{
    public class DomsPriceSet
    {
        public string PriceSetId { get; set; }
        public List<string> PriceGroupIds { get; set; }
        public List<string> GradeIds { get; set; }
        public Dictionary<string, string> CurrentPrices { get; set; }
    }
}
