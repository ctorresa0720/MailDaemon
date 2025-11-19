using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MailDaemon.Settings
{
    public class DaemonSettings
    {
        public int IntervalSeconds { get; set; }
        public string SqlConnection { get; set; } = "";
        public string Schema { get; set; } = "";   
    }
}
