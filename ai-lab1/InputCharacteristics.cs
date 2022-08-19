using Azure.AI.TextAnalytics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ai_lab1
{
    internal class InputCharacteristics
    {
        public string Input { get; set; }
        public string Intent { get; set; }
        public string Sentiment { get; set; }
        public DetectedLanguage Language { get; set; }
        public Dictionary<string, List<string>> Entities { get; set; }
    }
}
