using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace Common
{
    [DataContract]
    public class ServerAck
    {
        [DataMember]
        public bool Success { get; set; }

        [DataMember]
        public string Message { get; set; } = "";

        [DataMember]
        public int ReceivedCount { get; set; }

        [DataMember]
        public double PercentOfLimit { get; set; }
    }
}
