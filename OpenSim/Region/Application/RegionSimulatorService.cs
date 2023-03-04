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
    public class RegionSimulatorService : IHostedService
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

        private Task? _applicationTask;
        private int? _exitCode;

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

        protected void Initialize(string[] args)
        {
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

            // temporay set the platform dependent System.Drawing.Common.dll
            string targetdll = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                        "System.Drawing.Common.dll");
            string src = targetdll + (Util.IsWindows() ? ".win" : ".linux");
            try
            {
                if (!File.Exists(targetdll))
                    File.Copy(src, targetdll);
                else
                {
                    FileInfo targetInfo = new(targetdll);
                    FileInfo srcInfo = new(src);
                    if (targetInfo.Length != srcInfo.Length)
                        File.Copy(src, targetdll, true);
                }
            }
            catch (Exception e)
            {
                m_log.Error("Failed to copy System.Drawing.Common.dll for current platform" + e.Message);
                throw;
            }

            m_log.InfoFormat(
                "[OPENSIM MAIN]: System Locale is {0}", System.Threading.Thread.CurrentThread.CurrentCulture);
            if (!Util.IsWindows())
            {
                string monoThreadsPerCpu = System.Environment.GetEnvironmentVariable("MONO_THREADS_PER_CPU");
                m_log.InfoFormat(
                    "[OPENSIM MAIN]: Environment variable MONO_THREADS_PER_CPU is {0}", monoThreadsPerCpu ?? "unset");
            }

            // Verify the Threadpool allocates or uses enough worker and IO completion threads
            // .NET 2.0, workerthreads default to 50 *  numcores
            // .NET 3.0, workerthreads defaults to 250 * numcores
            // .NET 4.0, workerthreads are dynamic based on bitness and OS resources
            // Max IO Completion threads are 1000 on all 3 CLRs
            //
            // Mono 2.10.9 to at least Mono 3.1, workerthreads default to 100 * numcores, iocp threads to 4 * numcores
            int workerThreadsMin = 500;
            int workerThreadsMax = 1000; // may need further adjustment to match other CLR
            int iocpThreadsMin = 1000;
            int iocpThreadsMax = 2000; // may need further adjustment to match other CLR

            System.Threading.ThreadPool.GetMinThreads(out int currentMinWorkerThreads, out int currentMinIocpThreads);
            m_log.InfoFormat(
                "[OPENSIM MAIN]: Runtime gave us {0} min worker threads and {1} min IOCP threads",
                currentMinWorkerThreads, currentMinIocpThreads);

            System.Threading.ThreadPool.GetMaxThreads(out int workerThreads, out int iocpThreads);
            m_log.InfoFormat("[OPENSIM MAIN]: Runtime gave us {0} max worker threads and {1} max IOCP threads", workerThreads, iocpThreads);

            if (workerThreads < workerThreadsMin)
            {
                workerThreads = workerThreadsMin;
                m_log.InfoFormat("[OPENSIM MAIN]: Bumping up max worker threads to {0}", workerThreads);
            }
            if (workerThreads > workerThreadsMax)
            {
                workerThreads = workerThreadsMax;
                m_log.InfoFormat("[OPENSIM MAIN]: Limiting max worker threads to {0}", workerThreads);
            }

            // Increase the number of IOCP threads available.
            // Mono defaults to a tragically low number (24 on 6-core / 8GB Fedora 17)
            if (iocpThreads < iocpThreadsMin)
            {
                iocpThreads = iocpThreadsMin;
                m_log.InfoFormat("[OPENSIM MAIN]: Bumping up max IOCP threads to {0}", iocpThreads);
            }
            // Make sure we don't overallocate IOCP threads and thrash system resources
            if (iocpThreads > iocpThreadsMax)
            {
                iocpThreads = iocpThreadsMax;
                m_log.InfoFormat("[OPENSIM MAIN]: Limiting max IOCP completion threads to {0}", iocpThreads);
            }
            // set the resulting worker and IO completion thread counts back to ThreadPool
            if (System.Threading.ThreadPool.SetMaxThreads(workerThreads, iocpThreads))
            {
                m_log.InfoFormat(
                    "[OPENSIM MAIN]: Threadpool set to {0} max worker threads and {1} max IOCP threads",
                    workerThreads, iocpThreads);
            }
            else
            {
                m_log.Warn("[OPENSIM MAIN]: Threadpool reconfiguration failed, runtime defaults still in effect.");
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
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var args = Environment.GetCommandLineArgs();
            _logger.LogDebug($"Starting with arguments: {string.Join(" ", args)}");

            this.Initialize(args);

            CancellationTokenSource _cancellationTokenSource = null;

            _hostApplicationLifetime.ApplicationStarted.Register(() =>
            {
                _logger.LogDebug("Application has started");
                _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                _applicationTask = Task.Run(async () =>
                {
                    try
                    {
                        var startupConfig = _configuration.GetSection("Startup");
                        if (startupConfig == null)
                        {
                            throw new Exception("No Startup Configuration Defined");
                        }

                        OpenSimBase m_sim = null;

                        if (m_background == true)
                        {
                            // No console
                            m_sim = new OpenSimBackground(m_configSource);
                            m_sim.Startup();
                        }
                        else
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

                        _exitCode = 0;
                    }
                    catch (TaskCanceledException)
                    {
                        // This means the application is shutting down, so just swallow this exception
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unhandled exception!");
                        _exitCode = 1;
                    }
                    finally
                    {
                        // Stop the application once the work is done
                        _hostApplicationLifetime.StopApplication();
                    }
                });
            });

            _hostApplicationLifetime.ApplicationStopping.Register(() =>
            {
                _logger.LogDebug("Application is stopping");
                _cancellationTokenSource?.Cancel();
            });

            return Task.CompletedTask;
        }

        private void OnStarted()
        {
            _logger.LogInformation("2. OnStarted has been called.");
        }

        private void OnStopping()
        {
            _logger.LogInformation("3. OnStopping has been called.");
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            // Wait for the application logic to fully complete any cleanup tasks.
            // Note that this relies on the cancellation token to be properly used in the application.
            if (_applicationTask != null)
            {
                await _applicationTask;
            }

            _logger.LogDebug($"Exiting with return code: {_exitCode}");

            // Exit code may be null if the user cancelled via Ctrl+C/SIGTERM
            Environment.ExitCode = _exitCode.GetValueOrDefault(-1);
        }

        private void OnStopped()
        {
            _logger.LogInformation("5. OnStopped has been called.");
        }
    }
}
