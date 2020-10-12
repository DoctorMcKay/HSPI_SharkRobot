using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using HomeSeer.PluginSdk.Logging;
using HSPI_SharkRobot.DataContainers;
using HSPI_SharkRobot.Enums;

namespace HSPI_SharkRobot {
	public class AylaClient {
		private const string AppId = "Shark-Android-field-id";
		private const string AppSecret = "Shark-Android-field-Wv43MbdXRM297HUHotqe6lU1n-w";

		public string AccessToken;
		public string RefreshToken;
		public DateTime TokenExpirationTime;

		private readonly HSPI _hs;
		private readonly HttpClient _httpClient;
		private readonly JavaScriptSerializer _jsonSerializer;

		public AylaClient(HSPI hs) {
			_hs = hs;
			_httpClient = new HttpClient();
			_jsonSerializer = new JavaScriptSerializer();
			
			_httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
			_httpClient.DefaultRequestHeaders.Add("User-Agent", "Dalvik/2.1.0 (Linux; U; Android 8.1.0; Pixel XL Build/OPM4.171019.021.D1)");
			
			ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
		}

		public async Task<string> LoginWithPassword(string email, string password) {
			EmailPasswordLoginBody body = new EmailPasswordLoginBody {
				user = new EmailPasswordLoginBody.EmailPasswordUser {
					email = email,
					password = password,
					application = new EmailPasswordLoginBody.EmailPasswordUser.ApplicationCredentials {
						app_id = AppId,
						app_secret = AppSecret
					}
				}
			};
			
			HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "https://user-field.aylanetworks.com/users/sign_in.json");
			req.Headers.Add("Authorization", "none");

			req.Content = new StringContent(_jsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

			try {
				_hs.WriteLog(ELogType.Debug, "Sending password login request");
				HttpResponseMessage res = await _httpClient.SendAsync(req);
				HttpStatusCode statusCode = res.StatusCode;

				string responseString = await res.Content.ReadAsStringAsync();
				dynamic response = _jsonSerializer.DeserializeObject(responseString);
				if (!res.IsSuccessStatusCode) {
					string errMsg = response["error"] ?? "Unspecified error";
					req.Dispose();
					res.Dispose();
					return $"{errMsg} ({statusCode})";
				}

				req.Dispose();
				res.Dispose();

				AccessToken = response["access_token"];
				RefreshToken = response["refresh_token"];
				int? expiresIn = response["expires_in"];
				if (AccessToken == null || RefreshToken == null || expiresIn == null) {
					return $"Missing logon data ({statusCode})";
				}

				TokenExpirationTime = DateTime.Now.AddSeconds((double) expiresIn);
				return "";
			} catch (Exception ex) {
				string errMsg = ex.Message;
				Exception inner = ex;
				while ((inner = inner.InnerException) != null) {
					errMsg = $"{errMsg} [{inner.Message}]";
				}

				req.Dispose();
				return errMsg;
			}
		}
		
		public async Task<string> LoginWithTokens() {
			TokenLoginBody body = new TokenLoginBody {
				user = new TokenLoginBody.TokenLoginUser {
					refresh_token = RefreshToken
				}
			};
			
			HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "https://user-field.aylanetworks.com/users/refresh_token.json");
			req.Headers.Add("Authorization", "auth_token " + AccessToken);
			
			req.Content = new StringContent(_jsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

			try {
				_hs.WriteLog(ELogType.Debug, "Sending token login request");
				HttpResponseMessage res = await _httpClient.SendAsync(req);
				HttpStatusCode statusCode = res.StatusCode;

				string responseString = await res.Content.ReadAsStringAsync();
				dynamic response = _jsonSerializer.DeserializeObject(responseString);
				if (!res.IsSuccessStatusCode) {
					string errMsg = response["error"] ?? "Unspecified error";
					req.Dispose();
					res.Dispose();
					return $"{errMsg} ({statusCode})";
				}

				req.Dispose();
				res.Dispose();

				AccessToken = response["access_token"];
				RefreshToken = response["refresh_token"];
				int? expiresIn = response["expires_in"];
				if (AccessToken == null || RefreshToken == null || expiresIn == null) {
					return $"Missing logon data ({statusCode})";
				}

				TokenExpirationTime = DateTime.Now.AddSeconds((double) expiresIn);
				_hs.WriteLog(ELogType.Debug, $"New token expires in {expiresIn} seconds. Current time is {DateTime.Now}, expires at {TokenExpirationTime}");
				return "";
			} catch (Exception ex) {
				string errMsg = ex.Message;
				Exception inner = ex;
				while ((inner = inner.InnerException) != null) {
					errMsg = $"{errMsg} [{inner.Message}]";
				}

				req.Dispose();
				return errMsg;
			}
		}

		public async Task<Device[]> GetDevices() {
			HttpResponseMessage res = await _getUrl("https://ads-field.aylanetworks.com/apiv1/devices.json");
			HttpStatusCode statusCode = res.StatusCode;
			if (!res.IsSuccessStatusCode) {
				res.Dispose();
				throw new Exception($"Unsuccessful status {statusCode}");
			}

			string responseString = await res.Content.ReadAsStringAsync();
			res.Dispose();

			dynamic response = _jsonSerializer.DeserializeObject(responseString);
			dynamic[] deviceList = (dynamic[]) response;
			Device[] output = new Device[deviceList.Length];

			for (int i = 0; i < deviceList.Length; i++) {
				Device dev = new Device {
					ProductName = deviceList[i]["device"]["product_name"],
					Model = deviceList[i]["device"]["model"],
					Dsn = deviceList[i]["device"]["dsn"],
					OemModel = deviceList[i]["device"]["oem_model"],
					SwVersion = deviceList[i]["device"]["sw_version"],
					TemplateId = deviceList[i]["device"]["template_id"],
					Mac = deviceList[i]["device"]["mac"],
					UniqueHardwareId = deviceList[i]["device"]["unique_hardware_id"],
					LanIp = deviceList[i]["device"]["lan_ip"],
					ConnectedAt = deviceList[i]["device"]["connected_at"],
					Key = deviceList[i]["device"]["key"],
					LanEnabled = deviceList[i]["device"]["lan_enabled"],
					HasProperties = deviceList[i]["device"]["has_properties"],
					ProductClass = deviceList[i]["device"]["product_class"],
					ConnectionStatus = deviceList[i]["device"]["connection_status"],
					Lat = deviceList[i]["device"]["lat"],
					Lng = deviceList[i]["device"]["lng"],
					Locality = deviceList[i]["device"]["locality"],
					DeviceType = deviceList[i]["device"]["device_type"],
				};

				output[i] = dev;
			}

			return output;
		}

		public async Task<DeviceProperties> GetDeviceProperties(string dsn) {
			HttpResponseMessage res = await _getUrl($"https://ads-field.aylanetworks.com/apiv1/dsns/{dsn}/properties.json");
			HttpStatusCode statusCode = res.StatusCode;
			if (!res.IsSuccessStatusCode) {
				res.Dispose();
				throw new Exception($"Unsuccessful status {statusCode}");
			}

			string responseString = await res.Content.ReadAsStringAsync();
			res.Dispose();

			dynamic response = _jsonSerializer.DeserializeObject(responseString);
			dynamic[] properties = (dynamic[]) response;
			
			DeviceProperties output = new DeviceProperties();

			foreach (dynamic prop in properties) {
				switch (prop["property"]["name"]) {
					case "GET_Battery_Capacity":
						output.BatteryCapacity = prop["property"]["value"];
						output.DeviceName = prop["property"]["product_name"];
						break;
					
					case "GET_Operating_Mode":
						output.OperatingMode = (SharkOperatingMode) prop["property"]["value"];
						break;
					
					case "GET_Power_Mode":
						output.PowerMode = (SharkPowerMode) prop["property"]["value"];
						break;
					
					case "GET_Charging_Status":
						output.ChargingStatus = prop["property"]["value"] != 0;
						break;
					
					case "GET_DockedStatus":
						output.DockedStatus = prop["property"]["value"] != 0;
						break;
					
					case "GET_Error_Code":
						output.ErrorCode = prop["property"]["value"];
						break;
					
					case "GET_Recharging_To_Resume":
						output.RechargingToResume = prop["property"]["value"] != 0;
						break;
					
					case "GET_Robot_Room_List":
						string joinedList = prop["property"]["value"];
						if (joinedList != null) {
							string[] list = joinedList.Split(':');
							output.RoomList = new RoomList {
								RoomListId = list[0],
								RoomNames = new string[list.Length - 1]
							};

							for (int i = 1; i < list.Length; i++) {
								output.RoomList.Value.RoomNames[i - 1] = list[i];
							}
						}
						break;
				}
			}

			return output;
		}

		public async Task<string> SetPropertyInt(string dsn, string property, int desiredValue) {
			SetPropertyIntBody body = new SetPropertyIntBody {
				datapoint = new SetPropertyIntBody.DatapointValue {
					value = desiredValue
				}
			};

			_hs.WriteLog(ELogType.Trace, $"Setting property {dsn}:{property} value to int {desiredValue}");
			return await _setProperty(dsn, property, _jsonSerializer.Serialize(body));
		}

		public async Task<string> SetPropertyString(string dsn, string property, string desiredValue) {
			SetPropertyStringBody body = new SetPropertyStringBody {
				datapoint = new SetPropertyStringBody.DatapointValue {
					value = desiredValue
				}
			};
			
			_hs.WriteLog(ELogType.Trace, $"Setting property {dsn}:{property} to string \"{desiredValue}\"");
			return await _setProperty(dsn, property, _jsonSerializer.Serialize(body));
		}
		
		private async Task<string> _setProperty(string dsn, string property, string serializedBody) {
			string url = $"https://ads-field.aylanetworks.com/apiv1/dsns/{dsn}/properties/{property}/datapoints.json";
			HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url);
			req.Headers.Add("Authorization", "auth_token " + AccessToken);
			req.Content = new StringContent(serializedBody, Encoding.UTF8, "application/json");
			
			HttpResponseMessage res = await _httpClient.SendAsync(req);
			HttpStatusCode statusCode = res.StatusCode;
			
			_hs.WriteLog(ELogType.Trace, $"Setting property {dsn}:{property} status: {statusCode}");
			
			if (!res.IsSuccessStatusCode) {
				req.Dispose();
				res.Dispose();
				return $"Unsuccessful status {statusCode}";
			}

			req.Dispose();
			res.Dispose();
			
			return "";
		}

		private async Task<HttpResponseMessage> _getUrl(string url, bool withAuth = true) {
			_hs.WriteLog(ELogType.Trace, $"Requesting {url}");
			HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
			if (withAuth) {
				req.Headers.Add("Authorization", "auth_token " + AccessToken);
			}

			HttpResponseMessage res = await _httpClient.SendAsync(req);
			req.Dispose();

			if (!res.IsSuccessStatusCode) {
				string msg = $"Unsuccessful status {res.StatusCode}";
				res.Dispose();
				throw new Exception(msg);
			}
			
			return res;
		}
	}
}
