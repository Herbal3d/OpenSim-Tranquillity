using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using log4net;
using log4net.Config;
using Nini.Config;

using OpenSim.Framework;
using OpenSim.Framework.Console;
using System.IO;
using System.Net;
using OpenSim;

namespace OpenSim
{
    public sealed class RegionSimulatorService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<RegionSimulatorService> _logger;
        private readonly IHostApplicationLifetime _hostApplicationLifetime;

        /// <summary>
        /// Text Console Logger
        /// </summary>
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Save Crashes in the bin/crashes folder.  Configurable with m_crashDir
        /// </summary>
        private bool m_saveCrashDumps = false;

        /// <summary>
        /// Directory to save crash reports to.  Relative to bin/
        /// </summary>
        public static string m_crashDir = "crashes";

        /// <summary>
        /// Are we running this is the background (no console)
        /// </summary>
        private bool m_background = false;

        /// <summary>
        /// Instance of the OpenSim class.  This could be OpenSim or OpenSimBackground depending on the configuration
        /// </summary>
        private OpenSimBase m_sim = null;

        /// <summary>
        /// Old Style Nini configSource
        /// </summary>        
        ArgvConfigSource m_configSource = null;

        public RegionSimulatorService(
            IServiceProvider serviceProvider,
            IConfiguration configuration,
            ILogger<RegionSimulatorService> logger,
            IHostApplicationLifetime hostApplicationLifetime)
        {
            _serviceProvider = serviceProvider;
            _configuration = configuration;
            _logger = logger;
            _hostApplicationLifetime = hostApplicationLifetime;

            _hostApplicationLifetime.ApplicationStarted.Register(OnStarted);
            _hostApplicationLifetime.ApplicationStopping.Register(OnStopping);
            _hostApplicationLifetime.ApplicationStopped.Register(OnStopped);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var args = Environment.GetCommandLineArgs();

            _logger.LogDebug($"Starting with arguments: {string.Join(" ", args)}");
            System.AppContext.SetSwitch("System.Drawing.EnableUnixSupport", true);

            Culture.SetCurrentCulture();
            Culture.SetDefaultCurrentCulture();

            ServicePointManager.DefaultConnectionLimit = 32;
            ServicePointManager.MaxServicePointIdleTime = 30000;

            try { ServicePointManager.DnsRefreshTimeout = 5000; } catch { }
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;

            // Add the arguments supplied when running the application to the configuration
            m_configSource = new ArgvConfigSource(args);

            // Configure Log4Net
            m_configSource.AddSwitch("Startup", "logconfig");
            string logConfigFile = m_configSource.Configs["Startup"].GetString("logconfig", String.Empty);
            if (!string.IsNullOrEmpty(logConfigFile))
            {
                XmlConfigurator.Configure(new System.IO.FileInfo(logConfigFile));
                m_log.InfoFormat("[OPENSIM MAIN]: configured log4net using \"{0}\" as configuration file",
                                 logConfigFile);
            }
            else
            {
                XmlConfigurator.Configure(new System.IO.FileInfo("OpenSim.exe.config"));
                m_log.Info("[OPENSIM MAIN]: configured log4net using default OpenSim.exe.config");
            }

            m_log.InfoFormat(
                "[OPENSIM MAIN]: System Locale is {0}", System.Threading.Thread.CurrentThread.CurrentCulture);
            if (!Util.IsWindows())
            {
                string monoThreadsPerCpu = System.Environment.GetEnvironmentVariable("MONO_THREADS_PER_CPU");
                m_log.InfoFormat(
                    "[OPENSIM MAIN]: Environment variable MONO_THREADS_PER_CPU is {0}", monoThreadsPerCpu ?? "unset");
            }

            // Check if the system is compatible with OpenSimulator.
            // Ensures that the minimum system requirements are met
            string supported = String.Empty;
            if (Util.IsEnvironmentSupported(ref supported))
            {
                m_log.Info("[OPENSIM MAIN]: Environment is supported by OpenSimulator.");
            }
            else
            {
                m_log.Warn("[OPENSIM MAIN]: Environment is not supported by OpenSimulator (" + supported + ")\n");
            }

            m_log.InfoFormat("Default culture changed to {0}", Culture.GetDefaultCurrentCulture().DisplayName);

            m_configSource.Alias.AddAlias("On", true);
            m_configSource.Alias.AddAlias("Off", false);
            m_configSource.Alias.AddAlias("True", true);
            m_configSource.Alias.AddAlias("False", false);
            m_configSource.Alias.AddAlias("Yes", true);
            m_configSource.Alias.AddAlias("No", false);

            m_configSource.AddSwitch("Startup", "background");
            m_configSource.AddSwitch("Startup", "inifile");
            m_configSource.AddSwitch("Startup", "inimaster");
            m_configSource.AddSwitch("Startup", "inidirectory");
            m_configSource.AddSwitch("Startup", "physics");
            m_configSource.AddSwitch("Startup", "gui");
            m_configSource.AddSwitch("Startup", "console");
            m_configSource.AddSwitch("Startup", "save_crashes");
            m_configSource.AddSwitch("Startup", "crash_dir");

            m_configSource.AddConfig("StandAlone");
            m_configSource.AddConfig("Network");

            // Check if we're running in the background or not
            m_background = m_configSource.Configs["Startup"].GetBoolean("background", false);

            // Check if we're saving crashes
            m_saveCrashDumps = m_configSource.Configs["Startup"].GetBoolean("save_crashes", false);

            // load Crash directory config
            m_crashDir = m_configSource.Configs["Startup"].GetString("crash_dir", m_crashDir);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("StopAsync has been called.");

            return Task.CompletedTask;
        }

        private void OnStarted()
        {
            _logger.LogInformation("OnStarted has been called.");

            try
            {
                m_sim = new OpenSim(m_configSource);
                m_sim.Startup();

                while (true)
                {
                    try
                    {
                        // Block thread here for input
                        MainConsole.Instance.Prompt();
                    }
                    catch (Exception e)
                    {
                        _logger.LogError("Command error: {e}", e);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                // This means the application is shutting down, so just swallow this exception
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception!");
            }
            finally
            {
                _hostApplicationLifetime.StopApplication();
            }
        }

        private void OnStopping()
        {
            _logger.LogInformation("3. OnStopping has been called.");
        }

        private void OnStopped()
        {
            _logger.LogInformation("5. OnStopped has been called.");
        }
    }
}
