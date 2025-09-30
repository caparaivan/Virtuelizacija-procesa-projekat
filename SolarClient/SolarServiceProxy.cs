using System;
using System.Collections.Generic;
using System.ServiceModel;
using System.ServiceModel.Channels;
using Common;

namespace SolarClient
{
    public class SolarServiceProxy : IDisposable
    {
        private readonly ChannelFactory<ISolarService> _factory;
        private readonly ISolarService _channel;

        // koristi se naziv endpointa iz App.config
        public SolarServiceProxy(string endpointName = "NetTcpBinding_ISolarService")
        {
            _factory = new ChannelFactory<ISolarService>(endpointName);
            _channel = _factory.CreateChannel();
        }

        public ServerAck StartSession(PvMeta meta) => _channel.StartSession(meta);
        public ServerAck PushSample(PvSample sample) => _channel.PushSample(sample);
        public ServerAck EndSession() => _channel.EndSession();
        public List<string> GetWarnings() => _channel.GetWarnings();

        public void Dispose()
        {
            // zatvaranje kanala
            if (_channel is ICommunicationObject commChannel)
            {
                try
                {
                    if (commChannel.State != CommunicationState.Faulted)
                        commChannel.Close();
                    else
                        commChannel.Abort();
                }
                catch (Exception)
                {
                    commChannel.Abort();
                }
            }

            // zatvaranje factory kanala
            if (_factory is ICommunicationObject commFactory)
            {
                try
                {
                    if (commFactory.State != CommunicationState.Faulted)
                        commFactory.Close();
                    else
                        commFactory.Abort();
                }
                catch (Exception)
                {
                    commFactory.Abort();
                }
            }
        }
    }
}