using System;
using System.IO;
using Common;
using System.ServiceModel;

namespace SolarClient
{
    internal class Program
    {
        static void Main()
        {
            string csvPath = "input.csv";
            string rejectsPath = "rejected_client.csv";
            int limitN = 100;
            int breakAfter = 20;

            try
            {
                // provera postojanja fajla
                if (!File.Exists(csvPath))
                {
                    throw new FileNotFoundException($"Greška: Datoteka '{csvPath}' (CSV fajl za slanje) nije pronađena.", csvPath);
                }

                // 1. priprema PvMeta
                var meta = new PvMeta
                {
                    PlantId = "PLANT-001",
                    FileName = Path.GetFileName(csvPath),
                    // ukupan broj redova - zaglavlje
                    TotalRows = File.ReadAllLines(csvPath).Length - 1,
                    SchemaVersion = "1.0",
                    RowLimitN = limitN,
                    SessionDateUtc = DateTime.UtcNow
                };
                var reader = new CsvReader();

                // 2. koriscenje using bloka za proxy i pokretanje sesije
                using (var proxy = new SolarServiceProxy())
                {
                    var startAck = proxy.StartSession(meta);
                    Console.WriteLine($" Sesija pokrenuta: {startAck.Message}");

                    if (!startAck.Success) return;

                    int sent = 0;
                    // 3. sekvencijalni streaming
                    foreach (var sample in reader.ReadSamples(csvPath, limitN, rejectsPath))
                    {
                        var pushAck = proxy.PushSample(sample);
                        sent++;

                        if (!pushAck.Success)
                            Console.WriteLine($" Greška pri slanju uzorka {sample.RowIndex}: {pushAck.Message}");

                        // povremena provera upozorenja sa servera (Zadatak 8)
                        if (sent % 5 == 0)
                        {
                            var warnings = proxy.GetWarnings();
                            foreach (var w in warnings)
                                Console.WriteLine($" {w}");
                        }

                        // simulacija prekida veze (Zadatak 4)
                        if (sent == breakAfter)
                        {
                            Console.WriteLine($"Simulacija prekida veze nakon {sent} uzoraka...");
                            proxy.Dispose(); // namerni prekid i cleanup
                            return; // zavrsavamo program
                        }
                    }

                    // zavrsetak sesije ako nije doslo do prekida
                    var endAck = proxy.EndSession();
                    Console.WriteLine($" Sesija završena: {endAck.Message} ({endAck.ReceivedCount} redova, {endAck.PercentOfLimit}%)");
                }

                Console.WriteLine("Gotovo.");
            }
            catch (EndpointNotFoundException ex)
            {
                Console.WriteLine("\nKRITIČNA GREŠKA WCF: Endpoint nije pronađen.");
                Console.WriteLine("--> Proverite da li je SolarService host pokrenut i da li je adresa 'net.tcp://localhost:8088/SolarService' ispravna u App.config.");
                Console.WriteLine($"Detalji: {ex.Message}");
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine($"\nKRITIČNA GREŠKA: {ex.Message}");
                Console.WriteLine("--> Obavezno kreirajte dummy 'input.csv' fajl sa zaglavljem u root direktorijumu klijenta.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nKRITIČNA GREŠKA: Aplikacija je neočekivano prekinuta.");
                Console.WriteLine($"Detalji: {ex.Message}");
            }
            finally
            {
                Console.WriteLine("\nPritisnite ENTER za izlaz.");
                Console.ReadLine();
            }
        }
    }
}