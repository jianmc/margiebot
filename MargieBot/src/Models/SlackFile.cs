using System;
using System.Collections.Generic;
using System.Text;

namespace MargieBot
{
    public class SlackFile
    {
        public System.IO.Stream content { get; set; }
        public string filename { get; set; }
        public string title { get; set; }
    }
}
