// ReSharper disable InconsistentNaming
namespace HSPI_SharkRobot.DataContainers {
	public struct SetPropertyIntBody {
		public DatapointValue datapoint;

		public struct DatapointValue {
			public int value;
		}
	}
}
