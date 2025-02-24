using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Xsl;
using Xamarin;
using Xamarin.Utils;

namespace xharness
{
	public enum AppRunnerTarget
	{
		None,
		Simulator_iOS,
		Simulator_iOS32,
		Simulator_iOS64,
		Simulator_tvOS,
		Simulator_watchOS,
		Device_iOS,
		Device_tvOS,
		Device_watchOS,
	}

	public enum Extension
	{
		WatchKit2,
		TodayExtension,
	}

	public class AppRunner
	{
		public Harness Harness;
		public string ProjectFile;
		public string AppPath;

		public TestExecutingResult Result { get; private set; }
		public string FailureMessage { get; private set; }

		string appName;
		string appPath;
		string launchAppPath;
		string bundle_identifier;
		string platform;
		Extension? extension;
		bool isSimulator;

		string device_name;
		string companion_device_name;

		string configuration;
		public string Configuration {
			get { return configuration ?? Harness.Configuration; }
			set { configuration = value; }
		}

		public string DeviceName {
			get { return device_name; }
			set { device_name = value; }
		}

		public string CompanionDeviceName {
			get { return companion_device_name; }
			set { companion_device_name = value; }
		}

		public bool isExtension {
			get {
				return extension.HasValue;
			}
		}

		public double TimeoutMultiplier { get; set; } = 1;

		// For watch apps we end up with 2 simulators, the watch simulator (the main one), and the iphone simulator (the companion one).
		SimDevice[] simulators;
		SimDevice simulator { get { return simulators [0]; } }
		SimDevice companion_simulator { get { return simulators.Length == 2 ? simulators [1] : null; } }

		AppRunnerTarget target;
		public AppRunnerTarget Target {
			get { return target == AppRunnerTarget.None ? Harness.Target : target; }
			set { target = value; }
		}

		string log_directory;
		public string LogDirectory {
			get { return log_directory ?? Harness.LogDirectory; }
			set { log_directory = value; }
		}

		Logs logs;
		public Logs Logs {
			get {
				return logs ?? (logs = new Logs (LogDirectory));
			}
		}

		Log main_log;
		public Log MainLog {
			get { return main_log; }
			set { main_log = value; }
		}

		public SimDevice [] Simulators {
			get { return simulators; }
			set { simulators = value; }
		}

		public string BundleIdentifier {
			get {
				return bundle_identifier;
			}
		}

		string mode;

		async Task<bool> FindSimulatorAsync ()
		{
			if (simulators != null)
				return true;
			
			var sims = new Simulators () {
				Harness = Harness,
			};
			await sims.LoadAsync (Logs.Create ($"simulator-list-{Harness.Timestamp}.log", "Simulator list"));
			simulators = await sims.FindAsync (Target, main_log);

			return simulators != null;
		}

		void FindDevice ()
		{
			if (device_name != null)
				return;
			
			device_name = Environment.GetEnvironmentVariable ("DEVICE_NAME");
			if (!string.IsNullOrEmpty (device_name))
				return;

			var devs = new Devices () {
				Harness = Harness,
			};
			Task.Run (async () =>
			{
				await devs.LoadAsync (main_log);
			}).Wait ();

			string [] deviceClasses;
			switch (mode) {
			case "ios":
				deviceClasses = new string [] { "iPhone", "iPad", "iPod" };
				break;
			case "watchos":
				deviceClasses = new string [] { "Watch" };
				break;
			case "tvos":
				deviceClasses = new string [] { "AppleTV" }; // Untested
				break;
			default:
				throw new Exception ($"unknown mode: {mode}");
			}

			var selected = devs.ConnectedDevices.Where ((v) => deviceClasses.Contains (v.DeviceClass) && v.IsUsableForDebugging != false);
			Device selected_data;
			if (selected.Count () == 0) {
				throw new Exception ($"Could not find any applicable devices with device class(es): {string.Join (", ", deviceClasses)}");
			} else if (selected.Count () > 1) {
				selected_data = selected
					.OrderBy ((dev) =>
					{
						Version v;
						if (Version.TryParse (dev.ProductVersion, out v))
							return v;
						return new Version ();
					})
					.First ();
				main_log.WriteLine ("Found {0} devices for device class(es) '{1}': '{2}'. Selected: '{3}' (because it has the lowest version).", selected.Count (), string.Join ("', '", deviceClasses), string.Join ("', '", selected.Select ((v) => v.Name).ToArray ()), selected_data.Name);
			} else {
				selected_data = selected.First ();
			}
			device_name = selected_data.Name;

			if (mode == "watchos")
				companion_device_name = devs.FindCompanionDevice (main_log, selected_data).Name;
		}

		bool initialized;
		public void Initialize ()
		{
			if (initialized)
				return;
			initialized = true;

			var csproj = new XmlDocument ();
			csproj.LoadWithoutNetworkAccess (ProjectFile);
			appName = csproj.GetAssemblyName ();
			var info_plist_path = csproj.GetInfoPListInclude ();
			var info_plist = new XmlDocument ();
			info_plist.LoadWithoutNetworkAccess (Path.Combine (Path.GetDirectoryName (ProjectFile), info_plist_path.Replace ('\\', '/')));
			bundle_identifier = info_plist.GetCFBundleIdentifier ();

			var extensionPointIdentifier = info_plist.GetNSExtensionPointIdentifier ();
			if (!string.IsNullOrEmpty (extensionPointIdentifier))
				extension = extensionPointIdentifier.ParseFromNSExtensionPointIdentifier ();

			switch (Target) {
			case AppRunnerTarget.Simulator_iOS32:
				mode = "sim32";
				platform = "iPhoneSimulator";
				isSimulator = true;
				break;
			case AppRunnerTarget.Simulator_iOS64:
				mode = "sim64";
				platform = "iPhoneSimulator";
				isSimulator = true;
				break;
			case AppRunnerTarget.Simulator_iOS:
				mode = "classic";
				platform = "iPhoneSimulator";
				isSimulator = true;
				break;
			case AppRunnerTarget.Device_iOS:
				mode = "ios";
				platform = "iPhone";
				isSimulator = false;
				break;
			case AppRunnerTarget.Simulator_tvOS:
				mode = "tvos";
				platform = "iPhoneSimulator";
				isSimulator = true;
				break;
			case AppRunnerTarget.Device_tvOS:
				mode = "tvos";
				platform = "iPhone";
				isSimulator = false;
				break;
			case AppRunnerTarget.Simulator_watchOS:
				mode = "watchos";
				platform = "iPhoneSimulator";
				isSimulator = true;
				break;
			case AppRunnerTarget.Device_watchOS:
				mode = "watchos";
				platform = "iPhone";
				isSimulator = false;
				break;
			default:
				throw new Exception (string.Format ("Unknown target: {0}", Harness.Target));
			}

			appPath = Path.Combine (Path.GetDirectoryName (ProjectFile), csproj.GetOutputPath (platform, Configuration).Replace ('\\', '/'), appName + (isExtension ? ".appex" : ".app"));
			if (!Directory.Exists (appPath))
				throw new Exception (string.Format ("The app directory {0} does not exist. This is probably a bug in the test harness.", appPath));

			if (mode == "watchos") {
				launchAppPath = Directory.GetDirectories (Path.Combine (appPath, "Watch"), "*.app") [0];
			} else {
				launchAppPath = appPath;
			}
		}

		public async Task<ProcessExecutionResult> InstallAsync (CancellationToken cancellation_token)
		{
			Initialize ();

			if (isSimulator) {
				// We reset the simulator when running, so a separate install step does not make much sense.
				throw new Exception ("Installing to a simulator is not supported.");
			}

			FindDevice ();

			var args = new StringBuilder ();
			if (!string.IsNullOrEmpty (Harness.XcodeRoot))
				args.Append (" --sdkroot ").Append (Harness.XcodeRoot);
			for (int i = -1; i < Harness.Verbosity; i++)
				args.Append (" -v ");
			
			args.Append (" --installdev");
			args.AppendFormat (" \"{0}\" ", appPath);
			AddDeviceName (args, companion_device_name ?? device_name);

			if (mode == "watchos")
				args.Append (" --device ios,watchos");

			var totalSize = Directory.GetFiles (appPath, "*", SearchOption.AllDirectories).Select ((v) => new FileInfo (v).Length).Sum ();
			main_log.WriteLine ($"Installing '{appPath}' to '{companion_device_name ?? device_name}'. Size: {totalSize} bytes = {totalSize / 1024.0 / 1024.0:N2} MB");

			return await ProcessHelper.ExecuteCommandAsync (Harness.MlaunchPath, args.ToString (), main_log, TimeSpan.FromHours (1), cancellation_token: cancellation_token);
		}

		public async Task<ProcessExecutionResult> UninstallAsync ()
		{
			Initialize ();

			if (isSimulator)
				throw new Exception ("Uninstalling from a simulator is not supported.");

			FindDevice ();

			var args = new StringBuilder ();
			if (!string.IsNullOrEmpty (Harness.XcodeRoot))
				args.Append (" --sdkroot ").Append (Harness.XcodeRoot);
			for (int i = -1; i < Harness.Verbosity; i++)
				args.Append (" -v ");

			args.Append (" --uninstalldevbundleid");
			args.AppendFormat (" \"{0}\" ", bundle_identifier);
			AddDeviceName (args, companion_device_name ?? device_name);

			return await ProcessHelper.ExecuteCommandAsync (Harness.MlaunchPath, args.ToString (), main_log, TimeSpan.FromMinutes (1));
		}

		bool ensure_clean_simulator_state = true;
		public bool EnsureCleanSimulatorState {
			get {
				return ensure_clean_simulator_state && string.IsNullOrEmpty (Environment.GetEnvironmentVariable ("SKIP_SIMULATOR_SETUP"));
			}
			set {
				ensure_clean_simulator_state = value;
			}
		}

		bool IsTouchUnitResult (StreamReader stream)
		{
			// TouchUnitTestRun is the very first node in the TouchUnit xml result
			// which is not preset in the xunit xml, therefore we know the runner
			// quite quickly
			bool isTouchUnit = false;
			using (var reader = XmlReader.Create (stream)) {
				while (reader.Read ()) {
					if (reader.NodeType == XmlNodeType.Element && reader.Name == "TouchUnitTestRun") {
						isTouchUnit = true;
						break;
					}
				}
			}
			// we want to reuse the stream (and we are sync)
			stream.BaseStream.Position = 0;
			stream.DiscardBufferedData ();
			return isTouchUnit;
		}

		(string resultLine, bool failed) ParseTouchUnitXml (StreamReader stream, StreamWriter writer)
		{
			long total, errors, failed, notRun, inconclusive, ignored, skipped, invalid;
			total = errors = failed = notRun = inconclusive = ignored = skipped = invalid = 0L;
			using (var reader = XmlReader.Create (stream)) {
				while (reader.Read ()) {
					if (reader.NodeType == XmlNodeType.Element && reader.Name == "test-results") {
						total = long.Parse (reader ["total"]);
						errors = long.Parse (reader ["errors"]);
						failed = long.Parse (reader ["failures"]);
						notRun = long.Parse (reader ["not-run"]);
						inconclusive = long.Parse (reader ["inconclusive"]);
						ignored = long.Parse (reader ["ignored"]);
						skipped = long.Parse (reader ["skipped"]);
						invalid = long.Parse (reader ["invalid"]);
					}
					if (reader.NodeType == XmlNodeType.Element && reader.Name == "TouchUnitExtraData") {
						// move fwd to get to the CData
						if (reader.Read ())
							writer.Write (reader.Value);
					}
				}
			}
			var passed = total - errors - failed - notRun - inconclusive - ignored - skipped - invalid;
			var resultLine = $"Tests run: {total} Passed: {passed} Inconclusive: {inconclusive} Failed: {failed + errors} Ignored: {ignored + skipped + invalid}";
			return (resultLine, total == 0 || errors != 0 || failed != 0);
		}

		(string resultLine, bool failed) ParseNUnitXml (StreamReader stream, StreamWriter writer)
		{
			long total, errors, failed, notRun, inconclusive, ignored, skipped, invalid;
			total = errors = failed = notRun = inconclusive = ignored = skipped = invalid = 0L;
			using (var reader = XmlReader.Create (stream)) {
				while (reader.Read ()) {
					if (reader.NodeType == XmlNodeType.Element && reader.Name == "test-results") {
						total = long.Parse (reader ["total"]);
						errors = long.Parse (reader ["errors"]);
						failed = long.Parse (reader ["failures"]);
						notRun = long.Parse (reader ["not-run"]);
						inconclusive = long.Parse (reader ["inconclusive"]);
						ignored = long.Parse (reader ["ignored"]);
						skipped = long.Parse (reader ["skipped"]);
						invalid = long.Parse (reader ["invalid"]);
					}
					if (reader.NodeType == XmlNodeType.Element && reader.Name == "test-suite" && (reader["type"] == "TestFixture" || reader["type"] == "TestCollection")) {
						var testCaseName = reader ["name"];
						writer.WriteLine (testCaseName);
						var time = reader.GetAttribute ("time") ?? "0"; // some nodes might not have the time :/
						// get the first node and then move in the siblings of the same type
						reader.ReadToDescendant ("test-case");
						do {
							if (reader.Name != "test-case")
								break;
							// read the test cases in the current node
							var status = reader ["result"];
							switch (status) {
							case "Success":
								writer.Write ("\t[PASS] ");
								break;
							case "Ignored":
								writer.Write ("\t[IGNORED] ");
								break;
							case "Error":
							case "Failure":
								writer.Write ("\t[FAIL] ");
								break;
							case "Inconclusive":
								writer.Write ("\t[INCONCLUSIVE] ");
								break;
							default:
								writer.Write ("\t[INFO] ");
								break;
							}
							writer.Write (reader ["name"]);
							if (status == "Failure" || status == "Error") { //  we need to print the message
								writer.Write ($" : {reader.ReadElementContentAsString ()}");
							}
							// add a new line
							writer.WriteLine ();
						} while (reader.ReadToNextSibling ("test-case"));
						writer.WriteLine ($"{testCaseName} {time} ms");
					}
				}
			}
			var passed = total - errors - failed - notRun - inconclusive - ignored - skipped - invalid;
			string resultLine = $"Tests run: {total} Passed: {passed} Inconclusive: {inconclusive} Failed: {failed + errors} Ignored: {ignored + skipped + invalid}";
			writer.WriteLine (resultLine);
			
			return (resultLine, total == 0 | errors != 0 || failed != 0);
		}
		
		(string resultLine, bool failed, bool crashed) ParseResult (Log listener_log, bool timed_out, bool crashed)
		{
			if (!File.Exists (listener_log.FullPath))
				return (null, false, true); // if we do not have a log file, the test crashes

			// parsing the result is different if we are in jenkins or not.
			// When in Jenkins, Touch.Unit produces an xml file instead of a console log (so that we can get better test reporting).
			// However, for our own reporting, we still want the console-based log. This log is embedded inside the xml produced
			// by Touch.Unit, so we need to extract it and write it to disk. We also need to re-save the xml output, since Touch.Unit
			// wraps the NUnit xml output with additional information, which we need to unwrap so that Jenkins understands it.
			// 
			// On the other hand, the nunit and xunit do not have that data and have to be parsed.
			if (Harness.InJenkins) {
				(string resultLine, bool failed, bool crashed) parseResult = (null, false, false);
				// move the xml to a tmp path, that path will be use to read the xml
				// in the reader, and the writer will use the stream from the logger to
				// write the human readable log
				var tmpFile = Path.Combine (Path.GetTempPath (), Guid.NewGuid ().ToString ()); 

				File.Move (listener_log.FullPath, tmpFile);
				crashed = false;
				try {
					using (var streamReaderTmp = new StreamReader (tmpFile)) {
						var isTouchUnit = IsTouchUnitResult (streamReaderTmp); // method resets position
						using (var writer = new StreamWriter (listener_log.FullPath, true)) { // write the human result to the log file
							if (isTouchUnit) {
								var (resultLine, failed)= ParseTouchUnitXml (streamReaderTmp, writer);
								parseResult.resultLine = resultLine;
								parseResult.failed = failed;
							} else {
								var (resultLine, failed)= ParseNUnitXml (streamReaderTmp, writer);
								parseResult.resultLine = resultLine;
								parseResult.failed = failed;
							}
						}
						// reset pos of the stream
						streamReaderTmp.BaseStream.Position = 0;
						streamReaderTmp.DiscardBufferedData ();
						var path = listener_log.FullPath;
						path = Path.ChangeExtension (path, "xml");
						// both the nunit and xunit runners are not
						// setting the test results correctly, lets add them
						using (var xmlWriter = new StreamWriter (path)) {
							string line;
							while ((line = streamReaderTmp.ReadLine ()) != null) {
								if (line.Contains ("<test-results")) {
									if (line.Contains ("name=\"\"")) { // NUnit case
										xmlWriter.WriteLine (line.Replace ("name=\"\"", $"name=\"{appName + " " + configuration}\""));
										xmlWriter.WriteLine (line);
									} else if (line.Contains ($"name=\"com.xamarin.bcltests.{appName}\"")) { // xunit case
										xmlWriter.WriteLine (line.Replace ($"name=\"com.xamarin.bcltests.{appName}\"", $"name=\"{appName + " " + configuration}\""));
									}
								} else {
									xmlWriter.WriteLine (line);
								}
							}
						}
						// we do not longer need the tmp file
						Logs.AddFile (path, "Test xml");
					}
					return parseResult;
				} catch (Exception e) {
					main_log.WriteLine ("Could not parse xml result file: {0}", e);

					if (timed_out) {
						Harness.LogWrench ($"@MonkeyWrench: AddSummary: <b><i>{mode} timed out</i></b><br/>");
						return parseResult;
					} else {
						Harness.LogWrench ($"@MonkeyWrench: AddSummary: <b><i>{mode} crashed</i></b><br/>");
						main_log.WriteLine ("Test run crashed");
						crashed = true;
						parseResult.crashed = true;
						return parseResult;
					}
				} finally {
					if (File.Exists (tmpFile))
						File.Delete (tmpFile);
				}
				
			} else {
				// not the most efficient way but this just happens when we run
				// the tests locally and we usually do not run all tests, we are
				// more interested to be efficent on the bots
				string resultLine = null;
				using (var reader = new StreamReader (listener_log.FullPath)) {
					string line = null;
					bool failed = false;
					while ((line = reader.ReadLine ()) != null)
					{
						if (line.Contains ("Tests run:")) {
							Console.WriteLine (line);
							resultLine = line;
							break;
						} else if (line.Contains ("[FAIL]")) {
							Console.WriteLine (line);
							failed = true;
						}
					}
					return (resultLine, failed, false);
				}
			}
		}

		public bool TestsSucceeded (Log listener_log, bool timed_out, bool crashed)
		{
			var (resultLine, failed, crashed_out) = ParseResult (listener_log, timed_out, crashed);
			// read the parsed logs in a human readable way
			if (resultLine != null) {
				var tests_run = resultLine.Replace ("Tests run: ", "");
				if (failed) {
					Harness.LogWrench ("@MonkeyWrench: AddSummary: <b>{0} failed: {1}</b><br/>", mode, tests_run);
					main_log.WriteLine ("Test run failed");
					return false;
				} else {
					Harness.LogWrench ("@MonkeyWrench: AddSummary: {0} succeeded: {1}<br/>", mode, tests_run);
					main_log.WriteLine ("Test run succeeded");
					return true;
				}
			} else if (timed_out) {
				Harness.LogWrench ("@MonkeyWrench: AddSummary: <b><i>{0} timed out</i></b><br/>", mode);
				return false;
			} else {
				Harness.LogWrench ("@MonkeyWrench: AddSummary: <b><i>{0} crashed</i></b><br/>", mode);
				main_log.WriteLine ("Test run crashed");
				crashed = true;
				return false;
			}
		}

		[DllImport ("/usr/lib/libc.dylib")]
		extern static IntPtr ttyname (int filedes);

		public async Task<int> RunAsync ()
		{
			CrashReportSnapshot crash_reports;
			Log device_system_log = null;
			Log listener_log = null;
			Log run_log = main_log;

			Initialize ();

			if (!isSimulator)
				FindDevice ();

			crash_reports = new CrashReportSnapshot ()
			{
				Device = !isSimulator,
				DeviceName = device_name,
				Harness = Harness,
				Log = main_log,
				Logs = Logs,
				LogDirectory = LogDirectory,
			};

			var args = new StringBuilder ();
			if (!string.IsNullOrEmpty (Harness.XcodeRoot))
				args.Append (" --sdkroot ").Append (Harness.XcodeRoot);
			for (int i = -1; i < Harness.Verbosity; i++)
				args.Append (" -v ");
			args.Append (" -argument=-connection-mode -argument=none"); // This will prevent the app from trying to connect to any IDEs
			args.Append (" -argument=-app-arg:-autostart");
			args.Append (" -setenv=NUNIT_AUTOSTART=true");
			args.Append (" -argument=-app-arg:-autoexit");
			args.Append (" -setenv=NUNIT_AUTOEXIT=true");
			args.Append (" -argument=-app-arg:-enablenetwork");
			args.Append (" -setenv=NUNIT_ENABLE_NETWORK=true");
			// detect if we are using a jenkins bot.
			var useXmlOutput = Harness.InJenkins;
			if (useXmlOutput) {
				args.Append (" -setenv=NUNIT_ENABLE_XML_OUTPUT=true");
				args.Append (" -setenv=NUNIT_ENABLE_XML_MODE=wrapped");
			}

			if (Harness.InCI) {
				// We use the 'BUILD_REVISION' variable to detect whether we're running CI or not.
				args.Append ($" -setenv=BUILD_REVISION=${Environment.GetEnvironmentVariable ("BUILD_REVISION")}");
			}

			if (!Harness.IncludeSystemPermissionTests)
				args.Append (" -setenv=DISABLE_SYSTEM_PERMISSION_TESTS=1");

			if (isSimulator) {
				args.Append (" -argument=-app-arg:-hostname:127.0.0.1");
				args.Append (" -setenv=NUNIT_HOSTNAME=127.0.0.1");
			} else {
				var ips = new StringBuilder ();
				var ipAddresses = System.Net.Dns.GetHostEntry (System.Net.Dns.GetHostName ()).AddressList;
				for (int i = 0; i < ipAddresses.Length; i++) {
					if (i > 0)
						ips.Append (',');
					ips.Append (ipAddresses [i].ToString ());
				}

				args.AppendFormat (" -argument=-app-arg:-hostname:{0}", ips.ToString ());
				args.AppendFormat (" -setenv=NUNIT_HOSTNAME={0}", ips.ToString ());
			}
			string transport;
			if (mode == "watchos") {
				transport = isSimulator ? "FILE" : "HTTP";
			} else {
				transport = "TCP";
			}
			args.AppendFormat (" -argument=-app-arg:-transport:{0}", transport);
			args.AppendFormat (" -setenv=NUNIT_TRANSPORT={0}", transport);

			listener_log = Logs.Create ($"test-{mode}-{Harness.Timestamp}.log", "Test log", timestamp: !useXmlOutput);

			SimpleListener listener;
			switch (transport) {
			case "FILE":
				var fn = listener_log.FullPath + ".tmp";
				listener = new SimpleFileListener (fn);
				args.Append (" -setenv=NUNIT_LOG_FILE=").Append (StringUtils.Quote (fn));
				break;
			case "HTTP":
				listener = new SimpleHttpListener ();
				break;
			case "TCP":
				listener = new SimpleTcpListener ();
				break;
			default:
				throw new NotImplementedException ();
			}
			listener.TestLog = listener_log;
			listener.Log = main_log;
			listener.AutoExit = true;
			listener.Address = System.Net.IPAddress.Any;
			listener.XmlOutput = useXmlOutput;
			listener.Initialize ();

			args.AppendFormat (" -argument=-app-arg:-hostport:{0}", listener.Port);
			args.AppendFormat (" -setenv=NUNIT_HOSTPORT={0}", listener.Port);

			listener.StartAsync ();

			var cancellation_source = new CancellationTokenSource ();
			var timed_out = false;

			listener.ConnectedTask
				.TimeoutAfter (TimeSpan.FromMinutes (Harness.LaunchTimeout))
				.ContinueWith ((v) => {
					if (v.IsFaulted) {
						main_log.WriteLine ("Test launch failed: {0}", v.Exception);
					} else if (v.IsCanceled) {
						main_log.WriteLine ("Test launch was cancelled.");
					} else if (v.Result) {
						main_log.WriteLine ("Test run started");
					} else {
						cancellation_source.Cancel ();
						main_log.WriteLine ("Test launch timed out after {0} minute(s).", Harness.LaunchTimeout);
						timed_out = true;
					}
				}).DoNotAwait ();

			foreach (var kvp in Harness.EnvironmentVariables)
				args.AppendFormat (" -setenv={0}={1}", kvp.Key, kvp.Value);

			bool? success = null;
			bool launch_failure = false;

			if (isExtension) {
				switch (extension) {
				case Extension.TodayExtension:
					args.Append (isSimulator ? " --launchsimbundleid" : " --launchdevbundleid");
					args.Append (" todayviewforextensions:");
					args.Append (BundleIdentifier);
					args.Append (" --observe-extension ");
					args.Append (StringUtils.Quote (launchAppPath));
					break;
				case Extension.WatchKit2:
				default:
					throw new NotImplementedException ();
				}
			} else {
				args.Append (isSimulator ? " --launchsim " : " --launchdev ");
				args.Append (StringUtils.Quote (launchAppPath));
			}
			if (!isSimulator)
				args.Append (" --disable-memory-limits");

			var timeout = TimeSpan.FromMinutes (Harness.Timeout * TimeoutMultiplier);
			if (isSimulator) {
				if (!await FindSimulatorAsync ())
					return 1;

				if (mode != "watchos") {
					var stderr_tty = Marshal.PtrToStringAuto (ttyname (2));
					if (!string.IsNullOrEmpty (stderr_tty)) {
						args.Append (" --stdout=").Append (StringUtils.Quote (stderr_tty));
						args.Append (" --stderr=").Append (StringUtils.Quote (stderr_tty));
					} else {
						var stdout_log = Logs.CreateFile ($"stdout-{Harness.Timestamp}.log", "Standard output");
						var stderr_log = Logs.CreateFile ($"stderr-{Harness.Timestamp}.log", "Standard error");
						args.Append (" --stdout=").Append (StringUtils.Quote (stdout_log));
						args.Append (" --stderr=").Append (StringUtils.Quote (stderr_log));
					}
				}

				var systemLogs = new List<CaptureLog> ();
				foreach (var sim in simulators) {
					// Upload the system log
					main_log.WriteLine ("System log for the '{1}' simulator is: {0}", sim.SystemLog, sim.Name);
					bool isCompanion = sim != simulator;

					var log = new CaptureLog (Logs, sim.SystemLog, entire_file: Harness.Action != HarnessAction.Jenkins)
					{
						Path = Path.Combine (LogDirectory, sim.Name + ".log"),
						Description = isCompanion ? "System log (companion)" : "System log",
					};
					log.StartCapture ();
					Logs.Add (log);
					systemLogs.Add (log);
					Harness.LogWrench ("@MonkeyWrench: AddFile: {0}", log.Path);
				}

				main_log.WriteLine ("*** Executing {0}/{1} in the simulator ***", appName, mode);

				if (EnsureCleanSimulatorState) {
					foreach (var sim in simulators)
						await sim.PrepareSimulatorAsync (main_log, bundle_identifier);
				}

				args.Append (" --device=:v2:udid=").Append (simulator.UDID).Append (" ");

				await crash_reports.StartCaptureAsync ();

				main_log.WriteLine ("Starting test run");

				var result = await ProcessHelper.ExecuteCommandAsync (Harness.MlaunchPath, args.ToString (), run_log, timeout, cancellation_token: cancellation_source.Token);
				if (result.TimedOut) {
					timed_out = true;
					success = false;
					main_log.WriteLine ("Test run timed out after {0} minute(s).", timeout);
				} else if (result.Succeeded) {
					main_log.WriteLine ("Test run completed");
					success = true;
				} else {
					main_log.WriteLine ("Test run failed");
					success = false;
				}

				if (!success.Value) {
					// find pid
					var pid = -1;
					using (var reader = run_log.GetReader ()) {
						while (!reader.EndOfStream) {
							var line = reader.ReadLine ();
							if (line.StartsWith ("Application launched. PID = ", StringComparison.Ordinal)) {
								var pidstr = line.Substring ("Application launched. PID = ".Length);
								if (!int.TryParse (pidstr, out pid))
									main_log.WriteLine ("Could not parse pid: {0}", pidstr);
							} else if (line.Contains ("Xamarin.Hosting: Launched ") && line.Contains (" with pid ")) {
								var pidstr = line.Substring (line.LastIndexOf (' '));
								if (!int.TryParse (pidstr, out pid))
									main_log.WriteLine ("Could not parse pid: {0}", pidstr);
							} else if (line.Contains ("error MT1008")) {
								launch_failure = true;
							}
						}
					}
					if (pid > 0) {
						var launchTimedout = cancellation_source.IsCancellationRequested;
						var timeoutType = launchTimedout ? "Launch" : "Completion";
						var timeoutValue = launchTimedout ? Harness.LaunchTimeout : timeout.TotalSeconds;
						main_log.WriteLine ($"{timeoutType} timed out after {timeoutValue} seconds");
						await Process_Extensions.KillTreeAsync (pid, main_log, true);
					} else {
						main_log.WriteLine ("Could not find pid in mtouch output.");
					}
				}


				// cleanup after us
				if (EnsureCleanSimulatorState)
					await SimDevice.KillEverythingAsync (main_log);

				foreach (var log in systemLogs)
					log.StopCapture ();
				
			} else {
				main_log.WriteLine ("*** Executing {0}/{1} on device '{2}' ***", appName, mode, device_name);

				if (mode == "watchos") {
					args.Append (" --attach-native-debugger"); // this prevents the watch from backgrounding the app.
				} else {
					args.Append (" --wait-for-exit");
				}
				
				AddDeviceName (args);

				device_system_log = Logs.Create ($"device-{device_name}-{Harness.Timestamp}.log", "Device log");
				var logdev = new DeviceLogCapturer () {
					Harness =  Harness,
					Log = device_system_log,
					DeviceName = device_name,
				};
				logdev.StartCapture ();

				await crash_reports.StartCaptureAsync ();

				main_log.WriteLine ("Starting test run");

				bool waitedForExit = true;
				// We need to check for MT1111 (which means that mlaunch won't wait for the app to exit).
				var callbackLog = new CallbackLog ((line) => {
					// MT1111: Application launched successfully, but it's not possible to wait for the app to exit as requested because it's not possible to detect app termination when launching using gdbserver
					waitedForExit &= line?.Contains ("MT1111: ") != true;
					if (line?.Contains ("error MT1007") == true)
						launch_failure = true;
				});
				var runLog = Log.CreateAggregatedLog (callbackLog, main_log);
				var timeoutWatch = Stopwatch.StartNew ();
				var result = await ProcessHelper.ExecuteCommandAsync (Harness.MlaunchPath, args.ToString (), runLog, timeout, cancellation_token: cancellation_source.Token);

				if (!waitedForExit && !result.TimedOut) {
					// mlaunch couldn't wait for exit for some reason. Let's assume the app exits when the test listener completes.
					main_log.WriteLine ("Waiting for listener to complete, since mlaunch won't tell.");
					if (!await listener.CompletionTask.TimeoutAfter (timeout - timeoutWatch.Elapsed)) {
						result.TimedOut = true;
					}
				}

				if (result.TimedOut) {
					timed_out = true;
					success = false;
					main_log.WriteLine ("Test run timed out after {0} minute(s).", timeout.TotalMinutes);
				} else if (result.Succeeded) {
					main_log.WriteLine ("Test run completed");
					success = true;
				} else {
					main_log.WriteLine ("Test run failed");
					success = false;
				}

				logdev.StopCapture ();
				device_system_log.Dispose ();

				// Upload the system log
				if (File.Exists (device_system_log.FullPath)) {
					main_log.WriteLine ("A capture of the device log is: {0}", device_system_log.FullPath);
					Harness.LogWrench ("@MonkeyWrench: AddFile: {0}", device_system_log.FullPath);
				}
			}

			listener.Cancel ();
			listener.Dispose ();

			// check the final status
			var crashed = false;
			if (File.Exists (listener_log.FullPath)) {
				Harness.LogWrench ("@MonkeyWrench: AddFile: {0}", listener_log.FullPath);
				success = TestsSucceeded (listener_log, timed_out, crashed);
			} else if (timed_out) {
				Harness.LogWrench ("@MonkeyWrench: AddSummary: <b><i>{0} never launched</i></b><br/>", mode);
				main_log.WriteLine ("Test run never launched");
				success = false;
			} else if (launch_failure) {
 				Harness.LogWrench ("@MonkeyWrench: AddSummary: <b><i>{0} failed to launch</i></b><br/>", mode);
 				main_log.WriteLine ("Test run failed to launch");
 				success = false;
			} else {
				Harness.LogWrench ("@MonkeyWrench: AddSummary: <b><i>{0} crashed at startup (no log)</i></b><br/>", mode);
				main_log.WriteLine ("Test run crashed before it started (no log file produced)");
				crashed = true;
				success = false;
			}
				
			if (!success.HasValue)
				success = false;

			await crash_reports.EndCaptureAsync (TimeSpan.FromSeconds (success.Value ? 0 : 5));

			if (timed_out) {
				Result = TestExecutingResult.TimedOut;
			} else if (crashed) {
				Result = TestExecutingResult.Crashed;
			} else if (success.Value) {
				Result = TestExecutingResult.Succeeded;
			} else {
				Result = TestExecutingResult.Failed;
			}

			// Check crash reports to see if any of them explains why the test run crashed.
			if (!success.Value) {
				int pid = 0;
				string crash_reason = null;
				foreach (var crash in crash_reports.Logs) {
					try {
						if (pid == 0) {
							// Find the pid
							using (var log_reader = main_log.GetReader ()) {
								string line;
								while ((line = log_reader.ReadLine ()) != null) {
									const string str = "was launched with pid '";
									var idx = line.IndexOf (str, StringComparison.Ordinal);
									if (idx > 0) {
										idx += str.Length;
										var next_idx = line.IndexOf ('\'', idx);
										if (next_idx > idx)
											int.TryParse (line.Substring (idx, next_idx - idx), out pid);
									}
									if (pid != 0)
										break;
								}
							}
						}

						using (var crash_reader = crash.GetReader ()) {
							var text = crash_reader.ReadToEnd ();

							var reader = System.Runtime.Serialization.Json.JsonReaderWriterFactory.CreateJsonReader (Encoding.UTF8.GetBytes (text), new XmlDictionaryReaderQuotas ());
							var doc = new XmlDocument ();
							doc.Load (reader);
							foreach (XmlNode node in doc.SelectNodes ($"/root/processes/item[pid = '" + pid + "']")) {
								Console.WriteLine (node?.InnerXml);
								Console.WriteLine (node?.SelectSingleNode ("reason")?.InnerText);
								crash_reason = node?.SelectSingleNode ("reason")?.InnerText;
							}
						}
						if (crash_reason != null)
							break;
					} catch (Exception e) {
						Harness.Log (2, "Failed to process crash report '{1}': {0}", e.Message, crash.Description);
					}
				}
				if (!string.IsNullOrEmpty (crash_reason)) {
					if (crash_reason == "per-process-limit") {
						FailureMessage = "Killed due to using too much memory (per-process-limit).";
					} else {
						FailureMessage = $"Killed by the OS ({crash_reason})";
					}
				} else if (launch_failure) {
					FailureMessage = $"Launch failure";
				}
			}

			return success.Value ? 0 : 1;
		}

		public void AddDeviceName (StringBuilder args)
		{
			AddDeviceName (args, device_name);
		}

		public static void AddDeviceName (StringBuilder args, string device_name)
		{
			if (!string.IsNullOrEmpty (device_name)) {
				args.Append (" --devname ");
				args.Append (StringUtils.Quote (device_name));
			}
		}
	}

	// Monitor the output from 'mlaunch --installdev' and cancel the installation if there's no output for 1 minute.
	class AppInstallMonitorLog : Log {
		public override string FullPath => throw new NotImplementedException ();

		Log copy_to;
		CancellationTokenSource cancellation_source;
		
		public bool CopyingApp;
		public bool CopyingWatchApp;
		public TimeSpan AppCopyDuration;
		public TimeSpan WatchAppCopyDuration;
		public Stopwatch AppCopyStart = new Stopwatch ();
		public Stopwatch WatchAppCopyStart = new Stopwatch ();
		public int AppPercentComplete;
		public int WatchAppPercentComplete;
		public long AppBytes;
		public long WatchAppBytes;
		public long AppTotalBytes;
		public long WatchAppTotalBytes;

		public CancellationToken CancellationToken {
			get {
				return cancellation_source.Token;
			}
		}

		public AppInstallMonitorLog (Log copy_to)
				: base (copy_to.Logs, $"Watch transfer log for {copy_to.Description}")
		{
			this.copy_to = copy_to;
			cancellation_source = new CancellationTokenSource ();
			cancellation_source.Token.Register (() => {
				copy_to.WriteLine ("App installation cancelled: it timed out after no output for 1 minute.");
			});
		}

		public override Encoding Encoding => copy_to.Encoding;
		public override void Flush ()
		{
			copy_to.Flush ();
		}

		public override StreamReader GetReader ()
		{
			return copy_to.GetReader ();
		}

		protected override void Dispose (bool disposing)
		{
			base.Dispose (disposing);
			copy_to.Dispose ();
			cancellation_source.Dispose ();
		}

		void ResetTimer ()
		{
			cancellation_source.CancelAfter (TimeSpan.FromMinutes (1));
		}

		public override void WriteLine (string value)
		{
			var v = value.Trim ();
			if (v.StartsWith ("Installing application bundle", StringComparison.Ordinal)) {
				if (!CopyingApp) {
					CopyingApp = true;
					AppCopyStart.Start ();
				} else if (!CopyingWatchApp) {
					CopyingApp = false;
					CopyingWatchApp = true;
					AppCopyStart.Stop ();
					WatchAppCopyStart.Start ();
				}
			} else if (v.StartsWith ("PercentComplete: ", StringComparison.Ordinal) && int.TryParse (v.Substring ("PercentComplete: ".Length).Trim (), out var percent)) {
				if (CopyingApp)
					AppPercentComplete = percent;
				else if (CopyingWatchApp)
					WatchAppPercentComplete = percent;
			} else if (v.StartsWith ("NumBytes: ", StringComparison.Ordinal) && int.TryParse (v.Substring ("NumBytes: ".Length).Trim (), out var num_bytes)) {
				if (CopyingApp) {
					AppBytes = num_bytes;
					AppCopyDuration = AppCopyStart.Elapsed;
				} else if (CopyingWatchApp) {
					WatchAppBytes = num_bytes;
					WatchAppCopyDuration = WatchAppCopyStart.Elapsed;
				}
			} else if (v.StartsWith ("TotalBytes: ", StringComparison.Ordinal) && int.TryParse (v.Substring ("TotalBytes: ".Length).Trim (), out var total_bytes)) {
				if (CopyingApp)
					AppTotalBytes = total_bytes;
				else if (CopyingWatchApp)
					WatchAppTotalBytes = total_bytes;
			}

			ResetTimer ();

			copy_to.WriteLine (value);
		}

		public override void Write (byte [] buffer, int offset, int count)
		{
			copy_to.Write (buffer, offset, count);
		}
	}
}
