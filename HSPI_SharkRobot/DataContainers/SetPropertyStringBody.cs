// ReSharper disable InconsistentNaming
namespace HSPI_SharkRobot.DataContainers {
	public struct SetPropertyStringBody {
		public DatapointValue datapoint;

		public struct DatapointValue {
			public string value;
		}
	}
}
