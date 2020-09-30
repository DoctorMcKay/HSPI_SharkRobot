// ReSharper disable InconsistentNaming
namespace HSPI_SharkRobot.DataContainers {
	public struct TokenLoginBody {
		public TokenLoginUser user;

		public struct TokenLoginUser {
			public string refresh_token;
		}
	}
}
