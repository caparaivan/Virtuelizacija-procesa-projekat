using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ServiceModel;

namespace Common
{
    [ServiceContract(SessionMode = SessionMode.NotAllowed)]
    public interface ISolarService
    {
        [OperationContract]
        ServerAck StartSession(PvMeta meta);

        [OperationContract]
        ServerAck PushSample(PvSample sample);

        [OperationContract]
        ServerAck EndSession();

        [OperationContract]
        List<string> GetWarnings();
    }
}
