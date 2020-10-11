using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Timers;
using System.Web.Script.Serialization;
using HomeSeer.Jui.Types;
using HomeSeer.Jui.Views;
using HomeSeer.PluginSdk;
using HomeSeer.PluginSdk.Devices;
using HomeSeer.PluginSdk.Devices.Controls;
using HomeSeer.PluginSdk.Logging;
using HSPI_SharkRobot.DataContainers;
using HSPI_SharkRobot.Enums;

namespace HSPI_SharkRobot
{
	// ReSharper disable once InconsistentNaming
	public class HSPI : AbstractPlugin {
		public override string Name { get; } = "Shark Robot";
		public override string Id { get; } = "SharkRobot";

		private AylaClient _client;
		private HsDeviceMap[] _devices;
		private Timer _loginTimer;
		private Timer _refreshTimer;
		private Timer _pollTimer;
		private DateTime _fastPollUntil;
		private DateTime _lastTokenRefresh;
		private readonly Dictionary<int, double> _featureValueCache;
		private bool _debugLogging;

		public HSPI() {
			_fastPollUntil = DateTime.MinValue;
			_lastTokenRefresh = DateTime.Now;
			_featureValueCache = new Dictionary<int, double>();
		}

		protected override void Initialize() {
			WriteLog(ELogType.Debug, "Initialize");

			AnalyticsClient analytics = new AnalyticsClient(this, HomeSeerSystem);

			// Build the settings page
			PageFactory settingsPageFactory = PageFactory
				.CreateSettingsPage("SharkRobotSettings", "Shark Robot Settings")
				.WithLabel("plugin_status", "Status (refresh to update)", "x")
				.WithInput("shark_api_email", "Shark Account Email")
				.WithInput("shark_api_password", "Shark Account Password", EInputType.Password)
				.WithGroup("debug_group", "<hr>", new AbstractView[] {
					new LabelView("debug_system_id", "System ID (include this with any support requests)", analytics.CustomSystemId),
#if DEBUG
					new LabelView("debug_log", "Enable Debug Logging", "ON - DEBUG BUILD")
#else
					new ToggleView("debug_log", "Enable Debug Logging")
#endif
				});
			
			Settings.Add(settingsPageFactory.Page);

			Status = PluginStatus.Info("Initializing...");

			_client = new AylaClient(this);
			_debugLogging = HomeSeerSystem.GetINISetting("Debug", "debug_log", "0", SettingsFileName) == "1";
			
			// Do we need to refresh?
			string accessToken = HomeSeerSystem.GetINISetting("Credentials", "access_token", "", SettingsFileName);
			string refreshToken = HomeSeerSystem.GetINISetting("Credentials", "refresh_token", "", SettingsFileName);
			if (accessToken.Length > 0 && refreshToken.Length > 0) {
				_client.AccessToken = accessToken;
				_client.RefreshToken = refreshToken;
				_client.TokenExpirationTime = DateTime.Now;
				_refreshLogin();
			} else {
				Status = PluginStatus.Critical("No credentials configured");
			}

			analytics.ReportIn(5000);
		}

		public override async void SetIOMulti(List<ControlEvent> colSend) {
			WriteLog(ELogType.Trace, "SetIOMulti");
			
			foreach (ControlEvent ce in colSend) {
				HsFeature feature = HomeSeerSystem.GetFeatureByRef(ce.TargetRef);
				string[] addressParts = feature.Address.Split(':');

				string propName = "";
				int propVal = 0;

				switch (addressParts[2]) {
					case "Status":
						switch ((HsStatus) ce.ControlValue) {
							case HsStatus.ControlOnlyLocate:
								propName = "SET_Find_Device";
								propVal = 1;
								break;
							
							case HsStatus.Running:
								propName = "SET_Operating_Mode";
								propVal = (int) SharkOperatingMode.Running;
								break;
							
							case HsStatus.NotRunning:
								propName = "SET_Operating_Mode";
								propVal = (int) SharkOperatingMode.NotRunning;
								break;
							
							case HsStatus.ReturnToDock:
								propName = "SET_Operating_Mode";
								propVal = (int) SharkOperatingMode.Dock;
								break;
						}

						break;
					
					case "PowerMode":
						propName = "SET_Power_Mode";
						propVal = (int) ce.ControlValue;
						break;
				}

				if (propName.Length == 0) {
					WriteLog(ELogType.Warning, $"Unknown property key for ref {ce.TargetRef}");
				} else {
					await _client.SetPropertyInt(addressParts[1], propName, propVal);
					_fastPollUntil = DateTime.Now.AddSeconds(10);
					_enqueuePoll(true);
				}
			}
		}

		private async void _refreshLogin() {
			// Cancel polling
			_pollTimer?.Stop();
			_lastTokenRefresh = DateTime.Now;

			WriteLog(ELogType.Info, "Obtaining new access token using refresh token");
			string errMsg = await _client.LoginWithTokens();
			if (errMsg.Length > 0) {
				WriteLog(ELogType.Error, $"Failure refreshing login: {errMsg}");
				Status = PluginStatus.Critical(errMsg);
				if (errMsg.Contains("Unauthorized")) {
					_enqueuePasswordLogin();
				} else {
					_enqueueRefreshLogin();
				}

				return;
			}
			
			WriteLog(ELogType.Debug, "Successfully refreshed login");
			HomeSeerSystem.SaveINISetting("Credentials", "access_token", _client.AccessToken, SettingsFileName);
			HomeSeerSystem.SaveINISetting("Credentials", "refresh_token", _client.RefreshToken, SettingsFileName);

#if DEBUG
			Console.WriteLine("Access Token: " + _client.AccessToken);
#endif
			
			// Sync devices
			Device[] devices = await _client.GetDevices();
			_devices = new HsDeviceMap[devices.Length];

			for (int i = 0; i < devices.Length; i++) {
				Device dev = devices[i];
				// Find a matching HS device?
				HsDevice hsDevice = HomeSeerSystem.GetDeviceByAddress($"Shark:{dev.Dsn}");
				if (hsDevice == null) {
					_devices[i] = _createHsDevice(dev);
				} else {
					_devices[i] = _createHsDeviceMissingFeatures(new HsDeviceMap {
						SharkDevice = dev,
						HsDeviceRef = hsDevice.Ref,
						LastProperties = null,
						HsFeatureRefStatus = HomeSeerSystem.GetFeatureByAddress($"Shark:{dev.Dsn}:Status")?.Ref,
						HsFeatureRefPowerMode = HomeSeerSystem.GetFeatureByAddress($"Shark:{dev.Dsn}:PowerMode")?.Ref,
						HsFeatureRefBattery = HomeSeerSystem.GetFeatureByAddress($"Shark:{dev.Dsn}:Battery")?.Ref
					});
				}
			}

			_enqueuePoll(true);
		}

		private HsDeviceMap _createHsDevice(Device dev) {
			DeviceFactory deviceFactory = DeviceFactory.CreateDevice(Id)
				.WithName(dev.ProductName);

			int devRef = HomeSeerSystem.CreateDevice(deviceFactory.PrepareForHs());
			HomeSeerSystem.UpdatePropertyByRef(devRef, EProperty.Address, $"Shark:{dev.Dsn}");

			return _createHsDeviceMissingFeatures(new HsDeviceMap {
				SharkDevice = dev,
				HsDeviceRef = devRef,
				LastProperties = null,
				HsFeatureRefStatus = null,
				HsFeatureRefPowerMode = null,
				HsFeatureRefBattery = null
			});
		}

		private HsDeviceMap _createHsDeviceMissingFeatures(HsDeviceMap map) {
			string dsn = map.SharkDevice.Dsn;
			
			if (map.HsFeatureRefStatus == null) {
				FeatureFactory statusFactory = FeatureFactory.CreateFeature(Id)
					.OnDevice(map.HsDeviceRef)
					.WithName("Status")
					.AddGraphicForValue("/images/HomeSeer/status/off.gif", (double) HsStatus.Disconnected, "Offline")
					.AddGraphicForValue("/images/HomeSeer/status/electricity.gif", (double) HsStatus.Charging, "Charging")
					.AddGraphicForValue("/images/HomeSeer/status/ok.png", (double) HsStatus.FullyChargedOnDock,"Resting on Dock")
					.AddGraphicForValue("/images/HomeSeer/status/electricity.gif", (double) HsStatus.ChargingToResume,"Charging to Resume")
					.AddGraphicForValue("/images/HomeSeer/status/eject.png", (double) HsStatus.Evacuating, "Evacuating")
					.AddGraphicForValue("/images/HomeSeer/status/pause.png", (double) HsStatus.NotRunning, "Paused")
					.AddGraphicForValue("/images/HomeSeer/status/on.gif", (double) HsStatus.Running, "Cleaning")
					.AddGraphicForValue("/images/HomeSeer/status/record.png", (double) HsStatus.SpotClean, "Spot Clean")
					.AddGraphicForValue("/images/HomeSeer/status/refresh.png", (double) HsStatus.ReturnToDock, "Return To Dock")
					.AddGraphicForValue("/images/HomeSeer/status/alarm.png", (double) HsStatus.Stuck, "Stuck")
					.AddGraphicForValue("/images/HomeSeer/status/alarm.png", (double) HsStatus.UnknownError, "Unknown Error")
					.AddButton((double) HsStatus.Running, "Clean")
					.AddButton((double) HsStatus.NotRunning, "Pause")
					.AddButton((double) HsStatus.ReturnToDock, "Dock")
					.AddButton((double) HsStatus.ControlOnlyLocate, "Locate");

				map.HsFeatureRefStatus = HomeSeerSystem.CreateFeatureForDevice(statusFactory.PrepareForHs());
				HomeSeerSystem.UpdatePropertyByRef((int) map.HsFeatureRefStatus, EProperty.Address, $"Shark:{dsn}:Status");
				WriteLog(ELogType.Info, $"Created feature {map.HsFeatureRefStatus} for Status on device {dsn}");
			}

			if (map.HsFeatureRefPowerMode == null) {
				FeatureFactory powerModeFactory = FeatureFactory.CreateFeature(Id)
					.OnDevice(map.HsDeviceRef)
					.WithName("Power Mode")
					.AddGraphicForValue("/images/HomeSeer/status/fan1.png", (double) SharkPowerMode.Eco, "Eco")
					.AddGraphicForValue("/images/HomeSeer/status/fan2.png", (double) SharkPowerMode.Normal, "Normal")
					.AddGraphicForValue("/images/HomeSeer/status/fan3.png", (double) SharkPowerMode.Max, "Max")
					.AddButton((double) SharkPowerMode.Eco, "Eco")
					.AddButton((double) SharkPowerMode.Normal, "Normal")
					.AddButton((double) SharkPowerMode.Max, "Max");

				map.HsFeatureRefPowerMode = HomeSeerSystem.CreateFeatureForDevice(powerModeFactory.PrepareForHs());
				HomeSeerSystem.UpdatePropertyByRef((int) map.HsFeatureRefPowerMode, EProperty.Address, $"Shark:{dsn}:PowerMode");
				WriteLog(ELogType.Info, $"Created feature {map.HsFeatureRefPowerMode} for PowerMode on device {dsn}");
			}

			if (map.HsFeatureRefBattery == null) {
				FeatureFactory batteryFactory = FeatureFactory.CreateFeature(Id)
					.OnDevice(map.HsDeviceRef)
					.WithName("Battery")
					.WithMiscFlags(EMiscFlag.StatusOnly)
					.AddGraphicForRange("/images/HomeSeer/status/battery_0.png", 0, 3)
					.AddGraphicForRange("/images/HomeSeer/status/battery_25.png", 4, 36)
					.AddGraphicForRange("/images/HomeSeer/status/battery_50.png", 37, 64)
					.AddGraphicForRange("/images/HomeSeer/status/battery_75.png", 65, 89)
					.AddGraphicForRange("/images/HomeSeer/status/battery_100.png", 90, 100);

				map.HsFeatureRefBattery = HomeSeerSystem.CreateFeatureForDevice(batteryFactory.PrepareForHs());
				HomeSeerSystem.UpdatePropertyByRef((int) map.HsFeatureRefBattery, EProperty.Address, $"Shark:{dsn}:Battery");
				WriteLog(ELogType.Info, $"Created feature {map.HsFeatureRefBattery} for Battery on device {dsn}");
			}

			return map;
		}

		protected override void OnSettingsLoad() {
			// Called when the settings page is loaded. Use to pre-fill the inputs.
			string statusText = Status.Status.ToString().ToUpper();
			if (Status.StatusText.Length > 0) {
				statusText += ": " + Status.StatusText;
			}
			string acctEmail = HomeSeerSystem.GetINISetting("Credentials", "email", "", SettingsFileName);
			string acctPasswordObfuscated = HomeSeerSystem.GetINISetting("Credentials", "password", "", SettingsFileName);
			((LabelView) Settings.Pages[0].GetViewById("plugin_status")).Value = statusText;
			Settings.Pages[0].GetViewById("shark_api_email").UpdateValue(acctEmail);
			Settings.Pages[0].GetViewById("shark_api_password").UpdateValue(acctPasswordObfuscated.Length == 0 && _loginTimer == null ? "" : "*****");
		}

		protected override bool OnSettingChange(string pageId, AbstractView currentView, AbstractView changedView) {
			WriteLog(ELogType.Debug, $"Request to save setting {currentView.Id} on page {pageId}");

			if (pageId != "SharkRobotSettings") {
				WriteLog(ELogType.Warning, $"Request to save settings on unknown page {pageId}!");
				return true;
			}

			switch (currentView.Id) {
				case "shark_api_email":
					string email = changedView.GetStringValue();
					if (email == HomeSeerSystem.GetINISetting("Credentials", "email", "", SettingsFileName)) {
						return true; // no change
					}

					HomeSeerSystem.SaveINISetting("Credentials", "email", email, SettingsFileName);
					return true;
				
				case "shark_api_password":
					string password = changedView.GetStringValue();
					if (password == "*****") {
						return true; // no change
					}

					HomeSeerSystem.SaveINISetting("Credentials", "password", _obfuscateString(password), SettingsFileName);
					Status = PluginStatus.Info("Logging in...");
					_enqueuePasswordLogin();
					return true;
				
				case "debug_log":
					_debugLogging = changedView.GetStringValue() == "True";
					return true;
			}
			
			WriteLog(ELogType.Info, $"Request to save unknown setting {currentView.Id}");
			return false;
		}

		private string _obfuscateString(string str) {
			// This doesn't really provide any serious security, but it prevents casual sniffing of the ini file
			byte[] key = {0x97, 0xc3, 0xd1, 0xe6, 0xd0, 0xb3, 0x77, 0x3a};
			byte[] strBytes = Encoding.UTF8.GetBytes(str);
			byte[] obfuscated = new byte[strBytes.Length];
			for (int i = 0; i < strBytes.Length; i++) {
				obfuscated[i] = (byte) (strBytes[i] ^ key[i % key.Length]);
			}

			return Convert.ToBase64String(obfuscated);
		}

		private string _unobfuscateString(string str) {
			byte[] key = {0x97, 0xc3, 0xd1, 0xe6, 0xd0, 0xb3, 0x77, 0x3a};
			byte[] obfuscated = Convert.FromBase64String(str);
			byte[] strBytes = new byte[obfuscated.Length];
			for (int i = 0; i < obfuscated.Length; i++) {
				strBytes[i] = (byte) (obfuscated[i] ^ key[i % key.Length]);
			}

			return Encoding.UTF8.GetString(strBytes);
		}

		private void _enqueuePasswordLogin() {
			string password = HomeSeerSystem.GetINISetting("Credentials", "password", "", SettingsFileName);
			if (password.Length == 0) {
				Status = PluginStatus.Fatal("No password set");
				return;
			}

			password = _unobfuscateString(password);
			
			WriteLog(ELogType.Debug, "Enqueueing password login");
			_loginTimer?.Stop();

			_loginTimer = new Timer(1000) {
				Enabled = true,
				AutoReset = false
			};

			_loginTimer.Elapsed += async (src, arg) => {
				_loginTimer = null;

				string email = HomeSeerSystem.GetINISetting("Credentials", "email", "", SettingsFileName);
				WriteLog(ELogType.Debug, "Logging in with password");
				string errMsg = await _client.LoginWithPassword(email, password);
				if (errMsg.Length > 0) {
					WriteLog(ELogType.Error, $"Cannot authenticate with cloud: {errMsg}");
					Status = PluginStatus.Fatal(errMsg);
					return;
				}

				WriteLog(ELogType.Debug, "Authenticated successfully with cloud");

				HomeSeerSystem.SaveINISetting("Credentials", "access_token", _client.AccessToken, SettingsFileName);
				HomeSeerSystem.SaveINISetting("Credentials", "refresh_token", _client.RefreshToken, SettingsFileName);
				
				_enqueuePoll(true);
			};
		}

		private void _enqueueRefreshLogin() {
			WriteLog(ELogType.Debug, "Enqueueing refresh login");
			_refreshTimer?.Stop();
			
			_refreshTimer = new Timer(10000) {
				Enabled = true,
				AutoReset = false
			};

			_refreshTimer.Elapsed += (src, arg) => {
				_refreshTimer = null;
				_refreshLogin();
			};
		}

		private async void _enqueuePoll(bool immediate = false) {
			_pollTimer?.Stop();
			
			if (_client.TokenExpirationTime.Subtract(DateTime.Now).TotalMinutes <= 5) {
				// Token expires in 5 minutes or less
				WriteLog(ELogType.Debug, $"Refreshing access token, which expires {_client.TokenExpirationTime}");
				try {
					string errMsg = await _client.LoginWithTokens();
					if (errMsg.Length > 0) {
						WriteLog(ELogType.Error, "Unable to refresh access token: " + errMsg);
					} else {
						WriteLog(ELogType.Info, "Refreshed access token successfully");
					}
				} catch (Exception ex) {
					WriteLog(ELogType.Error, "Unable to refresh access token: " + ex.Message);
				}
			}

			WriteLog(ELogType.Trace, $"Enqueueing poll (access token expires {_client.TokenExpirationTime})");
			
			_pollTimer = new Timer(immediate || _fastPollUntil.Subtract(DateTime.Now).TotalSeconds <= 0 ? 1000 : 10000)
				{Enabled = true, AutoReset = false};
			_pollTimer.Elapsed += async (src, arg) => {
				_pollTimer = null;
				WriteLog(ELogType.Trace, "Performing poll");

				PluginStatus newStatus = PluginStatus.Ok();
				
				for (int i = 0; i < _devices.Length; i++) {
					try {
						HsDeviceMap deviceMap = _devices[i];
						DeviceProperties props = await _client.GetDeviceProperties(deviceMap.SharkDevice.Dsn);

						WriteLog(ELogType.Debug, $"Retrieved properties for \"{props.DeviceName}\" ({deviceMap.SharkDevice.Dsn})");

						// Update battery
						_updateFeatureValue((int) deviceMap.HsFeatureRefBattery, props.BatteryCapacity, props.BatteryCapacity + "%");

						// Update power mode
						_updateFeatureValue((int) deviceMap.HsFeatureRefPowerMode, (double) props.PowerMode);

#if DEBUG
						JavaScriptSerializer json = new JavaScriptSerializer();
						WriteLog(ELogType.Debug, deviceMap.SharkDevice.Dsn + ": " + json.Serialize(props));
#endif

						// Figure out status
						// TODO figure out disconnected
						HsStatus status;
						if ((props.ChargingStatus || props.DockedStatus) && props.BatteryCapacity == 100 && !props.RechargingToResume) {
							// We're docked and battery is full
							status = HsStatus.FullyChargedOnDock;
						} else if (props.ChargingStatus) {
							// We are currently charging
							status = props.RechargingToResume ? HsStatus.ChargingToResume : HsStatus.Charging;
						} else if (props.OperatingMode == SharkOperatingMode.Running) {
							// We're currently running
							status = HsStatus.Running;
						} else if (props.OperatingMode == SharkOperatingMode.SpotClean) {
							// Spot clean mode
							status = HsStatus.SpotClean;
						} else if (props.OperatingMode == SharkOperatingMode.Dock && !props.DockedStatus) {
							// Returning to dock
							status = HsStatus.ReturnToDock;
						} else if (props.ErrorCode == 5 || props.ErrorCode == 6 || props.ErrorCode == 8) {
							// Stuck (could be other error codes too)
							status = HsStatus.Stuck;
						} else if (props.ErrorCode > 0) {
							// Unknown error code
							status = HsStatus.UnknownError;
						} else {
							// No status matched, just fall back to "not running"
							status = HsStatus.NotRunning;
						}

						_updateFeatureValue((int) deviceMap.HsFeatureRefStatus, (double) status, status == HsStatus.UnknownError ? "Unknown Error " + props.ErrorCode : "");
						_devices[i].LastProperties = props;
					} catch (Exception ex) {
						string errMsg = ex.Message;
						Exception inner = ex;
						while ((inner = inner.InnerException) != null) {
							errMsg = $"{errMsg} [{inner.Message}]";
						}

						string logMsg = $"Unable to retrieve properties for device {_devices[i].SharkDevice.Dsn}: {errMsg}";
						WriteLog(ELogType.Error, logMsg);
						newStatus = PluginStatus.Warning(logMsg);

						// Only attempt refreshing tokens once every 5 minutes
						if (ex.Message == "Unsuccessful status Unauthorized" && DateTime.Now.Subtract(_lastTokenRefresh).TotalMinutes >= 5) {
							_refreshLogin();
						}
					}
				}

				Status = newStatus;
				
				_enqueuePoll();
			};
		}

		private void _updateFeatureValue(int devRef, double value, string stringValue = null) {
			if (_featureValueCache.ContainsKey(devRef) && Math.Abs(_featureValueCache[devRef] - value) <= 0.01) {
				return; // no change
			}

			HomeSeerSystem.UpdateFeatureValueByRef(devRef, value);
			if (stringValue != null) {
				HomeSeerSystem.UpdateFeatureValueStringByRef(devRef, stringValue);
			}
			
			_featureValueCache[devRef] = value;
		}

		protected override void BeforeReturnStatus() {
			// Nothing happens here as we update the status as events happen
		}

		public void WriteLog(ELogType logType, string message, [CallerLineNumber] int lineNumber = 0, [CallerMemberName] string caller = null) {
#if DEBUG
			bool isDebugMode = true;

			// Prepend calling function and line number
			message = $"[{caller}:{lineNumber}] {message}";
			
			// Also print to console in debug builds
			string type = logType.ToString().ToLower();
			Console.WriteLine($"[{type}] {message}");
#else
			if (logType == ELogType.Trace) {
				// Don't record Trace events in production builds even if debug logging is enabled
				return;
			}

			bool isDebugMode = _debugLogging;
#endif

			if (logType <= ELogType.Debug && !isDebugMode) {
				return;
			}
			
			HomeSeerSystem.WriteLog(logType, message, Name);
		}
	}
}
