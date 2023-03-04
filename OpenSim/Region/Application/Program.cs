using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using System;
using System.IO;
using System.Threading.Tasks;

namespace OpenSim
{
    internal class Program
    {
        public static async Task Main(string[] args)
        {
            await Host.CreateDefaultBuilder(args)
                .ConfigureHostConfiguration(hostConfig =>
                {
                    hostConfig.SetBasePath(Directory.GetCurrentDirectory());
                    hostConfig.AddIniFile("OpenSimDefaults.ini", optional: false, reloadOnChange: true);
                    hostConfig.AddIniFile("OpenSim.ini", optional: true, reloadOnChange: true);
                    hostConfig.AddJsonFile("opensim.settings.json", optional: true);
                    hostConfig.AddEnvironmentVariables(prefix: "OPENSIM_");
                    hostConfig.AddCommandLine(args);
                })
                .ConfigureServices((hostcontext, services) =>
                {
                    services.Configure<HostOptions>(options =>
                    {
                        options.ShutdownTimeout = TimeSpan.FromSeconds(20);
                    });

                    services.AddHostedService<RegionSimulatorService>();
                })
                .Build()
                .RunAsync();
        }
    }
}