// ReSharper disable InconsistentNaming
namespace HSPI_SharkRobot.DataContainers {
	public struct EmailPasswordLoginBody {
		public EmailPasswordUser user;

		public struct EmailPasswordUser {
			public string email;
			public string password;
			public ApplicationCredentials application;
			
			public struct ApplicationCredentials {
				public string app_id;
				public string app_secret;
			}
		}
	}
}
