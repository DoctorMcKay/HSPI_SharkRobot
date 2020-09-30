using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Web.Script.Serialization;
using System.Web.UI;
using HomeSeer.Jui.Types;
using HomeSeer.Jui.Views;
using HomeSeer.PluginSdk;
using HomeSeer.PluginSdk.Devices;
using HomeSeer.PluginSdk.Devices.Controls;
using HomeSeer.PluginSdk.Devices.Identification;
using HomeSeer.PluginSdk.Logging;
using HSPI_SharkRobot.DataContainers;
using HSPI_SharkRobot.Enums;
using Timer = System.Timers.Timer;

namespace HSPI_SharkRobot
{
	// ReSharper disable once InconsistentNaming
	public class HSPI : AbstractPlugin
	{
		public override string Name { get; } = "Shark Robot";
		public override string Id { get; } = "SharkRobot";

		private AylaClient _client;
		private HsDeviceMap[] _devices;
		private Timer _loginTimer = null;
		private Timer _refreshTimer = null;
		private Timer _pollTimer = null;
		private bool _debugLogging = false;

		protected override void Initialize() {
			WriteLog(ELogType.Debug, "Initialize");
			
			// Build the settings page
			PageFactory settingsPageFactory = PageFactory
				.CreateSettingsPage("SharkRobotSettings", "Shark Robot Settings")
				.WithLabel("shark_api_note", "Note", "If you update your email, you MUST also provide your password, even if it didn't change.")
				.WithInput("shark_api_email", "Shark Account Email")
				.WithInput("shark_api_password", "Shark Account Password", EInputType.Password)
#if DEBUG
				.WithLabel("debug_log", "Enable Debug Logging", "ON - DEBUG BUILD");
#else
				.WithToggle("debug_log", "Enable Debug Logging");
#endif
			
			Settings.Add(settingsPageFactory.Page);

			Status = PluginStatus.Ok();

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
			}
		}

		public override void SetIOMulti(List<ControlEvent> colSend) {
			WriteLog(ELogType.Trace, "SetIOMulti");
			
			foreach (ControlEvent ce in colSend) {
				HsFeature feature = HomeSeerSystem.GetFeatureByRef(ce.TargetRef);
				string[] addressParts = feature.Address.Split(':');
				
				HsDeviceMap? nullMap = null;
				foreach (HsDeviceMap devMap in _devices) {
					if (devMap.SharkDevice.Dsn == addressParts[1]) {
						nullMap = devMap;
						break;
					}
				}

				if (nullMap == null) {
					WriteLog(ELogType.Warning, $"Unable to find device with DSN {addressParts[1]} to control for ref {ce.TargetRef}");
					continue;
				}

				HsDeviceMap map = (HsDeviceMap) nullMap;

				int propKey = 0;
				int propVal = 0;

				switch (addressParts[2]) {
					case "Status":
						switch ((HsStatus) ce.ControlValue) {
							case HsStatus.ControlOnlyLocate:
								propKey = map.LastProperties?.SetFindDeviceKey ?? 0;
								propVal = 1;
								break;
							
							case HsStatus.Running:
								propKey = map.LastProperties?.SetOperatingModeKey ?? 0;
								propVal = (int) SharkOperatingMode.Running;
								break;
							
							case HsStatus.NotRunning:
								propKey = map.LastProperties?.SetOperatingModeKey ?? 0;
								propVal = (int) SharkOperatingMode.NotRunning;
								break;
							
							case HsStatus.ReturnToDock:
								propKey = map.LastProperties?.SetOperatingModeKey ?? 0;
								propVal = (int) SharkOperatingMode.Dock;
								break;
						}

						break;
				}

				if (propKey == 0) {
					WriteLog(ELogType.Warning, $"Unknown property key for ref {ce.TargetRef}");
				} else {
					_client.SetPropertyInt(propKey, propVal).ContinueWith((task) => { });
				}
			}
		}

		private async void _refreshLogin() {
			// Cancel polling
			_pollTimer?.Stop();
			
			WriteLog(ELogType.Info, "Obtaining new access token using refresh token");
			string errMsg = await _client.LoginWithTokens();
			if (errMsg.Length > 0) {
				WriteLog(ELogType.Error, $"Failure refreshing login: {errMsg}");
				Status = PluginStatus.Critical(errMsg);
				_enqueueRefreshLogin();
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
					HsDeviceMap map = new HsDeviceMap {
						SharkDevice = dev,
						HsDeviceRef = hsDevice.Ref,
						LastProperties = null,
						HsFeatureRefStatus = HomeSeerSystem.GetFeatureByAddress($"Shark:{dev.Dsn}:Status").Ref,
						HsFeatureRefBattery = HomeSeerSystem.GetFeatureByAddress($"Shark:{dev.Dsn}:Battery").Ref
					};
					// TODO if we release, don't crash if the features don't exist
					_devices[i] = map;
				}
			}

			_enqueuePoll();
		}

		private HsDeviceMap _createHsDevice(Device dev) {
			FeatureFactory statusFactory = FeatureFactory.CreateFeature(Id)
				.WithName("Status")
				.AddGraphicForValue("/images/HomeSeer/status/off.gif", (double) HsStatus.Disconnected, "Offline")
				.AddGraphicForValue("/images/HomeSeer/status/electricity.gif", (double) HsStatus.Charging, "Charging")
				.AddGraphicForValue("/images/HomeSeer/status/ok.png", (double) HsStatus.FullyChargedOnDock, "Resting on Dock")
				.AddGraphicForValue("/images/HomeSeer/status/electricity.gif", (double) HsStatus.ChargingToResume, "Charging to Resume")
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
			
			FeatureFactory powerModeFactory = FeatureFactory.CreateFeature(Id)
				.WithName("Power Mode")
				.AddGraphicForValue("/images/HomeSeer/status/fan1.png", (double) SharkPowerMode.Eco, "Eco")
				.AddGraphicForValue("/images/HomeSeer/status/fan2.png", (double) SharkPowerMode.Normal, "Normal")
				.AddGraphicForValue("/images/HomeSeer/status/fan3.png", (double) SharkPowerMode.Max, "Max");
			
			FeatureFactory batteryFactory = FeatureFactory.CreateFeature(Id)
				.WithName("Battery")
				.WithMiscFlags(EMiscFlag.StatusOnly)
				.AddGraphicForRange("/images/HomeSeer/status/battery_0.png", 0, 3)
				.AddGraphicForRange("/images/HomeSeer/status/battery_25.png", 4, 36)
				.AddGraphicForRange("/images/HomeSeer/status/battery_50.png", 37, 64)
				.AddGraphicForRange("/images/HomeSeer/status/battery_75.png", 65, 89)
				.AddGraphicForRange("/images/HomeSeer/status/battery_100.png", 90, 100);
			
			DeviceFactory deviceFactory = DeviceFactory.CreateDevice(Id)
				.WithName(dev.ProductName)
				.WithFeature(statusFactory)
				.WithFeature(powerModeFactory)
				.WithFeature(batteryFactory);

			int devRef = HomeSeerSystem.CreateDevice(deviceFactory.PrepareForHs());
			HomeSeerSystem.UpdatePropertyByRef(devRef, EProperty.Address, $"Shark:{dev.Dsn}");	
			HsDevice hsDevice = HomeSeerSystem.GetDeviceWithFeaturesByRef(devRef);

			HsDeviceMap map = new HsDeviceMap {SharkDevice = dev, HsDeviceRef = devRef, LastProperties = null};

			foreach (HsFeature feature in hsDevice.Features) {
				HomeSeerSystem.UpdatePropertyByRef(feature.Ref, EProperty.Address, $"Shark:{dev.Dsn}:{feature.Name.Replace(" ", "")}");
				switch (feature.Name) {
					case "Status":
						map.HsFeatureRefStatus = feature.Ref;
						break;
					
					case "Power Mode":
						map.HsFeatureRefPowerMode = feature.Ref;
						break;
					
					case "Battery":
						map.HsFeatureRefBattery = feature.Ref;
						break;
				}
			}

			return map;
		}

		protected override void OnSettingsLoad() {
			// Called when the settings page is loaded. Use to pre-fill the inputs.
			string acctEmail = HomeSeerSystem.GetINISetting("Credentials", "email", "", SettingsFileName);
			string acctToken = HomeSeerSystem.GetINISetting("Credentials", "refresh_token", "", SettingsFileName);
			Settings.Pages[0].GetViewById("shark_api_email").UpdateValue(acctEmail);
			Settings.Pages[0].GetViewById("shark_api_password").UpdateValue(acctToken.Length == 0 && _loginTimer == null ? "" : "*****");
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

					_loginTimer?.Stop();
					_loginTimer = null;
					
					HomeSeerSystem.SaveINISetting("Credentials", "email", email, SettingsFileName);
					return true;
				
				case "shark_api_password":
					string password = changedView.GetStringValue();
					if (password == "*****") {
						return true; // no change
					}

					_enqueuePasswordLogin(password);
					return true;
				
				case "debug_log":
					_debugLogging = changedView.GetStringValue() == "True";
					return true;
			}
			
			WriteLog(ELogType.Info, $"Request to save unknown setting {currentView.Id}");
			return false;
		}

		private void _enqueuePasswordLogin(string password) {
			WriteLog(ELogType.Debug, "Enqueueing password login");
			_loginTimer?.Stop();

			_loginTimer = new Timer(1000) {
				Enabled = true,
				AutoReset = false
			};

			_loginTimer.Elapsed += async (object src, ElapsedEventArgs a) => {
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
			};
		}

		private void _enqueueRefreshLogin() {
			WriteLog(ELogType.Debug, "Enqueueing refresh login");
			_refreshTimer?.Stop();
			
			_refreshTimer = new Timer(10000) {
				Enabled = true,
				AutoReset = false
			};

			_refreshTimer.Elapsed += (object src, ElapsedEventArgs a) => {
				_refreshTimer = null;
				_refreshLogin();
			};
		}

		private async void _enqueuePoll() {
			if (DateTime.Now.AddMinutes(5).CompareTo(_client.TokenExpirationTime) >= 0) {
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

		_pollTimer?.Stop();
			_pollTimer = new Timer(10000) {Enabled = true, AutoReset = false};
			_pollTimer.Elapsed += async (object src, ElapsedEventArgs a) => {
				_pollTimer = null;
				WriteLog(ELogType.Trace, "Performing poll");
				
				for (int i = 0; i < _devices.Length; i++) {
					try {
						HsDeviceMap deviceMap = _devices[i];
						DeviceProperties props = await _client.GetDeviceProperties(deviceMap.SharkDevice.Dsn);

						WriteLog(ELogType.Debug,
							$"Retrieved properties for \"{props.DeviceName}\" ({deviceMap.SharkDevice.Dsn})");

						// Update battery
						HomeSeerSystem.UpdateFeatureValueByRef(deviceMap.HsFeatureRefBattery, props.BatteryCapacity);
						HomeSeerSystem.UpdateFeatureValueStringByRef(deviceMap.HsFeatureRefBattery,
							props.BatteryCapacity + "%");

						// Update power mode
						HomeSeerSystem.UpdateFeatureValueByRef(deviceMap.HsFeatureRefPowerMode,
							(double) props.PowerMode);

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
						} else if (props.ErrorCode == 5) {
							// Stuck (could be other error codes too)
							status = HsStatus.Stuck;
						} else if (props.ErrorCode > 0) {
							// Unknown error code
							status = HsStatus.UnknownError;
						} else {
							// No status matched, just fall back to "not running"
							status = HsStatus.NotRunning;
						}

						HomeSeerSystem.UpdateFeatureValueByRef(deviceMap.HsFeatureRefStatus, (double) status);
						HomeSeerSystem.UpdateFeatureValueStringByRef(deviceMap.HsFeatureRefStatus,
							status == HsStatus.UnknownError ? "Unknown Error " + props.ErrorCode : "");

						_devices[i].LastProperties = props;
					} catch (Exception ex) {
						string errMsg = ex.Message;
						Exception inner = ex;
						while ((inner = inner.InnerException) != null) {
							errMsg = $"{errMsg} [{inner.Message}]";
						}
						
						WriteLog(ELogType.Error, $"Unable to retrieve properties for device {_devices[i].SharkDevice.Dsn}: {errMsg}");
					}
				}
				
				_enqueuePoll();
			};
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
