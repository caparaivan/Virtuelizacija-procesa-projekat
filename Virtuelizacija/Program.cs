using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Virtuelizacija.Services;
using System.ServiceModel;

namespace Virtuelizacija
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var service = new SolarService();  // singleton instanca

            // za logging simulaciju (Zadatak 8)
            service.OnTransferStarted += (s, e) => Console.WriteLine("Transfer started.");
            service.OnSampleReceived += (s, count) => Console.WriteLine($"Sample received: {count} total.");
            service.OnTransferCompleted += (s, e) => Console.WriteLine("Transfer completed.");
            service.OnWarningRaised += (s, e) => Console.WriteLine($"[WARNING] {e.Type}: {e.Message} (Row {e.RowIndex})");

            using (var host = new ServiceHost(service))  // prosledjuje instancu za singleton
            {
                host.Opened += (s, e) => Console.WriteLine("SolarService host opened.");
                host.Faulted += (s, e) => Console.WriteLine("SolarService host faulted.");
                host.Closed += (s, e) => Console.WriteLine("SolarService host closed.");

                host.Open();

                Console.WriteLine("Press ENTER to exit.");
                Console.ReadLine();

                host.Close();
            }

        }
    }
}
